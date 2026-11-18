using System.IO.Compression;
using System.Text;
using Nexora.PrinterIntegration;
using Nexora.Shared;
using Nexora.Shared.Pdf;
using Xunit;

namespace Nexora.Tests.Unit;

public class DocumentFormatTests
{
    private static string Temp(string name) => Path.Combine(Directory.CreateTempSubdirectory("nexora-fmt").FullName, name);

    /// <summary>A minimal XPS-shaped package; when <paramref name="interleaved"/>, parts are split into pieces like the spooler does.</summary>
    private static string MakeXpsZip(bool interleaved, int pages = 2)
    {
        var path = Temp(interleaved ? "spool.xps" : "plain.xps");
        var parts = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<Types/>",
            ["_rels/.rels"] = "<Relationships/>",
            ["FixedDocumentSequence.fdseq"] = "<FixedDocumentSequence><DocumentReference Source=\"Documents/1/FixedDocument.fdoc\"/></FixedDocumentSequence>",
            ["Documents/1/FixedDocument.fdoc"] = "<FixedDocument/>",
        };
        for (int i = 1; i <= pages; i++) parts[$"Documents/1/Pages/{i}.fpage"] = $"<FixedPage Width=\"816\" Height=\"1056\"><!-- page {i} --></FixedPage>";
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in parts)
        {
            if (!interleaved) { Write(zip, name, text); continue; }
            int half = text.Length / 2;
            Write(zip, $"{name}/[0].piece", text[..half]);
            Write(zip, $"{name}/[1].last.piece", text[half..]);
        }
        return path;

        static void Write(ZipArchive z, string name, string text)
        {
            using var s = z.CreateEntry(name).Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    [Fact]
    public void Pdf_is_detected()
    {
        var path = Temp("a.pdf");
        File.WriteAllBytes(path, TestPdfBuilder.Build("x", pages: 2));
        var c = DocumentFormats.Detect(path);
        Assert.Equal(NexoraInfo.PdfMediaType, c.Format);
        Assert.Equal(2, c.PageCount);
        Assert.True(c.IsValid);
        Assert.Equal(".pdf", c.Extension);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xps_is_detected_and_pages_counted_plain_or_interleaved(bool interleaved)
    {
        var c = DocumentFormats.Detect(MakeXpsZip(interleaved, pages: 3));
        Assert.Equal(NexoraInfo.XpsMediaType, c.Format);
        Assert.Equal(3, c.PageCount);
        Assert.True(c.IsValid);
        Assert.Equal(".xps", c.Extension);
    }

    [Fact]
    public void Zip_without_fixed_document_sequence_is_not_a_valid_xps()
    {
        var path = Temp("other.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");
        var c = DocumentFormats.Detect(path);
        Assert.False(c.IsValid);
        Assert.Contains("FixedDocumentSequence", c.Problem);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        var path = Temp("x.bin");
        File.WriteAllText(path, "MZ not a document");
        Assert.False(DocumentFormats.Detect(path).IsValid);
    }

    [Fact]
    public void Normalizer_joins_pieces_into_whole_parts()
    {
        var spool = MakeXpsZip(interleaved: true);
        Assert.True(XpsPackageNormalizer.IsInterleaved(spool));
        var plain = Temp("joined.xps");
        XpsPackageNormalizer.Normalize(spool, plain);
        Assert.False(XpsPackageNormalizer.IsInterleaved(plain));
        using var zip = ZipFile.OpenRead(plain);
        var seq = zip.GetEntry("FixedDocumentSequence.fdseq")!;
        using var r = new StreamReader(seq.Open());
        Assert.Equal("<FixedDocumentSequence><DocumentReference Source=\"Documents/1/FixedDocument.fdoc\"/></FixedDocumentSequence>", r.ReadToEnd());
        Assert.NotNull(zip.GetEntry("Documents/1/Pages/2.fpage"));
    }

    [Fact]
    public void Normalizer_rejects_missing_pieces()
    {
        var path = Temp("broken.xps");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("FixedDocumentSequence.fdseq/[0].piece");
            zip.CreateEntry("FixedDocumentSequence.fdseq/[2].last.piece");
        }
        Assert.Throws<InvalidDataException>(() => XpsPackageNormalizer.Normalize(path, Temp("out.xps")));
    }

    [Fact]
    public void Receiver_accepts_both_formats_only() =>
        Assert.True(NexoraInfo.IsSupportedFormat(NexoraInfo.XpsMediaType) && NexoraInfo.IsSupportedFormat(NexoraInfo.PdfMediaType)
                    && !NexoraInfo.IsSupportedFormat("application/x-msdownload"));
}
