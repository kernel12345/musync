using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MuSync.Models;
using MuSync.Players.Interfaces;
using MuSync.Utils;
using Windows.Media.Control;
namespace MuSync.Players;

/// <summary>
/// 汽水音乐（Soda Music，字节跳动）播放状态读取。
/// 汽水音乐是定制 Chromium/Electron 壳：官方无开放 API，且客户端会剥离 --remote-debugging-port
/// 等调试启动参数，CDP 与内存逆向路线均不可行。
/// 但其内核播放网页媒体时会自动注册到 Windows 系统媒体会话（SMTC，即音量面板 OSD 的数据源），
/// 通过 GlobalSystemMediaTransportControls API 可稳定读取歌名/歌手/播放状态/时间线/封面。
/// 该通道为 Windows 官方机制，对用户完全透明（无需任何启动参数或特殊设置）。
/// </summary>
internal sealed class SodaMusic : IMusicPlayer
{
    // 汽水音乐注册到系统媒体会话的 AppUserModelId（来自其渲染进程 --app-user-model-id 参数）。
    // 不同系统语言下理论上可能不同，附带可执行名兜底匹配。
    private const string SourceAumid = "汽水音乐";
    private static readonly string CoverDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MuSync", "soda-covers");

    // SMTC 会话管理器全进程共享一个；初始化失败不缓存，下次轮询自动重试
    private static readonly object ManagerInitLock = new();
    private static GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private static DateTime _lastManagerInitAttemptUtc = DateTime.MinValue;

    private string? _lastSongKey;
    private PlayerInfo? _lastInfoCache;

    public SodaMusic(int pid)
    {
        // 进程检测由调度层通过进程名完成，SMTC 会话按 AUMID 定位，pid 仅为保持工厂签名一致
        Debug.WriteLine($"[SodaMusic] 适配器已创建 (pid={pid})");
    }

