using Nexora.Shared;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Pdf;

namespace Nexora.Receiver.Core;

/// <summary>Durable receiver-side record of one job (jobs/&lt;jobId&gt;.json).</summary>
public sealed record ReceiverJobRecord
{
    public required JobMetadata Metadata { get; init; }
    public required JobState State { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public required string ClientAddress { get; init; }
    public string? StoredFile { get; init; }
    public string? PrinterName { get; init; }
    public int? SpoolerJobId { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; }
    public DateTimeOffset? PrintSubmittedUtc { get; init; }
    /// <summary>True while the job waits in the receiver's print queue (survives restarts; cleared on submission).</summary>
    public bool AwaitingPrint { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public List<StateChange> History { get; init; } = [];

    public JobStatus ToStatus() => new()
    {
        JobId = Metadata.JobId, State = State, ErrorCode = ErrorCode, Message = Message,
        PrinterName = PrinterName, UpdatedUtc = UpdatedUtc,
        IsFinal = State.IsTerminal() || (State == JobState.Validated && !AwaitingPrint && PrintSubmittedUtc is null),
    };
}

public sealed record StateChange(DateTimeOffset Utc, JobState State, string? Message);

/// <summary>Sidecar for a partial upload, used to decide whether a reconnecting client may resume.</summary>
internal sealed record PartialUpload(Guid JobId, string Sha256, long SizeBytes, string ClientId);

public enum BeginOutcome { Accept, Duplicate, Busy }

/// <summary>
/// Durable job storage for the receiver:
///   incoming/&lt;id&gt;.part (+ .offer.json)  bytes of a transfer in progress (resumable)
///   received_jobs/&lt;stamp&gt;_&lt;name&gt;_&lt;id8&gt;.pdf (+ .json)  validated documents the user can open
///   jobs/&lt;id&gt;.json  state record (duplicate detection, status queries, history)
/// A job is acknowledged as VALIDATED only after its bytes were flushed to disk, re-read and hashed.
/// </summary>
public sealed class JobStore
{
    private readonly string _incoming, _jobs, _received;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ReceiverJobRecord> _records = new();
    private readonly HashSet<Guid> _activeUploads = new();

    public event Action<ReceiverJobRecord>? Changed;

    public JobStore(string dataRoot, string? receivedFolder)
    {
        _incoming = Directory.CreateDirectory(Path.Combine(dataRoot, "incoming")).FullName;
        _jobs = Directory.CreateDirectory(Path.Combine(dataRoot, "jobs")).FullName;
        _received = Directory.CreateDirectory(receivedFolder ?? Path.Combine(dataRoot, "received_jobs")).FullName;
        LoadRecords();
    }

    public string ReceivedFolder => _received;

    private void LoadRecords()
    {
        foreach (var f in Directory.EnumerateFiles(_jobs, "*.json"))
        {
            try
            {
                var r = Json.Parse<ReceiverJobRecord>(File.ReadAllText(f));
                _records[r.Metadata.JobId] = r;
            }
            catch
            {
                File.Move(f, f + ".bad", overwrite: true);
            }
        }
    }

    public IReadOnlyList<ReceiverJobRecord> All()
    {
        lock (_gate) return _records.Values.OrderByDescending(r => r.ReceivedUtc).ToList();
    }

    public ReceiverJobRecord? Get(Guid id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    public int PendingCount()
    {
        lock (_gate) return _records.Values.Count(r => !r.State.IsTerminal() && r.State != JobState.Validated);
    }

    /// <summary>
    /// Starts (or resumes) an upload. Duplicate = a record for this job id already exists (it was fully received);
    /// the caller returns the existing status instead of accepting the bytes again.
    /// </summary>
    public BeginOutcome Begin(JobMetadata meta, out IncomingUpload? upload, out ReceiverJobRecord? existing)
    {
        upload = null;
        lock (_gate)
        {
            existing = _records.GetValueOrDefault(meta.JobId);
            if (existing is not null && existing.State != JobState.Failed) return BeginOutcome.Duplicate;
            if (existing is not null && existing.ErrorCode is not (ErrorCodes.ChecksumMismatch or ErrorCodes.StorageError))
                return BeginOutcome.Duplicate; // permanently failed jobs are not silently re-accepted
            if (!_activeUploads.Add(meta.JobId)) return BeginOutcome.Busy;
        }

        try
        {
            var part = Path.Combine(_incoming, meta.JobId.ToString("N") + ".part");
            var sidecar = part + ".offer.json";
            var expected = new PartialUpload(meta.JobId, meta.Sha256, meta.SizeBytes, meta.ClientId);
            long offset = 0;
            if (File.Exists(part) && File.Exists(sidecar))
            {
                PartialUpload? prior = null;
                try { prior = Json.Parse<PartialUpload>(File.ReadAllText(sidecar)); } catch { }
                var len = new FileInfo(part).Length;
                if (prior == expected && len <= meta.SizeBytes) offset = len;
            }
            if (offset == 0)
            {
                File.Delete(part);
                AtomicFile.WriteAllText(sidecar, Json.ToText(expected));
            }
            var fs = new FileStream(part, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16);
            fs.SetLength(offset);
            fs.Position = offset;
            upload = new IncomingUpload(this, meta, part, sidecar, fs, offset);
            return BeginOutcome.Accept;
        }
        catch
        {
            lock (_gate) _activeUploads.Remove(meta.JobId);
            throw;
        }
    }

    internal void EndUpload(Guid id)
    {
        lock (_gate) _activeUploads.Remove(id);
    }

    /// <summary>Verifies the finished upload from disk and moves it into received_jobs. Returns the new record.</summary>
    internal ReceiverJobRecord Finish(IncomingUpload up, string clientAddress)
    {
        var meta = up.Metadata;
        var now = DateTimeOffset.UtcNow;
        string actual = Hashing.Sha256File(up.PartPath);
        if (!actual.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(up.PartPath);
            File.Delete(up.SidecarPath);
            return Save(new ReceiverJobRecord
            {
                Metadata = meta, State = JobState.Failed, ErrorCode = ErrorCodes.ChecksumMismatch,
                Message = "SHA-256 of received bytes does not match the client's checksum; the client will resend",
                ClientAddress = clientAddress, ReceivedUtc = now, History = [new(now, JobState.Failed, "checksum mismatch")],
            });
        }

        var check = DocumentFormats.Detect(up.PartPath);
        var problem = check.Problem ?? (check.Format != meta.Format ? $"content is {check.Format ?? "unknown"}, but the job says {meta.Format}" : null);
        var history = new List<StateChange> { new(now, JobState.Received, $"{meta.SizeBytes} bytes, SHA-256 verified") };
        var stamp = now.ToLocalTime().ToString("yyyyMMdd_HHmmss");
        var baseName = $"{stamp}_{Path.GetFileNameWithoutExtension(SafeFileName.WithExtension(meta.DocumentName, check.Extension))}_{meta.JobId.ToString("N")[..8]}";
        string kind = meta.Format == NexoraInfo.XpsMediaType ? "XPS" : "PDF";
        if (problem is not null)
        {
            // Keep the rejected bytes out of received_jobs; nothing the user might open by mistake.
            var quarantine = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_jobs)!, "rejected")).FullName;
            var q = Path.Combine(quarantine, baseName + ".bin");
            File.Move(up.PartPath, q, overwrite: true);
            File.Delete(up.SidecarPath);
            history.Add(new(now, JobState.Failed, problem));
            return Save(new ReceiverJobRecord
            {
                Metadata = meta, State = JobState.Failed, ErrorCode = ErrorCodes.InvalidDocument,
                Message = $"Not a valid {kind}: {problem}", ClientAddress = clientAddress,
                ReceivedUtc = now, StoredFile = q, History = history,
            });
        }

        var target = Path.Combine(_received, baseName + check.Extension);
        File.Move(up.PartPath, target, overwrite: false);
        File.Delete(up.SidecarPath);
        AtomicFile.WriteAllText(Path.ChangeExtension(target, ".json"), Json.ToText(new
        {
            meta.JobId, meta.DocumentName, meta.Format, meta.SizeBytes, meta.Sha256,
            PageCount = meta.PageCount ?? check.PageCount, meta.Settings, meta.CreatedUtc,
            meta.ClientId, meta.ClientName, meta.SubmittedBy, ClientAddress = clientAddress, ReceivedUtc = now,
        }, indented: true));
        history.Add(new(now, JobState.Validated, $"{kind} structure valid; saved"));
        return Save(new ReceiverJobRecord
        {
            Metadata = meta with { PageCount = meta.PageCount ?? check.PageCount },
            State = JobState.Validated, Message = "Received and verified", ClientAddress = clientAddress,
            ReceivedUtc = now, StoredFile = target, History = history,
        });
    }

