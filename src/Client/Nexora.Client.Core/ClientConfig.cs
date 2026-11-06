using System.Net;
using Nexora.Shared;
using Nexora.Shared.Security;

namespace Nexora.Client.Core;

/// <summary>Client settings, stored as JSON in %LOCALAPPDATA%\Nexora\Client\config.json.</summary>
public sealed class ClientConfig
{
    public string ClientId { get; set; } = Guid.NewGuid().ToString("N");
    public string ClientName { get; set; } = Environment.MachineName;

    public bool AutoDiscovery { get; set; } = true;
    public int DiscoveryPort { get; set; } = NexoraInfo.DefaultDiscoveryPort;
    public int DiscoveryIntervalSeconds { get; set; } = 60;
    /// <summary>Manual fallback: "192.168.0.31" or "192.168.0.31:9102" or a host name. Used when set.</summary>
    public string? ManualReceiverAddress { get; set; }
    public int DefaultTransportPort { get; set; } = NexoraInfo.DefaultTransportPort;

    /// <summary>Receiver chosen by the user or auto-selected; remembered across restarts.</summary>
    public string? SelectedReceiverId { get; set; }
    public string? SelectedReceiverName { get; set; }
    public string? LastReceiverAddress { get; set; }
    public int? LastReceiverPort { get; set; }

    /// <summary>Pairing code of the receiver, DPAPI-protected.</summary>
    public string? ProtectedPairingCode { get; set; }

    public string PipeName { get; set; } = NexoraInfo.PipeName;
    public string QueueName { get; set; } = NexoraInfo.PrinterQueueName;
    public long MaxJobBytes { get; set; } = 512L * 1024 * 1024;
    public int ConnectTimeoutSeconds { get; set; } = 5;
    /// <summary>Give up retrying an undeliverable job after this many hours (it stays visible and can be retried manually).</summary>
    public int RetryWindowHours { get; set; } = 24;
    public int StatusPollSeconds { get; set; } = 3;
    public int JobRetentionDays { get; set; } = 14;

    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public string LogLevel { get; set; } = "Info";

    public PairingKey? GetPairingKey()
    {
        var plain = SecretProtector.TryUnprotect(ProtectedPairingCode);
        return plain is not null && PairingKey.TryParse(plain, out var k) ? k : null;
    }

    public void SetPairingKey(PairingKey? key) => ProtectedPairingCode = key is null ? null : SecretProtector.Protect(key.Code);

    /// <summary>Parses <see cref="ManualReceiverAddress"/> into host and port.</summary>
    public (string Host, int Port)? ParseManualAddress()
    {
        var s = ManualReceiverAddress?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        int port = DefaultTransportPort;
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p) && p is > 0 and < 65536)
        {
            port = p;
            s = s[..colon];
        }
        return (s, port);
    }

    public void Validate()
    {
        if (DiscoveryPort is < 1 or > 65535 || DefaultTransportPort is < 1 or > 65535) throw new InvalidOperationException("Ports must be 1-65535");
        if (ParseManualAddress() is { } m && Uri.CheckHostName(m.Host) == UriHostNameType.Unknown)
            throw new InvalidOperationException($"'{ManualReceiverAddress}' is not a valid IP address or host name");
        if (MaxJobBytes is < 1024 or > 4L * 1024 * 1024 * 1024) throw new InvalidOperationException("Max job size must be 1 KB - 4 GB");
        if (ManualReceiverAddress is { Length: > 0 } && IPAddress.TryParse(ParseManualAddress()!.Value.Host, out var ip) && !AddressFilter.IsPrivate(ip) && !IPAddress.IsLoopback(ip))
            throw new InvalidOperationException("Manual receiver address must be a private LAN address (Nexora is LAN-only)");
    }
}
