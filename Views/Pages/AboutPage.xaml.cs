using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Media.Imaging;

namespace MuSync;

/// <summary>关于页：版本、运行环境、版权许可与开源致谢。</summary>
public partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        LoadAppIcon();
        LoadVersionInfo();
    }

    private void LoadAppIcon()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("MuSync.Resources.icon.ico");
            if (stream == null) return;
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            if (frame != null)
            {
                frame.Freeze();
                AppIcon.Source = frame;
            }
        }
        catch
        {
            // 图标加载失败时保持空白，不影响关于页其他内容
        }
    }

    private void LoadVersionInfo()
    {
        var informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"
            : informational.Split('+', '-')[0];
        VersionText.Text = $"版本 {version}";

        var framework = RuntimeInformation.FrameworkDescription;
        var os = RuntimeInformation.OSDescription;
        RuntimeText.Text = $"运行环境：{framework} · {os} · x64";
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 没有默认浏览器等环境下静默忽略
        }
        e.Handled = true;
    }
}