    public async Task<PlayerInfo?> GetPlayerInfoAsync()
    {
        var manager = await TryGetSessionManagerAsync();
        if (manager is null) return _lastInfoCache;
        GlobalSystemMediaTransportControlsSession? session;
        try
        {
            session = FindSodaSession(manager);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SodaMusic] 枚举系统媒体会话失败: {ex.Message}");
            return _lastInfoCache;
        }
        if (session is null)
        {
            _lastInfoCache = null;
            _lastSongKey = null;
            return null;
        }
        try
        {
            var playback = session.GetPlaybackInfo();
            // Closed：媒体已彻底关闭，等同无播放信息；其余状态（Paused/Stopped 等）按“未在播放”处理，
            // 由调度层仲裁为空闲（Steam 显示空闲签名），但本地界面仍展示歌曲
            if (playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            {
                _lastInfoCache = null;
                _lastSongKey = null;
                return null;
            }
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null || string.IsNullOrWhiteSpace(props.Title))
            {
                return _lastInfoCache;
            }
            var songKey = BuildSongKey(props.Title, props.Artist, props.AlbumTitle);
            if (songKey != _lastSongKey)
            {
                _lastSongKey = songKey;
                Debug.WriteLine($"[SodaMusic] 切歌: {props.Title} - {props.Artist}");
            }
            var isPlaying = playback.PlaybackStatus ==
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double schedule = 0;
            double duration = 0;
            try
            {
                var timeline = session.GetTimelineProperties();
                duration = timeline.EndTime.TotalSeconds;
                schedule = timeline.Position.TotalSeconds;
                if (isPlaying)
                {
                    // SMTC 时间线更新频率取决于应用，按 LastUpdatedTime 做一次补偿，避免进度条跳变
                    schedule += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;
                }
                if (duration > 0)
                {
                    schedule = Math.Clamp(schedule, 0, duration);
                }
                else
                {
                    schedule = Math.Max(0, schedule);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SodaMusic] 读取时间线失败: {ex.Message}");
            }
            var coverUrl = await TryGetOrSaveCoverAsync(props.Thumbnail, songKey);
            var info = new PlayerInfo
            {
                Identity = songKey,
                Title = props.Title.Trim(),
                Artists = props.Artist ?? string.Empty,
                Album = props.AlbumTitle ?? string.Empty,
                Cover = coverUrl,
                Schedule = schedule,
                Duration = duration,
                Pause = !isPlaying,
                Url = string.Empty
            };
            _lastInfoCache = info;
            return info;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SodaMusic] 读取会话信息失败: {ex.Message}");
            return _lastInfoCache;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager?> TryGetSessionManagerAsync()
    {
        var cached = _sessionManager;
        if (cached is not null) return cached;
        // 管理器初始化在首次构造播放器时即可发起；失败时限流（每 10 秒重试一次）避免刷日志
        lock (ManagerInitLock)
        {
            if (_sessionManager is not null) return _sessionManager;
            if ((DateTime.UtcNow - _lastManagerInitAttemptUtc).TotalSeconds < 10) return null;
            _lastManagerInitAttemptUtc = DateTime.UtcNow;
        }
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (ManagerInitLock)
            {
                _sessionManager = manager;
            }
            Logger.Info("[SodaMusic] 系统媒体会话管理器已就绪");
            return manager;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SodaMusic] 系统媒体会话管理器初始化失败: {ex.Message}");
            return null;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? FindSodaSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        // 当前会话命中可直接返回；否则遍历全部会话按 AUMID 精确匹配
        var current = manager.GetCurrentSession();
        if (current is not null && IsSodaSession(current.SourceAppUserModelId))
        {
            return current;
        }
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = manager.GetSessions();
        foreach (var s in sessions)
        {
            if (IsSodaSession(s.SourceAppUserModelId))
            {
                return s;
            }
        }
        return null;
    }

    private static bool IsSodaSession(string? aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return false;
        if (aumid.Equals(SourceAumid, StringComparison.OrdinalIgnoreCase)) return true;
        return aumid.Contains("SodaMusic", StringComparison.OrdinalIgnoreCase) ||
               aumid.Contains("汽水音乐", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSongKey(string title, string artist, string album)
    {
        var raw = $"{title.Trim()}|{artist?.Trim() ?? ""}|{album?.Trim() ?? ""}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// SMTC 封面是内存流，无法走现有 HTTP 封面管线；按歌曲 hash 落盘到本地缓存目录，
    /// 返回 file:// URI 交给 ImageCacheManager 的本地文件分支加载。
    /// </summary>
    private static async Task<string> TryGetOrSaveCoverAsync(
        Windows.Storage.Streams.IRandomAccessStreamReference? thumbnail, string songKey)
    {
        if (thumbnail is null) return string.Empty;
        try
        {
            Directory.CreateDirectory(CoverDirectory);
            CleanupOrphanTempFiles();
            // 先探测是否已有缓存封面（内容类型未知，依次尝试常见扩展名）
            var existing = FindExistingCover(songKey);
            if (existing is not null)
            {
                return new Uri(existing).AbsoluteUri;
            }
            var tempPath = Path.Combine(CoverDirectory, songKey + ".tmp");
            string contentType;
            // 关键：读取与写入流必须在 File.Move 之前全部关闭（Flush+Dispose），
            // 否则 Windows 下目标文件仍被句柄占用，改名会抛 IOException
            using (var winrtStream = await thumbnail.OpenReadAsync())
            {
                contentType = winrtStream.ContentType;
                using var netStream = winrtStream.AsStreamForRead();
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    await netStream.CopyToAsync(fileStream);
                    await fileStream.FlushAsync();
                }
            }
            var extension = ResolveImageExtension(tempPath, contentType);
            var filePath = Path.Combine(CoverDirectory, songKey + extension);
            File.Move(tempPath, filePath, overwrite: true);
            return new Uri(filePath).AbsoluteUri;
        }
        catch (Exception ex)
        {
            Logger.Error($"[SodaMusic] 保存封面失败: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>按内容类型 + 文件头魔数确定扩展名；无法识别时默认 .jpg（SMTC 封面历史上多为 JPEG）。</summary>
    private static string ResolveImageExtension(string path, string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var ext = contentType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" or "image/jpg" => ".jpg",
                "image/bmp" => ".bmp",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => null
            };
            if (ext != null) return ext;
        }
        try
        {
            var header = new byte[12];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            var read = fs.Read(header, 0, header.Length);
            if (read >= 4 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ".png";
            if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ".jpg";
            if (read >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                return ".gif";
            if (read >= 2 && header[0] == 0x42 && header[1] == 0x4D)
                return ".bmp";
            if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ".webp";
        }
        catch
        {
            // 探测失败走默认值
        }
        return ".jpg";
    }

    /// <summary>进程生命周期内只清理一次历史遗留的 *.tmp（旧版本在流未关闭时改名失败产生）。</summary>
    private static bool _tempCleanupDone;
    private static readonly object TempCleanupLock = new();
    private static void CleanupOrphanTempFiles()
    {
        lock (TempCleanupLock)
        {
            if (_tempCleanupDone) return;
            _tempCleanupDone = true;
            try
            {
                foreach (var tmp in Directory.EnumerateFiles(CoverDirectory, "*.tmp"))
                {
                    try { File.Delete(tmp); } catch { /* 忽略单个文件清理失败 */ }
                }
            }
            catch
            {
                // 目录枚举失败不影响主流程
            }
        }
    }

    private static string? FindExistingCover(string songKey)
    {
        foreach (var ext in new[] { ".jpg", ".png", ".bmp", ".gif", ".webp" })
        {
            var path = Path.Combine(CoverDirectory, songKey + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}
