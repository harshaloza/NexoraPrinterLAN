using Nexora.Discovery;
using Nexora.PrinterIntegration;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.Shared.Logging;
using Nexora.Shared.Security;
using Nexora.Transport;

namespace Nexora.Receiver.Core;

/// <summary>Composition root for the receiver: config, job store, TCP server, discovery responder and print queue.</summary>
public sealed class ReceiverHost : IAsyncDisposable
{
    private readonly ConfigStore<ReceiverConfig> _configStore;
    private ReceiverConfig _cfg;
    private PairingKey _key;
    private ReceiverServer? _server;
    private DiscoveryResponder? _discovery;
    private PrintQueue? _print;
    private Timer? _purgeTimer;

    public NexoraPaths Paths { get; }
    public Log Log { get; }
    public JobStore Store { get; }
    public bool IsRunning { get; private set; }
    public string? StartupError { get; private set; }
    public bool PairingCodeWasCreated { get; }

    public event Action? StatusChanged;

    public ReceiverHost(NexoraPaths paths, Log? log = null)
    {
        Paths = paths;
        _configStore = new ConfigStore<ReceiverConfig>(paths.ConfigFile);
        _cfg = _configStore.Load();
        _cfg.Validate();
        _key = _cfg.GetOrCreatePairingKey(out bool created);
        PairingCodeWasCreated = created;
        _configStore.Save(_cfg);
        Log = log ?? new Log(paths.Logs, "receiver", Enum.TryParse<LogLevel>(_cfg.LogLevel, true, out var lvl) ? lvl : LogLevel.Info);
        Store = new JobStore(paths.Root, _cfg.ReceivedJobsFolder);
        Store.Changed += _ => StatusChanged?.Invoke();
    }

    public ReceiverConfig Config => _cfg;
    public PairingKey PairingKey => _key;
    public int TransportPort => _server?.Port ?? _cfg.TransportPort;
    public int DiscoveryPort => _discovery?.Port ?? _cfg.DiscoveryPort;
    public int ActiveConnections => _server?.ActiveConnections ?? 0;

    public void Start()
    {
        if (IsRunning) return;
        var log = Log.For("receiver");
        StartupError = null;
        try
        {
            var filter = new AddressFilter(_cfg.AllowedClientNetworks, _cfg.AllowLoopback);
            _print = new PrintQueue(Store, () => _cfg, Log.For("print"));
            _server = new ReceiverServer(_cfg, () => _key, Store, filter, BuildInfo, _print.OnValidated, _print.Cancel, Log.For("server"));
            _server.Start();
            if (_cfg.DiscoveryEnabled)
            {
                _discovery = new DiscoveryResponder(_cfg.DiscoveryPort, () => _key, BuildAnnouncement, filter, Log.For("discovery"), System.Net.IPAddress.Parse(_cfg.BindAddress));
                _discovery.Start();
            }
            _purgeTimer = new Timer(_ => Purge(), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));
            IsRunning = true;
            log.Info("receiver.start", $"{NexoraInfo.ProductName} Receiver {NexoraInfo.AppVersion} started as '{_cfg.ReceiverName}'",
                new { testMode = _cfg.TestMode, printer = _cfg.DestinationPrinter, tcp = TransportPort, udp = DiscoveryPort, keyFingerprint = _key.Fingerprint });
        }
        catch (Exception e)
        {
            StartupError = e.Message;
            log.Error("receiver.start", $"Receiver failed to start: {e.Message}", ex: e);
            StopAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
        finally
        {
            StatusChanged?.Invoke();
        }
    }

    public async ValueTask StopAsync()
    {
        _purgeTimer?.Dispose();
        if (_discovery is not null) await _discovery.DisposeAsync();
        if (_server is not null) await _server.DisposeAsync();
        if (_print is not null) await _print.DisposeAsync();
        _discovery = null;
        _server = null;
        _print = null;
        if (IsRunning) Log.For("receiver").Info("receiver.stop", "Receiver stopped");
        IsRunning = false;
        StatusChanged?.Invoke();
    }

