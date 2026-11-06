using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Pdf;
using Nexora.Shared.Windows;

namespace Nexora.PrintPipeline;

/// <summary>A print job captured from the "Nexora Printer LAN" queue.</summary>
public sealed record CapturedJob
{
    public required string FilePath { get; init; }
    public required long SizeBytes { get; init; }
    public required string DocumentName { get; init; }
    public string? UserName { get; init; }
    public uint? SpoolerJobId { get; init; }
    public int? PageCount { get; init; }
    public PrintSettings Settings { get; init; } = new();
    public string? Problem { get; init; }
}

/// <summary>
/// Receives rendered print jobs from the Windows spooler. The queue's port is a Local Port named
/// \\.\pipe\NexoraPrinterLAN; for each job the spooler opens the port (connects), writes the PDF produced by the
/// inbox "Microsoft Print To PDF" driver, and closes it. One connection therefore equals one complete job.
/// </summary>
public sealed class PipeCaptureServer : IAsyncDisposable
{
    private const int Instances = 4;
    private readonly string _pipeName;
    private readonly string _queueName;
    private readonly string _captureDir;
    private readonly long _maxBytes;
    private readonly Func<CapturedJob, Task> _onJob;
    private readonly LogScope _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _listeners = [];

    public PipeCaptureServer(string pipeName, string queueName, string captureDir, long maxBytes, Func<CapturedJob, Task> onJob, LogScope log)
    {
        _pipeName = pipeName;
        _queueName = queueName;
        _captureDir = Directory.CreateDirectory(captureDir).FullName;
        _maxBytes = maxBytes;
        _onJob = onJob;
        _log = log;
    }

    public bool IsListening { get; private set; }
    public string? ListenError { get; private set; }

    public void Start()
    {
        // Create the first instance synchronously so a name clash (another Nexora client running) surfaces immediately.
        NamedPipeServerStream first;
        try
        {
            first = Create(firstInstance: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ListenError = $"Cannot create pipe \\\\.\\pipe\\{_pipeName}: {e.Message}. Is another Nexora Client already running?";
            _log.Error("capture.start", ListenError, ex: e);
            throw new InvalidOperationException(ListenError, e);
        }
        IsListening = true;
        _listeners.Add(Task.Run(() => ListenLoopAsync(first, _cts.Token)));
        for (int i = 1; i < Instances; i++) _listeners.Add(Task.Run(() => ListenLoopAsync(null, _cts.Token)));
        _log.Info("capture.listening", $"Print capture listening on \\\\.\\pipe\\{_pipeName}");
        RestartStalledSpoolerJobs();
    }

    /// <summary>
    /// The pipe ACL lets local SYSTEM (spooler) and locally logged-on users write; network logons are denied, so the
    /// pipe cannot be reached remotely over SMB.
    /// </summary>
    private NamedPipeServerStream Create(bool firstInstance)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.ReadAttributes, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.InOut, Instances, PipeTransmissionMode.Byte, options, 1 << 16, 1 << 16, sec);
    }

    private async Task ListenLoopAsync(NamedPipeServerStream? pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                pipe ??= Create(firstInstance: false);
                await pipe.WaitForConnectionAsync(ct);
                // Read the spooler's job ticket now, while the job is in the PRINTING state.
                var job = FindPrintingJob();
                await CaptureAsync(pipe, job, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                _log.Error("capture.error", "Print capture failed", ex: e);
                try { await Task.Delay(500, ct); } catch { break; }
            }
            finally
            {
                if (pipe is not null)
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); } catch { }
                    await pipe.DisposeAsync();
                    pipe = null;
                }
            }
        }
    }

    private SpoolerJob? FindPrintingJob()
    {
        try
        {
            var jobs = Winspool.EnumerateJobs(_queueName);
            return jobs.FirstOrDefault(j => j.IsPrinting) ?? jobs.FirstOrDefault(j => (j.Status & Winspool.JobSpooling) == 0 && !j.IsDone);
        }
        catch (Exception e)
        {
            _log.Warn("capture.ticket", $"Could not read the job ticket from queue '{_queueName}'", ex: e);
            return null;
        }
    }

    private async Task CaptureAsync(NamedPipeServerStream pipe, SpoolerJob? job, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_captureDir, id.ToString("N") + ".capture");
        long total = 0;
        bool overflow = false;
        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            while (true)
            {
                int n = await pipe.ReadAsync(buf, ct);
                if (n == 0) break; // spooler closed the port: end of job
                total += n;
                if (total > _maxBytes) { overflow = true; continue; } // drain so the spooler finishes the job cleanly
                await fs.WriteAsync(buf.AsMemory(0, n), ct);
            }
            await fs.FlushAsync(ct);
        }

        if (total == 0)
        {
            File.Delete(path); // port probe / empty job
            _log.Debug("capture.empty", "Spooler opened the port without sending data");
            return;
        }

        var docName = string.IsNullOrWhiteSpace(job?.Document) ? "Untitled print job" : job!.Document!;
        string? problem = null;
        int? pages = job?.TotalPages > 0 ? (int)job.TotalPages : null;
        if (overflow) problem = $"Job is larger than the {_maxBytes / (1024 * 1024)} MB limit";
        else
        {
            var inspection = PdfInspector.Inspect(path);
            if (!inspection.IsPdf) problem = $"Queue produced non-PDF data ({inspection.Problem}). Check that the queue uses the '{NexoraInfo.SourceDriverName}' driver.";
            pages = inspection.PageCount ?? pages;
        }

        _log.Info("capture.job", $"Captured '{docName}' ({total:N0} bytes{(pages is null ? "" : $", {pages} page(s)")})",
            new { spoolerJob = job?.JobId, user = job?.UserName, bytes = total, problem });
        await _onJob(new CapturedJob
        {
            FilePath = path,
            SizeBytes = overflow ? 0 : total,
            DocumentName = docName,
            UserName = job?.UserName,
            SpoolerJobId = job?.JobId,
            PageCount = pages,
            Settings = job?.Settings ?? new PrintSettings(),
            Problem = problem,
        });
    }

    /// <summary>Jobs that errored because the client was not running (port could not be opened) are restarted.</summary>
    private void RestartStalledSpoolerJobs()
    {
        try
        {
            foreach (var j in Winspool.EnumerateJobs(_queueName).Where(j => j.HasError))
            {
                Winspool.ControlJob(_queueName, j.JobId, Winspool.JobControlRestart);
                _log.Info("capture.restart", $"Restarted stalled spooler job {j.JobId} '{j.Document}'");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Queue not installed yet; nothing to restart.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_listeners); } catch { }
        IsListening = false;
        _cts.Dispose();
    }
}
