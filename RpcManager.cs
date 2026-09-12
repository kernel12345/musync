using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using MuSync.Models;
using MuSync.Players;
using MuSync.Players.Interfaces;
using MuSync.Utils;
namespace MuSync;
/// <summary>
/// 轮询各播放器并将状态同步到 Steam。
/// 多播放器仲裁规则：同一时刻只有一个"活跃源"拥有 Steam 状态发言权——
/// 仅"正在播放（未暂停）"的播放器才算活跃源；歌曲暂停期间 Steam 状态让位给空闲签名，
/// 暂停中的播放器状态变化只刷新界面，不会覆盖 Steam 状态。
/// </summary>
internal class RpcManager(SteamStatusManager steamManager)
{
    private class PlayerState
    {
        public IMusicPlayer? Player { get; set; }
        public PlayerInfo? LastPolledInfo { get; set; }
        public DateTime LastPollTime { get; set; } = DateTime.MinValue;
        public PlayerInfo? PendingUpdateInfo { get; set; }
        public DateTime LastChangeDetectedTime { get; set; } = DateTime.MinValue;
        public ErrorCode LastError { get; set; } = ErrorCode.None;
        /// <summary>DLL 未就绪（播放器启动竞态）的起始时间，宽限期内不报错。</summary>
        public DateTime DllNotFoundSinceUtc { get; set; } = DateTime.MinValue;
    }

    public enum ErrorCode
    {
        None,
        PermissionDenied,
        DllNotFound,
        VersionNotSupported
    }

    // DLL 持续缺失超过该时长才提示（避免播放器启动瞬间误报）
    private static readonly TimeSpan DllMissingGracePeriod = TimeSpan.FromSeconds(5);

    private readonly PlayerState _netEaseState = new();
    private readonly PlayerState _tencentState = new();
    private readonly PlayerState _sodaMusicState = new();
    private volatile bool _stateRefreshRequested;
    private bool _realGameActivePreviously;
    private bool _wasLoggedOn;
    private PlayerState? _lastActiveState;
    private bool _lastActiveInfoNull = true;
    private const double JumpToleranceSeconds = 0.4;
    private const double DebounceWindowSeconds = 1.5;
    private const double ProgressUpdateIntervalSeconds = 1.0;
    // 有播放器在跑时高频轮询；空闲时降低频率省电
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromMilliseconds(233);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(1200);
    private DateTime _lastProgressUpdateTime = DateTime.MinValue;
    private DateTime _lastTrayStatusUpdateTime = DateTime.MinValue;

    public void RequestStateRefresh() => _stateRefreshRequested = true;

    public (PlayerInfo? PlayerInfo, string PlayerName) GetCurrentPlayerInfo()
    {
        var (state, name) = ResolveActiveState();
        return (state?.LastPolledInfo, name);
    }

    public (PlayerInfo? PlayerInfo, string PlayerName, bool IsActive, ErrorCode LastError)[] GetAllPlayersStatus()
    {
        return
        [
            (
                _netEaseState is { Player: not null, LastPolledInfo: not null }
                    ? _netEaseState.LastPolledInfo
                    : null,
                "网易云音乐",
                _netEaseState.Player != null,
                _netEaseState.LastError
            ),
            (
                _tencentState is { Player: not null, LastPolledInfo: not null }
                    ? _tencentState.LastPolledInfo
                    : null,
                "QQ音乐",
                _tencentState.Player != null,
                _tencentState.LastError
            ),
            (
                _sodaMusicState is { Player: not null, LastPolledInfo: not null }
                    ? _sodaMusicState.LastPolledInfo
                    : null,
                "汽水音乐",
                _sodaMusicState.Player != null,
                _sodaMusicState.LastError
            )
        ];
    }

    /// <summary>
    /// 挑选当前应占据 Steam 状态的播放器：仅"正在播放（未暂停）"者才算活跃源。
    /// 暂停中的播放器只刷新本地界面，Steam 状态走空闲签名；多个同时播放时按 网易云 → QQ → 汽水 取最先者。
    /// </summary>
    private (PlayerState? State, string Name) ResolveActiveState()
    {
        if (_netEaseState is { Player: not null, LastPolledInfo: { Pause: false } }) return (_netEaseState, "网易云音乐");
        if (_tencentState is { Player: not null, LastPolledInfo: { Pause: false } }) return (_tencentState, "QQ音乐");
        if (_sodaMusicState is { Player: not null, LastPolledInfo: { Pause: false } }) return (_sodaMusicState, "汽水音乐");
        return (null, "");
    }

