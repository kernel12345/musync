using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MuSync.Models;
using MuSync.Utils;
using MessageBox = System.Windows.MessageBox;
namespace MuSync;
/// <summary>
/// 设置页：Windows 11 设置风格卡片 + ToggleSwitch。
/// 四组卡片（程序/Steam 显示/预览/性能）+ 预览实时联动 + 确定/取消/应用三按钮语义，
/// 行为自 SettingsForm 逐行移植，仍直接读写 Configurations。
/// </summary>
public partial class SettingsPage : Page
{
    private readonly DispatcherTimer _perfTimer;
    private bool _initialized;

    public SettingsPage()
    {
        InitializeComponent();
        _perfTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _perfTimer.Tick += (_, _) => RefreshMemoryInfo();
        Loaded += (_, _) => LoadSettings();
        IsVisibleChanged += (_, e) =>
        {
            // 页面不可见时停止 2s 性能刷新
            if (e.NewValue is true) _perfTimer.Start();
            else _perfTimer.Stop();
        };
    }

    private void LoadSettings()
    {
        _initialized = false;
        var isAutoStartEnabled = Win32Api.AutoStart.Check();
        AutoStartToggle.IsChecked = isAutoStartEnabled;
        var settings = Configurations.Instance.Settings;
        settings.AutoStart = isAutoStartEnabled;
        CloseToTrayToggle.IsChecked = settings.CloseToTray;
        StartInTrayToggle.IsChecked = settings.StartInTray;
        ShowArtistNameToggle.IsChecked = settings.ShowArtistName;
        ShowProgressBarToggle.IsChecked = settings.ShowProgressBar;
        PauseWhenPlayingGameToggle.IsChecked = settings.PauseWhenPlayingGame;
        EnableSteamSyncToggle.IsChecked = settings.EnableSteamSync;
        EnableCustomPrefixToggle.IsChecked = settings.EnableCustomPrefix;
        CustomPrefixTextBox.Text = settings.CustomPrefix;
        CustomPrefixTextBox.IsEnabled = settings.EnableCustomPrefix;
        EnableCustomSignatureToggle.IsChecked = settings.EnableCustomSignature;
        CustomSignatureTextBox.Text = settings.CustomSignature;
        CustomSignatureTextBox.IsEnabled = settings.EnableCustomSignature;
        if (settings.StatusPriority == SteamStatusPriority.Artist)
        {
            PriorityArtistRadio.IsChecked = true;
        }
        else
        {
            PriorityProgressRadio.IsChecked = true;
        }
        _initialized = true;
        UpdatePreview();
    }

