using System.Drawing;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Nexora.PrinterIntegration;

/// <summary>
/// Renders PDF pages with the PDF engine built into Windows (Windows.Data.Pdf), so no third-party PDF
/// library is shipped. Pages become bitmaps at the destination printer's resolution and are then drawn
/// through the printer's installed driver by <see cref="GdiPdfPrinter"/>.
/// </summary>
public sealed class PdfRenderer : IDisposable
{
    private readonly FileStream _file;
    private readonly IRandomAccessStream _ras;
    private readonly PdfDocument _doc;

    /// <summary>Upper bound for one page bitmap (~45 MP ≈ 180 MB at 32 bpp). DPI is reduced for larger sheets.</summary>
    public const long MaxPixelsPerPage = 45_000_000;

    private PdfRenderer(FileStream file, IRandomAccessStream ras, PdfDocument doc)
    {
        _file = file;
        _ras = ras;
        _doc = doc;
    }

    public static PdfRenderer Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var ras = fs.AsRandomAccessStream();
            var doc = PdfDocument.LoadFromStreamAsync(ras).AsTask().GetAwaiter().GetResult();
            return new PdfRenderer(fs, ras, doc);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public int PageCount => (int)_doc.PageCount;

    /// <summary>Page size in points (1/72 inch), after the page's /Rotate is applied.</summary>
    public SizeF PageSizePoints(int index)
    {
        using var page = _doc.GetPage((uint)index);
        // PdfPage.Size is in DIPs (1/96 inch) and already accounts for rotation.
        return new SizeF((float)(page.Size.Width * 72 / 96), (float)(page.Size.Height * 72 / 96));
    }

    public Bitmap Render(int index, int dpi)
    {
        using var page = _doc.GetPage((uint)index);
        double wIn = page.Size.Width / 96, hIn = page.Size.Height / 96;
        double scale = dpi;
        if (wIn * hIn * scale * scale > MaxPixelsPerPage) scale = Math.Sqrt(MaxPixelsPerPage / (wIn * hIn));
        var opts = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(wIn * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(hIn * scale)),
        };
        using var ms = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(ms, opts).AsTask().GetAwaiter().GetResult();
        using var s = ms.AsStreamForRead();
        using var decoded = new Bitmap(s);
        var bmp = new Bitmap(decoded); // detach from the stream (GDI+ needs the source stream alive otherwise)
        bmp.SetResolution((float)scale, (float)scale);
        return bmp;
    }

    public void Dispose()
    {
        _ras.Dispose();
        _file.Dispose();
    }
}
