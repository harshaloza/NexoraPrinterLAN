using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using System.Windows.Xps.Serialization;
using Nexora.Shared.Jobs;
using Nexora.Shared.Windows;
using PageOrientation = System.Printing.PageOrientation;

namespace Nexora.PrinterIntegration;

/// <summary>Vector printing failed before the job was complete; the caller may fall back to the raster path.</summary>
public sealed class VectorPrintException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Vector print path for XPS jobs from the Nexora v4 driver: the pages are written with WPF's XpsDocumentWriter to the
/// destination queue. For XPS-capable (v4) drivers Windows passes XPS on; for GDI (v3) drivers such as Canon CAPT,
/// Windows converts the XPS to GDI drawing calls into that driver. Text and lines stay vectors (no 600-dpi bitmaps).
/// Per page: the closest paper the printer has (±6 mm) and the page's orientation are requested, and the page is scaled
/// as <see cref="PrintRequest.Scaling"/> says (by default 100% when it fits on the paper, else shrunk to the printable area).
/// </summary>
public sealed class XpsVectorPrinter
{
    private const double SixMmInDips = 6 / 25.4 * 96;

    public async Task<PrintOutcome> PrintAsync(PrintRequest req, Action<JobState, string>? onState, CancellationToken ct)
    {
        if (Winspool.FindPrinter(req.PrinterName) is null)
            return new(JobState.Failed, ErrorCodes.PrinterUnavailable, $"Printer '{req.PrinterName}' is not installed on this PC", null, 0);
        string spoolName = SpoolTracker.SpoolName(req);
        int pages;
        double minScale = 1;
        try
        {
            pages = await RunSta(() => PrintBlocking(req, spoolName, k => minScale = k, ct));
        }
        catch (OperationCanceledException)
        {
            return new(JobState.Cancelled, ErrorCodes.Cancelled, "Cancelled before the job was handed to Windows", null, 0);
        }
        catch (Exception e)
        {
            throw new VectorPrintException($"Vector printing to '{req.PrinterName}' failed: {e.Message}", e);
        }
        string scale = PagePlacement.Describe(minScale, req.Calibration);
        onState?.Invoke(JobState.SubmittedToPrinter, $"{pages} page(s) handed to the '{req.PrinterName}' driver (vector), {scale}");
        var outcome = await SpoolTracker.TrackAsync(req, spoolName, pages, onState, ct);
        return outcome with { Message = $"{outcome.Message} ({scale})" };
    }

    private static int PrintBlocking(PrintRequest req, string spoolName, Action<double> minScale, CancellationToken ct)
    {
        string? temp = null;
        try
        {
            if (XpsPackageNormalizer.IsInterleaved(req.DocumentPath))
            {
                temp = Path.Combine(Path.GetTempPath(), $"nexora-{Guid.NewGuid():N}.xps");
                XpsPackageNormalizer.Normalize(req.DocumentPath, temp);
            }
            using var doc = new XpsDocument(temp ?? req.DocumentPath, FileAccess.Read);
            var seq = doc.GetFixedDocumentSequence() ?? throw new InvalidDataException("XPS has no FixedDocumentSequence");
            var source = seq.DocumentPaginator;
            var sourceTicket = XpsInspector.ReadJobTicket(doc);

            using var server = new LocalPrintServer();
            using var queue = OpenQueue(server, req.PrinterName);
            var caps = queue.GetPrintCapabilities();
            var baseTicket = queue.UserPrintTicket ?? queue.DefaultPrintTicket;
            var jobTicket = sourceTicket is null ? baseTicket.Clone() : queue.MergeAndValidatePrintTicket(baseTicket, sourceTicket).ValidatedPrintTicket;

            int copies = Math.Clamp(Math.Max(req.Settings.Copies, sourceTicket?.CopyCount ?? 1), 1, 999);
            int maxCopies = caps.MaxCopyCount ?? 1;
            int passes = copies <= maxCopies ? 1 : copies;  // printer cannot do that many copies itself: send the document repeatedly
            jobTicket.CopyCount = passes == 1 ? copies : 1;
            if (req.Settings.ColorMode == ColorMode.Monochrome && caps.OutputColorCapability.Contains(OutputColor.Monochrome))
                jobTicket.OutputColor = OutputColor.Monochrome;

            var plans = PlanPages(queue, caps, jobTicket, source, req.Scaling, req.Calibration);
            minScale(plans.Min(p => p.Placement.Scale));
            // Drivers that only read the job/document ticket should still get the first page's paper and orientation.
            jobTicket.PageMediaSize = plans[0].Ticket.PageMediaSize;
            jobTicket.PageOrientation = plans[0].Ticket.PageOrientation;
            var paginator = new FittingPaginator(source, plans);

            queue.CurrentJobSettings.Description = spoolName;
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            int pageTicketIndex = 0;
            writer.WritingPrintTicketRequired += (_, e) =>
            {
                if (e.CurrentPrintTicketLevel == PrintTicketLevel.FixedPagePrintTicket)
                    e.CurrentPrintTicket = plans[pageTicketIndex++ % plans.Count].Ticket;
                else
                    e.CurrentPrintTicket = jobTicket;
            };
            for (int i = 0; i < passes; i++)
            {
                ct.ThrowIfCancellationRequested();
                writer.Write(paginator);
            }
            return plans.Count * passes;
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch { }
        }
    }

