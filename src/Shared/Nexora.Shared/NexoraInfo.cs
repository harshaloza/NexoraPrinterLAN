namespace Nexora.Shared;

/// <summary>Product-wide constants. Runtime values (ports, names) are defaults and can be overridden in config.</summary>
public static class NexoraInfo
{
    public const string ProductName = "Nexora Printer LAN";
    public const string PrinterQueueName = "Nexora Printer LAN";
    public const string PipeName = "NexoraPrinterLAN";
    public const string PortName = @"\\.\pipe\" + PipeName;
    /// <summary>Inbox fallback driver (jobs travel as PDF).</summary>
    public const string SourceDriverName = "Microsoft Print To PDF";
    /// <summary>Nexora's own v4 driver (jobs travel as XPS), installed by Setup when possible.</summary>
    public const string NexoraDriverName = "Nexora Printer LAN";

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
