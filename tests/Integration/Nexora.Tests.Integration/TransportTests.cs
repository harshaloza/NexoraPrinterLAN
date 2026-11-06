using System.Net;
using Nexora.Discovery;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Security;
using Nexora.Transport;
using Xunit;

namespace Nexora.Tests.Integration;

public class TransportTests
{
    [Fact] // Phase 7 #1
    public async Task Discovery_finds_receiver_and_marks_it_trusted()
    {
        await using var rx = await TestReceiver.StartAsync();
        var found = await DiscoveryClient.DiscoverAsync(rx.Host.DiscoveryPort, rx.Key, "itest", TimeSpan.FromSeconds(1.5), default, [IPAddress.Loopback]);
        var r = Assert.Single(found);
        Assert.True(r.Trusted);
        Assert.Equal(rx.Host.Config.ReceiverId, r.Announcement.ReceiverId);
        Assert.Equal(rx.Host.TransportPort, r.Announcement.TcpPort);
        Assert.Equal("ready", r.Announcement.Status);
        Assert.Same(r, DiscoveryClient.AutoSelect(found, null));

        var untrusted = await DiscoveryClient.DiscoverAsync(rx.Host.DiscoveryPort, PairingKey.Generate(), "itest", TimeSpan.FromSeconds(1.5), default, [IPAddress.Loopback]);
        Assert.False(Assert.Single(untrusted).Trusted);
    }

    [Fact] // #2
    public async Task Connection_and_receiver_info()
    {
        await using var rx = await TestReceiver.StartAsync();
        await using var s = await rx.ConnectAsync();
        var info = await s.GetInfoAsync(default);
        Assert.True(info.TestMode);
        Assert.Equal(rx.Host.Config.ReceiverName, info.ReceiverName);
        Assert.DoesNotContain(info.Printers, p => p.Name == "Nexora Printer LAN"); // never offered as a destination
    }

    [Fact]
    public async Task Wrong_pairing_code_is_rejected()
    {
        await using var rx = await TestReceiver.StartAsync();
        var e = await Assert.ThrowsAsync<ProtocolException>(() => rx.ConnectAsync(PairingKey.Generate()));
        Assert.Equal(ErrorCodes.AuthFailed, e.Code);
    }

    [Fact] // #3 + #5
    public async Task Small_pdf_arrives_intact_and_is_saved_in_test_mode()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf(pages: 2);
        await using var s = await rx.ConnectAsync();
        var st = await s.SendJobAsync(meta, path, null, default);

