using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Nexora.Shared.Pdf;

/// <summary>Result of a quick structural check of a print job file.</summary>
public sealed record DocumentCheck(string? Format, int? PageCount, string? Problem)
{
    public bool IsValid => Format is not null && Problem is null;
    public string Extension => Format == NexoraInfo.XpsMediaType ? ".xps" : ".pdf";
}

/// <summary>
/// Identifies the two job formats Nexora transports:
///   application/pdf               from the inbox "Microsoft Print To PDF" queue (1.0/1.1)
///   application/vnd.ms-xpsdocument from the Nexora v4 driver (XPS pass-through, 1.2)
/// The check is structural only; nothing is rendered or executed.
/// </summary>
public static partial class DocumentFormats
{
    [GeneratedRegex(@"^(?<part>.+?)(/\[\d+\](\.last)?\.piece)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    [GeneratedRegex(@"\.fpage$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FixedPage();

    public static DocumentCheck Detect(string path)
    {
        Span<byte> head = stackalloc byte[5];
        int n;
        using (var fs = File.OpenRead(path)) n = fs.Read(head);
        if (n >= 5 && head[0] == (byte)'%' && head[1] == (byte)'P' && head[2] == (byte)'D' && head[3] == (byte)'F')
        {
            var pdf = PdfInspector.Inspect(path);
            return new DocumentCheck(NexoraInfo.PdfMediaType, pdf.PageCount, pdf.IsPdf && pdf.HasEof ? null : pdf.Problem ?? "Invalid PDF");
        }
        if (n >= 2 && head[0] == (byte)'P' && head[1] == (byte)'K') return InspectXps(path);
        return new DocumentCheck(null, null, "Neither a PDF nor an XPS document");
    }

    /// <summary>Checks the XPS (OPC/ZIP) package: a FixedDocumentSequence must exist; pages are counted from FixedPage parts.</summary>
    public static DocumentCheck InspectXps(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var parts = zip.Entries.Select(e => PartName().Match(e.FullName).Groups["part"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!parts.Any(p => p.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase)))
                return new DocumentCheck(NexoraInfo.XpsMediaType, null, "XPS package has no FixedDocumentSequence");
            int pages = parts.Count(p => FixedPage().IsMatch(p));
            return pages == 0
                ? new DocumentCheck(NexoraInfo.XpsMediaType, 0, "XPS document has no pages")
                : new DocumentCheck(NexoraInfo.XpsMediaType, pages, null);
        }
        catch (InvalidDataException e)
        {
            return new DocumentCheck(NexoraInfo.XpsMediaType, null, $"Damaged XPS package: {e.Message}");
        }
    }
}
