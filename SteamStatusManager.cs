using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MuSync.Models;
using MuSync.Utils;
namespace MuSync;
internal class SteamStatusManager
{
    private readonly SteamSessionManager _session;
    private string _lastSetName = string.Empty;
    private const int ProgressBarLength = 10;
    /// <summary>Steam「正在玩」状态文本（game_extra_info）的 UTF-8 字节上限。</summary>
    private const int MaxStatusUtf8Bytes = 63;
    public bool IsReady => _session.IsLoggedOn;
    public bool IsRealGameActive => _session.IsRealGameActive;
    /// <summary>托盘“暂停同步”手动开关：开启时立即清状态并停止推送。</summary>
    public bool ManualPause
    {
        get => _manualPause;
        set
        {
            _manualPause = value;
            if (value) ClearStatus();
        }
    }
    private bool _manualPause;
    public SteamStatusManager(SteamSessionManager session)
    {
        _session = session;
    }
    public async Task UpdateStatusAsync(PlayerInfo? info, string playerName)
    {
        if (!_session.IsLoggedOn) return;
        var config = Configurations.Instance.Settings;
        if (config.PauseWhenPlayingGame && _session.IsRealGameActive) return;
        if (_manualPause) return;
        var newName = FormatStatusName(info, playerName, config);
        if (newName == _lastSetName) return;
        try
        {
            await _session.SetGameNameAsync(newName).ConfigureAwait(false);
            _lastSetName = newName;
            Debug.WriteLine($"[SteamStatus] 状态已更新: {newName}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SteamStatus] 更新状态失败: {ex.Message}");
        }
    }
    public void ClearStatus()
    {
        if (!_session.IsLoggedOn) return;
        _session.ClearGameName();
        _lastSetName = string.Empty;
        Logger.Info("[SteamStatus] 状态已清除");
    }

