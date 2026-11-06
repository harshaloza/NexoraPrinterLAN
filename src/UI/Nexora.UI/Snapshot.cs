using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Nexora.UI;

/// <summary>
/// "--snapshot DIR": renders each page of a window to PNG and exits. Used for documentation screenshots and
/// for checking the UI without driving the mouse.
/// </summary>
public static class Snapshot
{
    public static string? DirFromArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--snapshot");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static async Task CaptureAsync(Window window, string dir, string prefix, IEnumerable<string> pages, Action<string> showPage, Action refresh)
    {
        Directory.CreateDirectory(dir);
        window.Show();
        foreach (var page in pages)
        {
            showPage(page);
            refresh();
            await Task.Delay(1200);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var content = (FrameworkElement)window.Content;
            var dpi = VisualTreeHelper.GetDpi(content);
            var bmp = new RenderTargetBitmap((int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            bmp.Render(bg);
            bmp.Render(content);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            await using var fs = File.Create(Path.Combine(dir, $"{prefix}-{page}.png"));
            enc.Save(fs);
        }
    }
}
