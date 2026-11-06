using System.Text;
using System.Text.RegularExpressions;

namespace Nexora.Shared.Pdf;

public sealed record PdfInspection(bool IsPdf, string? Version, int? PageCount, bool HasEof, string? Problem);

/// <summary>
/// Lightweight structural check of a PDF: header, %%EOF trailer and an estimated page count.
/// It never executes or renders content; full rendering validation happens in the printer integration
/// using Windows.Data.Pdf. Page count is a best effort (exact for the uncompressed object streams
/// produced by Microsoft Print to PDF; may be null for PDFs that hide page objects in object streams).
/// </summary>
public static partial class PdfInspector
{
    public static PdfInspection Inspect(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Inspect(fs);
    }

    public static PdfInspection Inspect(Stream s)
    {
        long len = s.Length;
        if (len < 32) return new(false, null, null, false, "File is too small to be a PDF");

        var head = new byte[1024];
        s.Position = 0;
        int n = s.ReadAtLeast(head, (int)Math.Min(head.Length, len), throwOnEndOfStream: false);
        var headText = Encoding.Latin1.GetString(head, 0, n);
        int idx = headText.IndexOf("%PDF-", StringComparison.Ordinal);
        if (idx < 0) return new(false, null, null, false, "Missing %PDF- header");
        var version = headText.Length >= idx + 8 ? headText.Substring(idx + 5, 3) : null;

        int tailLen = (int)Math.Min(2048, len);
        var tail = new byte[tailLen];
        s.Position = len - tailLen;
        s.ReadExactly(tail);
        bool hasEof = Encoding.Latin1.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);

        int? pages = CountPages(s);
        s.Position = 0;
        return new(true, version, pages, hasEof, hasEof ? null : "Missing %%EOF trailer (file may be truncated)");
    }

    private static int? CountPages(Stream s)
    {
        // Prefer the largest /Type /Pages /Count; fall back to counting /Type /Page objects.
        if (s.Length > 512L * 1024 * 1024) return null;
        s.Position = 0;
        var text = Encoding.Latin1.GetString(ReadAll(s));
        int best = -1;
        foreach (Match m in PagesCountRegex().Matches(text))
            if (int.TryParse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value, out var c)) best = Math.Max(best, c);
        if (best >= 0) return best;
        int pages = PageRegex().Count(text);
        return pages > 0 ? pages : null;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    [GeneratedRegex(@"/Type\s*/Pages\b[^>]*?/Count\s+(\d+)|/Count\s+(\d+)[^>]*?/Type\s*/Pages\b", RegexOptions.CultureInvariant)]
    private static partial Regex PagesCountRegex();

    [GeneratedRegex(@"/Type\s*/Page(?![a-zA-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex PageRegex();
}
