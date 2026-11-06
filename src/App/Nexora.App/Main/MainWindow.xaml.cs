using System.ComponentModel;
using System.Windows;

namespace Nexora.App.Main;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>When true the window really closes (Exit from the tray); otherwise closing hides it to the tray.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}
