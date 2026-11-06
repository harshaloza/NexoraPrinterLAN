using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Nexora.Shared.Security;

/// <summary>An IPv4 network in CIDR form, e.g. 192.168.0.0/24.</summary>
public readonly record struct Ipv4Network(uint Network, int PrefixLength)
{
    private uint Mask => PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);

    public bool Contains(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        return (ToUInt(address) & Mask) == Network;
    }

    public static Ipv4Network Parse(string cidr)
    {
        if (!TryParse(cidr, out var n)) throw new FormatException($"Invalid IPv4 network '{cidr}'. Use CIDR form like 192.168.0.0/24.");
        return n;
    }

    public static bool TryParse(string? cidr, out Ipv4Network network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(cidr)) return false;
        var parts = cidr.Trim().Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        int prefix = 32;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix is < 0 or > 32)) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        network = new Ipv4Network(ToUInt(ip) & mask, prefix);
        return true;
    }

    public static Ipv4Network FromAddress(IPAddress address, int prefixLength) =>
        Parse($"{address}/{prefixLength}");

    internal static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public override string ToString()
    {
        var n = Network;
        return $"{n >> 24}.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}/{PrefixLength}";
    }
}

/// <summary>
/// Decides whether a remote LAN peer may talk to us. Rules:
///  * Loopback is allowed only when explicitly enabled (used by tests and single-PC demos).
///  * If an explicit allow-list of networks/addresses is configured, the peer must match it.
///  * Otherwise the peer must be on one of this machine's directly-connected private IPv4 subnets.
/// Public (Internet-routable) addresses are always rejected, so the receiver is never usable from the Internet
/// even if a firewall rule is misconfigured.
/// </summary>
public sealed class AddressFilter
{
    private readonly IReadOnlyList<Ipv4Network> _allowed;
    private readonly bool _allowLoopback;
    private readonly Func<IReadOnlyList<Ipv4Network>> _localSubnets;

    public AddressFilter(IEnumerable<string>? allowedNetworks, bool allowLoopback, Func<IReadOnlyList<Ipv4Network>>? localSubnets = null)
    {
        _allowed = (allowedNetworks ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(Ipv4Network.Parse).ToList();
        _allowLoopback = allowLoopback;
        _localSubnets = localSubnets ?? GetLocalPrivateSubnets;
    }

    public bool IsAllowed(IPAddress remote, out string reason)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
        {
            reason = _allowLoopback ? "loopback allowed" : "loopback connections are disabled";
            return _allowLoopback;
        }
        if (remote.AddressFamily != AddressFamily.InterNetwork)
        {
            reason = "only IPv4 LAN peers are supported";
            return false;
        }
        if (!IsPrivate(remote))
        {
            reason = $"{remote} is not a private LAN address";
            return false;
        }
        if (_allowed.Count > 0)
        {
            var hit = _allowed.FirstOrDefault(n => n.Contains(remote));
            reason = hit == default ? $"{remote} is not in the allowed client list" : $"matches {hit}";
            return hit != default;
        }
        var subnet = _localSubnets().FirstOrDefault(n => n.Contains(remote));
        reason = subnet == default ? $"{remote} is not on a local subnet of this PC" : $"on local subnet {subnet}";
        return subnet != default;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Private IPv4 subnets of the up, non-loopback interfaces on this machine.</summary>
    public static IReadOnlyList<Ipv4Network> GetLocalPrivateSubnets()
    {
        var list = new List<Ipv4Network>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(ua.Address)) continue;
                int prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength : 24;
                list.Add(Ipv4Network.FromAddress(ua.Address, prefix));
            }
        }
        return list;
    }
}
