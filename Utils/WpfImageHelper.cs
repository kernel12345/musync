using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MuSync.Utils;
namespace MuSync;
/// <summary>
/// GDI Image → WPF BitmapImage 的转换助手。
/// PNG 编码 + 解码 + Freeze 全部在后台线程执行，避免大图阻塞 UI（历史教训：GDI+ 解码 50-200ms/张）。
/// </summary>
internal static class WpfImageHelper
{
    /// <summary>把 System.Drawing.Image 编码为已冻结的 BitmapImage（后台线程执行，返回对象可跨线程使用）。</summary>
    public static async Task<BitmapImage?> ToFrozenBitmapAsync(Image? image)
    {
        if (image == null) return null;
        return await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream();
                image.Save(stream, ImageFormat.Png);
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Logger.Error($"封面图像转换失败: {ex.Message}");
                return null;
            }
        }).ConfigureAwait(false);
    }
    /// <summary>把 WinForms 图标转为冻结的 BitmapSource（用于 WPF 窗口图标）。</summary>
    public static BitmapSource ToBitmapSource(Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }
}
