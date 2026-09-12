using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MuSync.Utils;
using WinForms = System.Windows.Forms;
namespace MuSync;
/// <summary>
/// WinForms NotifyIcon 托管：托盘图标 / 右键菜单 / 悬停状态行 / 气球提示。
/// WPF 消息泵（Dispatcher）替代原 WinForms Application.Run；事件在创建线程（UI 线程）触发。
/// </summary>
internal static class TrayIconService
{
    private static WinForms.NotifyIcon? _trayIcon;
    private static WinForms.ToolStripMenuItem? _trayStatusItem;
    private static WinForms.ToolStripMenuItem? _pauseSyncItem;
    private static MainWindow? _mainWindow;
    public static void Initialize(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        // 菜单顶部状态行（禁用态，由 UpdateTrayStatus 节流刷新）
        _trayStatusItem = new WinForms.ToolStripMenuItem("MuSync") { Enabled = false };
        _pauseSyncItem = new WinForms.ToolStripMenuItem("暂停同步") { CheckOnClick = true };
        _pauseSyncItem.Click += (_, _) =>
        {
            var steamManager = AppServices.Steam;
            if (steamManager == null || _pauseSyncItem == null) return;
            steamManager.ManualPause = _pauseSyncItem.Checked;
            Logger.Info($"[TrayIcon] 手动暂停同步: {_pauseSyncItem.Checked}");
            if (!_pauseSyncItem.Checked)
            {
                // 恢复后立即重新推送当前状态
                AppServices.Rpc?.RequestStateRefresh();
            }
        };
        var showSettingsItem = new WinForms.ToolStripMenuItem("显示设置");
        var showMainWindowItem = new WinForms.ToolStripMenuItem("显示主窗口");
        var exitMenuItem = new WinForms.ToolStripMenuItem("退出");
        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.AddRange(
            _trayStatusItem, new WinForms.ToolStripSeparator(),
            showMainWindowItem, showSettingsItem, _pauseSyncItem, new WinForms.ToolStripSeparator(),
            exitMenuItem);
        showSettingsItem.Click += (_, _) => _mainWindow?.ShowAndNavigateToSettings();
        showMainWindowItem.Click += (_, _) => _mainWindow?.ShowAndActivate();
        exitMenuItem.Click += (_, _) =>
        {
            _mainWindow?.PrepareToExit();
            Application.Current.Shutdown();
        };
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = AppResource.Icon,
            Text = "MuSync",
            ContextMenuStrip = contextMenu
        };
        _trayIcon.DoubleClick += (_, _) => _mainWindow?.ShowAndActivate();
        _trayIcon.Visible = true;
    }
    /// <summary>
    /// 刷新托盘悬停提示与右键菜单状态行（由主轮询循环节流调用，约每 5 秒）。
    /// RpcManager 在后台线程调用，菜单项文本赋值必须 marshal 回 UI 线程。
    /// </summary>
    public static void UpdateTrayStatus()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(UpdateTrayStatus);
            return;
        }
        if (_trayIcon == null) return;
        try
        {
            string text;
            var config = Configurations.Instance.Settings;
            if (config.EnableSteamSync && config.PauseWhenPlayingGame &&
                AppServices.Steam?.IsRealGameActive == true)
            {
                text = "游戏中，音乐同步已暂停";
            }
            else if (AppServices.Steam?.ManualPause == true)
            {
                text = "同步已手动暂停";
            }
            else
            {
                var current = AppServices.Rpc?.GetCurrentPlayerInfo();
                text = current?.PlayerInfo is { } song
                    ? $"正在播放 {current.Value.PlayerName}: {song.Title}"
                    : "未在播放音乐";
            }
            text = StringUtils.GetTruncatedStringByMaxByteLength(text, 60);
            _trayIcon.Text = text;
            if (_trayStatusItem != null && _trayStatusItem.Text != text)
            {
                _trayStatusItem.Text = text;
            }
        }
        catch (Exception ex)
        {
            // 托盘刷新失败不影响主流程
            Logger.Error($"[TrayIcon] 刷新托盘状态失败: {ex.Message}");
        }
    }
    public static void ShowMinimizeToTrayNotification()
    {
        _trayIcon?.ShowBalloonTip(1000, "应用仍在运行", "MuSync 已最小化到托盘区域。", WinForms.ToolTipIcon.Info);
    }
    public static void Dispose()
    {
        if (_trayIcon == null) return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
        _trayStatusItem = null;
        _pauseSyncItem = null;
        _mainWindow = null;
    }
}
