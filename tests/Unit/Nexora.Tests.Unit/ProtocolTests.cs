using System.IO.Pipelines;
using System.Text;
using Nexora.Discovery;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Pdf;
using Nexora.Shared.Security;
using Nexora.Transport;
using Xunit;

namespace Nexora.Tests.Unit;

/// <summary>An in-memory full-duplex stream pair for protocol tests.</summary>
internal sealed class DuplexPipe
{
    public Stream A { get; }
    public Stream B { get; }

    public DuplexPipe()
    {
        var ab = new Pipe();
        var ba = new Pipe();
        A = new Duplex(ba.Reader.AsStream(), ab.Writer.AsStream());
        B = new Duplex(ab.Reader.AsStream(), ba.Writer.AsStream());
    }

    private sealed class Duplex(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] buffer, int offset, int count) => read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => read.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => write.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

public class FrameCodecTests
{
    [Fact]
    public async Task Round_trip()
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, FrameType.JobOffer, "hello"u8.ToArray(), default);
        ms.Position = 0;
        var f = await FrameCodec.ReadAsync(ms, default);
        Assert.Equal(FrameType.JobOffer, f.Type);
        Assert.Equal("hello", Encoding.UTF8.GetString(f.Payload.Span));
    }

    [Fact]
    public async Task Bad_magic_is_rejected()
    {
        var ms = new MemoryStream(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"));
        var e = await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(ms, default));
        Assert.Equal(ErrorCodes.BadRequest, e.Code);
    }

    [Fact]
    public async Task Oversized_length_is_rejected_before_allocation()
    {
        var header = new byte[8];
        FrameCodec.WriteHeader(header, FrameType.Data, int.MaxValue);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => FrameCodec.ReadAsync(new MemoryStream(header), default));
        Assert.Contains("exceeds", e.Message);
    }

    [Fact]
    public async Task Truncated_stream_throws_end_of_stream()
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, FrameType.Data, new byte[100], default);
        var cut = new MemoryStream(ms.ToArray()[..50]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => FrameCodec.ReadAsync(cut, default));
    }
}

public class HandshakeTests
{
    private static Task<(Handshake.ClientResult C, Handshake.ServerResult S)> Run(PairingKey clientKey, PairingKey serverKey, out DuplexPipe pipe)
    {
        var p = pipe = new DuplexPipe();
        var server = Handshake.ServerAsync(p.B, serverKey, "rx1", "Receiver", _ => new AuthOkMessage("rx1", "1.0", true, 1000, 1024), default);
        var client = Handshake.ClientAsync(p.A, clientKey, "cl1", "Client", default);
        return Both(client, server);

        static async Task<(Handshake.ClientResult, Handshake.ServerResult)> Both(Task<Handshake.ClientResult> c, Task<Handshake.ServerResult> s) => (await c, await s);
    }

    [Fact]
    public async Task Matching_keys_authenticate_and_exchange_encrypted_frames()
    {
        var k = PairingKey.Generate();
        var (c, s) = await Run(k, k, out _);
        Assert.True(c.Session.TestMode);
        Assert.Equal("cl1", s.Hello.ClientId);

        await c.Channel.SendAsync(FrameType.Data, "secret document bytes"u8.ToArray(), default);
        var f = await s.Channel.ReceiveAsync(default);
        Assert.Equal("secret document bytes", Encoding.UTF8.GetString(f.Payload.Span));

        await s.Channel.SendJsonAsync(FrameType.StatusReply, new StatusReplyMessage([]), default);
        var reply = await c.Channel.ReceiveJsonAsync<StatusReplyMessage>(FrameType.StatusReply, default);
        Assert.Empty(reply.Jobs);
    }

    [Fact]
    public async Task Client_with_wrong_key_rejects_receiver()
    {
        var e = await Assert.ThrowsAsync<ProtocolException>(() => Run(PairingKey.Generate(), PairingKey.Generate(), out _));
        Assert.Equal(ErrorCodes.AuthFailed, e.Code);
    }

