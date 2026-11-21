using System.Threading.Channels;
using Nexora.PrinterIntegration;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Windows;
using Nexora.Transport;

namespace Nexora.Receiver.Core;

/// <summary>
/// Serializes physical printing of validated jobs. In test mode jobs stay VALIDATED (saved, not printed).
/// A job is submitted to Windows at most once automatically; reprints are an explicit user action.
/// </summary>
public sealed class PrintQueue : IAsyncDisposable
{
    public const string TestModeMessage = "Saved in test mode — not printed";
    private readonly JobStore _store;
    private readonly Func<ReceiverConfig> _cfg;
    private readonly DocumentPrinter _printer = new();
    private readonly LogScope _log;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private Guid? _current;
    private CancellationTokenSource? _currentCts;

    /// <summary>Integration-test seam: print through the driver into a file instead of the device. Never set by the product.</summary>
    internal static Func<Guid, string?>? TestPrintToFile { get; set; }

    public PrintQueue(JobStore store, Func<ReceiverConfig> cfg, LogScope log)
    {
        _store = store;
        _cfg = cfg;
        _log = log;
        _worker = Task.Run(() => WorkAsync(_cts.Token));
        Recover();
    }

    /// <summary>Called when a job was validated. Decides between test-mode save and printing.</summary>
    public void OnValidated(Guid id)
    {
        var cfg = _cfg();
        if (cfg.TestMode)
        {
            _store.Update(id, r => r with { Message = $"{TestModeMessage}. File: {r.StoredFile}" });
            return;
        }
        Enqueue(id, explicitReprint: false);
    }

    public void Enqueue(Guid id, bool explicitReprint)
    {
        _store.Update(id, r => r with
        {
            AwaitingPrint = true,
            State = explicitReprint ? JobState.Validated : r.State,
            ErrorCode = explicitReprint ? null : r.ErrorCode,
            Message = "Waiting for printer queue",
        });
        _queue.Writer.TryWrite(id);
    }

    private void Recover()
    {
        foreach (var r in _store.All())
        {
            if (r.AwaitingPrint && r.PrintSubmittedUtc is null && r.State == JobState.Validated)
            {
                _log.Info("print.recover", $"Re-queueing '{r.Metadata.DocumentName}' after restart", new { r.Metadata.JobId });
                _queue.Writer.TryWrite(r.Metadata.JobId);
            }
            else if (r.State is JobState.SubmittedToPrinter or JobState.Printing)
            {
                // We cannot know what happened to the spooler job while we were not running; say so instead of guessing.
                _store.Update(r.Metadata.JobId, x => x with { Message = "Receiver restarted while tracking this job; check the Windows print queue" });
            }
        }
    }

    public CancelReplyMessage Cancel(Guid id)
    {
        var r = _store.Get(id);
        if (r is null) return new CancelReplyMessage(false, null, "Unknown job");
        if (r.State.IsTerminal()) return new CancelReplyMessage(false, r.ToStatus(), $"Job is already {r.State.ToWire()}");
        if (_current == id) _currentCts?.Cancel();
        if (r.SpoolerJobId is int sj && r.PrinterName is not null)
            Winspool.ControlJob(r.PrinterName, (uint)sj, Winspool.JobControlDelete);
        var updated = _store.Update(id, x => x with { State = JobState.Cancelled, AwaitingPrint = false, ErrorCode = ErrorCodes.Cancelled, Message = "Cancelled by request" });
        _log.Info("print.cancel", $"Cancelled '{r.Metadata.DocumentName}'", new { jobId = id });
        return new CancelReplyMessage(true, updated.ToStatus(), "Cancelled");
    }

    public int Pending => _store.All().Count(r => r.AwaitingPrint);

