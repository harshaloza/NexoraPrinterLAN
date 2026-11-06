namespace Nexora.Shared.Jobs;

/// <summary>Stable error codes used on the wire and in job records. See docs/NETWORK_PROTOCOL.md.</summary>
public static class ErrorCodes
{
    public const string ProtocolVersion = "PROTOCOL_VERSION";
    public const string BadRequest = "BAD_REQUEST";
    public const string AuthFailed = "AUTH_FAILED";
    public const string NotAllowed = "NOT_ALLOWED";
    public const string TooLarge = "TOO_LARGE";
    public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
    public const string ChecksumMismatch = "CHECKSUM_MISMATCH";
    public const string InvalidDocument = "INVALID_DOCUMENT";
    public const string StorageError = "STORAGE_ERROR";
    public const string Busy = "BUSY";
    public const string NotFound = "NOT_FOUND";
    public const string PrinterUnavailable = "PRINTER_UNAVAILABLE";
    public const string PrintFailed = "PRINT_FAILED";
    public const string Cancelled = "CANCELLED";
    public const string Timeout = "TIMEOUT";
    public const string Unreachable = "UNREACHABLE";
    public const string NoReceiver = "NO_RECEIVER";
    public const string Internal = "INTERNAL";

    /// <summary>Errors that will not succeed on retry with the same data and the same configuration.</summary>
    public static bool IsPermanent(string? code) => code is
        ProtocolVersion or BadRequest or TooLarge or UnsupportedFormat or InvalidDocument or Cancelled;
}
