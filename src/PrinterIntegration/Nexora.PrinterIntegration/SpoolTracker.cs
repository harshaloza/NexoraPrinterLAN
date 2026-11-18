using Nexora.Shared.Jobs;
using Nexora.Shared.Windows;

namespace Nexora.PrinterIntegration;

/// <summary>Follows a spooled job by its document name. COMPLETED means the spooler finished sending it to the device.</summary>
internal static class SpoolTracker
{
    public static async Task<PrintOutcome> TrackAsync(PrintRequest req, string spoolName, int pages, Action<JobState, string>? onState, CancellationToken ct)
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

    public static string SpoolName(PrintRequest req)
    {
        string name = $"Nexora {req.JobId.ToString("N")[..8]} - {req.DocumentName}";
        return name.Length > 120 ? name[..120] : name;
    }
}
