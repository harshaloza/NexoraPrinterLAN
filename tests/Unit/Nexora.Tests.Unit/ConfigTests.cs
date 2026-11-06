using Nexora.Client.Core;
using Nexora.Shared.Config;
using Nexora.Shared.Security;
using Xunit;

namespace Nexora.Tests.Unit;

public class ClientConfigTests
{
    [Theory]
    [InlineData("192.168.0.31", "192.168.0.31", 9102)]
    [InlineData("192.168.0.31:9200", "192.168.0.31", 9200)]
    [InlineData("HarshalOza-PC", "HarshalOza-PC", 9102)]
    public void Manual_address_parsing(string input, string host, int port)
    {
        var c = new ClientConfig { ManualReceiverAddress = input };
        Assert.Equal((host, port), c.ParseManualAddress());
        c.Validate();
    }

    [Fact]
    public void Public_manual_address_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => new ClientConfig { ManualReceiverAddress = "8.8.8.8" }.Validate());

    [Fact]
    public void Pairing_key_is_stored_protected()
    {
        var c = new ClientConfig();
        var k = PairingKey.Generate();
        c.SetPairingKey(k);
        Assert.DoesNotContain(k.Code, c.ProtectedPairingCode);
        Assert.Equal(k, c.GetPairingKey());
    }

    [Fact]
    public void Config_store_recovers_from_corrupt_file()
    {
        var dir = Directory.CreateTempSubdirectory("nexora-cfg").FullName;
        var path = Path.Combine(dir, "config.json");
        File.WriteAllText(path, "{ this is not json");
        var cfg = new ConfigStore<ClientConfig>(path).Load();
        Assert.True(cfg.AutoDiscovery);
        Assert.True(File.Exists(path + ".bad"));
        Directory.Delete(dir, true);
    }
}
