using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MuSync.Utils;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;
namespace MuSync;
internal class SteamSessionManager : IDisposable
{
    // 断线自动重连：初始退避 5 秒，翻倍至上限 60 秒
    private const int ReconnectInitialDelaySeconds = 5;
    private const int ReconnectMaxDelaySeconds = 60;
    // 启动后若长时间未连上 Steam（如开机时网络未就绪），进入自动重连的等待时间
    private const int FirstConnectWatchdogDelaySeconds = 20;

    private SteamClient? _steamClient;
    private CallbackManager? _callbackManager;
    private SteamUser? _steamUser;
    private SteamFriends? _steamFriends;
    private SteamApps? _steamApps;
    private readonly CancellationTokenSource _cts = new();
    private Task? _callbackTask;
    private bool _isRunning;
    private string _currentGameName = string.Empty;
    /// <summary>挂时长的真实 AppID 列表（并发上限 30）。</summary>
    private readonly List<uint> _idleAppIds = new();
    /// <summary>去重：上一次实际发送的 games_played 内容签名，断线后置空强制重发。</summary>
    private string? _lastPlayedSignature;
    private readonly object _gamesPlayedLock = new();
    private SteamID? _selfSteamId;
    private readonly ManualResetEventSlim _connectedEvent = new(false);
    private volatile bool _reconnectLoopRunning;
    private volatile bool _reconnectPending;
    private TaskCompletionSource<IReadOnlyList<uint>>? _licensesTcs;

    public bool IsConnected => _steamClient?.IsConnected ?? false;
    public bool IsLoggedOn { get; private set; }
    public bool IsRealGameActive { get; private set; }
    /// <summary>是否正在挂游戏时长。</summary>
    public bool IsIdling { get { lock (_gamesPlayedLock) return _idleAppIds.Count > 0; } }
    /// <summary>当前挂时长的 AppID 快照。</summary>
    public IReadOnlyList<uint> IdleAppIds { get { lock (_gamesPlayedLock) return _idleAppIds.ToList(); } }
    /// <summary>Steam 并发「正在玩」条目上限。</summary>
    public const int MaxIdleGames = 30;
    /// <summary>密码登录成功后是否保存 refresh token（用于下次自动登录）；不保存则每次启动需手动登录。</summary>
    public bool RememberSession { get; set; } = true;
    public string? Username { get; private set; }
    public string? LoginError { get; private set; }
    public event Action<bool>? OnSteamGuardRequired;
    private TaskCompletionSource<string>? _guardCodeTcs;

