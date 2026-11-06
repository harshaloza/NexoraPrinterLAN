using System.Net;
using System.Net.Sockets;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Pdf;
using Nexora.Shared.Security;
using Nexora.Transport;

namespace Nexora.Tests.Integration;

/// <summary>A receiver bound to 127.0.0.1 on free ports with its own temporary data directory.</summary>
public sealed class TestReceiver : IAsyncDisposable
{
    public string DataDir { get; }
    public ReceiverHost Host { get; private set; } = null!;
    public PairingKey Key => Host.PairingKey;
    public IPEndPoint Endpoint => new(IPAddress.Loopback, Host.TransportPort);
    private readonly bool _ownsDir;

    private TestReceiver(string dir, bool owns)
    {
        DataDir = dir;
        _ownsDir = owns;
    }

    public static async Task<TestReceiver> StartAsync(Action<ReceiverConfig>? configure = null, string? dataDir = null)
    {
        var r = new TestReceiver(dataDir ?? Directory.CreateTempSubdirectory("nexora-rx").FullName, dataDir is null);
        await r.StartHostAsync(configure);
        return r;
    }

    public async Task StartHostAsync(Action<ReceiverConfig>? configure = null)
    {
        Host = new ReceiverHost(new NexoraPaths("Receiver", DataDir), Log.Memory());
        int tcp = FreeTcpPort(), udp = FreeUdpPort();
        await Host.UpdateConfigAsync(c =>
        {
            c.BindAddress = "127.0.0.1";
            c.AllowLoopback = true;
            c.TransportPort = tcp;
            c.DiscoveryPort = udp;
            configure?.Invoke(c);
            // TCP and UDP port numbers are allocated independently and can coincide; the receiver requires them to differ.
            while (c.DiscoveryPort == c.TransportPort) c.DiscoveryPort = FreeUdpPort();
        });
        Host.Start();
    }

    /// <summary>Simulates a receiver restart: stop listeners, then build a new host over the same data directory.</summary>
    public async Task RestartAsync()
    {
        int tcp = Host.Config.TransportPort;
        await Host.DisposeAsync();
        Host = new ReceiverHost(new NexoraPaths("Receiver", DataDir), Log.Memory());
        await Host.UpdateConfigAsync(c => c.TransportPort = tcp);
        Host.Start();
    }

    public Task<ClientSession> ConnectAsync(PairingKey? key = null, string clientId = "itest-client") =>
        TransportClient.ConnectAsync(Endpoint, key ?? Key, new ClientIdentity(clientId, "Integration test"), TimeSpan.FromSeconds(5), default);

    public static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        if (_ownsDir) try { Directory.Delete(DataDir, true); } catch { }
    }
}

public static class Jobs
{
    public static (string Path, JobMetadata Meta) MakePdf(string clientId = "itest-client", int pages = 1, long padTo = 0, string name = "Test document.pdf")
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("nexora-job").FullName, "job.pdf");
        File.WriteAllBytes(path, TestPdfBuilder.Build("Integration", pages, padToBytes: padTo));
        return (path, Meta(path, clientId, name));
    }

    public static JobMetadata Meta(string path, string clientId = "itest-client", string name = "Test document.pdf") => new()
    {
        JobId = Guid.NewGuid(), DocumentName = name, Format = NexoraInfo.PdfMediaType, SizeBytes = new FileInfo(path).Length,
        Sha256 = Hashing.Sha256File(path), CreatedUtc = DateTimeOffset.UtcNow, ClientId = clientId, Settings = new PrintSettings { Copies = 1 },
    };
}
