using Microsoft.Win32;

namespace Nexora.App.Main;

/// <summary>
/// Which parts of Nexora run on this PC. Setup records the roles chosen in its feature tree under
/// HKLM\SOFTWARE\Nexora\PrinterLAN (Client = 1, Receiver = 1); "--roles client,receiver" overrides for development.
/// Without either (running from a build folder) both roles are enabled.
/// </summary>
public sealed record Roles(bool Client, bool Receiver, string Source)
{
    public const string RegistryKey = @"SOFTWARE\Nexora\PrinterLAN";

    public static Roles Detect(string[] args)
    {
        int i = Array.IndexOf(args, "--roles");
        if (i >= 0 && i + 1 < args.Length)
        {
            var parts = args[i + 1].ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new Roles(parts.Contains("client"), parts.Contains("receiver"), "command line");
        }
        using var k = Registry.LocalMachine.OpenSubKey(RegistryKey);
        if (k is not null)
        {
            bool c = k.GetValue("Client") is int ci && ci == 1;
            bool r = k.GetValue("Receiver") is int ri && ri == 1;
            if (c || r) return new Roles(c, r, "installed features");
        }
        return new Roles(true, true, "not installed (development build)");
    }

    public string Describe() => (Client, Receiver) switch
    {
        (true, true) => "Prints to another PC and receives jobs for this PC's printers",
        (true, false) => "Prints to another PC (client)",
        (false, true) => "Receives jobs and prints on this PC's printers (receiver)",
        _ => "No role installed",
    };
}