    public ReceiverJobRecord Update(Guid id, Func<ReceiverJobRecord, ReceiverJobRecord> change)
    {
        ReceiverJobRecord updated;
        lock (_gate)
        {
            var cur = _records.GetValueOrDefault(id) ?? throw new KeyNotFoundException(id.ToString());
            updated = change(cur);
            if (updated.State != cur.State || updated.Message != cur.Message)
                updated = updated with { History = [.. cur.History, new(DateTimeOffset.UtcNow, updated.State, updated.Message)] };
            updated = updated with { UpdatedUtc = DateTimeOffset.UtcNow };
        }
        return Save(updated);
    }

    public ReceiverJobRecord SetState(Guid id, JobState state, string? message, string? errorCode = null) =>
        Update(id, r => r with { State = state, Message = message, ErrorCode = errorCode });

    private ReceiverJobRecord Save(ReceiverJobRecord r)
    {
        lock (_gate)
        {
            AtomicFile.WriteAllText(Path.Combine(_jobs, r.Metadata.JobId.ToString("N") + ".json"), Json.ToText(r, indented: true));
            _records[r.Metadata.JobId] = r;
        }
        try { Changed?.Invoke(r); } catch { }
        return r;
    }

    /// <summary>Deletes terminal records and their documents older than the retention period, and stale partial uploads.</summary>
    public int Purge(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        int removed = 0;
        List<ReceiverJobRecord> old;
        lock (_gate) old = _records.Values.Where(r => (r.State.IsTerminal() || r.State == JobState.Validated) && r.UpdatedUtc < cutoff).ToList();
        foreach (var r in old)
        {
            try
            {
                if (r.StoredFile is not null)
                {
                    File.Delete(r.StoredFile);
                    File.Delete(Path.ChangeExtension(r.StoredFile, ".json"));
                }
                File.Delete(Path.Combine(_jobs, r.Metadata.JobId.ToString("N") + ".json"));
                lock (_gate) _records.Remove(r.Metadata.JobId);
                removed++;
            }
            catch { }
        }
        foreach (var p in Directory.EnumerateFiles(_incoming))
            if (File.GetLastWriteTimeUtc(p) < DateTime.UtcNow - TimeSpan.FromDays(2)) { try { File.Delete(p); } catch { } }
        return removed;
    }
}

