using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using Nexora.Shared.Jobs;
using Nexora.Shared.Windows;

namespace Nexora.PrinterIntegration;

public sealed record PrintRequest
{
    public required Guid JobId { get; init; }
    public required string PdfPath { get; init; }
    public required string DocumentName { get; init; }
    public required string PrinterName { get; init; }
    public PrintSettings Settings { get; init; } = new();
    public bool FitToPrintableArea { get; init; } = true;
    public int MaxRenderDpi { get; init; } = 600;
    /// <summary>Test hook: print through the driver into this file instead of the device port.</summary>
    public string? PrintToFile { get; init; }
    public TimeSpan TrackTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed record PrintOutcome(JobState State, string? ErrorCode, string Message, int? SpoolerJobId, int PagesSent);

/// <summary>
/// Prints a PDF through the destination printer's own installed Windows driver (GDI/System.Drawing.Printing).
/// The driver — e.g. Canon CAPT for the LBP2900 — produces the device language; nothing is sent RAW.
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

        string spoolName = $"Nexora {req.JobId.ToString("N")[..8]} - {req.DocumentName}";
        if (spoolName.Length > 120) spoolName = spoolName[..120];
        int pagesSent = 0;

        try
        {
            await Task.Run(() => pagesSent = PrintBlocking(req, settings, spoolName, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return new(JobState.Cancelled, ErrorCodes.Cancelled, "Cancelled before the job was handed to Windows", null, pagesSent);
        }
        catch (Exception e) when (e is InvalidPrinterException or System.Runtime.InteropServices.ExternalException or IOException or ArgumentException)
        {
            return new(JobState.Failed, ErrorCodes.PrintFailed, $"Printing failed: {e.Message}", null, pagesSent);
        }

        onState?.Invoke(JobState.SubmittedToPrinter, $"{pagesSent} page(s) handed to the '{req.PrinterName}' driver");
        return await TrackAsync(req, spoolName, pagesSent, onState, ct);
    }

    private static int PrintBlocking(PrintRequest req, PrinterSettings settings, string spoolName, CancellationToken ct)
    {
        using var pdf = PdfRenderer.Open(req.PdfPath);
        if (pdf.PageCount == 0) throw new IOException("PDF has no pages");

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
            var size = pdf.PageSizePoints(page);
            e.PageSettings.Landscape = size.Width > size.Height;
            var paper = MatchPaper(settings, size);
            if (paper is not null) e.PageSettings.PaperSize = paper;
            e.PageSettings.Color = color;
        };
        doc.PrintPage += (_, e) =>
        {
            ct.ThrowIfCancellationRequested();
            var g = e.Graphics!;
            var sizePt = pdf.PageSizePoints(page);
            int deviceDpi = (int)Math.Max(g.DpiX, 72);
            using var bmp = pdf.Render(page, Math.Min(deviceDpi, req.MaxRenderDpi));

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
            e.HasMorePages = page < pdf.PageCount;
        };
        doc.Print(); // returns after EndDoc: the job is fully spooled
        return page;
    }

    /// <summary>Closest installed paper for the PDF page (either orientation), within 6 mm; null = keep the driver default.</summary>
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

    /// <summary>Follows the spooled job. COMPLETED means the spooler finished sending it to the device.</summary>
    private static async Task<PrintOutcome> TrackAsync(PrintRequest req, string spoolName, int pages, Action<JobState, string>? onState, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + req.TrackTimeout;
        uint? jobId = null;
        string? lastStatus = null;
        bool sawPrinting = false;
        int missingPolls = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            SpoolerJob? job = null;
            try
            {
                job = Winspool.EnumerateJobs(req.PrinterName)
                    .FirstOrDefault(j => jobId is null ? j.Document == spoolName : j.JobId == jobId);
            }
            catch (System.ComponentModel.Win32Exception) { }

            if (job is null)
            {
                // Gone from the queue: either already printed, or it never showed up because it was very fast.
                if (jobId is not null || ++missingPolls >= 3)
                    return new(JobState.Completed, null,
                        $"Windows spooler finished sending {pages} page(s) to '{req.PrinterName}'" + (sawPrinting ? "" : " (job left the queue quickly)"),
                        (int?)jobId, pages);
            }
            else
            {
                jobId = job.JobId;
                if (job.IsDone)
                    return new(JobState.Completed, null, $"Spooler reports job {job.JobId} printed on '{req.PrinterName}'", (int)job.JobId, pages);
                var text = job.HasError
                    ? $"Waiting in Windows queue: {job.StatusText ?? Winspool.DescribePrinterStatus(Winspool.FindPrinter(req.PrinterName)?.Status ?? 0, Winspool.FindPrinter(req.PrinterName)?.Attributes ?? 0)}"
                    : job.IsPrinting ? $"Printing ({job.PagesPrinted}/{Math.Max(job.TotalPages, (uint)pages)} pages)" : "Queued in Windows spooler";
                var state = job.IsPrinting && !job.HasError ? JobState.Printing : JobState.SubmittedToPrinter;
                if (job.IsPrinting) sawPrinting = true;
                if (text != lastStatus)
                {
                    onState?.Invoke(state, text);
                    lastStatus = text;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return new(JobState.SubmittedToPrinter, null,
            $"Still in the Windows queue of '{req.PrinterName}' after {req.TrackTimeout.TotalMinutes:0} min ({lastStatus ?? "no status"})", (int?)jobId, pages);
    }
}
