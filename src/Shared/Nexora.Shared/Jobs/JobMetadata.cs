namespace Nexora.Shared.Jobs;

public enum PageOrientation { Unspecified, Portrait, Landscape }
public enum ColorMode { Unspecified, Color, Monochrome }

/// <summary>Job ticket captured from the Windows spooler (DEVMODE) on the client.</summary>
public sealed record PrintSettings
{
    /// <summary>Paper name as known to Windows, e.g. "A4", "A3", "Letter", or "Custom".</summary>
    public string? PaperSize { get; init; }
    /// <summary>Paper width/length in tenths of a millimetre when known (DEVMODE dmPaperWidth/dmPaperLength).</summary>
    public int? PaperWidthTenthMm { get; init; }
    public int? PaperLengthTenthMm { get; init; }
    public PageOrientation Orientation { get; init; } = PageOrientation.Unspecified;
    public ColorMode ColorMode { get; init; } = ColorMode.Unspecified;
    public int Copies { get; init; } = 1;
    public bool Collate { get; init; } = true;
    public int? ResolutionDpi { get; init; }
    public string? Duplex { get; init; }
}

/// <summary>Immutable description of a job; this is the JOB_OFFER body on the wire.</summary>
public sealed record JobMetadata
{
    public required Guid JobId { get; init; }
    public required string DocumentName { get; init; }
    public required string Format { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }
    public int? PageCount { get; init; }
    public PrintSettings Settings { get; init; } = new();
    public required DateTimeOffset CreatedUtc { get; init; }
    public required string ClientId { get; init; }
    public string? ClientName { get; init; }
    public string? SubmittedBy { get; init; }
}

/// <summary>Status of a job as reported by the receiver.</summary>
public sealed record JobStatus
{
    public required Guid JobId { get; init; }
    public required JobState State { get; init; }
    public string? ErrorCode { get; init; }
    public string? Message { get; init; }
    public string? PrinterName { get; init; }
    /// <summary>True when the receiver will not change this job any further on its own (e.g. saved in test mode).</summary>
    public bool IsFinal { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
