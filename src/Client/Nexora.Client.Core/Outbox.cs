using Nexora.Shared;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;

namespace Nexora.Client.Core;

/// <summary>Client-side record of one job (outbox/&lt;id&gt;.json).</summary>
public sealed record ClientJobRecord
{
    public required JobMetadata Metadata { get; init; }
    public required string FilePath { get; init; }
    public required JobState State { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? ReceiverId { get; init; }
    public string? ReceiverName { get; init; }
    public string? Destination { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? NextAttemptUtc { get; init; }
    public long BytesSent { get; init; }
    /// <summary>Receiver will not change the job further (e.g. saved in test mode): stop polling.</summary>
    public bool ReceiverFinal { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool AwaitingTransfer => State is JobState.Queued or JobState.Transferring;
    public bool AwaitingReceiverStatus => !AwaitingTransfer && !State.IsTerminal() && !ReceiverFinal;
}

/// <summary>Durable outbox: the captured PDF plus its record. Jobs survive client restarts and are resent with the same id.</summary>
public sealed class Outbox
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ClientJobRecord> _jobs = new();

    public event Action<ClientJobRecord>? Changed;

    public Outbox(string dir)
    {
        _dir = Directory.CreateDirectory(dir).FullName;
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var r = Json.Parse<ClientJobRecord>(File.ReadAllText(f));
                // A transfer that was in flight when the client stopped is queued again; the receiver resumes it.
                if (r.State == JobState.Transferring) r = r with { State = JobState.Queued, Message = "Resuming after restart", NextAttemptUtc = null };
                _jobs[r.Metadata.JobId] = r;
            }
            catch
            {
                File.Move(f, f + ".bad", overwrite: true);
            }
        }
    }

    public string Directory_ => _dir;

    public ClientJobRecord Add(JobMetadata meta, string sourceFile, string? message = null, JobState state = JobState.Queued, string? errorCode = null)
    {
        var target = Path.Combine(_dir, meta.JobId.ToString("N") + ".pdf");
        if (!sourceFile.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Move(sourceFile, target, overwrite: false);
        return Save(new ClientJobRecord { Metadata = meta, FilePath = target, State = state, Message = message ?? "Waiting to send", ErrorCode = errorCode });
    }

    public IReadOnlyList<ClientJobRecord> All()
    {
        lock (_gate) return _jobs.Values.OrderByDescending(j => j.Metadata.CreatedUtc).ToList();
    }

    public ClientJobRecord? Get(Guid id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    public ClientJobRecord Update(Guid id, Func<ClientJobRecord, ClientJobRecord> change)
    {
        ClientJobRecord r;
        lock (_gate) r = change(_jobs[id]) with { UpdatedUtc = DateTimeOffset.UtcNow };
        return Save(r);
    }

    private ClientJobRecord Save(ClientJobRecord r)
    {
        lock (_gate)
        {
            AtomicFile.WriteAllText(Path.Combine(_dir, r.Metadata.JobId.ToString("N") + ".json"), Json.ToText(r, indented: true));
            _jobs[r.Metadata.JobId] = r;
        }
        try { Changed?.Invoke(r); } catch { }
        return r;
    }

    public void Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_jobs.Remove(id, out var r)) return;
            try { File.Delete(r.FilePath); } catch { }
            try { File.Delete(Path.Combine(_dir, id.ToString("N") + ".json")); } catch { }
        }
    }

    public int Purge(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        var old = All().Where(j => (j.State.IsTerminal() || j.ReceiverFinal) && j.UpdatedUtc < cutoff).ToList();
        foreach (var j in old) Remove(j.Metadata.JobId);
        return old.Count;
    }
}
