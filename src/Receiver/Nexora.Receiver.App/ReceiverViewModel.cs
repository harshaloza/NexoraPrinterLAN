using System.Collections.ObjectModel;
using System.Windows;
using Nexora.PrinterIntegration;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Pdf;
using Nexora.Transport;
using Nexora.UI;

namespace Nexora.Receiver.App;

public sealed class ReceivedJobRow : ObservableObject
{
    public Guid Id { get; }
    public ReceivedJobRow(Guid id) => Id = id;
    private string _name = "", _client = "", _received = "", _size = "", _state = "", _printer = "", _details = "";
    private bool _canCancel;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Client { get => _client; set => Set(ref _client, value); }
    public string Received { get => _received; set => Set(ref _received, value); }
    public string Size { get => _size; set => Set(ref _size, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public string Printer { get => _printer; set => Set(ref _printer, value); }
    public string Details { get => _details; set => Set(ref _details, value); }
    public bool CanCancel { get => _canCancel; set => Set(ref _canCancel, value); }
    public string? File { get; set; }
}

public sealed class ReceiverViewModel : ObservableObject
{
    private readonly ReceiverHost _host;
    private readonly Action<string, string, bool> _notify;

    public ReceiverViewModel(ReceiverHost host, Action<string, string, bool> notify)
    {
        _host = host;
        _notify = notify;
        ToggleRunning = new AsyncCommand(async () =>
        {
            if (_host.IsRunning) await _host.StopAsync();
            else _host.Start();
            Refresh();
        });
        CopyPairing = new RelayCommand(() => { Clipboard.SetText(_host.PairingKey.Display); Dialogs.Info("Pairing code copied."); });
        ShowPairing = new RelayCommand(() => PairingVisible = !PairingVisible);
        Regenerate = new AsyncCommand(async () =>
        {
            if (!Dialogs.Confirm("Generate a new pairing code? Every client must then enter the new code before it can send jobs.")) return;
            await _host.RegeneratePairingKeyAsync();
            PairingVisible = true;
            Refresh();
        });
        OpenFolder = new RelayCommand(() => Shell.Open(_host.Store.ReceivedFolder));
        OpenLogs = new RelayCommand(() => Shell.Open(_host.Paths.Logs));
        OpenJob = new RelayCommand(p => { if (p is ReceivedJobRow { File: { } f } && System.IO.File.Exists(f)) Shell.Open(f); });
        RevealJob = new RelayCommand(p => { if (p is ReceivedJobRow { File: { } f } && System.IO.File.Exists(f)) Shell.Reveal(f); });
        Reprint = new RelayCommand(p =>
        {
            if (p is not ReceivedJobRow r) return;
            if (_host.Config.TestMode || _host.Config.DestinationPrinter is null)
            {
                Dialogs.Error("Turn off test mode and choose a destination printer on the Printers page first.");
                return;
            }
            if (Dialogs.Confirm($"Print '{r.Name}' on '{_host.Config.DestinationPrinter}'?")) _host.Reprint(r.Id);
        });
        CancelJob = new RelayCommand(p => { if (p is ReceivedJobRow r) Dialogs.Info(_host.Cancel(r.Id).Message ?? ""); });
        RefreshPrinters = new RelayCommand(LoadPrinters);
        UseAsDestination = new AsyncCommand(async () =>
        {
            if (SelectedPrinter is null) return;
            await _host.UpdateConfigAsync(c => c.DestinationPrinter = SelectedPrinter.Name);
            Refresh();
        }, () => SelectedPrinter is not null);
        PrintTestPage = new AsyncCommand(PrintTestPageAsync, () => SelectedPrinter is not null);
        SetTestMode = new AsyncCommand(async p =>
        {
            bool on = p is string s ? s == "on" : !_host.Config.TestMode;
            if (!on && _host.Config.DestinationPrinter is null)
            {
                Dialogs.Error("Choose a destination printer before turning test mode off.");
                return;
            }
            await _host.UpdateConfigAsync(c => c.TestMode = on);
            Refresh();
        });
        SaveSettings = new AsyncCommand(DoSaveSettingsAsync);
        LoadSettings();
        LoadPrinters();
        Refresh();
    }

    private string _page = "dashboard";
    public string Page { get => _page; set => Set(ref _page, value); }
    public string Version => NexoraInfo.AppVersion;

    // ----------------------------------------------------------- dashboard
    private bool? _running, _printerReady;
    private string _statusText = "", _modeText = "", _destText = "", _listenText = "", _pairing = "", _fingerprint = "", _folder = "";
    private bool _pairingVisible, _testMode;
    private int _received, _printing, _failed, _connections;
    public bool? Running { get => _running; set => Set(ref _running, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public bool TestMode { get => _testMode; set => Set(ref _testMode, value); }
    public string ModeText { get => _modeText; set => Set(ref _modeText, value); }
    public bool? PrinterReady { get => _printerReady; set => Set(ref _printerReady, value); }
    public string DestinationText { get => _destText; set => Set(ref _destText, value); }
    public string ListenText { get => _listenText; set => Set(ref _listenText, value); }
    public string Pairing { get => _pairing; set => Set(ref _pairing, value); }
    public string Fingerprint { get => _fingerprint; set => Set(ref _fingerprint, value); }
    public bool PairingVisible { get => _pairingVisible; set { Set(ref _pairingVisible, value); Refresh(); } }
    public string ReceivedFolder { get => _folder; set => Set(ref _folder, value); }
    public int ReceivedCount { get => _received; set => Set(ref _received, value); }
    public int PrintingCount { get => _printing; set => Set(ref _printing, value); }
    public int FailedCount { get => _failed; set => Set(ref _failed, value); }
    public int Connections { get => _connections; set => Set(ref _connections, value); }
    public string ToggleText => _host.IsRunning ? "Stop receiver" : "Start receiver";

    // ----------------------------------------------------------- jobs / printers
    public ObservableCollection<ReceivedJobRow> Jobs { get; } = [];
    public ObservableCollection<PrinterDescriptor> Printers { get; } = [];
    private PrinterDescriptor? _selectedPrinter;
    public PrinterDescriptor? SelectedPrinter { get => _selectedPrinter; set => Set(ref _selectedPrinter, value); }

    // ----------------------------------------------------------- settings
    private string _name = "", _tcp = "", _udp = "", _bind = "", _allowed = "", _maxMb = "", _retention = "", _dpi = "";
    private bool _startWithWindows, _discovery, _actualSize;
    public string ReceiverName { get => _name; set => Set(ref _name, value); }
    public string TcpPort { get => _tcp; set => Set(ref _tcp, value); }
    public string UdpPort { get => _udp; set => Set(ref _udp, value); }
    public string BindAddress { get => _bind; set => Set(ref _bind, value); }
    public string AllowedNetworks { get => _allowed; set => Set(ref _allowed, value); }
    public string MaxJobMb { get => _maxMb; set => Set(ref _maxMb, value); }
    public string RetentionDays { get => _retention; set => Set(ref _retention, value); }
    public string MaxDpi { get => _dpi; set => Set(ref _dpi, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool DiscoveryEnabled { get => _discovery; set => Set(ref _discovery, value); }
    public bool ActualSize { get => _actualSize; set => Set(ref _actualSize, value); }

    public AsyncCommand ToggleRunning { get; }
    public RelayCommand CopyPairing { get; }
    public RelayCommand ShowPairing { get; }
    public AsyncCommand Regenerate { get; }
    public RelayCommand OpenFolder { get; }
    public RelayCommand OpenLogs { get; }
    public RelayCommand OpenJob { get; }
    public RelayCommand RevealJob { get; }
    public RelayCommand Reprint { get; }
    public RelayCommand CancelJob { get; }
    public RelayCommand RefreshPrinters { get; }
    public AsyncCommand UseAsDestination { get; }
    public AsyncCommand PrintTestPage { get; }
    public AsyncCommand SetTestMode { get; }
    public AsyncCommand SaveSettings { get; }

    private readonly Dictionary<Guid, JobState> _lastStates = new();
    private int _tick;

    public void Refresh()
    {
        var c = _host.Config;
        Running = _host.IsRunning ? true : _host.StartupError is null ? null : false;
        StatusText = _host.IsRunning ? "Receiving jobs" : _host.StartupError ?? "Stopped";
        ListenText = _host.IsRunning ? $"TCP {_host.TransportPort} · UDP {(c.DiscoveryEnabled ? _host.DiscoveryPort.ToString() : "off")} · {(c.AllowedClientNetworks.Count == 0 ? "local subnet only" : string.Join(", ", c.AllowedClientNetworks))}" : "—";
        TestMode = c.TestMode;
        ModeText = c.TestMode ? "Test mode — received documents are saved, not printed" : "Print mode — received documents are printed automatically";
        if (_tick++ % 4 == 0 && Page != "printers") LoadPrinters(keepSelection: true);
        var dest = c.DestinationPrinter is null ? null : Printers.FirstOrDefault(p => p.Name == c.DestinationPrinter);
        PrinterReady = dest?.IsReady;
        DestinationText = c.DestinationPrinter is null ? "None selected"
            : dest is null ? $"{c.DestinationPrinter} — not installed on this PC"
            : $"{dest.Name} — {dest.StatusText}";
        Pairing = PairingVisible ? _host.PairingKey.Display : "••••-••••-••••-••••-••••";
        Fingerprint = $"Fingerprint {_host.PairingKey.Fingerprint}";
        ReceivedFolder = _host.Store.ReceivedFolder;
        Connections = _host.ActiveConnections;
        Raise(nameof(ToggleText));

        var all = _host.Store.All();
        ReceivedCount = all.Count(r => r.State != JobState.Failed);
        PrintingCount = all.Count(r => r.State is JobState.SubmittedToPrinter or JobState.Printing || r.AwaitingPrint);
        FailedCount = all.Count(r => r.State == JobState.Failed);
        SyncJobs(all);
    }

    private void SyncJobs(IReadOnlyList<ReceiverJobRecord> all)
    {
        var ids = all.Select(r => r.Metadata.JobId).ToHashSet();
        foreach (var gone in Jobs.Where(r => !ids.Contains(r.Id)).ToList()) Jobs.Remove(gone);
        for (int i = 0; i < all.Count; i++)
        {
            var r = all[i];
            var row = Jobs.FirstOrDefault(x => x.Id == r.Metadata.JobId);
            if (row is null)
            {
                row = new ReceivedJobRow(r.Metadata.JobId);
                Jobs.Insert(Math.Min(i, Jobs.Count), row);
            }
            row.Name = r.Metadata.DocumentName;
            row.Client = $"{r.Metadata.ClientName ?? r.Metadata.ClientId} ({r.ClientAddress})";
            row.Received = r.ReceivedUtc.ToLocalTime().ToString("dd MMM HH:mm:ss");
            row.Size = Shell.FormatBytes(r.Metadata.SizeBytes) + (r.Metadata.PageCount is int pc ? $" · {pc} p" : "") + (r.Metadata.Settings.Copies > 1 ? $" · {r.Metadata.Settings.Copies}×" : "");
            row.State = r.State.ToDisplay();
            row.Printer = r.PrinterName ?? "—";
            row.Details = (r.ErrorCode is null ? "" : $"[{r.ErrorCode}] ") + r.Message;
            row.CanCancel = !r.State.IsTerminal() && (r.AwaitingPrint || r.State is JobState.SubmittedToPrinter or JobState.Printing);
            row.File = r.StoredFile;

            if (_lastStates.TryGetValue(r.Metadata.JobId, out var before) && before != r.State)
            {
                if (r.State == JobState.Failed) _notify("Job failed", $"{r.Metadata.DocumentName}: {r.Message}", true);
                else if (r.State == JobState.Completed) _notify("Printed", $"{r.Metadata.DocumentName} on {r.PrinterName}", false);
            }
            else if (!_lastStates.ContainsKey(r.Metadata.JobId) && _lastStates.Count > 0 && r.State == JobState.Validated)
                _notify("Job received", $"{r.Metadata.DocumentName} from {r.Metadata.ClientName}", false);
            _lastStates[r.Metadata.JobId] = r.State;
        }
        if (_lastStates.Count == 0 && all.Count == 0) _lastStates[Guid.Empty] = JobState.Queued; // arm "new job" notifications
    }

    private void LoadPrinters() => LoadPrinters(keepSelection: true);

    private void LoadPrinters(bool keepSelection)
    {
        var sel = SelectedPrinter?.Name;
        IReadOnlyList<PrinterDescriptor> list;
        try { list = PrinterCatalog.List(); }
        catch (Exception e) { _host.Log.For("ui").Warn("printers", "Printer enumeration failed", ex: e); return; }
        Printers.Clear();
        foreach (var p in list) Printers.Add(p);
        if (keepSelection) SelectedPrinter = Printers.FirstOrDefault(p => p.Name == sel);
    }

    private async Task PrintTestPageAsync()
    {
        var printer = SelectedPrinter!.Name;
        if (!Dialogs.Confirm($"Print one Nexora test page on '{printer}' now?")) return;
        var file = Path.Combine(Path.GetTempPath(), $"nexora-receiver-test-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(file, TestPdfBuilder.Build($"Nexora receiver test page — {printer}"));
        try
        {
            var outcome = await new GdiPdfPrinter().PrintAsync(new PrintRequest
            {
                JobId = Guid.NewGuid(), PdfPath = file, DocumentName = "Nexora test page", PrinterName = printer,
                FitToPrintableArea = !_host.Config.Scaling.Equals(PrintScaling.ActualSize), MaxRenderDpi = _host.Config.MaxRenderDpi,
                TrackTimeout = TimeSpan.FromMinutes(2),
            }, null, default);
            _host.Log.For("ui").Info("print.testpage", $"Test page on '{printer}': {outcome.State.ToWire()} {outcome.Message}");
            Dialogs.Info($"{outcome.State.ToDisplay()}: {outcome.Message}\n\nCheck the printer for a physical page — Windows can only confirm that the job was sent.");
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private void LoadSettings()
    {
        var c = _host.Config;
        ReceiverName = c.ReceiverName;
        TcpPort = c.TransportPort.ToString();
        UdpPort = c.DiscoveryPort.ToString();
        BindAddress = c.BindAddress;
        AllowedNetworks = string.Join(", ", c.AllowedClientNetworks);
        MaxJobMb = (c.MaxJobBytes / (1024 * 1024)).ToString();
        RetentionDays = c.JobRetentionDays.ToString();
        MaxDpi = c.MaxRenderDpi.ToString();
        StartWithWindows = StartupRegistration.IsEnabled("NexoraReceiver");
        DiscoveryEnabled = c.DiscoveryEnabled;
        ActualSize = c.Scaling == PrintScaling.ActualSize;
    }

    private async Task DoSaveSettingsAsync()
    {
        try
        {
            var nets = AllowedNetworks.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).ToList();
            await _host.UpdateConfigAsync(c =>
            {
                c.ReceiverName = string.IsNullOrWhiteSpace(ReceiverName) ? Environment.MachineName : ReceiverName.Trim();
                c.TransportPort = int.Parse(TcpPort);
                c.DiscoveryPort = int.Parse(UdpPort);
                c.BindAddress = BindAddress.Trim();
                c.AllowedClientNetworks = nets;
                c.MaxJobBytes = Math.Max(1, long.Parse(MaxJobMb)) * 1024 * 1024;
                c.JobRetentionDays = Math.Max(1, int.Parse(RetentionDays));
                c.MaxRenderDpi = int.Parse(MaxDpi);
                c.DiscoveryEnabled = DiscoveryEnabled;
                c.Scaling = ActualSize ? PrintScaling.ActualSize : PrintScaling.FitToPrintableArea;
                c.StartWithWindows = StartWithWindows;
            });
            StartupRegistration.Set("NexoraReceiver", StartWithWindows);
            LoadSettings();
            Refresh();
            Dialogs.Info("Settings saved." + (_host.IsRunning ? "" : $"\n\nThe receiver is not running: {_host.StartupError}"));
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            Dialogs.Error(e.Message);
        }
    }
}
