namespace Nexora.Shared;

/// <summary>Product-wide constants. Runtime values (ports, names) are defaults and can be overridden in config.</summary>
public static class NexoraInfo
{
    public const string ProductName = "Nexora Printer LAN";
    public const string PrinterQueueName = "Nexora Printer LAN";
    public const string PipeName = "NexoraPrinterLAN";
    public const string PortName = @"\\.\pipe\" + PipeName;
    public const string SourceDriverName = "Microsoft Print To PDF";

    /// <summary>Wire protocol version for NXP (TCP job transport) and discovery.</summary>
    public const int ProtocolVersion = 2;
    public const int MinSupportedProtocolVersion = 2;

    public const int DefaultDiscoveryPort = 9101;
    public const int DefaultTransportPort = 9102;

    public const string PdfMediaType = "application/pdf";
    public const string XpsMediaType = "application/vnd.ms-xpsdocument";

    public static bool IsSupportedFormat(string? format) => format is PdfMediaType or XpsMediaType;

    public static string AppVersion =>
        typeof(NexoraInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
