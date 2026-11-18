using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Nexora.Shared;
using Nexora.Shared.Security;

namespace Nexora.Discovery;

/// <summary>Client side: broadcasts a discovery request on every LAN interface and collects receiver replies.</summary>
public static class DiscoveryClient
{
    public static async Task<IReadOnlyList<DiscoveredReceiver>> DiscoverAsync(int port, PairingKey? key, string clientId,
        TimeSpan duration, CancellationToken ct, IEnumerable<IPAddress>? extraTargets = null)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = Json.ToBytes(new DiscoveryRequest(DiscoveryRequest.ExpectedMagic, NexoraInfo.ProtocolVersion, nonce, clientId));

        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        // Ignore WSAECONNRESET from ICMP port-unreachable on the unicast targets.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch { }

        var targets = new HashSet<IPAddress>(BroadcastAddresses()) { IPAddress.Broadcast };
        foreach (var t in extraTargets ?? []) targets.Add(t);

        var found = new Dictionary<string, DiscoveredReceiver>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);

        // Send twice (UDP is lossy on Wi-Fi), half a second apart.
        _ = Task.Run(async () =>
        {
            for (int round = 0; round < 2 && !cts.IsCancellationRequested; round++)
            {
                foreach (var t in targets)
                {
                    try { await udp.SendAsync(request, new IPEndPoint(t, port), cts.Token); } catch { }
                }
                try { await Task.Delay(500, cts.Token); } catch { }
            }
        }, cts.Token);

        while (!cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            var body = DiscoveryCodec.DecodeReply(r.Buffer, nonce, key, out bool trusted);
            if (body is null) continue;
            var addr = r.RemoteEndPoint.Address.IsIPv4MappedToIPv6 ? r.RemoteEndPoint.Address.MapToIPv4() : r.RemoteEndPoint.Address;
            found[body.ReceiverId] = new DiscoveredReceiver(body, addr, trusted, DateTimeOffset.UtcNow);
        }
        return found.Values.OrderByDescending(d => d.Trusted).ThenBy(d => d.Announcement.ServerName).ToList();
    }

    /// <summary>Directed broadcast address of each up IPv4 interface (more reliable than 255.255.255.255 on multi-NIC PCs).</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.PrefixLength is <= 0 or >= 31) continue;
                yield return DirectedBroadcast(ua.Address, ua.PrefixLength);
            }
        }
    }

    /// <summary>Directed broadcast address of an IPv4 address/prefix, e.g. 192.168.1.14/24 → 192.168.1.255.</summary>
    public static IPAddress DirectedBroadcast(IPAddress address, int prefixLength)
    {
        // Network byte order is big-endian; explicit conversion avoids the Enumerable/Span Reverse ambiguity of C# 14.
        uint ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var bytes = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, ip | ~mask);
        return new IPAddress(bytes);
    }

    /// <summary>
    /// Auto-selection rule: exactly one trusted, compatible receiver → choose it. Otherwise prefer the last receiver
    /// by id if it is trusted; else null (the user picks manually).
    /// </summary>
    public static DiscoveredReceiver? AutoSelect(IReadOnlyList<DiscoveredReceiver> found, string? lastReceiverId)
    {
        var trusted = found.Where(f => f.Trusted && f.IsCompatible).ToList();
        if (trusted.Count == 1) return trusted[0];
        return trusted.FirstOrDefault(f => f.Announcement.ReceiverId == lastReceiverId);
    }
}
