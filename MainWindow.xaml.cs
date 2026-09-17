using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MuSync.Utils;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
namespace MuSync;
/// <summary>
/// 主窗口：FluentWindow + TitleBar + NavigationView（主界面/播放器/设置）。
/// Mica 背景 + SystemThemeWatcher 深浅色跟随系统；保留 CloseToTray / Ctrl+R 清缓存行为。
/// </summary>
public partial class MainWindow : FluentWindow
{
    private Type _pendingPage = typeof(DashboardPage);
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        Icon = WpfImageHelper.ToBitmapSource(AppResource.Icon);
        // Mica 不支持的环境（Win10）下 WindowBackdropType 属性设置会被内部跳过，自动回落主题背景
        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: true);
        IsVisibleChanged += Window_IsVisibleChanged;
    }

    /// <summary>窗口显隐同步给后台轮询，并在隐藏到托盘时回收内存。</summary>
    private void Window_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var visible = e.NewValue is true;
        AppServices.IsMainWindowVisible = visible;
        if (visible)
        {
            MemoryOptimizer.OnWindowShown();
        }
        else
        {
            MemoryOptimizer.OnWindowHidden();
        }
    }

    /// <summary>标记为显式退出（托盘“退出”/设置页“退出登录”），关闭窗口时不再拦截为最小化到托盘。</summary>
    public void PrepareToExit() => _isExiting = true;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.Navigate(_pendingPage);
    }

    /// <summary>导航回主界面（设置页确定/取消后调用，不激活窗口）。</summary>
    public void NavigateToDashboard()
    {
        if (IsLoaded)
        {
            RootNavigation.Navigate(typeof(DashboardPage));
        }
        else
        {
            _pendingPage = typeof(DashboardPage);
        }
    }

    /// <summary>托盘“显示主窗口”/双击：仅显示窗口，不打断当前导航页。</summary>
    public void ShowAndActivate()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Show();
        Activate();
    }

    /// <summary>托盘“显示设置”：显示窗口并切到设置页。</summary>
    public void ShowAndNavigateToSettings()
    {
        if (IsLoaded)
        {
            RootNavigation.Navigate(typeof(SettingsPage));
        }
        else
        {
            _pendingPage = typeof(SettingsPage);
        }
        ShowAndActivate();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        var config = Configurations.Instance;
        if (_isExiting)
        {
            // 显式退出：直接关闭，不再拦截为最小化到托盘
            return;
        }
        if (config.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            TrayIconService.ShowMinimizeToTrayNotification();
        }
        else
        {
            Application.Current.Shutdown();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyboardDevice.Modifiers != ModifierKeys.Control || e.Key != Key.R) return;
        var beforeMemory = MemoryPressureMonitor.GetCurrentMemoryUsage();
        ImageCacheManager.ForceCleanupCache();
        var afterMemory = MemoryPressureMonitor.GetCurrentMemoryUsage();
        var freedMemory = beforeMemory - afterMemory;
        MessageBox.Show(
            $"缓存清理完成！\n清理前: {beforeMemory / 1024 / 1024:F1} MB\n清理后: {afterMemory / 1024 / 1024:F1} MB\n释放: {freedMemory / 1024 / 1024:F1} MB\n\n提示: 按 Ctrl+R 可随时清理缓存",
            "缓存清理", MessageBoxButton.OK, MessageBoxImage.Information);
        e.Handled = true;
    }
}
