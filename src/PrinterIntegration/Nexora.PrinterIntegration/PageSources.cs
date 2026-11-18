using System.Drawing;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Xps.Packaging;
using Nexora.Shared.Pdf;

namespace Nexora.PrinterIntegration;

/// <summary>A document whose pages can be measured and rasterised for the GDI print path.</summary>
public interface IPageSource : IDisposable
{
    int PageCount { get; }
    /// <summary>Page size in points (1/72 inch).</summary>
    SizeF PageSizePoints(int index);
    Bitmap Render(int index, int dpi);
}

public static class PageSources
{
    public static IPageSource Open(string path) =>
        DocumentFormats.Detect(path).Format == Shared.NexoraInfo.XpsMediaType ? XpsPageSource.Open(path) : PdfRenderer.Open(path);
}

/// <summary>
/// XPS pages for the raster (GDI) print path. WPF objects belong to the thread that created them, so the document
/// lives on a private STA thread with its own dispatcher and every call is marshalled there.
/// </summary>
public sealed class XpsPageSource : IPageSource
{
    private readonly Dispatcher _dispatcher;
    private readonly XpsDocument _doc;
    private readonly DocumentPaginator _paginator;
    private readonly List<System.Windows.Size> _sizes;
    private readonly string? _tempFile;

    private XpsPageSource(Dispatcher d, XpsDocument doc, DocumentPaginator p, List<System.Windows.Size> sizes, string? temp)
    {
        _dispatcher = d;
        _doc = doc;
        _paginator = p;
        _sizes = sizes;
        _tempFile = temp;
    }

    public static XpsPageSource Open(string path)
    {
        var ready = new TaskCompletionSource<XpsPageSource>();
        var thread = new Thread(() =>
        {
            try
            {
                string? temp = null;
                if (XpsPackageNormalizer.IsInterleaved(path))
                {
                    temp = Path.Combine(Path.GetTempPath(), $"nexora-{Guid.NewGuid():N}.xps");
                    XpsPackageNormalizer.Normalize(path, temp);
                }
                var doc = new XpsDocument(temp ?? path, FileAccess.Read);
                var paginator = (doc.GetFixedDocumentSequence() ?? throw new InvalidDataException("XPS has no FixedDocumentSequence")).DocumentPaginator;
                var sizes = new List<System.Windows.Size>();
                for (int i = 0; ; i++)
                {
                    var page = paginator.GetPage(i);
                    if (page == DocumentPage.Missing) break;
                    sizes.Add(page.Size);
                }
                ready.SetResult(new XpsPageSource(Dispatcher.CurrentDispatcher, doc, paginator, sizes, temp));
            }
            catch (Exception e)
            {
                ready.SetException(e);
                return;
            }
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Nexora XPS page source" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    public int PageCount => _sizes.Count;

    public SizeF PageSizePoints(int index) => new((float)(_sizes[index].Width * 72 / 96), (float)(_sizes[index].Height * 72 / 96));

    public Bitmap Render(int index, int dpi) => _dispatcher.Invoke(() =>
    {
        var page = _paginator.GetPage(index);
        double wIn = page.Size.Width / 96, hIn = page.Size.Height / 96;
        double scale = dpi;
        if (wIn * hIn * scale * scale > PdfRenderer.MaxPixelsPerPage) scale = Math.Sqrt(PdfRenderer.MaxPixelsPerPage / (wIn * hIn));
        var rtb = new RenderTargetBitmap((int)Math.Max(1, Math.Round(wIn * scale)), (int)Math.Max(1, Math.Round(hIn * scale)), scale, scale, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(page.Size));
        rtb.Render(bg);
        rtb.Render(page.Visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var decoded = new Bitmap(ms);
        var bmp = new Bitmap(decoded);
        bmp.SetResolution((float)scale, (float)scale);
        return bmp;
    });

    public void Dispose()
    {
        try { _dispatcher.Invoke(() => _doc.Close()); } catch { }
        _dispatcher.InvokeShutdown();
        if (_tempFile is not null) try { File.Delete(_tempFile); } catch { }
    }
}
