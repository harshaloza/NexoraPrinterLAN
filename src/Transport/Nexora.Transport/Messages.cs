using Nexora.Shared.Jobs;

namespace Nexora.Transport;

// JSON bodies of NXP/2 frames. Field names are camelCase on the wire (see docs/NETWORK_PROTOCOL.md).

public sealed record HelloMessage(string Protocol, int Version, string ClientId, string ClientName, string Nonce);

public sealed record HelloReplyMessage(int Version, string ReceiverId, string ReceiverName, string Nonce, string Proof);

public sealed record AuthMessage(string Proof);

public sealed record AuthOkMessage(string ReceiverId, string ReceiverVersion, bool TestMode, long MaxJobBytes, int ChunkSize);

public enum OfferDecision { Accept, Duplicate, Reject }

public sealed record OfferReplyMessage
{
    public required OfferDecision Decision { get; init; }
    /// <summary>For Accept: byte offset to resume from (0 for a new transfer).</summary>
    public long ResumeOffset { get; init; }
    /// <summary>For Duplicate: the current state of the earlier, identical job.</summary>
    public JobStatus? Existing { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
}

public sealed record DataEndMessage(long TotalBytes);

public sealed record JobResultMessage(JobStatus Status);

public sealed record StatusQueryMessage(IReadOnlyList<Guid> JobIds);

public sealed record StatusReplyMessage(IReadOnlyList<JobStatus> Jobs);

public sealed record CancelMessage(Guid JobId);

public sealed record CancelReplyMessage(bool Cancelled, JobStatus? Status, string? Message);

public sealed record PrinterDescriptor
{
    public required string Name { get; init; }
    public string? DriverName { get; init; }
    public string? PortName { get; init; }
    public bool IsDefault { get; init; }
    /// <summary>True when Windows reports the queue as usable (not offline / paused / in error). Never guessed.</summary>
    public bool IsReady { get; init; }
    public string? StatusText { get; init; }
    public bool IsVirtual { get; init; }
}

public sealed record ReceiverInfoMessage
{
    public required string ReceiverId { get; init; }
    public required string ReceiverName { get; init; }
    public required string Version { get; init; }
    public required bool TestMode { get; init; }
    public string? DestinationPrinter { get; init; }
    public IReadOnlyList<PrinterDescriptor> Printers { get; init; } = [];
    public int PendingJobs { get; init; }
}

public sealed record ErrorMessage(string Code, string Message);
