using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MuSync;

/// <summary>桌面增强页：双击空白隐藏图标开关、桌面图标常驻透明度调节。</summary>
public partial class DesktopPage : Page
{
    private bool _initialized;
    private DispatcherTimer? _saveDebounce;

    public DesktopPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSettings();
        Unloaded += (_, _) => FlushPendingSave(); // 切走页面前确保滑块值已落盘
    }

    private void LoadSettings()
    {
        if (_initialized) return;
        _initialized = true;
        var settings = Configurations.Instance.Settings;
        DoubleClickHideToggle.IsChecked = settings.DoubleClickHideDesktopIcons;
        IconOpacitySlider.Value = settings.DesktopIconOpacity is >= 20 and <= 100
            ? settings.DesktopIconOpacity
            : 100;
        // 开关状态变化即时启停钩子
        DoubleClickHideToggle.Checked += (_, _) => SaveDoubleClickSetting(true);
        DoubleClickHideToggle.Unchecked += (_, _) => SaveDoubleClickSetting(false);
    }

    private void SaveDoubleClickSetting(bool enabled)
    {
        if (!_initialized) return;
        var settings = Configurations.Instance.Settings;
        settings.DoubleClickHideDesktopIcons = enabled;
        Configurations.Instance.Save();
        if (enabled) DesktopIconService.Start();
        else DesktopIconService.StopHook();
    }

    private void IconOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized || OpacityValueText == null) return;
        var percent = (int)IconOpacitySlider.Value;
        OpacityValueText.Text = $"{percent}%";
        // 拖动期间只更新内存与实时效果，绝不逐帧写磁盘；停止操作 400ms 后才落盘一次
        Configurations.Instance.Settings.DesktopIconOpacity = percent;
        DesktopIconService.SetOpacity(percent);
        _saveDebounce ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = System.TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Stop();
        _saveDebounce.Tick -= SaveDebounce_Tick;
        _saveDebounce.Tick += SaveDebounce_Tick;
        _saveDebounce.Start();
    }

    private void SaveDebounce_Tick(object? sender, System.EventArgs e)
    {
        FlushPendingSave();
    }

    private void FlushPendingSave()
    {
        if (_saveDebounce?.IsEnabled == true)
        {
            _saveDebounce.Stop();
            Configurations.Instance.Save();
        }
    }
}