    public void Start()
    {
        if (_isRunning) return;
        _isRunning = true;
        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamFriends = _steamClient.GetHandler<SteamFriends>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        _callbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);
        _callbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        // 恢复上次退出时的挂时长选择，登录成功后自动重发
        var savedIdle = Configurations.Instance.Settings;
        if (savedIdle.GameIdleEnabled)
        {
            lock (_gamesPlayedLock)
            {
                _idleAppIds.AddRange(savedIdle.GameIdleAppIds.Take(MaxIdleGames));
            }
        }
        _callbackTask = Task.Run(() => CallbackLoop(_cts.Token));
        _steamClient.Connect();
        Debug.WriteLine("[SteamSession] 正在连接到 Steam...");
        StartFirstConnectWatchdog();
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Debug.WriteLine("[SteamSession] 已连接到 Steam");
        Logger.Info("[SteamSession] 已连接到 Steam");
        _connectedEvent.Set();
        // 仅自动重连场景需要在此补登录；首次启动由启动流程负责登录
        if (!_reconnectPending || IsLoggedOn) return;
        _reconnectPending = false;
        var settings = Configurations.Instance.Settings;
        if (string.IsNullOrEmpty(settings.SteamUsername) ||
            string.IsNullOrEmpty(settings.SteamRefreshToken))
        {
            return;
        }
        Debug.WriteLine("[SteamSession] 连接已恢复，尝试用保存的令牌自动重新登录...");
        Logger.Info("[SteamSession] 连接已恢复，尝试用保存的令牌自动重新登录...");
        _ = Task.Run(async () =>
        {
            try
            {
                await LoginWithTokenAsync(settings.SteamUsername, settings.SteamRefreshToken);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamSession] 自动重登异常: {ex.Message}");
                Logger.Error($"[SteamSession] 自动重登异常: {ex.Message}");
            }
        });
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        IsLoggedOn = false;
        _connectedEvent.Reset();
        lock (_gamesPlayedLock) _lastPlayedSignature = null;
        Debug.WriteLine($"[SteamSession] 已断开连接 (UserInitiated={cb.UserInitiated})");
        Logger.Info($"[SteamSession] 已断开连接 (UserInitiated={cb.UserInitiated})");
        if (cb.UserInitiated || !_isRunning || _reconnectLoopRunning) return;
        _reconnectLoopRunning = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await AutoReconnectLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 程序退出中，静默忽略
            }
            finally
            {
                _reconnectLoopRunning = false;
            }
        }, _cts.Token);
    }

    /// <summary>启动 watchdog：首次连接长时间不成功（网络未就绪/服务器不可达）时自动进入退避重连。</summary>
    private void StartFirstConnectWatchdog()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(FirstConnectWatchdogDelaySeconds), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_isRunning || _steamClient?.IsConnected == true || _reconnectLoopRunning) return;
            _reconnectLoopRunning = true;
            try
            {
                Logger.Warn("[SteamSession] 首次连接超时，转入自动重连");
                await AutoReconnectLoopAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 程序退出中，静默忽略
            }
            finally
            {
                _reconnectLoopRunning = false;
            }
        }, _cts.Token);
    }

    /// <summary>指数退避重连：反复 Connect 直到连上 Steam（无论是否已登录）。</summary>
    private async Task AutoReconnectLoopAsync(CancellationToken token)
    {
        var delaySeconds = ReconnectInitialDelaySeconds;
        while (_isRunning && !token.IsCancellationRequested && _steamClient?.IsConnected != true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_isRunning || token.IsCancellationRequested || _steamClient?.IsConnected == true) return;
            try
            {
                _reconnectPending = true;
                Debug.WriteLine($"[SteamSession] 尝试自动重连 (下次失败退避 {delaySeconds}s)...");
                Logger.Info($"[SteamSession] 尝试自动重连 (下次失败退避 {delaySeconds}s)...");
                _steamClient?.Connect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SteamSession] 自动重连异常: {ex.Message}");
                Logger.Error($"[SteamSession] 自动重连异常: {ex.Message}");
            }
            // 等待连接结果（最多约 8 秒）；连上即退出循环，令牌重登由 ConnectedCallback 触发
            for (var i = 0; i < 16 && !token.IsCancellationRequested && _steamClient?.IsConnected != true; i++)
            {
                try
                {
                    await Task.Delay(500, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            delaySeconds = Math.Min(delaySeconds * 2, ReconnectMaxDelaySeconds);
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result == EResult.OK)
        {
            IsLoggedOn = true;
            _selfSteamId = cb.ClientSteamID;
            IsRealGameActive = false;
            LoginError = null;
            Debug.WriteLine($"[SteamSession] 登录成功! SteamID: {cb.ClientSteamID}");
            Logger.Info($"[SteamSession] 登录成功! SteamID: {cb.ClientSteamID}");
            _steamFriends?.SetPersonaState(EPersonaState.Online);
            Debug.WriteLine("[SteamSession] 已设置在线状态");
            // 新会话需要重新声明 games_played：恢复挂时长（音乐状态由 RpcManager 随后强制重推）
            lock (_gamesPlayedLock) _lastPlayedSignature = null;
            SendGamesPlayed();
        }
        else
        {
            IsLoggedOn = false;
            LoginError = cb.Result.ToString();
            Debug.WriteLine($"[SteamSession] 登录失败: {cb.Result} / {cb.ExtendedResult}");
            Logger.Error($"[SteamSession] 登录失败: {cb.Result} / {cb.ExtendedResult}");
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        IsLoggedOn = false;
        Debug.WriteLine($"[SteamSession] 已登出: {cb.Result}");
        Logger.Info($"[SteamSession] 已登出: {cb.Result}");
    }

    public bool WaitForConnection(int timeoutMs = 8000)
    {
        return _connectedEvent.Wait(timeoutMs);
    }

    public async Task<bool> LoginAsync(string username, string password)
    {
        if (_steamClient == null)
        {
            LoginError = "Steam 客户端未初始化";
            return false;
        }
        if (!IsConnected)
        {
            if (!WaitForConnection())
            {
                LoginError = "无法连接到 Steam 服务器";
                return false;
            }
        }
        Username = username;
        LoginError = null;
        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = new SteamGuardAuthenticator(this),
                    GuardData = Configurations.Instance.Settings.SteamGuardData,
                    DeviceFriendlyName = "MuSync"
                }
            ).ConfigureAwait(false);
            var pollResult = await authSession.PollingWaitForResultAsync().ConfigureAwait(false);
            if (!string.IsNullOrEmpty(pollResult.NewGuardData) && RememberSession)
            {
                Configurations.Instance.Settings.SteamGuardData = pollResult.NewGuardData;
                Configurations.Instance.Save();
            }
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = pollResult.RefreshToken,
                LoginID = 1243,
                ShouldRememberPassword = true,
                MachineName = "MuSync"
            });
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(500).ConfigureAwait(false);
                if (IsLoggedOn)
                {
                    var settings = Configurations.Instance.Settings;
                    settings.SteamUsername = username;
                    if (RememberSession)
                    {
                        settings.SteamRefreshToken = pollResult.RefreshToken;
                    }
                    else
                    {
                        // 未勾选“记住我”：不保存令牌与 Guard 数据，下次启动需手动登录
                        settings.SteamRefreshToken = "";
                        settings.SteamGuardData = "";
                    }
                    Configurations.Instance.Save();
                    return true;
                }
                if (LoginError != null)
                {
                    return false;
                }
            }
            LoginError = "登录超时";
            return false;
        }
        catch (AuthenticationException ex)
        {
            LoginError = $"认证失败: {ex.Result} - {ex.Message}";
            Debug.WriteLine($"[SteamSession] {LoginError}");
            Logger.Error($"[SteamSession] {LoginError}");
            return false;
        }
        catch (Exception ex)
        {
            LoginError = $"登录异常: {ex.Message}";
            Debug.WriteLine($"[SteamSession] {LoginError}");
            Logger.Error($"[SteamSession] {LoginError}");
            return false;
        }
    }

    public async Task<bool> LoginWithTokenAsync(string username, string refreshToken)
    {
        if (_steamClient == null)
        {
            LoginError = "Steam 客户端未初始化";
            return false;
        }
        if (!IsConnected)
        {
            if (!WaitForConnection())
            {
                LoginError = "无法连接到 Steam 服务器";
                return false;
            }
        }
        Username = username;
        LoginError = null;
        _steamUser!.LogOn(new SteamUser.LogOnDetails
        {
            Username = username,
            AccessToken = refreshToken,
            LoginID = 1243,
            ShouldRememberPassword = true,
            MachineName = "MuSync"
        });
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            if (IsLoggedOn) return true;
            if (LoginError != null) break;
            // 登录过程中网络断开：保留令牌不清除，等自动重连后再试
            if (!IsConnected) return false;
        }
        Debug.WriteLine("[SteamSession] Token 登录失败，清除已保存令牌");
        Logger.Warn("[SteamSession] Token 登录失败，已清除保存的令牌，需要重新登录");
        Configurations.Instance.Settings.SteamRefreshToken = "";
        Configurations.Instance.Save();
        return false;
    }

    public Task SetGameNameAsync(string gameName)
    {
        if (!IsLoggedOn || _steamClient == null)
        {
            _currentGameName = gameName ?? string.Empty;
            return Task.CompletedTask;
        }
        _currentGameName = gameName ?? string.Empty;
        SendGamesPlayed();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 设置挂时长的真实游戏 AppID（最多 <see cref="MaxIdleGames"/> 个），并持久化以便登录后自动恢复。
    /// 音乐状态文本不受影响：音乐条目始终位于 games_played 首位，真实 appid 紧随其后。
    /// </summary>
    public Task SetIdleGamesAsync(IReadOnlyCollection<uint> appIds)
    {
        var distinct = appIds.Where(id => id > 0).Distinct().Take(MaxIdleGames).ToList();
        lock (_gamesPlayedLock)
        {
            _idleAppIds.Clear();
            _idleAppIds.AddRange(distinct);
        }
        var settings = Configurations.Instance.Settings;
        settings.GameIdleEnabled = distinct.Count > 0;
        settings.GameIdleAppIds = distinct;
        Configurations.Instance.Save();
        Logger.Info($"[SteamSession] 挂时长游戏已更新: {(distinct.Count > 0 ? string.Join(", ", distinct) : "（已停止）")}");
        if (IsLoggedOn) SendGamesPlayed();
        return Task.CompletedTask;
    }

    public void ClearGameName()
    {
        _currentGameName = string.Empty;
        if (IsLoggedOn) SendGamesPlayed();
    }

    /// <summary>
    /// 统一发送 ClientGamesPlayed：音乐条目（Shortcut + game_extra_info）排首位，
    /// 之后追加挂时长真实 appid。两者皆空时发送空列表清除状态。
    /// </summary>
    private void SendGamesPlayed()
    {
        if (!IsLoggedOn || _steamClient == null) return;
        lock (_gamesPlayedLock)
        {
            var signature = BuildPlayedSignature(_currentGameName, _idleAppIds);
            if (signature == _lastPlayedSignature) return;
            var request = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayedWithDataBlob)
            {
                Body =
                {
                    client_os_type = unchecked((uint)EOSType.Windows10)
                }
            };
            if (!string.IsNullOrEmpty(_currentGameName))
            {
                request.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                {
                    game_extra_info = _currentGameName,
                    game_id = new GameID
                    {
                        AppType = GameID.GameType.Shortcut,
                        ModID = uint.MaxValue
                    }
                });
            }
            foreach (var appId in _idleAppIds)
            {
                request.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
                {
                    game_id = new GameID(appId)
                });
            }
            _steamClient.Send(request);
            _lastPlayedSignature = signature;
            Debug.WriteLine($"[SteamSession] games_played 已发送 (音乐文本: '{_currentGameName}', 挂时长 {_idleAppIds.Count} 个)");
        }
    }

    private static string BuildPlayedSignature(string gameName, IEnumerable<uint> idleAppIds)
    {
        var sb = new StringBuilder(gameName ?? string.Empty);
        foreach (var id in idleAppIds.OrderBy(x => x))
        {
            sb.Append('|').Append(id);
        }
        return sb.ToString();
    }

    public void SubmitSteamGuardCode(string code)
    {
        _guardCodeTcs?.TrySetResult(code);
    }

    private void OnPersonaState(SteamFriends.PersonaStateCallback cb)
    {
        if (_selfSteamId is not { } self || cb.FriendID != self) return;
        var realGame = IsRealGameState(cb);
        if (realGame == IsRealGameActive) return;
        IsRealGameActive = realGame;
        Debug.WriteLine(realGame
            ? "[SteamSession] 检测到本账号正在玩真实 Steam 游戏，将暂停音乐同步"
            : "[SteamSession] 真实游戏已结束，恢复音乐同步");
    }

    private bool IsRealGameState(SteamFriends.PersonaStateCallback cb)
    {
        if (cb.GameID is not { } gameId || gameId.AppID == 0) return false;
        if (gameId.AppType is not (GameID.GameType.App or GameID.GameType.GameMod)) return false;
        // 自己挂时长的 appid 会被 Steam 回报为「正在玩」，不算外部真实游戏
        lock (_gamesPlayedLock)
        {
            if (_idleAppIds.Contains(gameId.AppID)) return false;
        }
        return true;
    }

    private void OnLicenseList(SteamApps.LicenseListCallback cb)
    {
        var packageIds = cb.LicenseList
            .Select(l => l.PackageID)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        Debug.WriteLine($"[SteamSession] 收到许可证列表: {packageIds.Count} 个套餐");
        var tcs = _licensesTcs;
        if (tcs == null)
        {
            tcs = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs = Interlocked.CompareExchange(ref _licensesTcs, tcs, null) ?? tcs;
        }
        tcs.TrySetResult(packageIds);
    }

    /// <summary>等待登录后 Steam 下发的许可证套餐列表（库存解析用）。</summary>
    public async Task<IReadOnlyList<uint>> WaitForLicensesAsync(CancellationToken cancellationToken = default)
    {
        var tcs = _licensesTcs;
        if (tcs == null)
        {
            tcs = new TaskCompletionSource<IReadOnlyList<uint>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = Interlocked.CompareExchange(ref _licensesTcs, tcs, null);
            if (existing != null) tcs = existing;
        }
        if (!IsLoggedOn) return Array.Empty<uint>();
        using (cancellationToken.Register(static state =>
                   ((TaskCompletionSource<IReadOnlyList<uint>>)state!).TrySetCanceled(), tcs))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    internal SteamApps? SteamAppsHandler => _steamApps;

    private void CallbackLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _isRunning)
        {
            _callbackManager?.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }
    }

    private class SteamGuardAuthenticator(SteamSessionManager manager) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            manager._guardCodeTcs = new TaskCompletionSource<string>();
            manager.OnSteamGuardRequired?.Invoke(true);
            return manager._guardCodeTcs.Task;
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            manager._guardCodeTcs = new TaskCompletionSource<string>();
            manager.OnSteamGuardRequired?.Invoke(false);
            return manager._guardCodeTcs.Task;
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            manager.OnSteamGuardRequired?.Invoke(true);
            return Task.FromResult(true);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        if (IsLoggedOn)
        {
            // 退出时清空整个 games_played（音乐文本 + 挂时长），避免账号卡在「正在玩」
            lock (_gamesPlayedLock)
            {
                _idleAppIds.Clear();
                _currentGameName = string.Empty;
                _lastPlayedSignature = null;
            }
            ClearGameName();
            _steamUser?.LogOff();
        }
        _steamClient?.Disconnect();
        _callbackTask?.Wait(3000);
        _cts.Dispose();
        _connectedEvent.Dispose();
    }
}