    private void SaveSettings()
    {
        var settings = Configurations.Instance.Settings;
        var isAutoStartChecked = AutoStartToggle.IsChecked == true;
        settings.AutoStart = isAutoStartChecked;
        settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        settings.StartInTray = StartInTrayToggle.IsChecked == true;
        settings.ShowArtistName = ShowArtistNameToggle.IsChecked == true;
        settings.ShowProgressBar = ShowProgressBarToggle.IsChecked == true;
        settings.PauseWhenPlayingGame = PauseWhenPlayingGameToggle.IsChecked == true;
        settings.EnableSteamSync = EnableSteamSyncToggle.IsChecked == true;
        settings.EnableCustomPrefix = EnableCustomPrefixToggle.IsChecked == true;
        settings.CustomPrefix = CustomPrefixTextBox.Text;
        settings.EnableCustomSignature = EnableCustomSignatureToggle.IsChecked == true;
        settings.CustomSignature = CustomSignatureTextBox.Text;
        settings.StatusPriority = PriorityArtistRadio.IsChecked == true
            ? SteamStatusPriority.Artist
            : SteamStatusPriority.ProgressBar;
        Configurations.Instance.Save();
        AppServices.Rpc?.RequestStateRefresh();
        if (isAutoStartChecked == Win32Api.AutoStart.Check()) return;
        var success = Win32Api.AutoStart.Set(isAutoStartChecked);
        if (!success)
        {
            MessageBox.Show(
                $"无法 {(isAutoStartChecked ? "设置" : "取消")} 开机自启。\n请尝试以管理员权限运行本程序一次。",
                "操作失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>启用空闲签名后，预览切换为签名原文（可直观看到超长截断效果）；取消勾选恢复歌曲预览。</summary>
    private void UpdatePreview()
    {
        if (!_initialized) return;
        if (EnableCustomSignatureToggle.IsChecked == true)
        {
            var signaturePreview = SteamStatusManager.GetIdleSignature(new ConfigData
            {
                EnableCustomSignature = true,
                CustomSignature = CustomSignatureTextBox.Text ?? ""
            });
            PreviewText.Text = string.IsNullOrEmpty(signaturePreview)
                ? "（签名内容为空，请在上方输入）"
                : signaturePreview;
            return;
        }
        var dummyInfo = new PlayerInfo
        {
            Title = "稻香",
            Artists = "周杰伦",
            Schedule = 150,
            Duration = 255,
            Pause = false,
            Url = "",
            Cover = "",
            Album = "",
            Identity = ""
        };
        PreviewText.Text = SteamStatusManager.GetStatusPreview(dummyInfo, "MuSync", new ConfigData
        {
            ShowArtistName = ShowArtistNameToggle.IsChecked == true,
            ShowProgressBar = ShowProgressBarToggle.IsChecked == true,
            StatusPriority = PriorityArtistRadio.IsChecked == true
                ? SteamStatusPriority.Artist
                : SteamStatusPriority.ProgressBar,
            EnableCustomPrefix = EnableCustomPrefixToggle.IsChecked == true,
            CustomPrefix = CustomPrefixTextBox.Text ?? ""
        });
    }

    private void RefreshMemoryInfo()
    {
        try
        {
            var memoryInfo = PerformanceMonitor.GetMemoryInfo();
            var cacheStats = PerformanceMonitor.GetCacheStatistics();
            MemoryInfoText.Text =
                $"工作集: {memoryInfo.GetFormattedWorkingSet()}, 私有: {memoryInfo.GetFormattedPrivateMemory()}, 虚拟: {memoryInfo.GetFormattedVirtualMemory()}\n" +
                $"GC托管: {memoryInfo.GetFormattedGcMemory()}\n" +
                $"图片缓存: {cacheStats.ImageCacheCount} 项 | 模块缓存: {cacheStats.ModuleCacheCount + cacheStats.ProcessModuleCacheCount} 项\n" +
                $"更新于: {memoryInfo.Timestamp:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            MemoryInfoText.Text = $"获取内存信息失败: {ex.Message}";
        }
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        // 自定义前缀/签名开关联动输入框可用性
        CustomPrefixTextBox.IsEnabled = EnableCustomPrefixToggle.IsChecked == true;
        CustomSignatureTextBox.IsEnabled = EnableCustomSignatureToggle.IsChecked == true;
        UpdatePreview();
    }

    private void TextBox_Changed(object sender, TextChangedEventArgs e)
    {
        UpdatePreview();
    }

    private void RefreshMemoryButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshMemoryInfo();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        (Window.GetWindow(this) as MainWindow)?.NavigateToDashboard();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        // 取消 = 丢弃未保存的改动，从配置回读后返回主页
        LoadSettings();
        (Window.GetWindow(this) as MainWindow)?.NavigateToDashboard();
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要退出登录吗？\n退出后需要重新打开程序并输入 Steam 账号密码。",
            "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        var settings = Configurations.Instance.Settings;
        settings.SteamUsername = "";
        settings.SteamRefreshToken = "";
        settings.SteamGuardData = "";
        Configurations.Instance.Save();
        Application.Current.Shutdown();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var session = AppServices.Session;
        if (session == null) return;
        var loginWindow = new SteamLoginWindow(session) { Owner = Window.GetWindow(this) };
        loginWindow.ShowDialog();
        if (!loginWindow.LoginSucceeded) return;
        MessageBox.Show("Steam 登录成功，音乐状态已恢复同步。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
        AppServices.Rpc?.RequestStateRefresh();
    }
}
