using System.Windows;
using System.Windows.Threading;
using Nexora.App.Client;
using Nexora.App.Receiver;
using Nexora.App.Main;
using Nexora.Client.Core;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.UI;

namespace Nexora.App;

/// <summary>
/// Nexora Printer LAN: one tray app that runs the client role (capture + send), the receiver role
/// (receive + print), or both, depending on what Setup installed on this PC.
/// </summary>
public partial class App : Application
{
    private SingleInstance? _instance;
    private ClientEngine? _engine;
    private ReceiverHost? _host;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Snapshot mode only renders pages (documentation/UI checks) and may run beside the real app.
        _instance = new SingleInstance(Snapshot.DirFromArgs(e.Args) is null ? "NexoraPrinterLAN.App" : $"NexoraPrinterLAN.Snapshot.{Environment.ProcessId}");
        if (!_instance.IsFirst)
        {
            if (!e.Args.Contains("--minimized")) // autostart may fire twice (per-user and all-users entries)
                MessageBox.Show("Nexora Printer LAN is already running (see the notification area).", NexoraInfo.ProductName);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            (_engine?.Log ?? _host?.Log)?.For("ui").Error("ui.crash", "Unhandled UI exception", ex: ex.Exception);
            Dialogs.Error(ex.Exception.Message);
            ex.Handled = true;
        };

        var roles = Roles.Detect(e.Args);
        var startupErrors = new List<string>();
        if (roles.Client)
        {
            _engine = new ClientEngine(new NexoraPaths("Client"));
            _engine.Start();
            if (_engine.CaptureError is not null) startupErrors.Add(_engine.CaptureError);
        }
        if (roles.Receiver)
        {
            _host = new ReceiverHost(new NexoraPaths("Receiver"));
            try { _host.Start(); }
            catch (Exception ex) { startupErrors.Add($"The receiver could not start: {ex.Message}"); }
        }

        _window = new MainWindow();
        var menu = new List<(string, Action)>();
        if (_engine is not null) menu.Add(("Send test page", () => _engine.SubmitTestPage()));
        if (_host is not null) menu.Add(("Open received documents", () => Shell.Open(_host.Store.ReceivedFolder)));
        menu.Add(("-", () => { }));
        menu.Add(("Exit", ExitApp));
        _tray = new TrayIcon(_window, NexoraInfo.ProductName, Path.Combine(AppContext.BaseDirectory, "nexora.ico"), menu);

        void Notify(string t, string m, bool warn) => Dispatcher.BeginInvoke(() => _tray.Notify(t, m, warn));
        var client = _engine is null ? null : new ClientViewModel(_engine, Notify);
        var receiver = _host is null ? null : new ReceiverViewModel(_host, Notify);
        var vm = new ShellViewModel(roles, client, receiver);
        _window.DataContext = vm;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => vm.Refresh();
        _timer.Start();

        if (Snapshot.DirFromArgs(e.Args) is { } snapDir)
        {
            _window.Width = 1180; _window.Height = 780;
            var pages = new List<string> { "dashboard" };
            if (client is not null) pages.AddRange(["jobs", "devices", "printer"]);
            if (receiver is not null) pages.AddRange(["received", "rx-printers"]);
            pages.Add("settings");
            await Snapshot.CaptureAsync(_window, snapDir, "app", pages, p => vm.Page = p, vm.Refresh);
            await ShutdownCoreAsync();
            return;
        }

        bool firstReceiverRun = _host?.PairingCodeWasCreated ?? false;
        if (!e.Args.Contains("--minimized") || startupErrors.Count > 0 || firstReceiverRun) _window.Show();
        if (startupErrors.Count > 0) Dialogs.Error(string.Join("\n\n", startupErrors));
        else if (firstReceiverRun && receiver is not null)
        {
            receiver.PairingVisible = true;
            Dialogs.Info($"Pairing code for the PCs that will print here:\n\n{_host!.PairingKey.Display}\n\nThe receiver starts in TEST MODE: received documents are saved but not printed until you choose a printer.");
        }
    }

    private async void ExitApp()
    {
        if (!Dialogs.Confirm("Exit Nexora Printer LAN? While it is not running, jobs printed to “Nexora Printer LAN” wait in the Windows queue, and other PCs cannot print to this PC.")) return;
        await ShutdownCoreAsync();
    }

    private async Task ShutdownCoreAsync()
    {
        _timer?.Stop();
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        _tray?.Dispose();
        if (_engine is not null) await _engine.DisposeAsync();
        if (_host is not null) await _host.DisposeAsync();
        _instance?.Dispose();
        Shutdown();
    }
}
