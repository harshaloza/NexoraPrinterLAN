namespace Nexora.Shared.Config;

/// <summary>
/// Per-user data locations. Both apps run in the interactive user session (see docs/ARCHITECTURE.md §4),
/// so data lives under %LOCALAPPDATA%\Nexora\&lt;Role&gt;. NEXORA_DATA_DIR overrides the root (tests, portable use).
/// </summary>
public sealed class NexoraPaths
{
    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string Logs => Path.Combine(Root, "logs");

    public NexoraPaths(string role, string? rootOverride = null)
    {
        var baseDir = rootOverride
            ?? Environment.GetEnvironmentVariable("NEXORA_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nexora");
        Root = rootOverride is null ? Path.Combine(baseDir, role) : rootOverride;
        Directory.CreateDirectory(Root);
    }

    public string Sub(string name)
    {
        var p = Path.Combine(Root, name);
        Directory.CreateDirectory(p);
        return p;
    }
}
