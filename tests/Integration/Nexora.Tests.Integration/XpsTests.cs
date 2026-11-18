using System.IO.Pipes;
using Nexora.PrinterIntegration;
using Nexora.Shared;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Nexora.Tests.Integration;

/// <summary>Version 1.2 path: XPS jobs from the Nexora v4 driver, transported and printed without PDF.</summary>
public class XpsTests(ITestOutputHelper output)
{
    private const string TestQueue = "Nexora Printer LAN (XPS test)";
    private const string TestPipe = "NexoraPrinterLAN-XPS";

    private static string TempDir() => Directory.CreateTempSubdirectory("nexora-xps").FullName;

    [Fact]
    public async Task Xps_job_is_transferred_verified_and_stored_as_xps()
    {
        await using var rx = await TestReceiver.StartAsync();
        var path = TestXps.Create(Path.Combine(TempDir(), "job.xps"), 3, TestXps.A4);
        var meta = Jobs.Meta(path, name: "Drawing") with { Format = NexoraInfo.XpsMediaType };
        await using var s = await rx.ConnectAsync();
        var st = await s.SendJobAsync(meta, path, null, default);

        Assert.Equal(JobState.Validated, st.State);
        var rec = rx.Host.Store.Get(meta.JobId)!;
        Assert.EndsWith(".xps", rec.StoredFile);
        Assert.Equal(meta.Sha256, Hashing.Sha256File(rec.StoredFile!));
        Assert.Equal(3, rec.Metadata.PageCount);
    }

    [Fact]
    public async Task Pdf_bytes_announced_as_xps_are_rejected()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf();
        await using var s = await rx.ConnectAsync();
        var st = await s.SendJobAsync(meta with { Format = NexoraInfo.XpsMediaType }, path, null, default);
        Assert.Equal(JobState.Failed, st.State);
        Assert.Equal(ErrorCodes.InvalidDocument, st.ErrorCode);
    }

    [Fact]
    public async Task Xps_prints_as_images_through_an_installed_driver()
    {
        if (Winspool.FindPrinter("Microsoft Print to PDF") is null) { output.WriteLine("SKIPPED: Microsoft Print to PDF not present"); return; }
        var dir = TempDir();
        var src = TestXps.Create(Path.Combine(dir, "in.xps"), 2, TestXps.A3Landscape);
        var dst = Path.Combine(dir, "out.pdf");
        var outcome = await new DocumentPrinter().PrintAsync(new PrintRequest
        {
            JobId = Guid.NewGuid(), DocumentPath = src, DocumentName = "itest-xps-raster", PrinterName = "Microsoft Print to PDF",
            XpsMethod = XpsPrintMethod.Raster, PrintToFile = dst, TrackTimeout = TimeSpan.FromSeconds(30),
        }, null, default);
        Assert.Equal(JobState.Completed, outcome.State);
        for (int i = 0; i < 50 && !File.Exists(dst); i++) await Task.Delay(100);
        using var pdf = PdfRenderer.Open(dst);
        Assert.Equal(2, pdf.PageCount);
        Assert.InRange(pdf.PageSizePoints(0).Width, 1185, 1196); // A3 landscape chosen on the driver
    }

    [Fact]
    public async Task Xps_prints_as_vectors_with_paper_orientation_and_copies()
    {
        if (Winspool.FindPrinter(TestQueue) is null)
        {
            output.WriteLine($"SKIPPED: '{TestQueue}' not installed (driver\\Install-TestDriver.ps1)");
            return;
        }
        var dir = TempDir();
        var src = TestXps.Create(Path.Combine(dir, "in.xps"), 2, TestXps.A3Landscape);
        var captured = Path.Combine(dir, "captured.xps");

        // Stand in for the client: read what the test queue's port delivers.
        using var pipe = new NamedPipeServerStream(TestPipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var capture = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync();
            await using var fs = File.Create(captured);
            await pipe.CopyToAsync(fs);
        });

        var outcome = await new DocumentPrinter().PrintAsync(new PrintRequest
        {
            JobId = Guid.NewGuid(), DocumentPath = src, DocumentName = "itest-xps-vector", PrinterName = TestQueue,
            XpsMethod = XpsPrintMethod.Vector, Settings = new PrintSettings { Copies = 2 }, TrackTimeout = TimeSpan.FromSeconds(30),
        }, null, default);
        Assert.Equal(JobState.Completed, outcome.State);
        Assert.True(await Task.WhenAny(capture, Task.Delay(20000)) == capture, "the test queue did not deliver the job");

        var info = XpsInspector.Inspect(captured);
        Assert.Null(info.Problem);
        Assert.Equal(2, info.PageCount);
        Assert.InRange(info.Pages[0].WidthMm, 415, 425);
        Assert.Equal(2, info.Copies);
        Assert.Equal("ISOA3", info.MediaSize);
        Assert.Equal("Landscape", info.Orientation);
    }
}
