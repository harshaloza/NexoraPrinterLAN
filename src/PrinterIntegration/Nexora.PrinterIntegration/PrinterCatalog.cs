using Nexora.Shared;
using Nexora.Shared.Windows;
using Nexora.Transport;

namespace Nexora.PrinterIntegration;

/// <summary>Lists the printers installed on this PC with the status Windows actually reports.</summary>
public static class PrinterCatalog
{
    private static readonly string[] VirtualDriverHints =
        ["Microsoft Print To PDF", "Microsoft XPS Document Writer", "OneNote", "Fax", "AnyDesk", "Nexora", "Send To"];

    public static IReadOnlyList<PrinterDescriptor> List()
    {
        var def = Winspool.GetDefaultPrinter();
        return Winspool.EnumeratePrinters()
            .Where(p => !p.Name.Equals(NexoraInfo.PrinterQueueName, StringComparison.OrdinalIgnoreCase)) // never loop jobs back into ourselves
            .Select(p => new PrinterDescriptor
            {
                Name = p.Name,
                DriverName = p.DriverName,
                PortName = p.PortName,
                IsDefault = p.Name.Equals(def, StringComparison.OrdinalIgnoreCase),
                IsReady = p.IsReady,
                StatusText = p.StatusText,
                IsVirtual = IsVirtual(p),
            })
            .OrderBy(p => p.IsVirtual).ThenBy(p => p.Name)
            .ToList();
    }

    public static PrinterDescriptor? Find(string? name) =>
        name is null ? null : List().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsVirtual(SpoolerPrinter p) =>
        VirtualDriverHints.Any(h => (p.DriverName ?? "").Contains(h, StringComparison.OrdinalIgnoreCase))
        || p.PortName is "PORTPROMPT:" or "nul:" or "SHRFAX:";
}
