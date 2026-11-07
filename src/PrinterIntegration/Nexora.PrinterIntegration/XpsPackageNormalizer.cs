using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Nexora.PrinterIntegration;

/// <summary>
/// The Windows spooler writes XPS as an interleaved OPC package: every part is split into numbered pieces
/// ("Name/[0].piece", "Name/[1].piece", …, "Name/[n].last.piece"). .NET's System.IO.Packaging cannot read
/// interleaved packages, so this joins the pieces of each part back into one ZIP entry. Non-piece entries are
/// copied unchanged; the content of the document is not modified.
/// </summary>
public static partial class XpsPackageNormalizer
{
    [GeneratedRegex(@"^(?<part>.+)/\[(?<index>\d+)\](?<last>\.last)?\.piece$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PieceName();

    public static bool IsInterleaved(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.Entries.Any(e => PieceName().IsMatch(e.FullName));
    }

    /// <summary>Writes a non-interleaved copy of <paramref name="source"/> to <paramref name="target"/>.</summary>
    public static void Normalize(string source, string target)
    {
        using var input = ZipFile.OpenRead(source);
        var parts = new Dictionary<string, SortedList<int, ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var e in input.Entries)
        {
            var m = PieceName().Match(e.FullName);
            string name = m.Success ? m.Groups["part"].Value : e.FullName;
            int index = m.Success ? int.Parse(m.Groups["index"].Value) : 0;
            if (!parts.TryGetValue(name, out var pieces))
            {
                parts[name] = pieces = new SortedList<int, ZipArchiveEntry>();
                order.Add(name);
            }
            if (!pieces.TryAdd(index, e)) throw new InvalidDataException($"Duplicate piece {index} of part '{name}'");
        }

        using var fs = new FileStream(target, FileMode.Create, FileAccess.Write);
        using var output = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var name in order)
        {
            var pieces = parts[name];
            if (pieces.Keys.Select((k, i) => k == i).Contains(false))
                throw new InvalidDataException($"Missing piece in part '{name}'");
            var entry = output.CreateEntry(name, CompressionLevel.Fastest);
            using var dst = entry.Open();
            foreach (var piece in pieces.Values)
            {
                using var src = piece.Open();
                src.CopyTo(dst);
            }
        }
    }
}
