using System.Windows;
using System.Windows.Threading;
using Nexora.Client.Core;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.UI;

namespace Nexora.Client.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private ClientEngine? _engine;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private DispatcherTimer? _timer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instance = new SingleInstance("NexoraPrinterLAN.Client");
        if (!_instance.IsFirst)
        {
            if (!e.Args.Contains("--minimized")) // autostart may fire twice (per-user and all-users entries)
                MessageBox.Show("Nexora Client is already running (see the notification area).", NexoraInfo.ProductName);
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            _engine?.Log.For("ui").Error("ui.crash", "Unhandled UI exception", ex: ex.Exception);
            Dialogs.Error(ex.Exception.Message);
            ex.Handled = true;
        };

        _engine = new ClientEngine(new NexoraPaths("Client"));
        _engine.Start();

        _window = new MainWindow();
        _tray = new TrayIcon(_window, "Nexora Printer LAN — Client", System.IO.Path.Combine(AppContext.BaseDirectory, "nexora-client.ico"),
        [
            ("Send test page", () => _engine.SubmitTestPage()),
            ("Print jobs", () => { ((MainViewModel)_window.DataContext).Page = "jobs"; _tray!.Show(); }),
            ("-", () => { }),
            ("Exit", ExitApp),
        ]);
        var vm = new MainViewModel(_engine, (t, m, warn) => Dispatcher.BeginInvoke(() => _tray.Notify(t, m, warn)));
        _window.DataContext = vm;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _timer.Tick += (_, _) => vm.Refresh();
        _timer.Start();

        if (Snapshot.DirFromArgs(e.Args) is { } snapDir)
        {
            _window.Width = 1180; _window.Height = 760;
            await Snapshot.CaptureAsync(_window, snapDir, "client", ["dashboard", "printers", "devices", "jobs", "settings"], p => vm.Page = p, vm.Refresh);
            _window.AllowClose = true;
            _window.Close();
            _tray.Dispose();
            await _engine.DisposeAsync();
            Shutdown();
            return;
        }

        bool minimized = e.Args.Contains("--minimized");
        if (!minimized) _window.Show();
        if (_engine.CaptureError is not null)
        {
            _tray.Notify("Nexora Client", _engine.CaptureError, warning: true);
            _window.Show();
        }
    }

    private async void ExitApp()
    {
        if (!Dialogs.Confirm("Exit Nexora Client? While it is not running, jobs printed to “Nexora Printer LAN” wait in the Windows queue and are sent when it starts again.")) return;
        _timer?.Stop();
        if (_window is not null) { _window.AllowClose = true; _window.Close(); }
        _tray?.Dispose();
        if (_engine is not null) await _engine.DisposeAsync();
        _instance?.Dispose();
        Shutdown();
    }
}
