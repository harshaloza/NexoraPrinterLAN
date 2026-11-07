using System.Diagnostics;
using Nexora.Shared;
using Nexora.Shared.Windows;

namespace Nexora.PrintPipeline;

public sealed record QueueStatus(bool Exists, bool PortCorrect, bool DriverCorrect, string? PortName, string? DriverName,
    bool IsDefault, bool DriverAvailable, string Summary)
{
    public bool Healthy => Exists && PortCorrect && DriverCorrect;
    /// <summary>True when the queue uses Nexora's own v4 driver (jobs travel as XPS); false = inbox PDF driver.</summary>
    public bool UsesNexoraDriver => NexoraInfo.NexoraDriverName.Equals(DriverName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reports the real state of the "Nexora Printer LAN" queue and launches the (elevated) install script.</summary>
public static class QueueInspector
{
    public static QueueStatus Inspect(string queueName = NexoraInfo.PrinterQueueName, string portName = NexoraInfo.PortName)
    {
        var printers = Winspool.EnumeratePrinters();
        var q = printers.FirstOrDefault(p => p.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));
        var drivers = Winspool.EnumerateDriverNames();
        bool driverAvailable = drivers.Contains(NexoraInfo.NexoraDriverName, StringComparer.OrdinalIgnoreCase)
            || drivers.Contains(NexoraInfo.SourceDriverName, StringComparer.OrdinalIgnoreCase);
        if (q is null)
            return new(false, false, false, null, null, false, driverAvailable,
                driverAvailable ? $"Queue '{queueName}' is not installed" : $"Queue '{queueName}' is not installed and no usable driver is present");
        bool portOk = portName.Equals(q.PortName, StringComparison.OrdinalIgnoreCase);
        bool nexora = NexoraInfo.NexoraDriverName.Equals(q.DriverName, StringComparison.OrdinalIgnoreCase);
        bool driverOk = nexora || NexoraInfo.SourceDriverName.Equals(q.DriverName, StringComparison.OrdinalIgnoreCase);
        bool isDefault = queueName.Equals(Winspool.GetDefaultPrinter(), StringComparison.OrdinalIgnoreCase);
        string summary = !(portOk && driverOk) ? $"Misconfigured (port '{q.PortName}', driver '{q.DriverName}')"
            : nexora ? "Installed — Nexora driver (sends XPS, no PDF)"
            : "Installed — Windows' Microsoft Print To PDF driver (sends PDF)";
        return new(true, portOk, driverOk, q.PortName, q.DriverName, isDefault, driverAvailable, summary);
    }

    /// <summary>Runs scripts\Install-NexoraQueue.ps1 (or Uninstall) elevated. Windows shows a UAC prompt; nothing is installed silently.</summary>
    public static int RunElevatedScript(string scriptPath, string arguments = "")
    {
        if (!File.Exists(scriptPath)) throw new FileNotFoundException("Queue script not found", scriptPath);
        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Normal,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell");
        p.WaitForExit();
        return p.ExitCode;
    }
}