    /// <summary>活跃源（或其有无信息）变化时，同步一次 Steam 状态；force 则无条件推送当前活跃源。</summary>
    private async Task SynchronizeActiveSourceAsync(bool force = false)
    {
        var (state, name) = ResolveActiveState();
        var infoNull = state?.LastPolledInfo is null;
        if (!force && state == _lastActiveState && infoNull == _lastActiveInfoNull) return;
        _lastActiveState = state;
        _lastActiveInfoNull = infoNull;
        if (state?.LastPolledInfo is { } info)
        {
            Debug.WriteLine($"[MuSync] 活跃源切换为 {name}: {info.Title}");
            Logger.Info($"[MuSync] 活跃源切换为 {name}: {info.Title}");
            await UpdateOrClearSteamStatusAsync(info, name);
        }
        else
        {
            Debug.WriteLine("[MuSync] 无活跃播放器，应用空闲签名（未配置则清除 Steam 状态）");
            Logger.Info("[MuSync] 无活跃播放器，应用空闲签名（未配置则清除 Steam 状态）");
            await steamManager.ApplyIdleSignatureAsync();
        }
    }

    public async Task Start()
    {
        while (true)
        {
            var currentTime = DateTime.UtcNow;
            var anyPlayerActive = false;
            try
            {
                var neteaseHwnd = Win32Api.User32.FindWindow("OrpheusBrowserHost", null);
                if (neteaseHwnd != IntPtr.Zero &&
                    Win32Api.User32.GetWindowThreadProcessId(neteaseHwnd, out var neteasePid) != 0)
                {
                    anyPlayerActive = true;
                    try
                    {
                        await PollAndUpdatePlayer(_netEaseState, "NetEase CloudMusic", neteasePid,
                            pid => new NetEase(pid), currentTime);
                        RecordPollSuccess(_netEaseState);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (_netEaseState.LastError != ErrorCode.PermissionDenied)
                        {
                            Logger.Error("[NetEase] 无权限读取进程内存，可能需要以管理员身份运行。");
                        }
                        _netEaseState.LastError = ErrorCode.PermissionDenied;
                    }
                    catch (DllNotFoundException)
                    {
                        // cloudmusic.dll 通常比主窗口晚加载：宽限期后仍失败才提示
                        RecordDllMissing(_netEaseState);
                        Debug.WriteLine("[NetEase] 等待 cloudmusic.dll 加载 (DllNotFound).");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        if (_netEaseState.LastError != ErrorCode.VersionNotSupported)
                        {
                            Logger.Error("[NetEase] 内存特征码未命中，播放器版本可能不兼容。");
                        }
                        _netEaseState.LastError = ErrorCode.VersionNotSupported;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[NetEase] Error: {ex.Message}");
                        _netEaseState.LastError = ErrorCode.None;
                    }
                }
                else
                {
                    CleanupPlayerState(_netEaseState, "NetEase CloudMusic");
                    RecordPollSuccess(_netEaseState);
                }

                var tencentHwnd = Win32Api.User32.FindWindow("QQMusic_Daemon_Wnd", null);
                if (tencentHwnd != IntPtr.Zero &&
                    Win32Api.User32.GetWindowThreadProcessId(tencentHwnd, out var tencentPid) != 0)
                {
                    anyPlayerActive = true;
                    try
                    {
                        await PollAndUpdatePlayer(_tencentState, "Tencent QQMusic", tencentPid,
                            pid => new Tencent(pid), currentTime);
                        RecordPollSuccess(_tencentState);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (_tencentState.LastError != ErrorCode.PermissionDenied)
                        {
                            Logger.Error("[Tencent] 无权限读取进程内存，可能需要以管理员身份运行。");
                        }
                        _tencentState.LastError = ErrorCode.PermissionDenied;
                    }
                    catch (DllNotFoundException)
                    {
                        RecordDllMissing(_tencentState);
                        Debug.WriteLine("[Tencent] 等待 QQMusic.dll 加载 (DllNotFound).");
                    }
                    catch (EntryPointNotFoundException)
                    {
                        if (_tencentState.LastError != ErrorCode.VersionNotSupported)
                        {
                            Logger.Error("[Tencent] 内存特征码未命中，播放器版本可能不兼容。");
                        }
                        _tencentState.LastError = ErrorCode.VersionNotSupported;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Tencent] Error: {ex.Message}");
                        _tencentState.LastError = ErrorCode.None;
                    }
                }
                else
                {
                    CleanupPlayerState(_tencentState, "Tencent QQMusic");
                    RecordPollSuccess(_tencentState);
                }

                var sodaProcess = Process.GetProcessesByName("SodaMusic").FirstOrDefault();
                if (sodaProcess != null)
                {
                    anyPlayerActive = true;
                    try
                    {
                        // 汽水音乐通过 Windows 系统媒体会话（SMTC）读取，无需访问进程内存
                        await PollAndUpdatePlayer(_sodaMusicState, "SodaMusic", sodaProcess.Id,
                            pid => new SodaMusic(pid), currentTime);
                        RecordPollSuccess(_sodaMusicState);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SodaMusic] Error: {ex.Message}");
                        _sodaMusicState.LastError = ErrorCode.None;
                    }
                }
                else
                {
                    CleanupPlayerState(_sodaMusicState, "SodaMusic");
                    RecordPollSuccess(_sodaMusicState);
                }

                // 登录状态边沿（首次登录/手动重登/断线重连后）：强制重推一次，
                // 保证空闲签名等“无状态变化”场景也能在会话建立后立即生效
                var isLoggedOn = steamManager.IsReady;
                if (isLoggedOn && !_wasLoggedOn)
                {
                    Debug.WriteLine("[MuSync] Steam 会话已建立，强制刷新当前状态");
                    Logger.Info("[MuSync] Steam 会话已建立，强制刷新当前状态");
                    await SynchronizeActiveSourceAsync(force: true);
                }
                _wasLoggedOn = isLoggedOn;

                // 活跃源仲裁：变化时自动切换 Steam 状态
                await SynchronizeActiveSourceAsync();

                var realGamePause = steamManager.IsRealGameActive &&
                                    Configurations.Instance.Settings.PauseWhenPlayingGame;
                if (realGamePause != _realGameActivePreviously)
                {
                    _realGameActivePreviously = realGamePause;
                    if (realGamePause)
                    {
                        Debug.WriteLine("[MuSync] 真实游戏进行中，暂停 Steam 状态同步");
                        Logger.Info("[MuSync] 真实游戏进行中，暂停 Steam 状态同步");
                        steamManager.ClearStatus();
                    }
                    else
                    {
                        Debug.WriteLine("[MuSync] 真实游戏已结束，恢复 Steam 状态同步");
                        Logger.Info("[MuSync] 真实游戏已结束，恢复 Steam 状态同步");
                        await SynchronizeActiveSourceAsync(force: true);
                    }
                }
                if (_stateRefreshRequested)
                {
                    _stateRefreshRequested = false;
                    Debug.WriteLine("Settings changed. Refreshing current Steam status.");
                    await SynchronizeActiveSourceAsync(force: true);
                }
                if ((currentTime - _lastProgressUpdateTime).TotalSeconds >= ProgressUpdateIntervalSeconds)
                {
                    await UpdateProgressForActivePlayers(currentTime);
                    _lastProgressUpdateTime = currentTime;
                }
                if ((currentTime - _lastTrayStatusUpdateTime).TotalSeconds >= 5)
                {
                    TrayIconService.UpdateTrayStatus();
                    _lastTrayStatusUpdateTime = currentTime;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL ERROR] An exception occurred in the main poll loop: {ex.Message}");
                Logger.Error($"[FATAL ERROR] An exception occurred in the main poll loop: {ex.Message}");
                ClearAllPlayers();
            }
            finally
            {
                await Task.Delay(anyPlayerActive ? ActivePollInterval : IdlePollInterval);
            }
        }
    }

    private async Task PollAndUpdatePlayer(PlayerState state, string playerName,
        int pid,
        Func<int, IMusicPlayer> playerFactory, DateTime currentTime)
    {
        if (state.Player is null)
        {
            Debug.WriteLine($"[{playerName}] Player process detected. Creating instance.");
            state.Player = playerFactory(pid);
        }
        var currentInfo = await state.Player.GetPlayerInfoAsync();
        var isStateChanged = DetectStateChange(currentInfo, state.LastPolledInfo, currentTime, state.LastPollTime,
            JumpToleranceSeconds);
        if (isStateChanged)
        {
            Debug.WriteLine(
                $"[{playerName}] State change detected. Resetting debounce timer for: {currentInfo?.Title ?? "None (Clear)"}");
            state.PendingUpdateInfo = currentInfo;
            state.LastChangeDetectedTime = currentTime;
        }
        if (state.PendingUpdateInfo is not null &&
            (currentTime - state.LastChangeDetectedTime).TotalSeconds > DebounceWindowSeconds)
        {
            // 只有活跃源才允许把变化推送到 Steam，其余播放器变化只影响界面
            if (state == ResolveActiveState().State)
            {
                Debug.WriteLine($"[{playerName}] Debounce window passed. Sending Steam status update.");
                await UpdateOrClearSteamStatusAsync(state.PendingUpdateInfo, playerName);
            }
            state.PendingUpdateInfo = null;
        }
        state.LastPolledInfo = currentInfo;
        state.LastPollTime = currentTime;
    }

    private void CleanupPlayerState(PlayerState state, string playerName)
    {
        if (state.Player is null) return;
        Debug.WriteLine($"[{playerName}] Player process lost. Clearing local state (active source re-evaluated).");
        state.Player = null;
        state.LastPolledInfo = null;
        state.PendingUpdateInfo = null;
    }

    private void ClearAllPlayers()
    {
        CleanupPlayerState(_netEaseState, "NetEase CloudMusic");
        CleanupPlayerState(_tencentState, "Tencent QQMusic");
        CleanupPlayerState(_sodaMusicState, "SodaMusic");
        _lastActiveState = null;
        _lastActiveInfoNull = true;
        steamManager.ClearStatus();
    }

    private static void RecordPollSuccess(PlayerState state)
    {
        state.DllNotFoundSinceUtc = DateTime.MinValue;
    }

    private static void RecordDllMissing(PlayerState state)
    {
        if (state.DllNotFoundSinceUtc == DateTime.MinValue)
        {
            state.DllNotFoundSinceUtc = DateTime.UtcNow;
        }
        var newError = (DateTime.UtcNow - state.DllNotFoundSinceUtc) >= DllMissingGracePeriod
            ? ErrorCode.DllNotFound
            : ErrorCode.None;
        if (newError == ErrorCode.DllNotFound && state.LastError != ErrorCode.DllNotFound)
        {
            Logger.Warn("播放器核心组件持续未就绪 (DllNotFound)");
        }
        state.LastError = newError;
    }

    private static bool DetectStateChange(PlayerInfo? current, PlayerInfo? last, DateTime currentTime,
        DateTime lastTime, double tolerance)
    {
        if ((current is null && last is not null) || (current is not null && last is null)) return true;
        if (current is not { } c || last is not { } l) return false;
        if (c.Identity != l.Identity || c.Pause != l.Pause) return true;
        if (c.Pause) return false;
        var elapsed = (currentTime - lastTime).TotalSeconds;
        var progressDelta = c.Schedule - l.Schedule;
        return Math.Abs(progressDelta - elapsed) > tolerance;
    }

    private async Task UpdateOrClearSteamStatusAsync(PlayerInfo? info, string playerName)
    {
        if (info is not { } playerInfo)
        {
            await steamManager.ApplyIdleSignatureAsync();
            return;
        }
        Debug.WriteLine(
            $"pause: {playerInfo.Pause}, progress: {playerInfo.Schedule}, duration: {playerInfo.Duration}");
        Debug.WriteLine(
            $"id: {playerInfo.Identity}, name: {playerInfo.Title}, singer: {playerInfo.Artists}, album: {playerInfo.Album}");
        await steamManager.UpdateStatusAsync(info, playerName);
    }

    private async Task UpdateProgressForActivePlayers(DateTime currentTime)
    {
        var (state, name) = ResolveActiveState();
        if (state?.LastPolledInfo is { Pause: false } info)
        {
            var elapsedSincePoll = (currentTime - state.LastPollTime).TotalSeconds;
            var interpolatedSchedule = info.Schedule + elapsedSincePoll;
            var clampedSchedule = Math.Min(interpolatedSchedule, info.Duration);
            var updatedInfo = info with { Schedule = clampedSchedule };
            await steamManager.UpdateStatusAsync(updatedInfo, name);
        }
    }
}
