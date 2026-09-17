using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using MuSync.Utils;
using MessageBox = System.Windows.MessageBox;
namespace MuSync;
/// <summary>
/// WPF 入口：启动序列自 Program.Main 逐行映射（单实例 Mutex → 注册迁移 →
/// 后台服务初始化 → 托盘 → 主窗口 → 空闲后 Steam 登录流程）。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, "MuSyncMutex", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("MuSync is already running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Win32Api.AutoStart.MigrateLegacyRegistration();
        _cts = new CancellationTokenSource();
        AppServices.Initialize();
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        TrayIconService.Initialize(mainWindow);
        var desktopSettings = Configurations.Instance.Settings;
        if (desktopSettings.DoubleClickHideDesktopIcons)
            DesktopIconService.Start();
        DesktopIconService.SetOpacity(desktopSettings.DesktopIconOpacity);
        // 首次启动强制显示主窗口（即便 StartInTray 已开启），标记一次后后续启动才按设置驻留托盘
        var firstLaunch = !Configurations.Instance.Settings.HasLaunchedBefore;
        if (firstLaunch)
        {
            Configurations.Instance.Settings.HasLaunchedBefore = true;
            Configurations.Instance.Save();
        }
        if (!firstLaunch && Configurations.Instance.Settings.StartInTray)
        {
            // 启动即驻留托盘：窗口从未 Show，IsVisibleChanged 不会触发，需显式置位并启动回收
            AppServices.IsMainWindowVisible = false;
            MemoryOptimizer.OnWindowHidden();
            TrayIconService.ShowMinimizeToTrayNotification();
        }
        else
        {
            mainWindow.Show();
        }
        // 消息循环空闲后再启动登录流程，避免启动阶段阻塞 UI（最长 15 秒白屏的问题）
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, StartupSteamLogin);
    }

    private void StartupSteamLogin()
    {
        _ = StartupSteamLoginAsync();
    }

    /// <summary>后台尝试令牌自动登录；失败则在 UI 线程弹出登录窗口。</summary>
    private async Task StartupSteamLoginAsync()
    {
        try
        {
            var config = Configurations.Instance.Settings;
            if (!config.EnableSteamSync) return;
            var session = AppServices.Session;
            if (session == null) return;
            var hasSavedToken = !string.IsNullOrEmpty(config.SteamUsername) &&
                                !string.IsNullOrEmpty(config.SteamRefreshToken);
            if (hasSavedToken)
            {
                var username = config.SteamUsername;
                var refreshToken = config.SteamRefreshToken;
                var ok = await Task.Run(() => session.LoginWithTokenAsync(username, refreshToken));
                if (ok)
                {
                    Logger.Info("[App] Token 自动登录成功");
                    return;
                }
                Logger.Warn("[App] Token 自动登录失败，弹出登录窗口");
            }
            // 走到这里说明需要手动登录；Idle 回调运行在 UI 线程，await 后仍回 UI 线程，弹模态窗安全
            var loginWindow = new SteamLoginWindow(session) { Owner = MainWindow };
            loginWindow.ShowDialog();
            if (loginWindow.LoginSucceeded)
            {
                Logger.Info("[App] Steam 登录成功，开始同步音乐状态");
            }
            else
            {
                SteamLoginWindow.ShowNotLoggedInHint();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[App] 启动登录流程异常: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        _cts?.Cancel();
        AppServices.Steam?.ClearStatus();
        AppServices.Session?.Dispose();
        DesktopIconService.Stop();
        TrayIconService.Dispose();
        _mutex?.Dispose();
    }
}
