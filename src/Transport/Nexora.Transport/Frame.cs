using System.Buffers.Binary;

namespace Nexora.Transport;

/// <summary>NXP/2 frame types. See docs/NETWORK_PROTOCOL.md §3.</summary>
public enum FrameType : byte
{
    // Handshake (plaintext)
    Hello = 0x01,
    HelloReply = 0x02,
    Auth = 0x03,
    // Everything below is AES-256-GCM protected
    AuthOk = 0x04,
    JobOffer = 0x10,
    OfferReply = 0x11,
    Data = 0x12,
    DataEnd = 0x13,
    JobResult = 0x14,
    StatusQuery = 0x20,
    StatusReply = 0x21,
    Cancel = 0x22,
    CancelReply = 0x23,
    InfoQuery = 0x30,
    InfoReply = 0x31,
    Error = 0x7F,
}

public readonly record struct Frame(FrameType Type, ReadOnlyMemory<byte> Payload);

/// <summary>
/// Wire framing: "NX" magic (2) | type (1) | reserved (1) | payload length (u32 big-endian) | payload.
/// </summary>
public static class FrameCodec
{
    public const int HeaderSize = 8;
    public const int MaxPayload = 1024 * 1024 + 64; // 1 MiB data chunk + AEAD tag + slack
    private const byte M0 = (byte)'N', M1 = (byte)'X';

    public static void WriteHeader(Span<byte> header, FrameType type, int length)
    {
        header[0] = M0;
        header[1] = M1;
        header[2] = (byte)type;
        header[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)length);
    }

    public static async Task WriteAsync(Stream stream, FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new ProtocolException(Shared.Jobs.ErrorCodes.BadRequest, "Frame too large");
        var header = new byte[HeaderSize];
        WriteHeader(header, type, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<Frame> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (header[0] != M0 || header[1] != M1)
            throw new ProtocolException(Shared.Jobs.ErrorCodes.BadRequest, "Not a Nexora NXP/2 stream (bad magic)");
        var type = (FrameType)header[2];
        if (!Enum.IsDefined(type)) throw new ProtocolException(Shared.Jobs.ErrorCodes.BadRequest, $"Unknown frame type 0x{header[2]:X2}");
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        if (len > MaxPayload) throw new ProtocolException(Shared.Jobs.ErrorCodes.BadRequest, $"Frame length {len} exceeds limit");
        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);
        return new Frame(type, payload);
    }

    private static async Task ReadExactAsync(Stream s, Memory<byte> buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf[read..], ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Connection closed by peer");
            read += n;
        }
    }
}

/// <summary>A protocol-level failure with a stable error code.</summary>
public sealed class ProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