    private async Task WorkAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            var r = _store.Get(id);
            if (r is null || !r.AwaitingPrint || r.State == JobState.Cancelled) continue;
            var cfg = _cfg();
            var printer = cfg.DestinationPrinter;
            if (string.IsNullOrWhiteSpace(printer))
            {
                _store.Update(id, x => x with { State = JobState.Failed, AwaitingPrint = false, ErrorCode = ErrorCodes.PrinterUnavailable, Message = "Saved but not printed: no destination printer selected on the receiver" });
                continue;
            }
            if (r.StoredFile is null || !File.Exists(r.StoredFile))
            {
                _store.Update(id, x => x with { State = JobState.Failed, AwaitingPrint = false, ErrorCode = ErrorCodes.NotFound, Message = "The document copy no longer exists on this PC (Keep copies of printed jobs is off); print it again from the client" });
                continue;
            }

            _current = id;
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                _store.Update(id, x => x with { PrinterName = printer, AwaitingPrint = false, PrintSubmittedUtc = DateTimeOffset.UtcNow, Message = $"Rendering for '{printer}'" });
                _log.Info("print.start", $"Printing '{r.Metadata.DocumentName}' on '{printer}'", new { jobId = id, printer });
                var outcome = await _printer.PrintAsync(new PrintRequest
                {
                    JobId = id,
                    DocumentPath = r.StoredFile!,
                    DocumentName = r.Metadata.DocumentName,
                    PrinterName = printer,
                    Settings = r.Metadata.Settings,
                    Scaling = cfg.Scaling,
                    Calibration = cfg.CalibrationFor(printer),
                    MaxRenderDpi = cfg.MaxRenderDpi,
                    XpsMethod = cfg.XpsPrintMethod,
                    PrintToFile = TestPrintToFile?.Invoke(id),
                },(state, msg) => _store.Update(id, x => x with { State = state, Message = msg }), _currentCts.Token);

                _store.Update(id, x => x with { State = outcome.State, ErrorCode = outcome.ErrorCode, Message = outcome.Message, SpoolerJobId = outcome.SpoolerJobId ?? x.SpoolerJobId });
                if (outcome.State == JobState.Completed && !_cfg().KeepPrintedCopies) DeletePrintedCopy(id);
                var lvl = outcome.State == JobState.Failed ? LogLevel.Error : LogLevel.Info;
                _log.Root.Write(lvl, "print", "print.result", $"'{r.Metadata.DocumentName}' → {outcome.State.ToWire()}: {outcome.Message}",
                    new Dictionary<string, object?> { ["jobId"] = id, ["printer"] = printer, ["pages"] = outcome.PagesSent });
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Cancel() already recorded the state.
            }
            catch (Exception e)
            {
                _log.Error("print.crash", $"Printing '{r.Metadata.DocumentName}' failed unexpectedly", new { jobId = id }, e);
                _store.Update(id, x => x with { State = JobState.Failed, ErrorCode = ErrorCodes.PrintFailed, Message = e.Message });
            }
            finally
            {
                _current = null;
                _currentCts.Dispose();
                _currentCts = null;
            }
        }
    }

    /// <summary>
    /// The PDF is only the transport format. Once Windows has finished sending a job to the printer the copy is
    /// removed, unless the user chose to keep copies. Test-mode jobs are never deleted here (that is their purpose).
    /// </summary>
    private void DeletePrintedCopy(Guid id)
    {
        var r = _store.Get(id);
        if (r?.StoredFile is not { } file) return;
        try
        {
            File.Delete(file);
            File.Delete(Path.ChangeExtension(file, ".json"));
            _store.Update(id, x => x with { StoredFile = null, Message = $"{x.Message}. Document copy removed (Keep copies of printed jobs is off)" });
        }
        catch (IOException e)
        {
            _log.Warn("print.cleanup", $"Could not remove the printed copy of '{r.Metadata.DocumentName}'", new { jobId = id }, e);
        }
        catch (UnauthorizedAccessException e)
        {
            _log.Warn("print.cleanup", $"Could not remove the printed copy of '{r.Metadata.DocumentName}'", new { jobId = id }, e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        _cts.Cancel();
        try { await _worker; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
