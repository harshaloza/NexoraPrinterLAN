using System.Net;
using System.Net.Sockets;
using Nexora.Shared;
using Nexora.Shared.Logging;
using Nexora.Shared.Security;

namespace Nexora.Discovery;

/// <summary>Receiver side: answers NXDISC2 broadcasts from allowed LAN clients.</summary>
public sealed class DiscoveryResponder : IAsyncDisposable
{
    private readonly int _port;
    private readonly IPAddress _bind;
    private readonly Func<PairingKey> _key;
    private readonly Func<string, ReceiverAnnouncement> _announce;
    private readonly AddressFilter _filter;
    private readonly LogScope _log;
    private UdpClient? _udp;
    private Task? _loop;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<IPAddress, (DateTime Window, int Count)> _rate = new();

    public DiscoveryResponder(int port, Func<PairingKey> key, Func<string, ReceiverAnnouncement> announce, AddressFilter filter, LogScope log, IPAddress? bind = null)
    {
        _bind = bind ?? IPAddress.Any;
        _port = port;
        _key = key;
        _announce = announce;
        _filter = filter;
        _log = log;
    }

    public int Port => ((IPEndPoint?)_udp?.Client.LocalEndPoint)?.Port ?? _port;

    public void Start()
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        try
        {
            sock.Bind(new IPEndPoint(_bind, _port));
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            sock.Dispose();
            throw new InvalidOperationException(
                $"UDP port {_port} is already in use. Stop any older Nexora receiver (e.g. the Phase 1 receiver.py) or change the discovery port.", e);
        }
        _udp = new UdpClient { Client = sock };
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info("discovery.listening", $"Discovery listening on UDP {Port}");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await _udp!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset) { continue; } // ICMP port unreachable from a prior reply
            catch (ObjectDisposedException) { break; }

            try
            {
                await HandleAsync(r, ct);
            }
            catch (Exception e)
            {
                _log.Warn("discovery.error", "Failed to answer discovery request", new { from = r.RemoteEndPoint.ToString() }, e);
            }
        }
    }

    private async Task HandleAsync(UdpReceiveResult r, CancellationToken ct)
    {
        if (r.Buffer.Length > DiscoveryRequest.MaxBytes) return;
        if (!_filter.IsAllowed(r.RemoteEndPoint.Address, out var reason))
        {
            _log.Debug("discovery.denied", $"Ignored discovery from {r.RemoteEndPoint.Address}: {reason}");
            return;
        }
        if (!RateOk(r.RemoteEndPoint.Address)) return;
        DiscoveryRequest req;
        try { req = Json.Parse<DiscoveryRequest>(r.Buffer); }
        catch { return; }
        if (req.Magic != DiscoveryRequest.ExpectedMagic || string.IsNullOrEmpty(req.Nonce) || req.Nonce.Length > 64) return;

        var body = _announce(req.Nonce);
        var reply = DiscoveryCodec.EncodeReply(body, _key());
        await _udp!.SendAsync(reply, r.RemoteEndPoint, ct);
        _log.Debug("discovery.replied", $"Answered discovery from {r.RemoteEndPoint}", new { client = req.ClientId });
    }

    private bool RateOk(IPAddress ip)
    {
        var now = DateTime.UtcNow;
        lock (_rate)
        {
            if (_rate.Count > 1024) _rate.Clear();
            if (!_rate.TryGetValue(ip, out var e) || now - e.Window > TimeSpan.FromSeconds(1)) e = (now, 0);
            e.Count++;
            _rate[ip] = e;
            return e.Count <= 20;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _udp?.Dispose();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _cts.Dispose();
    }
}