    internal sealed record PagePlan(Size PaperSize, Rect Imageable, bool Landscape, Size ContentSize, PrintTicket Ticket, PagePlacement Placement);

    private static List<PagePlan> PlanPages(PrintQueue queue, PrintCapabilities caps, PrintTicket jobTicket, DocumentPaginator source, PrintScaling scaling, PrintCalibration? calibration)
    {
        var media = caps.PageMediaSizeCapability.Where(m => m.Width is > 0 && m.Height is > 0).ToList();
        var defaultMedia = jobTicket.PageMediaSize;
        var areaCache = new Dictionary<string, PageImageableArea?>();
        var plans = new List<PagePlan>();
        for (int i = 0; ; i++)
        {
            var page = source.GetPage(i);
            if (page == DocumentPage.Missing) break;
            var size = page.Size;
            bool landscape = size.Width > size.Height;
            double shortSide = Math.Min(size.Width, size.Height), longSide = Math.Max(size.Width, size.Height);

            PageMediaSize? chosen = media
                .Select(m => (m, err: Math.Abs(Math.Min(m.Width!.Value, m.Height!.Value) - shortSide) + Math.Abs(Math.Max(m.Width!.Value, m.Height!.Value) - longSide)))
                .Where(x => x.err <= 2 * SixMmInDips)
                .OrderBy(x => x.err).Select(x => x.m).FirstOrDefault();
            chosen ??= defaultMedia?.Width is > 0 ? defaultMedia : media.FirstOrDefault();
            if (chosen?.Width is null || chosen.Height is null) throw new InvalidOperationException("The printer reports no usable paper size");

            var ticket = jobTicket.Clone();
            ticket.PageMediaSize = chosen;
            ticket.PageOrientation = landscape ? PageOrientation.Landscape : PageOrientation.Portrait;

            double pw = Math.Min(chosen.Width.Value, chosen.Height.Value), ph = Math.Max(chosen.Width.Value, chosen.Height.Value);
            var paper = landscape ? new Size(ph, pw) : new Size(pw, ph);
            string key = $"{chosen.PageMediaSizeName}|{pw:0}x{ph:0}|{landscape}";
            if (!areaCache.TryGetValue(key, out var area))
            {
                try { area = queue.GetPrintCapabilities(ticket).PageImageableArea; } catch (PrintQueueException) { area = null; }
                areaCache[key] = area;
            }
            Rect imageable = area is null || area.ExtentWidth <= 0 || area.ExtentHeight <= 0
                ? new Rect(paper)
                : new Rect(area.OriginWidth, area.OriginHeight, area.ExtentWidth, area.ExtentHeight);
            // Capabilities describe the imageable area in portrait; turn it with the page.
            if (landscape && imageable.Width < imageable.Height)
                imageable = new Rect(imageable.Y, imageable.X, imageable.Height, imageable.Width);
            // XPS page coordinates start at the paper's corner, so the placement is used as computed.
            var placement = PagePlacement.Compute(scaling, size.Width, size.Height, paper.Width, paper.Height,
                imageable.X, imageable.Y, imageable.Width, imageable.Height).Calibrate(calibration, landscape);
            plans.Add(new PagePlan(paper, imageable, landscape, size, ticket, placement));
        }
        if (plans.Count == 0) throw new InvalidDataException("The XPS document has no pages");
        return plans;
    }

