using Nexora.PrinterIntegration;
using Nexora.Shared;
using Nexora.Shared.Security;

namespace Nexora.Receiver.Core;

/// <summary>Receiver settings, stored as JSON in %LOCALAPPDATA%\Nexora\Receiver\config.json.</summary>
public sealed class ReceiverConfig
{
    /// <summary>Version of the settings layout; <see cref="Upgrade"/> brings older files up to <see cref="CurrentSettingsVersion"/>.</summary>
    public const int CurrentSettingsVersion = 2;
    /// <summary>0 = written by Nexora 1.2.0 or earlier (the field did not exist).</summary>
    public int SettingsVersion { get; set; }

    public string ReceiverId { get; set; } = Guid.NewGuid().ToString("N");
    public string ReceiverName { get; set; } = Environment.MachineName;
    public int DiscoveryPort { get; set; } = NexoraInfo.DefaultDiscoveryPort;
    public int TransportPort { get; set; } = NexoraInfo.DefaultTransportPort;
    public bool DiscoveryEnabled { get; set; } = true;
    /// <summary>Local address to listen on. 0.0.0.0 = all interfaces (filtered by AllowedClientNetworks); 127.0.0.1 for single-PC tests.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Test mode saves jobs only. It is the default until a destination printer is chosen and test mode is turned off.</summary>
    public bool TestMode { get; set; } = true;
    public string? DestinationPrinter { get; set; }
    /// <summary>Default Auto: true scale (100%) when the page fits on the paper, shrink only pages that are larger.</summary>
    public PrintScaling Scaling { get; set; } = PrintScaling.Auto;
    /// <summary>Size calibration per destination printer name (e.g. a laser printer that prints 0.8% short along the feed).</summary>
    public Dictionary<string, PrintCalibration> Calibrations { get; set; } = [];

    /// <summary>The calibration for a printer, or null when none is set.</summary>
    public PrintCalibration? CalibrationFor(string? printer)
    {
        if (printer is null) return null;
        var hit = Calibrations.FirstOrDefault(kv => string.Equals(kv.Key, printer, StringComparison.OrdinalIgnoreCase)).Value;
        return hit is null || hit.IsNone ? null : hit;
    }

    /// <summary>Sets (or with null / 100% × 100%, removes) a printer's calibration.</summary>
    public void SetCalibration(string printer, PrintCalibration? calibration)
    {
        foreach (var key in Calibrations.Keys.Where(k => string.Equals(k, printer, StringComparison.OrdinalIgnoreCase)).ToList())
            Calibrations.Remove(key);
        if (calibration is not null && !calibration.IsNone)
        {
            calibration.Validate();
            Calibrations[printer] = calibration;
        }
    }
    /// <summary>Keep the received PDF after it was printed. Off by default: the PDF is only the transport format.</summary>
    public bool KeepPrintedCopies { get; set; }
    /// <summary>XPS jobs (Nexora driver): vector through the destination driver, falling back to images if that fails.</summary>
    public Nexora.PrinterIntegration.XpsPrintMethod XpsPrintMethod { get; set; } = Nexora.PrinterIntegration.XpsPrintMethod.Auto;
    /// <summary>Upper bound on rendering resolution. The printer's own resolution is used when lower.</summary>
    public int MaxRenderDpi { get; set; } = 600;

    /// <summary>CIDR networks or single addresses allowed to connect. Empty = this PC's own private subnets.</summary>
    public List<string> AllowedClientNetworks { get; set; } = [];
    public bool AllowLoopback { get; set; }
    public long MaxJobBytes { get; set; } = 512L * 1024 * 1024;
    public int MaxConcurrentConnections { get; set; } = 8;
    public int TransferTimeoutSeconds { get; set; } = 1800;
    public int IdleTimeoutSeconds { get; set; } = 60;

    public int JobRetentionDays { get; set; } = 30;
    /// <summary>Folder for received documents. Null = %LOCALAPPDATA%\Nexora\Receiver\received_jobs.</summary>
    public string? ReceivedJobsFolder { get; set; }

    public bool StartWithWindows { get; set; }
    public string LogLevel { get; set; } = "Info";

    /// <summary>Pairing code, DPAPI-protected (never stored in plain text).</summary>
    public string? ProtectedPairingCode { get; set; }

    public PairingKey GetOrCreatePairingKey(out bool created)
    {
        created = false;
        var plain = SecretProtector.TryUnprotect(ProtectedPairingCode);
        if (plain is not null && PairingKey.TryParse(plain, out var k)) return k!;
        var key = PairingKey.Generate();
        ProtectedPairingCode = SecretProtector.Protect(key.Code);
        created = true;
        return key;
    }

    public void SetPairingKey(PairingKey key) => ProtectedPairingCode = SecretProtector.Protect(key.Code);

    /// <summary>
    /// Brings settings written by an older version up to date. Returns true when something changed (the caller saves).
    /// Version 2: Nexora 1.2.0 and earlier always stored the then-default "fit to printable area", which shrank full-size
    /// pages (e.g. CAD plots at 1:1) by a few percent; those move to the new default Auto. "Actual size" is kept.
    /// </summary>
    public bool Upgrade()
    {
        if (SettingsVersion >= CurrentSettingsVersion) return false;
        if (SettingsVersion < 2 && Scaling == PrintScaling.FitToPrintableArea) Scaling = PrintScaling.Auto;
        SettingsVersion = CurrentSettingsVersion;
        return true;
    }

    public void Validate()
    {
        if (!Enum.IsDefined(Scaling)) throw new InvalidOperationException($"Unknown print scaling '{Scaling}'");
        Calibrations ??= [];
        foreach (var c in Calibrations.Values) c.Validate();
        if (DiscoveryPort is < 1 or > 65535 || TransportPort is < 1 or > 65535) throw new InvalidOperationException("Ports must be 1-65535");
        if (DiscoveryPort == TransportPort) throw new InvalidOperationException("Discovery and transport ports must differ");
        if (MaxJobBytes is < 1024 or > 4L * 1024 * 1024 * 1024) throw new InvalidOperationException("Max job size must be between 1 KB and 4 GB");
        foreach (var n in AllowedClientNetworks) Ipv4Network.Parse(n);
        if (!System.Net.IPAddress.TryParse(BindAddress, out _)) throw new InvalidOperationException($"Invalid bind address '{BindAddress}'");
        if (MaxRenderDpi is < 72 or > 1200) throw new InvalidOperationException("Max render DPI must be 72-1200");
    }
}