/// <summary>An open, resumable upload. Dispose without Complete leaves the partial file for a later resume.</summary>
public sealed class IncomingUpload : IDisposable
{
    private readonly JobStore _store;
    private FileStream? _fs;

    internal IncomingUpload(JobStore store, JobMetadata meta, string part, string sidecar, FileStream fs, long offset)
    {
        _store = store;
        Metadata = meta;
        PartPath = part;
        SidecarPath = sidecar;
        _fs = fs;
        ResumeOffset = offset;
        Written = offset;
    }

    public JobMetadata Metadata { get; }
    public long ResumeOffset { get; }
    public long Written { get; private set; }
    internal string PartPath { get; }
    internal string SidecarPath { get; }

    public async Task WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        if (Written + chunk.Length > Metadata.SizeBytes)
            throw new Transport.ProtocolException(ErrorCodes.BadRequest, "Client sent more data than announced");
        await _fs!.WriteAsync(chunk, ct);
        Written += chunk.Length;
    }

    public ReceiverJobRecord Complete(string clientAddress)
    {
        if (Written != Metadata.SizeBytes)
            throw new Transport.ProtocolException(ErrorCodes.BadRequest, $"Transfer ended at {Written} of {Metadata.SizeBytes} bytes");
        _fs!.Flush(flushToDisk: true);
        _fs.Dispose();
        _fs = null;
        return _store.Finish(this, clientAddress);
    }

    public void Dispose()
    {
        try { _fs?.Flush(flushToDisk: true); } catch { }
        _fs?.Dispose();
        _fs = null;
        _store.EndUpload(Metadata.JobId);
    }
}