    /// <summary>Applies new settings; restarts listeners when network settings changed.</summary>
    public async Task UpdateConfigAsync(Action<ReceiverConfig> change)
    {
        var copy = Json.Parse<ReceiverConfig>(Json.ToText(_cfg));
        change(copy);
        copy.Validate();
        bool restart = copy.TransportPort != _cfg.TransportPort || copy.DiscoveryPort != _cfg.DiscoveryPort
            || copy.DiscoveryEnabled != _cfg.DiscoveryEnabled || copy.BindAddress != _cfg.BindAddress || !copy.AllowedClientNetworks.SequenceEqual(_cfg.AllowedClientNetworks)
            || copy.AllowLoopback != _cfg.AllowLoopback || copy.MaxConcurrentConnections != _cfg.MaxConcurrentConnections;
        _configStore.Save(copy);
        _cfg = copy;
        _key = _cfg.GetOrCreatePairingKey(out _); // pairing code may have been changed
        if (restart && IsRunning)
        {
            await StopAsync();
            Start();
        }
        StatusChanged?.Invoke();
    }

    public async Task<PairingKey> RegeneratePairingKeyAsync()
    {
        var k = PairingKey.Generate();
        await UpdateConfigAsync(c => c.SetPairingKey(k));
        Log.For("receiver").Warn("pairing.regenerated", "Pairing code regenerated; existing clients must be re-paired", new { keyFingerprint = k.Fingerprint });
        return k;
    }

    public void Reprint(Guid id) => _print?.Enqueue(id, explicitReprint: true);
    public CancelReplyMessage Cancel(Guid id) => _print?.Cancel(id) ?? new CancelReplyMessage(false, null, "Receiver is stopped");

    public ReceiverInfoMessage BuildInfo() => new()
    {
        ReceiverId = _cfg.ReceiverId,
        ReceiverName = _cfg.ReceiverName,
        Version = NexoraInfo.AppVersion,
        TestMode = _cfg.TestMode,
        DestinationPrinter = _cfg.DestinationPrinter,
        Printers = SafePrinters(),
        PendingJobs = _print?.Pending ?? 0,
    };

    private ReceiverAnnouncement BuildAnnouncement(string nonce)
    {
        var dest = _cfg.DestinationPrinter is null ? null : PrinterCatalog.Find(_cfg.DestinationPrinter);
        string status = _cfg.TestMode ? "ready"
            : dest is null ? "no-printer"
            : dest.IsReady ? "ready" : "printer-offline";
        return new ReceiverAnnouncement
        {
            Protocol = ReceiverAnnouncement.ProtocolTag,
            Version = NexoraInfo.ProtocolVersion,
            ReceiverId = _cfg.ReceiverId,
            ServerName = _cfg.ReceiverName,
            TcpPort = TransportPort,
            AppVersion = NexoraInfo.AppVersion,
            TestMode = _cfg.TestMode,
            Status = status,
            DestinationPrinter = _cfg.DestinationPrinter,
            DestinationPrinterReady = dest?.IsReady ?? false,
            Nonce = nonce,
        };
    }

    private IReadOnlyList<PrinterDescriptor> SafePrinters()
    {
        try { return PrinterCatalog.List(); }
        catch (Exception e)
        {
            Log.For("receiver").Warn("printers.enum", "Could not enumerate printers", ex: e);
            return [];
        }
    }

    private void Purge()
    {
        try
        {
            int n = Store.Purge(TimeSpan.FromDays(Math.Max(1, _cfg.JobRetentionDays)));
            if (n > 0) Log.For("receiver").Info("retention.purge", $"Removed {n} job(s) older than {_cfg.JobRetentionDays} days");
        }
        catch (Exception e)
        {
            Log.For("receiver").Warn("retention.purge", "Retention purge failed", ex: e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Log.Dispose();
    }
}
