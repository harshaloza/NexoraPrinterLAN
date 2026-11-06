using System.Net;
using Nexora.Shared.IO;
using Nexora.Shared.Security;
using Xunit;

namespace Nexora.Tests.Unit;

public class PairingKeyTests
{
    [Fact]
    public void Generated_key_round_trips_through_display_form()
    {
        var k = PairingKey.Generate();
        Assert.Equal(24, k.Display.Length); // 20 chars + 4 dashes
        Assert.True(PairingKey.TryParse(k.Display, out var parsed));
        Assert.Equal(k, parsed);
    }

    [Fact]
    public void Parse_is_case_insensitive_and_accepts_crockford_aliases()
    {
        var k = PairingKey.Parse("7KQ2-M9XD-4TPA-ZR3W-HN60");
        Assert.Equal(k, PairingKey.Parse("7kq2 m9xd 4tpa zr3w hn6o")); // o → 0
    }

    [Theory]
    [InlineData("")]
    [InlineData("SHORT")]
    [InlineData("7KQ2-M9XD-4TPA-ZR3W-HN6U")] // U is not in Crockford base32
    [InlineData("7KQ2-M9XD-4TPA-ZR3W-HN6CC")]
    public void Invalid_codes_are_rejected(string code) => Assert.False(PairingKey.TryParse(code, out _));

    [Fact]
    public void Different_keys_have_different_fingerprints() =>
        Assert.NotEqual(PairingKey.Generate().Fingerprint, PairingKey.Generate().Fingerprint);
}

public class SecretProtectorTests
{
    [Fact]
    public void Protected_secret_is_not_plaintext_and_round_trips()
    {
        var blob = SecretProtector.Protect("7KQ2M9XD4TPAZR3WHN6C");
        Assert.DoesNotContain("7KQ2", blob);
        Assert.Equal("7KQ2M9XD4TPAZR3WHN6C", SecretProtector.TryUnprotect(blob));
    }

    [Fact]
    public void Garbage_blob_returns_null() => Assert.Null(SecretProtector.TryUnprotect("bm90IGEgZHBhcGkgYmxvYg=="));
}

public class AddressFilterTests
{
    private static IReadOnlyList<Ipv4Network> Lan() => [Ipv4Network.Parse("192.168.0.0/24")];

    [Theory]
    [InlineData("192.168.0.31", true)]
    [InlineData("192.168.0.140", true)]
    [InlineData("192.168.1.5", false)]   // private but another subnet
    [InlineData("8.8.8.8", false)]       // public
    [InlineData("127.0.0.1", false)]     // loopback disabled
    public void Default_policy_allows_only_local_subnet(string ip, bool expected)
    {
        var f = new AddressFilter(null, allowLoopback: false, localSubnets: Lan);
        Assert.Equal(expected, f.IsAllowed(IPAddress.Parse(ip), out _));
    }

    [Fact]
    public void Explicit_allow_list_overrides_local_subnets()
    {
        var f = new AddressFilter(["192.168.0.140/32"], allowLoopback: false, localSubnets: Lan);
        Assert.True(f.IsAllowed(IPAddress.Parse("192.168.0.140"), out _));
        Assert.False(f.IsAllowed(IPAddress.Parse("192.168.0.31"), out var reason));
        Assert.Contains("not in the allowed", reason);
    }

    [Fact]
    public void Public_addresses_are_rejected_even_if_listed()
    {
        var f = new AddressFilter(["0.0.0.0/0"], allowLoopback: false, localSubnets: Lan);
        Assert.False(f.IsAllowed(IPAddress.Parse("203.0.113.9"), out _));
    }

    [Fact]
    public void Loopback_and_mapped_ipv6()
    {
        var f = new AddressFilter(null, allowLoopback: true, localSubnets: Lan);
        Assert.True(f.IsAllowed(IPAddress.Loopback, out _));
        Assert.True(f.IsAllowed(IPAddress.Parse("192.168.0.31").MapToIPv6(), out _));
    }

    [Theory]
    [InlineData("192.168.0.0/24", "192.168.0.255", true)]
    [InlineData("192.168.0.7", "192.168.0.7", true)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    public void Cidr_contains(string cidr, string ip, bool expected) =>
        Assert.Equal(expected, Ipv4Network.Parse(cidr).Contains(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("192.168.0.0/33")]
    [InlineData("not-an-ip")]
    [InlineData("::1/128")]
    public void Bad_cidr_rejected(string cidr) => Assert.False(Ipv4Network.TryParse(cidr, out _));
}

public class SafeFileNameTests
{
    [Theory]
    [InlineData(@"..\..\Windows\System32\evil.pdf", "evil.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.pdf", "_nul.pdf")]
    [InlineData("a:b*c?d\"e<f>g|h", "a_b_c_d_e_f_g_h")]
    [InlineData("   ", "document")]
    [InlineData("report...", "report")]
    public void Sanitizes(string input, string expected) => Assert.Equal(expected, SafeFileName.Sanitize(input));

    [Fact]
    public void Long_names_are_truncated() => Assert.True(SafeFileName.Sanitize(new string('x', 500)).Length <= 80);

    [Theory]
    [InlineData("Drawing.dwg", "Drawing.pdf")]
    [InlineData("Microsoft Word - Report", "Microsoft Word - Report.pdf")]
    [InlineData("file.PDF", "file.PDF")]
    public void Forces_extension(string input, string expected) => Assert.Equal(expected, SafeFileName.WithExtension(input, ".pdf"));
}