    /// <summary>
    /// 空闲（无音乐可同步）时推送状态：配置了自定义签名则显示签名，否则清空状态位。
    /// 正在玩真实 Steam 游戏或手动暂停同步时不推送（保持清空），避免覆盖真实游戏状态。
    /// </summary>
    public async Task ApplyIdleSignatureAsync()
    {
        if (!_session.IsLoggedOn) return;
        var config = Configurations.Instance.Settings;
        if (config.PauseWhenPlayingGame && _session.IsRealGameActive) return;
        if (_manualPause) return;
        var signature = GetIdleSignature(config);
        if (signature == null)
        {
            // 空闲轮询会高频进入本方法：状态位已是空的就不重复下发（手动暂停/游戏占用时由调用方保证不进入）
            if (_lastSetName.Length == 0) return;
            ClearStatus();
            return;
        }
        if (signature == _lastSetName) return;
        try
        {
            await _session.SetGameNameAsync(signature).ConfigureAwait(false);
            _lastSetName = signature;
            Debug.WriteLine($"[SteamStatus] 空闲签名已更新: {signature}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SteamStatus] 更新空闲签名失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 推送「实时操作」状态：把鼠标当前操作的应用名显示到 Steam 状态位（如「正在使用 Chrome」）。
    /// 仅在用户启用「推送实时操作」且无音乐可同步时由 RpcManager 调用；真实游戏/手动暂停时同样不推送。
    /// </summary>
    public async Task ApplyRealtimeActivityAsync(string appName)
    {
        if (!_session.IsLoggedOn) return;
        var config = Configurations.Instance.Settings;
        if (config.PauseWhenPlayingGame && _session.IsRealGameActive) return;
        if (_manualPause) return;
        var text = FormatRealtimeActivity(appName);
        if (text == _lastSetName) return;
        try
        {
            await _session.SetGameNameAsync(text).ConfigureAwait(false);
            _lastSetName = text;
            Debug.WriteLine($"[SteamStatus] 实时操作已更新: {text}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[SteamStatus] 更新实时操作失败: {ex.Message}");
        }
    }

    private static string FormatRealtimeActivity(string appName)
    {
        var name = string.IsNullOrWhiteSpace(appName) ? "未知应用" : appName.Trim();
        return TruncateToUtf8ByteLength($"正在使用 {name}", MaxStatusUtf8Bytes);
    }

    /// <summary>返回空闲时应显示的签名（已去除首尾空白并截断到 Steam 字节上限）；未启用或内容为空时返回 null。</summary>
    public static string? GetIdleSignature(ConfigData config)
    {
        if (!config.EnableCustomSignature) return null;
        if (string.IsNullOrWhiteSpace(config.CustomSignature)) return null;
        return TruncateToUtf8ByteLength(config.CustomSignature.Trim(), MaxStatusUtf8Bytes);
    }
    public string GetStatusPreview(PlayerInfo? info, string playerName)
    {
        return FormatStatusName(info, playerName, Configurations.Instance.Settings);
    }
    public static string GetStatusPreview(PlayerInfo? info, string playerName, ConfigData config)
    {
        return FormatStatusName(info, playerName, config);
    }
    private static int GetUtf8ByteCount(string str)
    {
        return System.Text.Encoding.UTF8.GetByteCount(str);
    }
    private static string TruncateToUtf8ByteLength(string str, int maxBytes)
    {
        if (string.IsNullOrEmpty(str)) return str;
        var bytes = System.Text.Encoding.UTF8.GetBytes(str);
        if (bytes.Length <= maxBytes) return str;
        var count = maxBytes;
        while (count > 0 && (bytes[count] & 0xC0) == 0x80)
        {
            count--;
        }
        int byteCount = 0;
        int charCount = 0;
        foreach (var c in str)
        {
            int cBytes = System.Text.Encoding.UTF8.GetByteCount(new [] { c });
            if (byteCount + cBytes > maxBytes) break;
            byteCount += cBytes;
            charCount++;
        }
        return str.Substring(0, charCount);
    }
    private static string FormatStatusName(PlayerInfo? info, string playerName, ConfigData config)
    {
        if (info is not { } playerInfo)
        {
            return "MuSync";
        }
        var prefix = config.EnableCustomPrefix && !string.IsNullOrEmpty(config.CustomPrefix)
            ? config.CustomPrefix
            : string.Empty;
        var prefixBytes = GetUtf8ByteCount(prefix);
        var title = playerInfo.Title;
        var artistPart = string.Empty;
        var progressPart = string.Empty;
        if (config.ShowArtistName && !string.IsNullOrEmpty(playerInfo.Artists))
        {
            artistPart = $" - {playerInfo.Artists}";
        }
        if (config.ShowProgressBar && !playerInfo.Pause && playerInfo.Duration > 0)
        {
            var sbStats = new System.Text.StringBuilder();
            sbStats.Append(" [");
            var progress = Math.Clamp(playerInfo.Schedule / playerInfo.Duration, 0, 1);
            var filledCount = (int)(progress * ProgressBarLength);
            sbStats.Append(new string('#', filledCount));
            sbStats.Append(new string('-', ProgressBarLength - filledCount));
            sbStats.Append($"] {FormatTime(playerInfo.Schedule)}/{FormatTime(playerInfo.Duration)}");
            progressPart = sbStats.ToString();
        }
        else if (playerInfo.Pause)
        {
            progressPart = " (Paused)"; 
        }
        var contentMaxBytes = MaxStatusUtf8Bytes - prefixBytes;
        if (contentMaxBytes <= 0) return TruncateToUtf8ByteLength(prefix, MaxStatusUtf8Bytes);
        var fullString = $"{title}{artistPart}{progressPart}";
        if (GetUtf8ByteCount(fullString) <= contentMaxBytes) return $"{prefix}{fullString}";
        if (config.StatusPriority == SteamStatusPriority.Artist)
        {
            var artistString = $"{title}{artistPart}";
            if (GetUtf8ByteCount(artistString) <= contentMaxBytes) return $"{prefix}{artistString}";
            return $"{prefix}{TruncateToUtf8ByteLength(title, contentMaxBytes)}";
        }
        else  
        {
            var progressString = $"{title}{progressPart}";
            if (GetUtf8ByteCount(progressString) <= contentMaxBytes) return $"{prefix}{progressString}";
            return $"{prefix}{TruncateToUtf8ByteLength(title, contentMaxBytes)}";
        }
    }
    private static string FormatTime(double totalSeconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
        return ts.Hours > 0
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}