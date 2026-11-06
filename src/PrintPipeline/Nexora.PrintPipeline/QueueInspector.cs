using System.Diagnostics;
using Nexora.Shared;
using Nexora.Shared.Windows;

namespace Nexora.PrintPipeline;

public sealed record QueueStatus(bool Exists, bool PortCorrect, bool DriverCorrect, string? PortName, string? DriverName,
    bool IsDefault, bool DriverAvailable, string Summary)
{
    public bool Healthy => Exists && PortCorrect && DriverCorrect;
}

/// <summary>Reports the real state of the "Nexora Printer LAN" queue and launches the (elevated) install script.</summary>
public static class QueueInspector
{
    public static QueueStatus Inspect(string queueName = NexoraInfo.PrinterQueueName, string portName = NexoraInfo.PortName)
    {
        var printers = Winspool.EnumeratePrinters();
        var q = printers.FirstOrDefault(p => p.Name.Equals(queueName, StringComparison.OrdinalIgnoreCase));
        bool driverAvailable = Winspool.EnumerateDriverNames().Contains(NexoraInfo.SourceDriverName, StringComparer.OrdinalIgnoreCase);
        if (q is null)
            return new(false, false, false, null, null, false, driverAvailable,
                driverAvailable ? $"Queue '{queueName}' is not installed" : $"Queue '{queueName}' is not installed and the '{NexoraInfo.SourceDriverName}' driver is missing");
        bool portOk = portName.Equals(q.PortName, StringComparison.OrdinalIgnoreCase);
        bool driverOk = NexoraInfo.SourceDriverName.Equals(q.DriverName, StringComparison.OrdinalIgnoreCase);
        bool isDefault = queueName.Equals(Winspool.GetDefaultPrinter(), StringComparison.OrdinalIgnoreCase);
        string summary = portOk && driverOk ? "Installed" : $"Misconfigured (port '{q.PortName}', driver '{q.DriverName}')";
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