    private static PrintQueue OpenQueue(LocalPrintServer local, string name)
    {
        if (name.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int cut = name.IndexOf('\\', 2);
            return new PrintQueue(new PrintServer(name[..cut]), name[(cut + 1)..]);
        }
        return local.GetPrintQueue(name);
    }

    /// <summary>Wraps the source pages: each is placed on its chosen paper as its plan's placement says.</summary>
    private sealed class FittingPaginator(DocumentPaginator source, List<PagePlan> plans) : DocumentPaginator
    {
        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber >= plans.Count) return DocumentPage.Missing;
            var plan = plans[pageNumber];
            var page = source.GetPage(pageNumber);
            var container = new ContainerVisual();
            container.Children.Add(page.Visual);
            var p = plan.Placement;
            if (p.ScaleX != 1 || p.ScaleY != 1 || p.X != 0 || p.Y != 0)
            {
                var t = new TransformGroup();
                t.Children.Add(new ScaleTransform(p.ScaleX, p.ScaleY));
                t.Children.Add(new TranslateTransform(p.X, p.Y));
                container.Transform = t;
            }
            var box = new Rect(plan.PaperSize);
            return new DocumentPage(container, plan.PaperSize, box, box);
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => plans.Count;
        public override Size PageSize { get => plans[0].PaperSize; set { } }
        public override IDocumentPaginatorSource? Source => null;
    }

    private static Task<T> RunSta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        }) { IsBackground = true, Name = "Nexora XPS vector print" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
}

/// <summary>
/// Chooses the print path for a received document: PDF → raster; XPS → vector, falling back to raster when vector
/// printing fails before the job was spooled (Auto), or as configured.
/// </summary>
public sealed class DocumentPrinter
{
    public async Task<PrintOutcome> PrintAsync(PrintRequest req, Action<JobState, string>? onState, CancellationToken ct)
    {
        bool xps = Shared.Pdf.DocumentFormats.Detect(req.DocumentPath).Format == Shared.NexoraInfo.XpsMediaType;
        if (!xps || req.XpsMethod == XpsPrintMethod.Raster)
            return await new GdiPdfPrinter().PrintAsync(req, onState, ct);
        try
        {
            return await new XpsVectorPrinter().PrintAsync(req, onState, ct);
        }
        catch (VectorPrintException e) when (req.XpsMethod == XpsPrintMethod.Auto)
        {
            // Remove anything partly spooled under this job's name before printing again as images.
            var name = SpoolTracker.SpoolName(req);
            try
            {
                foreach (var j in Winspool.EnumerateJobs(req.PrinterName).Where(j => j.Document == name))
                    Winspool.ControlJob(req.PrinterName, j.JobId, Winspool.JobControlDelete);
            }
            catch (System.ComponentModel.Win32Exception) { }
            onState?.Invoke(JobState.SubmittedToPrinter, $"{e.Message} — printing as images instead");
            var outcome = await new GdiPdfPrinter().PrintAsync(req, onState, ct);
            return outcome with { Message = $"{outcome.Message} (raster fallback: {e.InnerException?.Message})" };
        }
        catch (VectorPrintException e)
        {
            return new(JobState.Failed, ErrorCodes.PrintFailed, e.Message, null, 0);
        }
    }
}
