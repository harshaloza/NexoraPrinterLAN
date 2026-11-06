namespace Nexora.Shared.Jobs;

/// <summary>
/// Lifecycle of a print job. Receipt states (RECEIVED, VALIDATED) are deliberately distinct from
/// printing states: bytes arriving does not mean anything was printed.
/// </summary>
public enum JobState
{
    Queued,
    Transferring,
    Received,
    Validated,
    SubmittedToPrinter,
    Printing,
    Completed,
    Failed,
    Cancelled,
}

public static class JobStateExtensions
{
    public static bool IsTerminal(this JobState s) =>
        s is JobState.Completed or JobState.Failed or JobState.Cancelled;

    /// <summary>The upper-case wire/document name, e.g. SUBMITTED_TO_PRINTER.</summary>
    public static string ToWire(this JobState s) => s switch
    {
        JobState.Queued => "QUEUED",
        JobState.Transferring => "TRANSFERRING",
        JobState.Received => "RECEIVED",
        JobState.Validated => "VALIDATED",
        JobState.SubmittedToPrinter => "SUBMITTED_TO_PRINTER",
        JobState.Printing => "PRINTING",
        JobState.Completed => "COMPLETED",
        JobState.Failed => "FAILED",
        JobState.Cancelled => "CANCELLED",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static JobState FromWire(string value) => value switch
    {
        "QUEUED" => JobState.Queued,
        "TRANSFERRING" => JobState.Transferring,
        "RECEIVED" => JobState.Received,
        "VALIDATED" => JobState.Validated,
        "SUBMITTED_TO_PRINTER" => JobState.SubmittedToPrinter,
        "PRINTING" => JobState.Printing,
        "COMPLETED" => JobState.Completed,
        "FAILED" => JobState.Failed,
        "CANCELLED" => JobState.Cancelled,
        _ => throw new FormatException($"Unknown job state '{value}'"),
    };

    public static string ToDisplay(this JobState s) => s switch
    {
        JobState.SubmittedToPrinter => "Submitted to printer",
        _ => s.ToString(),
    };
}
