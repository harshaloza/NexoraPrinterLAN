using System.Windows;
using System.Windows.Threading;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.UI;

namespace Nexora.Receiver.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ReceiverHost? _host;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance("NexoraPrinterLAN.Receiver");
        if (!_instance.IsFirst)
        {
            if (!e.Args.Contains("--minimized")) // autostart may fire twice (per-user and all-users entries)
                MessageBox.Show("Nexora Receiver is already running (see the notification area).", NexoraInfo.ProductName);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            _host?.Log.For("ui").Error("ui.crash", "Unhandled UI exception", ex: ex.Exception);
            Dialogs.Error(ex.Exception.Message);
            ex.Handled = true;
        };

        _host = new ReceiverHost(new NexoraPaths("Receiver"));
        string? startError = null;
        try { _host.Start(); }
        catch (Exception ex) { startError = ex.Message; }

        _window = new MainWindow();
        _tray = new TrayIcon(_window, "Nexora Printer LAN — Receiver", System.IO.Path.Combine(AppContext.BaseDirectory, "nexora-receiver.ico"),
        [
            ("Open received documents", () => Shell.Open(_host.Store.ReceivedFolder)),
            ("-", () => { }),
            ("Exit", ExitApp),
        ]);
        var vm = new ReceiverViewModel(_host, (t, m, warn) => Dispatcher.BeginInvoke(() => _tray.Notify(t, m, warn)));
        _window.DataContext = vm;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => vm.Refresh();
        _timer.Start();

        if (Snapshot.DirFromArgs(e.Args) is { } snapDir)
        {
            _window.Width = 1180; _window.Height = 760;
            await Snapshot.CaptureAsync(_window, snapDir, "receiver", ["dashboard", "jobs", "printers", "settings"], p => vm.Page = p, vm.Refresh);
            _window.AllowClose = true;
            _window.Close();
            _tray.Dispose();
            await _host.DisposeAsync();
            Shutdown();
            return;
        }

        if (!e.Args.Contains("--minimized") || startError is not null || _host.PairingCodeWasCreated) _window.Show();
        if (startError is not null) Dialogs.Error($"The receiver could not start:\n\n{startError}");
        else if (_host.PairingCodeWasCreated)
        {
            vm.PairingVisible = true;
            Dialogs.Info($"Welcome to Nexora Receiver.\n\nPairing code for your client PCs:\n\n{_host.PairingKey.Display}\n\nThe receiver starts in TEST MODE: received documents are saved but not printed until you choose a printer.");
        }
    }

    private async void ExitApp()
    {
        if (!Dialogs.Confirm("Exit Nexora Receiver? Clients cannot send jobs to this PC until it runs again.")) return;
        _timer?.Stop();
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        _tray?.Dispose();
        if (_host is not null) await _host.DisposeAsync();
        _instance?.Dispose();
        Shutdown();
    }
}