    [Fact]
    public async Task Encrypted_payload_does_not_contain_plaintext()
    {
        var k = PairingKey.Generate();
        var rec = new RecordingStream();
        var ch = new SecureChannel_TestAccess(rec, k);
        await ch.Channel.SendAsync(FrameType.Data, "TOP SECRET DRAWING"u8.ToArray(), default);
        Assert.DoesNotContain("TOP SECRET", Encoding.ASCII.GetString(rec.ToArray()));
    }

    [Fact]
    public async Task Tampered_frame_is_detected()
    {
        var k = PairingKey.Generate();
        var rec = new RecordingStream();
        var sender = new SecureChannel_TestAccess(rec, k);
        await sender.Channel.SendAsync(FrameType.Data, "abcdef"u8.ToArray(), default);
        var bytes = rec.ToArray();
        bytes[10] ^= 0x01; // flip one ciphertext bit
        var receiver = new SecureChannel_TestAccess(new MemoryStream(bytes), k, asServer: true);
        var e = await Assert.ThrowsAsync<ProtocolException>(() => receiver.Channel.ReceiveAsync(default));
        Assert.Equal(ErrorCodes.AuthFailed, e.Code);
    }

    [Fact]
    public async Task Replayed_frame_is_detected()
    {
        var k = PairingKey.Generate();
        var rec = new RecordingStream();
        var sender = new SecureChannel_TestAccess(rec, k);
        await sender.Channel.SendAsync(FrameType.Data, "once"u8.ToArray(), default);
        var frame = rec.ToArray();
        var receiver = new SecureChannel_TestAccess(new MemoryStream([.. frame, .. frame]), k, asServer: true);
        await receiver.Channel.ReceiveAsync(default);
        await Assert.ThrowsAsync<ProtocolException>(() => receiver.Channel.ReceiveAsync(default));
    }

    private sealed class RecordingStream : MemoryStream;
}

/// <summary>Builds a SecureChannel with fixed nonces (test-only access to the internal constructor).</summary>
internal sealed class SecureChannel_TestAccess
{
    public SecureChannel Channel { get; }

    public SecureChannel_TestAccess(Stream s, PairingKey k, bool asServer = false)
    {
        var (c2s, s2c) = KeySchedule.SessionKeys(k, new byte[32], new byte[32]);
        Channel = asServer ? new SecureChannel(s, s2c, c2s, isClient: false) : new SecureChannel(s, c2s, s2c, isClient: true);
    }
}

public class DiscoveryCodecTests
{
    private static ReceiverAnnouncement Body(string nonce) => new()
    {
        Protocol = ReceiverAnnouncement.ProtocolTag, Version = 2, ReceiverId = "abc", ServerName = "HarshalOza-PC", TcpPort = 9102,
        AppVersion = "1.0.0", TestMode = true, Status = "ready", Nonce = nonce,
    };

    [Fact]
    public void Reply_signed_with_same_key_is_trusted()
    {
        var k = PairingKey.Generate();
        var bytes = DiscoveryCodec.EncodeReply(Body("n1"), k);
        var body = DiscoveryCodec.DecodeReply(bytes, "n1", k, out bool trusted);
        Assert.NotNull(body);
        Assert.True(trusted);
        Assert.Equal("HarshalOza-PC", body!.ServerName);
    }

    [Fact]
    public void Reply_from_unpaired_receiver_is_listed_but_untrusted()
    {
        var bytes = DiscoveryCodec.EncodeReply(Body("n1"), PairingKey.Generate());
        Assert.NotNull(DiscoveryCodec.DecodeReply(bytes, "n1", PairingKey.Generate(), out bool trusted));
        Assert.False(trusted);
    }

    [Fact]
    public void Modified_reply_loses_trust()
    {
        var k = PairingKey.Generate();
        var text = Encoding.UTF8.GetString(DiscoveryCodec.EncodeReply(Body("n1"), k)).Replace("9102", "9103");
        Assert.NotNull(DiscoveryCodec.DecodeReply(Encoding.UTF8.GetBytes(text), "n1", k, out bool trusted));
        Assert.False(trusted);
    }

