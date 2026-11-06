using System.Text;

namespace Nexora.Shared.IO;

/// <summary>Turns an untrusted document name into a safe single file-name component.</summary>
public static class SafeFileName
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Sanitize(string? name, int maxLength = 80, string fallback = "document")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        // Only the last path segment; strip any directory the sender tried to smuggle in.
        name = name.Replace('/', '\\');
        name = name[(name.LastIndexOf('\\') + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsControl(c) || invalid.Contains(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|') sb.Append('_');
            else sb.Append(c);
        }
        var s = sb.ToString().Trim().TrimEnd('.', ' ');
        while (s.Contains("..")) s = s.Replace("..", ".");
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd('.', ' ');
        if (s.Length == 0) return fallback;
        var stem = s.Split('.')[0];
        if (Reserved.Contains(stem)) s = "_" + s;
        return s;
    }

    /// <summary>Sanitized name with the extension forced to <paramref name="extension"/> (e.g. ".pdf").</summary>
    public static string WithExtension(string? name, string extension)
    {
        var s = Sanitize(name);
        var ext = Path.GetExtension(s);
        if (!ext.Equals(extension, StringComparison.OrdinalIgnoreCase))
            s = (ext.Length > 0 && ext.Length <= 5 ? s[..^ext.Length] : s) + extension;
        return s;
    }
}
