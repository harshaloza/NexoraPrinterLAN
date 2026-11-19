using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using Nexora.Shared.Jobs;
using Nexora.Shared.Windows;

namespace Nexora.PrinterIntegration;

/// <summary>How an XPS job is printed on the receiver.</summary>
public enum XpsPrintMethod
{
    /// <summary>Vector (Windows XPS printing into the destination driver); raster if that fails before submission.</summary>
    Auto,
    Vector,
    Raster,
}

public sealed record PrintRequest
{
    public required Guid JobId { get; init; }
    /// <summary>The received document: PDF or XPS.</summary>
    public required string DocumentPath { get; init; }
    public required string DocumentName { get; init; }
    public required string PrinterName { get; init; }
    public PrintSettings Settings { get; init; } = new();
    public PrintScaling Scaling { get; init; } = PrintScaling.Auto;
    public int MaxRenderDpi { get; init; } = 600;
    public XpsPrintMethod XpsMethod { get; init; } = XpsPrintMethod.Auto;
    /// <summary>Test hook: print through the driver into this file instead of the device port (raster path only).</summary>
    public string? PrintToFile { get; init; }
    public TimeSpan TrackTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record PrintOutcome(JobState State, string? ErrorCode, string Message, int? SpoolerJobId, int PagesSent);

/// <summary>
/// Raster print path: renders each page (PDF via Windows.Data.Pdf, XPS via WPF) at the printer's resolution and draws it
/// through the destination printer's own installed Windows driver (GDI/System.Drawing.Printing). The driver — e.g.
/// Canon CAPT for the LBP2900 — produces the device language; nothing is sent RAW.
/// Must run in a desktop session (Microsoft does not support System.Drawing.Printing from services).
/// </summary>
public sealed class GdiPdfPrinter
{
    public async Task<PrintOutcome> PrintAsync(PrintRequest req, Action<JobState, string>? onState, CancellationToken ct)
    {
        var printer = Winspool.FindPrinter(req.PrinterName);
        if (printer is null)
            return new(JobState.Failed, ErrorCodes.PrinterUnavailable, $"Printer '{req.PrinterName}' is not installed on this PC", null, 0);

        var settings = new PrinterSettings { PrinterName = req.PrinterName };
        if (!settings.IsValid)
            return new(JobState.Failed, ErrorCodes.PrinterUnavailable, $"Windows cannot open printer '{req.PrinterName}'", null, 0);

        string spoolName = SpoolTracker.SpoolName(req);
        int pagesSent = 0;
        double minScale = 1;

        try
        {
            await Task.Run(() => pagesSent = PrintBlocking(req, settings, spoolName, k => minScale = Math.Min(minScale, k), ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new(JobState.Cancelled, ErrorCodes.Cancelled, "Cancelled before the job was handed to Windows", null, pagesSent);
        }
        catch (Exception e) when (e is InvalidPrinterException or System.Runtime.InteropServices.ExternalException or IOException or ArgumentException or InvalidDataException)
        {
            return new(JobState.Failed, ErrorCodes.PrintFailed, $"Printing failed: {e.Message}", null, pagesSent);
        }

        string scale = PagePlacement.Describe(minScale);
        onState?.Invoke(JobState.SubmittedToPrinter, $"{pagesSent} page(s) handed to the '{req.PrinterName}' driver, {scale}");
        var outcome = await SpoolTracker.TrackAsync(req, spoolName, pagesSent, onState, ct);
        return outcome with { Message = $"{outcome.Message} ({scale})" };
    }

    private static int PrintBlocking(PrintRequest req, PrinterSettings settings, string spoolName, Action<double> scaleUsed, CancellationToken ct)
    {
        using var src = PageSources.Open(req.DocumentPath);
        if (src.PageCount == 0) throw new IOException("The document has no pages");

        var s = req.Settings;
        settings.Copies = (short)Math.Clamp(s.Copies, 1, Math.Max((int)settings.MaximumCopies, 1));
        settings.Collate = s.Collate;
        if (req.PrintToFile is not null)
        {
            settings.PrintToFile = true;
            settings.PrintFileName = req.PrintToFile;
        }
        bool color = s.ColorMode != ColorMode.Monochrome && settings.SupportsColor;

        using var doc = new PrintDocument
        {
            PrinterSettings = settings,
            DocumentName = spoolName,
            PrintController = new StandardPrintController(), // no progress dialog
            OriginAtMargins = false,
        };
        doc.DefaultPageSettings.Color = color;

        int page = 0;
        doc.QueryPageSettings += (_, e) =>
        {
            var size = src.PageSizePoints(page);
            e.PageSettings.Landscape = size.Width > size.Height;
            var paper = MatchPaper(settings, size);
            if (paper is not null) e.PageSettings.PaperSize = paper;
            e.PageSettings.Color = color;
        };
        doc.PrintPage += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            var g = e.Graphics!;
            var sizePt = src.PageSizePoints(page);
            int deviceDpi = (int)Math.Max(g.DpiX, 72);
            using var bmp = src.Render(page, Math.Min(deviceDpi, req.MaxRenderDpi));

            // Graphics units are 1/100 inch with the origin at the printable-area corner, which sits at the hard margin
            // from the paper's corner. Placement is computed in paper coordinates, then shifted by the hard margin.
            var printable = g.VisibleClipBounds;
            float hmX = e.PageSettings.HardMarginX, hmY = e.PageSettings.HardMarginY;
            float pageW = sizePt.Width / 72f * 100f, pageH = sizePt.Height / 72f * 100f;
            var place = PagePlacement.Compute(req.Scaling, pageW, pageH, e.PageBounds.Width, e.PageBounds.Height,
                hmX + printable.X, hmY + printable.Y, printable.Width, printable.Height);
            scaleUsed(place.Scale);
            var dest = new RectangleF((float)place.X - hmX, (float)place.Y - hmY, (float)(pageW * place.Scale), (float)(pageH * place.Scale));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(bmp, dest);

            page++;
            e.HasMorePages = page < src.PageCount;
        };
        doc.Print(); // returns after EndDoc: the job is fully spooled
        return page;
    }

    /// <summary>Closest installed paper for the page (either orientation), within 6 mm; null = keep the driver default.</summary>
    internal static PaperSize? MatchPaper(PrinterSettings settings, SizeF pagePt)
    {
        float w = Math.Min(pagePt.Width, pagePt.Height) / 72f * 100f, h = Math.Max(pagePt.Width, pagePt.Height) / 72f * 100f;
        PaperSize? best = null;
        float bestErr = float.MaxValue;
        foreach (PaperSize p in settings.PaperSizes)
        {
            float pw = Math.Min(p.Width, p.Height), ph = Math.Max(p.Width, p.Height);
            float err = Math.Abs(pw - w) + Math.Abs(ph - h);
            if (err < bestErr) { bestErr = err; best = p; }
        }
        return bestErr <= 2 * 23.6f ? best : null; // 23.6 hundredths of an inch ≈ 6 mm per side
    }
}
