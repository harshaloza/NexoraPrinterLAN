using System.Collections.ObjectModel;
using Nexora.Client.Core;
using Nexora.Discovery;
using Nexora.PrintPipeline;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Security;
using Nexora.Shared.Windows;
using Nexora.Transport;
using Nexora.UI;

namespace Nexora.App.Client;

public sealed class JobRow : ObservableObject
{
    public Guid Id { get; }
    public JobRow(Guid id) => Id = id;

    private string _name = "", _submitted = "", _size = "", _state = "", _destination = "", _details = "";
    private bool _canRetry, _canCancel;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Submitted { get => _submitted; set => Set(ref _submitted, value); }
    public string Size { get => _size; set => Set(ref _size, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public string Destination { get => _destination; set => Set(ref _destination, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public bool CanRetry { get => _canRetry; set => Set(ref _canRetry, value); }
    public bool CanCancel { get => _canCancel; set => Set(ref _canCancel, value); }
    public string? FilePath { get; set; }
}

public sealed record DeviceRow(DiscoveredReceiver Receiver)
{
    public string Name => Receiver.Announcement.ServerName;
    public string Address => $"{Receiver.Address}:{Receiver.Announcement.TcpPort}";
    public string Status => Receiver.Announcement.Status switch
    {
        "ready" => Receiver.Announcement.TestMode ? "Ready (test mode)" : "Ready",
        "printer-offline" => "Printer offline",
        "no-printer" => "No printer selected",
        var s => s,
    };
    public string Printer => Receiver.Announcement.TestMode ? "— (saves files only)" : Receiver.Announcement.DestinationPrinter ?? "—";
    public string Paired => Receiver.Trusted ? "Paired" : "Not paired";
    public string Version => Receiver.Announcement.AppVersion;
}

public sealed class ClientViewModel : ObservableObject
{
    private readonly ClientEngine _engine;
    private readonly Action<string, string, bool> _notify;

    public ClientViewModel(ClientEngine engine, Action<string, string, bool> notify)
    {
        _engine = engine;
        _notify = notify;
        SendTestPage = new AsyncCommand(async () => { await Task.Run(() => _engine.SubmitTestPage()); Navigate?.Invoke("jobs"); });
        CheckConnection = new AsyncCommand(CheckConnectionAsync);
        Discover = new AsyncCommand(async () => { await _engine.DiscoverNowAsync(); Refresh(); });
        UseSelectedDevice = new RelayCommand(UseDevice, () => SelectedDevice is not null);
        InstallQueue = new AsyncCommand(() => RunQueueScript("Install-NexoraQueue.ps1"));
        SetDefaultPrinter = new RelayCommand(() => { Winspool.SetDefaultPrinter(_engine.Config.QueueName); Refresh(); }, () => QueueInstalled && !QueueIsDefault);
        OpenPrinterSettings = new RelayCommand(() => Shell.Open("ms-settings:printers"));
        Retry = new RelayCommand(p => { if (p is JobRow r) { _engine.Retry(r.Id); Refresh(); } });
        Cancel = new AsyncCommand(async p => { if (p is JobRow r) Dialogs.Info(await _engine.CancelAsync(r.Id)); Refresh(); });
        OpenJobFile = new RelayCommand(p => { if (p is JobRow { FilePath: { } f } && File.Exists(f)) Shell.Open(f); });
        SaveSettings = new RelayCommand(DoSaveSettings);
        SavePairing = new RelayCommand(DoSavePairing);
        OpenLogs = new RelayCommand(() => Shell.Open(_engine.Paths.Logs));
        LoadSettings();
        Refresh();
    }

    // ------------------------------------------------------------ navigation
    /// <summary>Asks the shell to show another page (e.g. Print jobs after sending a test page).</summary>
    public Action<string>? Navigate { get; set; }

    // ------------------------------------------------------------ dashboard
    private bool? _captureOk, _queueOk, _receiverOk;
    private string _captureText = "", _queueText = "", _receiverText = "", _receiverName = "—", _receiverAddress = "—",
        _destinationPrinter = "—", _discoveryText = "", _lastError = "";
    private int _pending, _completed, _failed;
    public bool? CaptureOk { get => _captureOk; set => Set(ref _captureOk, value); }
    public string CaptureText { get => _captureText; set => Set(ref _captureText, value); }
    public bool? QueueOk { get => _queueOk; set => Set(ref _queueOk, value); }
    public string QueueText { get => _queueText; set => Set(ref _queueText, value); }
    public bool? ReceiverOk { get => _receiverOk; set => Set(ref _receiverOk, value); }
    public string ReceiverText { get => _receiverText; set => Set(ref _receiverText, value); }
    public string ReceiverName { get => _receiverName; set => Set(ref _receiverName, value); }
    public string ReceiverAddress { get => _receiverAddress; set => Set(ref _receiverAddress, value); }
    public string DestinationPrinter { get => _destinationPrinter; set => Set(ref _destinationPrinter, value); }
    public string DiscoveryText { get => _discoveryText; set => Set(ref _discoveryText, value); }
    public string LastError { get => _lastError; set => Set(ref _lastError, value); }
    public int Pending { get => _pending; set => Set(ref _pending, value); }
    public int Completed { get => _completed; set => Set(ref _completed, value); }
    public int Failed { get => _failed; set => Set(ref _failed, value); }

    // ------------------------------------------------------------ printers page
    private bool _queueInstalled, _queueIsDefault;
    private string _queuePort = "—", _queueDriver = "—";
    public bool QueueInstalled { get => _queueInstalled; set => Set(ref _queueInstalled, value); }
    public bool QueueIsDefault { get => _queueIsDefault; set => Set(ref _queueIsDefault, value); }
    public string QueuePort { get => _queuePort; set => Set(ref _queuePort, value); }
    public string QueueDriver { get => _queueDriver; set => Set(ref _queueDriver, value); }
    public string QueueName => _engine.Config.QueueName;

    // ------------------------------------------------------------ devices page
    public ObservableCollection<DeviceRow> Devices { get; } = [];
    private DeviceRow? _selectedDevice;
    public DeviceRow? SelectedDevice { get => _selectedDevice; set => Set(ref _selectedDevice, value); }
    public ObservableCollection<PrinterDescriptor> ReceiverPrinters { get; } = [];

    // ------------------------------------------------------------ jobs page
    public ObservableCollection<JobRow> Jobs { get; } = [];

    // ------------------------------------------------------------ settings page
    private bool _autoDiscovery, _notifications;
    private string _manualAddress = "", _discoveryPort = "", _transportPort = "", _retentionDays = "", _maxJobMb = "", _pairingInput = "", _pairingStatus = "";
    public bool AutoDiscovery { get => _autoDiscovery; set => Set(ref _autoDiscovery, value); }
    public bool Notifications { get => _notifications; set => Set(ref _notifications, value); }
    public string ManualAddress { get => _manualAddress; set => Set(ref _manualAddress, value); }
    public string DiscoveryPort { get => _discoveryPort; set => Set(ref _discoveryPort, value); }
    public string TransportPort { get => _transportPort; set => Set(ref _transportPort, value); }
    public string RetentionDays { get => _retentionDays; set => Set(ref _retentionDays, value); }
    public string MaxJobMb { get => _maxJobMb; set => Set(ref _maxJobMb, value); }
    public string PairingInput { get => _pairingInput; set => Set(ref _pairingInput, value); }
    public string PairingStatus { get => _pairingStatus; set => Set(ref _pairingStatus, value); }
    public string ClientName => _engine.Config.ClientName;
    public string Version => NexoraInfo.AppVersion;

    public AsyncCommand SendTestPage { get; }
    public AsyncCommand CheckConnection { get; }
    public AsyncCommand Discover { get; }
    public RelayCommand UseSelectedDevice { get; }
    public AsyncCommand InstallQueue { get; }
    public RelayCommand SetDefaultPrinter { get; }
    public RelayCommand OpenPrinterSettings { get; }
    public RelayCommand Retry { get; }
    public AsyncCommand Cancel { get; }
    public RelayCommand OpenJobFile { get; }
    public RelayCommand SaveSettings { get; }
    public RelayCommand SavePairing { get; }
    public RelayCommand OpenLogs { get; }

    private readonly Dictionary<Guid, JobState> _lastStates = new();
    private QueueStatus? _queue;
    private int _tick;

    /// <summary>Pulls a fresh snapshot from the engine. Everything shown comes from real state; unknown stays unknown.</summary>
    public void Refresh()
    {
        var cfg = _engine.Config;
        CaptureOk = _engine.CaptureListening;
        CaptureText = _engine.CaptureListening ? $"Listening on \\\\.\\pipe\\{cfg.PipeName}" : _engine.CaptureError ?? "Not running";

        // The spooler query is comparatively expensive; refresh it every few ticks.
        if (_queue is null || _tick++ % 4 == 0) _queue = SafeQueue();
        var q = _queue;
        QueueInstalled = q?.Exists ?? false;
        QueueOk = q?.Healthy;
        QueueText = q?.Summary ?? "Could not query the Windows spooler";
        QueuePort = q?.PortName ?? "—";
        QueueDriver = q?.DriverName ?? "—";
        QueueIsDefault = q?.IsDefault ?? false;

        var h = _engine.Health;
        ReceiverOk = h.Reachable;
        ReceiverName = h.Name ?? cfg.SelectedReceiverName ?? (cfg.ManualReceiverAddress is { Length: > 0 } ? "Manual address" : "Not selected");
        ReceiverAddress = h.Endpoint?.ToString() ?? (cfg.ManualReceiverAddress is { Length: > 0 } m ? $"{m} (manual)" :
            cfg.LastReceiverAddress is { } a ? $"{a}:{cfg.LastReceiverPort}" : "—");
        ReceiverText = h.Reachable switch
        {
            true => $"Connected {h.LastContactUtc?.ToLocalTime():HH:mm:ss}",
            false => h.Error ?? "Unreachable",
            null => cfg.GetPairingKey() is null ? "Enter the receiver's pairing code in Settings" : "Not contacted yet",
        };
        DestinationPrinter = h.Info is null ? "Unknown until connected"
            : h.Info.TestMode ? "Test mode — receiver saves files, does not print"
            : h.Info.DestinationPrinter is { } p ? $"{p} ({(h.Info.Printers.FirstOrDefault(x => x.Name == p)?.StatusText ?? "status unknown")})" : "None selected on receiver";
        LastError = h.Error ?? "";
        DiscoveryText = _engine.IsDiscovering ? "Searching…" :
            !cfg.AutoDiscovery ? "Automatic discovery is off" :
            _engine.LastDiscoveryUtc is null ? "Not run yet" :
            $"{_engine.Discovered.Count} receiver(s) found at {_engine.LastDiscoveryUtc.Value.ToLocalTime():HH:mm:ss}";

        SyncDevices();
        ReceiverPrinters.Clear();
        foreach (var printer in h.Info?.Printers ?? []) ReceiverPrinters.Add(printer);
        SyncJobs();
    }

    private static QueueStatus? SafeQueue()
    {
        try { return QueueInspector.Inspect(); } catch { return null; }
    }

    private void SyncDevices()
    {
        var selectedId = SelectedDevice?.Receiver.Announcement.ReceiverId;
        var current = _engine.Discovered;
        if (Devices.Select(d => d.Receiver).SequenceEqual(current)) return;
        Devices.Clear();
        foreach (var r in current) Devices.Add(new DeviceRow(r));
        SelectedDevice = Devices.FirstOrDefault(d => d.Receiver.Announcement.ReceiverId == selectedId);
    }

    private void SyncJobs()
    {
        var all = _engine.Outbox.All();
        Pending = all.Count(j => !j.State.IsTerminal() && !j.ReceiverFinal);
        Completed = all.Count(j => j.State == JobState.Completed || (j.ReceiverFinal && j.State == JobState.Validated));
        Failed = all.Count(j => j.State == JobState.Failed);

        var ids = all.Select(j => j.Metadata.JobId).ToHashSet();
        foreach (var gone in Jobs.Where(r => !ids.Contains(r.Id)).ToList()) Jobs.Remove(gone);
        for (int i = 0; i < all.Count; i++)
        {
            var j = all[i];
            var row = Jobs.FirstOrDefault(r => r.Id == j.Metadata.JobId);
            if (row is null)
            {
                row = new JobRow(j.Metadata.JobId);
                Jobs.Insert(Math.Min(i, Jobs.Count), row);
            }
            row.Name = j.Metadata.DocumentName;
            row.Submitted = j.Metadata.CreatedUtc.ToLocalTime().ToString("dd MMM HH:mm:ss");
            row.Size = Shell.FormatBytes(j.Metadata.SizeBytes) + (j.Metadata.PageCount is int pc ? $" · {pc} p" : "");
            row.State = j.ReceiverFinal && j.State == JobState.Validated ? "Validated" : j.State.ToDisplay();
            row.Destination = j.Destination ?? j.ReceiverName ?? "—";
            row.Details = (j.ErrorCode is null ? "" : $"[{j.ErrorCode}] ") + (j.Message ?? "");
            row.CanRetry = j.State is JobState.Failed or JobState.Cancelled || (j.State == JobState.Queued && j.Attempts > 0);
            row.CanCancel = !j.State.IsTerminal() && !j.ReceiverFinal;
            row.FilePath = j.FilePath;

            if (_lastStates.TryGetValue(j.Metadata.JobId, out var before) && before != j.State && _engine.Config.ShowNotifications)
            {
                if (j.State == JobState.Failed) _notify("Print job failed", $"{j.Metadata.DocumentName}: {j.Message}", true);
                else if (j.State == JobState.Completed) _notify("Printed", $"{j.Metadata.DocumentName} → {j.Destination}", false);
                else if (j.State == JobState.Validated && j.ReceiverFinal) _notify("Delivered", $"{j.Metadata.DocumentName} saved on {j.ReceiverName}", false);
            }
            _lastStates[j.Metadata.JobId] = j.State;
        }
    }

    private async Task CheckConnectionAsync()
    {
        var info = await _engine.CheckConnectionAsync();
        Refresh();
        Dialogs.Info($"Connected to {info.ReceiverName} (v{info.Version}).\n" +
            (info.TestMode ? "Receiver is in TEST MODE: jobs are saved, not printed." : $"Destination printer: {info.DestinationPrinter ?? "none selected"}"));
    }

    private void UseDevice()
    {
        if (SelectedDevice is null) return;
        if (!SelectedDevice.Receiver.Trusted && !Dialogs.Confirm($"'{SelectedDevice.Name}' is not paired with this PC. Enter its pairing code in Settings before printing. Select it anyway?"))
            return;
        _engine.SelectReceiver(SelectedDevice.Receiver);
        _engine.UpdateConfig(c => c.ManualReceiverAddress = null);
        LoadSettings();
        Refresh();
    }

    private async Task RunQueueScript(string script)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", script);
        // Setup installs the signed Nexora driver package next to the app; without it the inbox PDF driver is used.
        var driverDir = Path.Combine(AppContext.BaseDirectory, "driver");
        var args = Directory.Exists(driverDir) ? $"-NoPause -DriverDir \"{driverDir}\"" : "-NoPause";
        int code = await Task.Run(() => QueueInspector.RunElevatedScript(path, args));
        _queue = null;
        Refresh();
        if (code != 0) Dialogs.Error($"{script} exited with code {code}. See the PowerShell window output for details.");
    }

    private void LoadSettings()
    {
        var c = _engine.Config;
        AutoDiscovery = c.AutoDiscovery;
        Notifications = c.ShowNotifications;
        ManualAddress = c.ManualReceiverAddress ?? "";
        DiscoveryPort = c.DiscoveryPort.ToString();
        TransportPort = c.DefaultTransportPort.ToString();
        RetentionDays = c.JobRetentionDays.ToString();
        MaxJobMb = (c.MaxJobBytes / (1024 * 1024)).ToString();
        var k = c.GetPairingKey();
        PairingStatus = k is null ? "No pairing code saved" : $"Pairing code saved (fingerprint {k.Fingerprint})";
    }

    private void DoSaveSettings()
    {
        try
        {
            _engine.UpdateConfig(c =>
            {
                c.AutoDiscovery = AutoDiscovery;
                c.ShowNotifications = Notifications;
                c.ManualReceiverAddress = string.IsNullOrWhiteSpace(ManualAddress) ? null : ManualAddress.Trim();
                c.DiscoveryPort = int.Parse(DiscoveryPort);
                c.DefaultTransportPort = int.Parse(TransportPort);
                c.JobRetentionDays = Math.Max(1, int.Parse(RetentionDays));
                c.MaxJobBytes = Math.Max(1, long.Parse(MaxJobMb)) * 1024 * 1024;
            });
            LoadSettings();
            Dialogs.Info("Settings saved.");
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            Dialogs.Error(e.Message);
        }
    }

    private void DoSavePairing()
    {
        if (!PairingKey.TryParse(PairingInput, out var k))
        {
            Dialogs.Error("That is not a valid pairing code. It has 20 letters/digits, e.g. 7KQ2-M9XD-4TPA-ZR3W-HN6C (shown on the receiver).");
            return;
        }
        _engine.UpdateConfig(c => c.SetPairingKey(k));
        PairingInput = "";
        LoadSettings();
        _ = _engine.DiscoverNowAsync();
    }
}
