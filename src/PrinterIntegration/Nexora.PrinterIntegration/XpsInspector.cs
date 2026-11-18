using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace Nexora.PrinterIntegration;

public sealed record XpsPageInfo(double WidthMm, double HeightMm);

public sealed record XpsInfo(int PageCount, IReadOnlyList<XpsPageInfo> Pages, int? Copies, string? MediaSize, string? Orientation, string? Problem);

/// <summary>
/// Reads XPS documents produced by the Nexora v4 driver (XPS pass-through): page count and sizes, the job's
/// PrintTicket (copies, paper, orientation), and optional rendering of a page to PNG for checking.
/// WPF's XPS APIs need an STA thread, so every call runs on its own STA thread.
/// </summary>
public static class XpsInspector
{
    public static bool LooksLikeXps(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[4];
        return fs.Read(head) == 4 && head[0] == (byte)'P' && head[1] == (byte)'K'; // OPC package = ZIP
    }

    public static XpsInfo Inspect(string path, string? pngOut = null, int pageIndex = 0, int dpi = 72) => RunSta(() =>
    {
        string? normalized = null;
        try
        {
            if (XpsPackageNormalizer.IsInterleaved(path))
            {
                normalized = Path.Combine(Path.GetTempPath(), $"nexora-{Guid.NewGuid():N}.xps");
                XpsPackageNormalizer.Normalize(path, normalized);
            }
            using var doc = new XpsDocument(normalized ?? path, FileAccess.Read);
            var seq = doc.GetFixedDocumentSequence() ?? throw new InvalidDataException("No FixedDocumentSequence");
            var paginator = seq.DocumentPaginator;
            var pages = new List<XpsPageInfo>();
            for (int i = 0; i < 10_000; i++)
            {
                var page = paginator.GetPage(i);
                if (page == DocumentPage.Missing) break;
                pages.Add(new XpsPageInfo(page.Size.Width / 96 * 25.4, page.Size.Height / 96 * 25.4));
                if (pngOut is not null && i == pageIndex) SavePng(page, pngOut, dpi);
            }
            var ticket = ReadJobTicket(doc);
            return new XpsInfo(pages.Count, pages, ticket?.CopyCount, ticket?.PageMediaSize?.PageMediaSizeName?.ToString(),
                ticket?.PageOrientation?.ToString(), null);
        }
        catch (Exception e)
        {
            return new XpsInfo(0, [], null, null, null, e.Message);
        }
        finally
        {
            if (normalized is not null) try { File.Delete(normalized); } catch { }
        }
    });

    /// <summary>
    /// The job's settings (copies, paper, orientation). The spooler stores them at sequence, document or page level
    /// depending on the application, so the first one present wins, in that order.
    /// </summary>
    public static PrintTicket? ReadJobTicket(XpsDocument doc)
    {
        var reader = doc.FixedDocumentSequenceReader;
        if (reader is null) return null;
        if (reader.PrintTicket is PrintTicket job) return job;
        foreach (var d in reader.FixedDocuments)
        {
            if (d.PrintTicket is PrintTicket docTicket) return docTicket;
            if (d.FixedPages.FirstOrDefault()?.PrintTicket is PrintTicket pageTicket) return pageTicket;
        }
        return null;
    }

    private static void SavePng(DocumentPage page, string path, int dpi)
    {
        int w = (int)Math.Ceiling(page.Size.Width * dpi / 96), h = (int)Math.Ceiling(page.Size.Height * dpi / 96);
        var bmp = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(page.Size));
        bmp.Render(bg);
        bmp.Render(page.Visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static T RunSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw error;
        return result;
    }
}
