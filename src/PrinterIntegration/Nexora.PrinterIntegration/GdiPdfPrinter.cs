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
    public bool FitToPrintableArea { get; init; } = true;
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

        try
        {
            await Task.Run(() => pagesSent = PrintBlocking(req, settings, spoolName, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new(JobState.Cancelled, ErrorCodes.Cancelled, "Cancelled before the job was handed to Windows", null, pagesSent);
        }
        catch (Exception e) when (e is InvalidPrinterException or System.Runtime.InteropServices.ExternalException or IOException or ArgumentException or InvalidDataException)
        {
            return new(JobState.Failed, ErrorCodes.PrintFailed, $"Printing failed: {e.Message}", null, pagesSent);
        }

        onState?.Invoke(JobState.SubmittedToPrinter, $"{pagesSent} page(s) handed to the '{req.PrinterName}' driver");
        return await SpoolTracker.TrackAsync(req, spoolName, pagesSent, onState, ct);
    }

    private static int PrintBlocking(PrintRequest req, PrinterSettings settings, string spoolName, CancellationToken ct)
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

            // Graphics units are 1/100 inch with origin at the printable-area corner.
            var printable = g.VisibleClipBounds;
            float pageW = sizePt.Width / 72f * 100f, pageH = sizePt.Height / 72f * 100f;
            RectangleF dest;
            if (req.FitToPrintableArea)
            {
                // Shrink (never enlarge) so the whole sheet fits inside the printer's hard margins.
                float k = Math.Min(1f, Math.Min(printable.Width / pageW, printable.Height / pageH));
                float w = pageW * k, h = pageH * k;
                dest = new RectangleF(printable.X + (printable.Width - w) / 2, printable.Y + (printable.Height - h) / 2, w, h);
            }
            else
            {
                // Actual size: place the sheet at the physical page origin (compensate the hard margin).
                dest = new RectangleF(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY, pageW, pageH);
            }
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
