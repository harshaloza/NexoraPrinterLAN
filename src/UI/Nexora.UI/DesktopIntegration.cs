using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Nexora.UI;

/// <summary>Per-user "start with Windows" through HKCU\...\Run (no admin rights, no service).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string valueName) => IsMachineWide(valueName) || IsSetFor(Registry.CurrentUser, valueName);

    /// <summary>The installer registers autostart for all users under HKLM.</summary>
    public static bool IsMachineWide(string valueName) => IsSetFor(Registry.LocalMachine, valueName);

    private static bool IsSetFor(RegistryKey hive, string valueName)
    {
        using var k = hive.OpenSubKey(RunKey);
        return k?.GetValue(valueName) is string;
    }

    public static void Set(string valueName, bool enabled, string arguments = "--minimized")
    {
        if (!enabled && IsMachineWide(valueName))
            throw new InvalidOperationException("Start with Windows was enabled for all users by the installer. To turn it off, rerun the installer with -NoAutoStart or uninstall.");
        if (enabled && IsMachineWide(valueName)) return;
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) k.SetValue(valueName, $"\"{Environment.ProcessPath}\" {arguments}");
        else k.DeleteValue(valueName, throwOnMissingValue: false);
    }
}

/// <summary>Ensures one instance per user session (the client owns the capture pipe; the receiver owns its ports).</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    public bool IsFirst { get; }

    public SingleInstance(string name)
    {
        _mutex = new Mutex(true, $@"Local\{name}", out bool created);
        IsFirst = created;
    }

    public void Dispose()
    {
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>Notification-area icon with a context menu. Closing the window hides it to the tray.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    public TrayIcon(Window window, string tooltip, string iconPath, IEnumerable<(string Text, Action Action)> menu)
    {
        _window = window;
        _icon = new Forms.NotifyIcon
        {
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            Icon = System.IO.File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Open", null, (_, _) => Show());
        _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
        foreach (var (text, action) in menu)
        {
            if (text == "-") _icon.ContextMenuStrip.Items.Add(new Forms.ToolStripSeparator());
            else _icon.ContextMenuStrip.Items.Add(text, null, (_, _) => action());
        }
        _icon.DoubleClick += (_, _) => Show();
    }

    public void Show()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Notify(string title, string text, bool warning = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text.Length > 250 ? text[..250] : text;
        _icon.BalloonTipIcon = warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _icon.ShowBalloonTip(4000);
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

public static class Shell
{
    public static void Open(string pathOrUri)
    {
        try { Process.Start(new ProcessStartInfo(pathOrUri) { UseShellExecute = true }); }
        catch (Exception e) { Dialogs.Error($"Could not open {pathOrUri}: {e.Message}"); }
    }

    public static void Reveal(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (Exception e) { Dialogs.Error(e.Message); }
    }

    public static string FormatBytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };
}