    [Fact]
    public void Stale_nonce_is_ignored()
    {
        var k = PairingKey.Generate();
        Assert.Null(DiscoveryCodec.DecodeReply(DiscoveryCodec.EncodeReply(Body("old"), k), "new", k, out _));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"body\":5}")]
    [InlineData("{\"protocol\":\"NexoraDiscovery/1\"}")]
    public void Garbage_is_ignored(string s) => Assert.Null(DiscoveryCodec.DecodeReply(Encoding.UTF8.GetBytes(s), "n", null, out _));

    [Theory]
    [InlineData("192.168.1.14", 24, "192.168.1.255")]
    [InlineData("192.168.0.43", 24, "192.168.0.255")]
    [InlineData("10.1.2.3", 8, "10.255.255.255")]
    [InlineData("172.16.5.9", 20, "172.16.15.255")]
    public void Directed_broadcast_address(string ip, int prefix, string expected) =>
        Assert.Equal(expected, DiscoveryClient.DirectedBroadcast(System.Net.IPAddress.Parse(ip), prefix).ToString());

    [Fact]
    public void Auto_select_picks_single_trusted_receiver_only()
    {
        DiscoveredReceiver R(string id, bool trusted) => new(Body("n") with { ReceiverId = id }, System.Net.IPAddress.Loopback, trusted, DateTimeOffset.UtcNow);
        Assert.Equal("a", DiscoveryClient.AutoSelect([R("a", true), R("b", false)], null)!.Announcement.ReceiverId);
        Assert.Null(DiscoveryClient.AutoSelect([R("a", true), R("b", true)], null));
        Assert.Equal("b", DiscoveryClient.AutoSelect([R("a", true), R("b", true)], "b")!.Announcement.ReceiverId);
        Assert.Null(DiscoveryClient.AutoSelect([R("a", false)], "a"));
    }
}

public class PdfAndStateTests
{
    [Fact]
    public void Generated_pdf_is_valid_with_correct_page_count()
    {
        var pdf = TestPdfBuilder.Build("x", pages: 3);
        var r = PdfInspector.Inspect(new MemoryStream(pdf));
        Assert.True(r.IsPdf);
        Assert.True(r.HasEof);
        Assert.Equal(3, r.PageCount);
    }

    [Fact]
    public void Padding_produces_requested_size()
    {
        var pdf = TestPdfBuilder.Build("x", padToBytes: 2_000_000);
        Assert.InRange(pdf.Length, 1_990_000, 2_010_000);
        Assert.True(PdfInspector.Inspect(new MemoryStream(pdf)).HasEof);
    }

    [Fact]
    public void Non_pdf_and_truncated_pdf_are_flagged()
    {
        Assert.False(PdfInspector.Inspect(new MemoryStream(Encoding.ASCII.GetBytes(new string('A', 200)))).IsPdf);
        var pdf = TestPdfBuilder.Build("x");
        var r = PdfInspector.Inspect(new MemoryStream(pdf[..(pdf.Length - 40)]));
        Assert.True(r.IsPdf);
        Assert.False(r.HasEof);
    }

    [Fact]
    public void Job_states_round_trip_on_the_wire()
    {
        foreach (var s in Enum.GetValues<JobState>())
            Assert.Equal(s, JobStateExtensions.FromWire(s.ToWire()));
        Assert.Equal("\"SUBMITTED_TO_PRINTER\"", Json.ToText(JobState.SubmittedToPrinter));
        Assert.True(JobState.Completed.IsTerminal());
        Assert.False(JobState.Validated.IsTerminal());
    }

    [Fact]
    public void Unknown_state_is_rejected() => Assert.Throws<FormatException>(() => JobStateExtensions.FromWire("PRINTED_PROBABLY"));
}
