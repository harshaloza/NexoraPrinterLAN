using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nexora.Shared;
using Nexora.Shared.Security;
using Nexora.Transport;

namespace Nexora.Discovery;

/// <summary>UDP discovery request, broadcast by clients. See docs/NETWORK_PROTOCOL.md §5.</summary>
public sealed record DiscoveryRequest(string Magic, int V, string Nonce, string ClientId)
{
    public const string ExpectedMagic = "NXDISC2";
    public const int MaxBytes = 512;
}

/// <summary>Body of a discovery reply. Signed with the receiver's pairing key so clients can tell trusted receivers apart.</summary>
public sealed record ReceiverAnnouncement
{
    public const string ProtocolTag = "NexoraDiscovery/2";
    public required string Protocol { get; init; }
    public required int Version { get; init; }
    public required string ReceiverId { get; init; }
    public required string ServerName { get; init; }
    public required int TcpPort { get; init; }
    public required string AppVersion { get; init; }
    public required bool TestMode { get; init; }
    /// <summary>"ready", "no-printer", "printer-offline" or "busy" — computed from real spooler state.</summary>
    public required string Status { get; init; }
    public string? DestinationPrinter { get; init; }
    public bool DestinationPrinterReady { get; init; }
    public required string Nonce { get; init; }
}

/// <summary>A receiver found on the network, with the address the reply actually came from.</summary>
public sealed record DiscoveredReceiver(ReceiverAnnouncement Announcement, IPAddress Address, bool Trusted, DateTimeOffset SeenUtc)
{
    public IPEndPoint TransportEndpoint => new(Address, Announcement.TcpPort);
    public bool IsCompatible => Announcement.Version >= NexoraInfo.MinSupportedProtocolVersion;
}

public static class DiscoveryCodec
{
    public static byte[] EncodeReply(ReceiverAnnouncement body, PairingKey key)
    {
        var bodyBytes = Json.ToBytes(body);
        var tag = Tag(key, body.Nonce, bodyBytes);
        // {"body":<raw json>,"tag":"..."} — the raw body bytes are what the tag covers.
        var prefix = "{\"body\":"u8.ToArray();
        var suffix = Encoding.UTF8.GetBytes($",\"tag\":\"{Convert.ToBase64String(tag)}\"}}");
        return [.. prefix, .. bodyBytes, .. suffix];
    }

    /// <summary>Parses a reply. <paramref name="trusted"/> is true only if the tag verifies under <paramref name="key"/>.</summary>
    public static ReceiverAnnouncement? DecodeReply(ReadOnlySpan<byte> data, string expectedNonce, PairingKey? key, out bool trusted)
    {
        trusted = false;
        if (data.Length > 8192) return null;
        try
        {
            using var doc = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (!doc.RootElement.TryGetProperty("body", out var bodyEl) || bodyEl.ValueKind != JsonValueKind.Object) return null;
            var rawBody = Encoding.UTF8.GetBytes(bodyEl.GetRawText());
            var body = Json.Parse<ReceiverAnnouncement>(rawBody);
            if (body.Protocol != ReceiverAnnouncement.ProtocolTag || body.Nonce != expectedNonce) return null;
            if (body.TcpPort is < 1 or > 65535) return null;
            if (key is not null && doc.RootElement.TryGetProperty("tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
            {
                var tag = Convert.FromBase64String(tagEl.GetString()!);
                trusted = CryptographicOperations.FixedTimeEquals(tag, Tag(key, body.Nonce, rawBody));
            }
            return body;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static byte[] Tag(PairingKey key, string nonce, byte[] body) =>
        HMACSHA256.HashData(KeySchedule.DiscoveryKey(key), (byte[])[.. Encoding.ASCII.GetBytes(nonce), .. body]);
}
