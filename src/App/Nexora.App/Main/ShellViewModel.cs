using Nexora.App.Client;
using Nexora.App.Receiver;
using Nexora.Shared;
using Nexora.UI;

namespace Nexora.App.Main;

/// <summary>Top-level window state: navigation, which roles are present, and app-wide settings.</summary>
public sealed class ShellViewModel : ObservableObject
{
    public const string AutostartName = "NexoraPrinterLAN";

    public ShellViewModel(Roles roles, ClientViewModel? client, ReceiverViewModel? receiver)
    {
        Roles = roles;
        Client = client;
        Receiver = receiver;
        if (client is not null) client.Navigate = p => Page = p;
        _startWithWindows = StartupRegistration.IsEnabled(AutostartName);
        SaveGeneral = new RelayCommand(() =>
        {
            try
            {
                StartupRegistration.Set(AutostartName, StartWithWindows);
                Dialogs.Info("Saved.");
            }
            catch (InvalidOperationException e)
            {
                Dialogs.Error(e.Message);
                StartWithWindows = StartupRegistration.IsEnabled(AutostartName);
            }
        });
    }

    public Roles Roles { get; }
    public ClientViewModel? Client { get; }
    public ReceiverViewModel? Receiver { get; }
    public bool HasClient => Client is not null;
    public bool HasReceiver => Receiver is not null;
    public string RoleText => $"{Roles.Describe()} — {Roles.Source}";
    public string Version => NexoraInfo.AppVersion;

    private string _page = "dashboard";
    public string Page
    {
        get => _page;
        set
        {
            if (!Set(ref _page, value)) return;
            if (Receiver is not null) Receiver.PrintersPageActive = value == "rx-printers";
        }
    }

    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public bool AutostartMachineWide => StartupRegistration.IsMachineWide(AutostartName);
    public RelayCommand SaveGeneral { get; }

    public void Refresh()
    {
        Client?.Refresh();
        Receiver?.Refresh();
    }
}