        Assert.Equal(JobState.Validated, st.State);
        Assert.True(st.IsFinal);
        var rec = rx.Host.Store.Get(meta.JobId)!;
        Assert.Equal(meta.Sha256, Hashing.Sha256File(rec.StoredFile!));
        Assert.StartsWith(rx.Host.Store.ReceivedFolder, rec.StoredFile);
        Assert.True(File.Exists(Path.ChangeExtension(rec.StoredFile!, ".json")));
        Assert.Contains(rec.History, h => h.State == JobState.Received);
    }

    [Fact] // #4
    public async Task Large_pdf_is_chunked_and_verified()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf(padTo: 60L * 1024 * 1024);
        await using var s = await rx.ConnectAsync();
        long reports = 0;
        var st = await s.SendJobAsync(meta, path, new Progress<TransferProgress>(_ => Interlocked.Increment(ref reports)), default);
        Assert.Equal(JobState.Validated, st.State);
        Assert.Equal(meta.Sha256, Hashing.Sha256File(rx.Host.Store.Get(meta.JobId)!.StoredFile!));
        Assert.True(Interlocked.Read(ref reports) > 100);
    }

    [Fact] // #5
    public async Task Checksum_mismatch_is_detected_and_not_saved()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf();
        meta = meta with { Sha256 = new string('a', 64) };
        await using var s = await rx.ConnectAsync();
        var st = await s.SendJobAsync(meta, path, null, default);
        Assert.Equal(JobState.Failed, st.State);
        Assert.Equal(ErrorCodes.ChecksumMismatch, st.ErrorCode);
        Assert.Empty(Directory.GetFiles(rx.Host.Store.ReceivedFolder, "*.pdf"));
    }

    [Fact] // #6
    public async Task Interrupted_transfer_resumes_from_the_received_offset()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf(padTo: 8L * 1024 * 1024);
        await using (var s1 = await rx.ConnectAsync())
            await Assert.ThrowsAsync<IOException>(() => s1.SendJobAsync(meta, path, null, default, stopAfterBytesForTest: 3 * 1024 * 1024));
        await Task.Delay(300); // let the receiver notice the drop and flush the partial file

        long resumedFrom = -1;
        await using var s2 = await rx.ConnectAsync();
        var st = await s2.SendJobAsync(meta, path, new Progress<TransferProgress>(p => resumedFrom = p.ResumedFrom), default);
        Assert.Equal(JobState.Validated, st.State);
        await Task.Delay(100);
        Assert.True(resumedFrom >= 3 * 1024 * 1024, $"resumed from {resumedFrom}");
        Assert.Equal(meta.Sha256, Hashing.Sha256File(rx.Host.Store.Get(meta.JobId)!.StoredFile!));
    }

    [Fact] // #8
    public async Task Duplicate_job_is_not_stored_twice()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (path, meta) = Jobs.MakePdf();
        await using var s = await rx.ConnectAsync();
        await s.SendJobAsync(meta, path, null, default);
        var again = await s.SendJobAsync(meta, path, null, default);
        Assert.Equal(JobState.Validated, again.State);
        Assert.Single(Directory.GetFiles(rx.Host.Store.ReceivedFolder, "*.pdf"));
    }

    [Fact] // #9
    public async Task Receiver_restart_keeps_job_records_and_partial_uploads()
    {
        await using var rx = await TestReceiver.StartAsync();
        var (p1, done) = Jobs.MakePdf();
        await using (var s = await rx.ConnectAsync()) await s.SendJobAsync(done, p1, null, default);
        var (p2, partial) = Jobs.MakePdf(padTo: 4L * 1024 * 1024);
        await using (var s = await rx.ConnectAsync())
            await Assert.ThrowsAsync<IOException>(() => s.SendJobAsync(partial, p2, null, default, stopAfterBytesForTest: 1024 * 1024));
        await Task.Delay(300);

        await rx.RestartAsync();

        await using var s3 = await rx.ConnectAsync();
        var statuses = await s3.QueryStatusAsync([done.JobId], default);
        Assert.Equal(JobState.Validated, Assert.Single(statuses).State);
        long resumed = -1;
        var st = await s3.SendJobAsync(partial, p2, new Progress<TransferProgress>(p => resumed = p.ResumedFrom), default);
        Assert.Equal(JobState.Validated, st.State);
        await Task.Delay(100);
        Assert.True(resumed > 0);
    }

    [Fact] // #11
    public async Task Non_pdf_payload_is_rejected_and_quarantined()
    {
        await using var rx = await TestReceiver.StartAsync();
        var path = Path.Combine(Directory.CreateTempSubdirectory("nexora-bad").FullName, "evil.pdf");
        File.WriteAllText(path, "MZ this is an executable pretending to be a PDF ............................");
        await using var s = await rx.ConnectAsync();
        var st = await s.SendJobAsync(Jobs.Meta(path, name: @"..\..\evil.pdf"), path, null, default);
        Assert.Equal(JobState.Failed, st.State);
        Assert.Equal(ErrorCodes.InvalidDocument, st.ErrorCode);
        Assert.Empty(Directory.GetFiles(rx.Host.Store.ReceivedFolder));
    }

    [Fact]
    public async Task Oversized_and_unsupported_offers_are_rejected_before_transfer()
    {
        await using var rx = await TestReceiver.StartAsync(c => c.MaxJobBytes = 4096);
        var (path, meta) = Jobs.MakePdf(padTo: 10_000);
        await using var s = await rx.ConnectAsync();
        var e = await Assert.ThrowsAsync<ProtocolException>(() => s.SendJobAsync(meta, path, null, default));
        Assert.Equal(ErrorCodes.TooLarge, e.Code);

        var (p2, m2) = Jobs.MakePdf();
        var e2 = await Assert.ThrowsAsync<ProtocolException>(() => s.SendJobAsync(m2 with { Format = "application/x-msdownload" }, p2, null, default));
        Assert.Equal(ErrorCodes.UnsupportedFormat, e2.Code);

        var e3 = await Assert.ThrowsAsync<ProtocolException>(() => s.SendJobAsync(m2 with { ClientId = "someone-else" }, p2, null, default));
        Assert.Equal(ErrorCodes.BadRequest, e3.Code);
    }

    [Fact] // #12
    public async Task Clients_outside_allowed_networks_are_refused()
    {
        await using var rx = await TestReceiver.StartAsync(c => c.AllowLoopback = false);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => rx.ConnectAsync());
        Assert.Equal(ErrorCodes.NotAllowed, e.Code);

        // Discovery must stay silent for refused clients too.
        var found = await DiscoveryClient.DiscoverAsync(rx.Host.DiscoveryPort, rx.Key, "itest", TimeSpan.FromSeconds(1), default, [IPAddress.Loopback]);
        Assert.Empty(found);
    }

    [Fact]
    public async Task Print_mode_without_installed_printer_reports_failure_honestly()
    {
        await using var rx = await TestReceiver.StartAsync(c => { c.TestMode = false; c.DestinationPrinter = "No Such Printer 123"; });
        var (path, meta) = Jobs.MakePdf();
        await using var s = await rx.ConnectAsync();
        await s.SendJobAsync(meta, path, null, default);

        JobStatus? st = null;
        for (int i = 0; i < 50 && st?.State != JobState.Failed; i++)
        {
            await Task.Delay(100);
            st = (await s.QueryStatusAsync([meta.JobId], default))[0];
        }
        Assert.Equal(JobState.Failed, st!.State);
        Assert.Equal(ErrorCodes.PrinterUnavailable, st.ErrorCode);
        Assert.True(File.Exists(rx.Host.Store.Get(meta.JobId)!.StoredFile)); // the document itself is still saved
    }
}
