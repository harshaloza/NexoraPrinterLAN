using Nexora.Client.Core;
using Nexora.PrinterIntegration;
using Nexora.PrintPipeline;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Pdf;
using Nexora.Shared.Security;
using Nexora.Shared.Windows;
using Xunit;
using Xunit.Abstractions;

namespace Nexora.Tests.Integration;

public class ClientEngineTests(ITestOutputHelper output)
{
    private static ClientEngine NewEngine(string dir, int port, PairingKey key, bool capture = false)
    {
        var e = new ClientEngine(new NexoraPaths("Client", dir), Log.Memory()) { EnableCapture = capture };
        e.UpdateConfig(c =>
        {
            c.AutoDiscovery = false;
            c.ManualReceiverAddress = $"127.0.0.1:{port}";
            c.SetPairingKey(key);
            c.StatusPollSeconds = 1;
            c.ConnectTimeoutSeconds = 2;
        });
        return e;
    }

    private static async Task<ClientJobRecord> WaitFor(ClientEngine e, Guid id, Func<ClientJobRecord, bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var r = e.Outbox.Get(id);
            if (r is not null && done(r)) return r;
            await Task.Delay(200);
        }
        return e.Outbox.Get(id)!;
    }

    [Fact]
    public async Task Engine_delivers_submitted_pdf_and_tracks_final_status()
    {
        await using var rx = await TestReceiver.StartAsync();
        var dir = Directory.CreateTempSubdirectory("nexora-cx").FullName;
        await using var engine = NewEngine(dir, rx.Host.TransportPort, rx.Key);
        engine.Start();
        var job = engine.SubmitTestPage();
        var r = await WaitFor(engine, job.Metadata.JobId, j => j.ReceiverFinal, 20);
        Assert.Equal(JobState.Validated, r.State);
        Assert.Equal(rx.Host.Config.ReceiverName, r.ReceiverName);
        Assert.NotNull(rx.Host.Store.Get(job.Metadata.JobId));
    }

    [Fact] // Phase 7 #7 retry + #10 client restart
    public async Task Queued_job_survives_client_restart_and_is_retried_until_receiver_appears()
    {
        int port = TestReceiver.FreeTcpPort();
        var key = PairingKey.Generate();
        var dir = Directory.CreateTempSubdirectory("nexora-cx").FullName;
        Guid id;
        await using (var e1 = NewEngine(dir, port, key))
        {
            e1.Start();
            id = e1.SubmitTestPage().Metadata.JobId;
            var r = await WaitFor(e1, id, j => j.Attempts >= 1 && j.State == JobState.Queued && j.ErrorCode is not null, 15);
            Assert.Equal(ErrorCodes.Unreachable, r.ErrorCode);
            Assert.NotNull(r.NextAttemptUtc);
        } // client "crashes"/exits with the job still queued

        var rxDir = Directory.CreateTempSubdirectory("nexora-rx").FullName;
        await using var rx = await TestReceiver.StartAsync(c => { c.TransportPort = port; c.SetPairingKey(key); }, rxDir);
        await using var e2 = NewEngine(dir, port, key);
        e2.Start();
        var final = await WaitFor(e2, id, j => j.ReceiverFinal || j.State.IsTerminal(), 30);
        Assert.Equal(JobState.Validated, final.State);
        Assert.True(final.Attempts >= 2);
    }

    [Fact]
    public async Task Wrong_pairing_code_keeps_job_queued_with_clear_error()
    {
        await using var rx = await TestReceiver.StartAsync();
        var dir = Directory.CreateTempSubdirectory("nexora-cx").FullName;
        await using var engine = NewEngine(dir, rx.Host.TransportPort, PairingKey.Generate());
        engine.Start();
        var id = engine.SubmitTestPage().Metadata.JobId;
        var r = await WaitFor(engine, id, j => j.ErrorCode is not null, 10);
        Assert.Equal(JobState.Queued, r.State);
        Assert.Equal(ErrorCodes.AuthFailed, r.ErrorCode);
        Assert.Null(rx.Host.Store.Get(id));
    }

    [Fact]
    public async Task Entering_the_pairing_code_sends_waiting_jobs_without_backoff_delay()
    {
        await using var rx = await TestReceiver.StartAsync();
        var dir = Directory.CreateTempSubdirectory("nexora-cx").FullName;
        await using var engine = NewEngine(dir, rx.Host.TransportPort, PairingKey.Generate());
        engine.Start();
        var id = engine.SubmitTestPage().Metadata.JobId;
        // Let a few attempts fail so the backoff grows beyond the wait below.
        await WaitFor(engine, id, j => j.Attempts >= 3, 40);

        engine.UpdateConfig(c => c.SetPairingKey(rx.Key));
        var r = await WaitFor(engine, id, j => j.ReceiverFinal, 4);
        Assert.Equal(JobState.Validated, r.State);
    }

    [Fact] // Phase 7 #13
    public void Windows_queue_state_is_reported()
    {
        var q = QueueInspector.Inspect();
        output.WriteLine($"Queue: {q.Summary} port={q.PortName} driver={q.DriverName} driverAvailable={q.DriverAvailable}");
        Assert.True(q.DriverAvailable, "Microsoft Print To PDF driver should be installed on Windows 10/11");
        if (q.Exists) Assert.True(q.Healthy, q.Summary);
    }

    [Fact] // Phase 7 #14: real spooler → inbox driver → named pipe → client → receiver
    public async Task Printing_to_the_windows_queue_is_captured_and_delivered()
    {
        var q = QueueInspector.Inspect();
        if (!q.Healthy)
        {
            output.WriteLine($"SKIPPED: queue '{NexoraInfo.PrinterQueueName}' not installed ({q.Summary}). Run scripts\\Install-NexoraQueue.ps1.");
            return;
        }
        await using var rx = await TestReceiver.StartAsync();
        var dir = Directory.CreateTempSubdirectory("nexora-cx").FullName;
        await using var engine = NewEngine(dir, rx.Host.TransportPort, rx.Key, capture: true);
        engine.Start();
        if (!engine.CaptureListening)
        {
            output.WriteLine($"SKIPPED: pipe busy (is Nexora Client running?): {engine.CaptureError}");
            return;
        }

        var src = Path.Combine(dir, "a3-landscape.pdf");
        File.WriteAllBytes(src, TestPdfBuilder.Build("Queue capture test", pages: 2, TestPdfBuilder.A3, landscape: true));
        var outcome = await new GdiPdfPrinter().PrintAsync(new PrintRequest
        {
            JobId = Guid.NewGuid(), DocumentPath = src, DocumentName = "itest-queue-capture", PrinterName = NexoraInfo.PrinterQueueName,
            TrackTimeout = TimeSpan.FromSeconds(30),
        }, null, default);
        Assert.Equal(JobState.Completed, outcome.State);

        ClientJobRecord? rec = null;
        for (int i = 0; i < 100 && rec is not { ReceiverFinal: true }; i++)
        {
            await Task.Delay(200);
            rec = engine.Outbox.All().FirstOrDefault(j => j.Metadata.DocumentName.Contains("itest-queue-capture"));
        }
        Assert.NotNull(rec);
        Assert.Equal(JobState.Validated, rec!.State);
        var stored = rx.Host.Store.Get(rec.Metadata.JobId)!.StoredFile!;
        using var pdf = PdfRenderer.Open(stored);
        Assert.Equal(2, pdf.PageCount);
        var size = pdf.PageSizePoints(0);
        Assert.InRange(size.Width, 1185, 1196);  // A3 landscape preserved through the Windows print pipeline
        Assert.InRange(size.Height, 837, 847);
        output.WriteLine($"Captured {rec.Metadata.SizeBytes} bytes; stored {stored}");
    }

    [Fact]
    public async Task Printed_copy_is_removed_unless_keep_copies_is_on()
    {
        if (Winspool.FindPrinter("Microsoft Print to PDF") is null)
        {
            output.WriteLine("SKIPPED: Microsoft Print to PDF queue not present");
            return;
        }
        var outDir = Directory.CreateTempSubdirectory("nexora-printout").FullName;
        Nexora.Receiver.Core.PrintQueue.TestPrintToFile = id => Path.Combine(outDir, id.ToString("N") + ".pdf");
        try
        {
            foreach (bool keep in new[] { false, true })
            {
                await using var rx = await TestReceiver.StartAsync(c => { c.TestMode = false; c.DestinationPrinter = "Microsoft Print to PDF"; c.KeepPrintedCopies = keep; });
                var (path, meta) = Jobs.MakePdf(pages: 2);
                await using var s = await rx.ConnectAsync();
                await s.SendJobAsync(meta, path, null, default);
                JobStatus? st = null;
                for (int i = 0; i < 150 && st?.State is not (JobState.Completed or JobState.Failed); i++)
                {
                    await Task.Delay(200);
                    st = (await s.QueryStatusAsync([meta.JobId], default))[0];
                }
                Assert.Equal(JobState.Completed, st!.State);
                var rec = rx.Host.Store.Get(meta.JobId)!;
                if (keep) Assert.True(File.Exists(rec.StoredFile), "copy should be kept");
                else
                {
                    Assert.Null(rec.StoredFile);
                    Assert.Empty(Directory.GetFiles(rx.Host.Store.ReceivedFolder, "*.pdf"));
                    Assert.Contains("copy removed", st.Message);
                }
                Assert.True(File.Exists(Path.Combine(outDir, meta.JobId.ToString("N") + ".pdf")), "driver output should exist");
            }
        }
        finally
        {
            Nexora.Receiver.Core.PrintQueue.TestPrintToFile = null;
        }
    }

    [Fact] // receiver print path through a real installed driver (PDF output, no hardware)
    public async Task Receiver_print_path_renders_through_installed_driver()
    {
        if (Winspool.FindPrinter("Microsoft Print to PDF") is null)
        {
            output.WriteLine("SKIPPED: Microsoft Print to PDF queue not present");
            return;
        }
        var dir = Directory.CreateTempSubdirectory("nexora-print").FullName;
        var src = Path.Combine(dir, "in.pdf");
        var dst = Path.Combine(dir, "out.pdf");
        File.WriteAllBytes(src, TestPdfBuilder.Build("Print path", pages: 3, TestPdfBuilder.A4));
        var outcome = await new GdiPdfPrinter().PrintAsync(new PrintRequest
        {
            JobId = Guid.NewGuid(), DocumentPath = src, DocumentName = "itest-print-path", PrinterName = "Microsoft Print to PDF",
            PrintToFile = dst, TrackTimeout = TimeSpan.FromSeconds(30),
        }, null, default);
        Assert.Equal(JobState.Completed, outcome.State);
        Assert.Equal(3, outcome.PagesSent);
        for (int i = 0; i < 50 && !File.Exists(dst); i++) await Task.Delay(100);
        using var pdf = PdfRenderer.Open(dst);
        Assert.Equal(3, pdf.PageCount);
        Assert.InRange(pdf.PageSizePoints(0).Width, 590, 600); // A4 portrait selected on the driver
    }
}
