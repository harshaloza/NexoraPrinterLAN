using System.Buffers.Binary;
using System.Security.Cryptography;
using Nexora.Shared;
using Nexora.Shared.Jobs;

namespace Nexora.Transport;

/// <summary>
/// An authenticated, encrypted NXP/2 session over a stream. Each frame payload is AES-256-GCM sealed
/// with a per-direction key; the nonce is a direction tag plus a strictly increasing 64-bit counter, and
/// the frame type and counter are bound as associated data, so frames cannot be reordered, replayed,
/// re-typed or modified without detection.
/// </summary>
public sealed class SecureChannel : IAsyncDisposable
{
    private const int TagSize = 16;
    private readonly Stream _stream;
    private readonly AesGcm _send, _recv;
    private readonly uint _sendDir, _recvDir;
    private ulong _sendSeq, _recvSeq;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    internal SecureChannel(Stream stream, byte[] sendKey, byte[] recvKey, bool isClient)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
        _sendDir = isClient ? 1u : 2u;
        _recvDir = isClient ? 2u : 1u;
    }

    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public async Task SendAsync(FrameType type, ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sealedPayload = new byte[plaintext.Length + TagSize];
            Span<byte> nonce = stackalloc byte[12];
            Span<byte> aad = stackalloc byte[9];
            Fill(nonce, aad, _sendDir, _sendSeq, type);
            _send.Encrypt(nonce, plaintext.Span, sealedPayload.AsSpan(0, plaintext.Length), sealedPayload.AsSpan(plaintext.Length), aad);
            _sendSeq++;
            await FrameCodec.WriteAsync(_stream, type, sealedPayload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task SendJsonAsync<T>(FrameType type, T message, CancellationToken ct) => SendAsync(type, Json.ToBytes(message), ct);

    public async Task<Frame> ReceiveAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);
        Frame frame;
        try
        {
            frame = await FrameCodec.ReadAsync(_stream, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProtocolException(ErrorCodes.Timeout, $"No data from peer for {ReadTimeout.TotalSeconds:0}s");
        }
        if (frame.Payload.Length < TagSize) throw new ProtocolException(ErrorCodes.AuthFailed, "Frame too short for authentication tag");
        var plain = new byte[frame.Payload.Length - TagSize];
        byte[] nonce = new byte[12];
        byte[] aad = new byte[9];
        Fill(nonce, aad, _recvDir, _recvSeq, frame.Type);
        try
        {
            var p = frame.Payload.Span;
            _recv.Decrypt(nonce, p[..plain.Length], p[plain.Length..], plain, aad);
        }
        catch (CryptographicException)
        {
            throw new ProtocolException(ErrorCodes.AuthFailed, "Frame failed integrity check (tampered, replayed or wrong key)");
        }
        _recvSeq++;
        return new Frame(frame.Type, plain);
    }

    /// <summary>Receives a frame and parses its JSON body; an Error frame from the peer is raised as ProtocolException.</summary>
    public async Task<T> ReceiveJsonAsync<T>(FrameType expected, CancellationToken ct)
    {
        var f = await ReceiveAsync(ct).ConfigureAwait(false);
        return ExpectJson<T>(f, expected);
    }

    public static T ExpectJson<T>(Frame f, FrameType expected)
    {
        if (f.Type == FrameType.Error)
        {
            var err = Json.Parse<ErrorMessage>(f.Payload.Span);
            throw new ProtocolException(err.Code, err.Message);
        }
        if (f.Type != expected) throw new ProtocolException(ErrorCodes.BadRequest, $"Expected {expected} but received {f.Type}");
        try
        {
            return Json.Parse<T>(f.Payload.Span);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new ProtocolException(ErrorCodes.BadRequest, $"Malformed {expected} message: {e.Message}");
        }
    }

    public Task SendErrorAsync(string code, string message, CancellationToken ct) =>
        SendJsonAsync(FrameType.Error, new ErrorMessage(code, message), ct);

    private static void Fill(Span<byte> nonce, Span<byte> aad, uint dir, ulong seq, FrameType type)
    {
        BinaryPrimitives.WriteUInt32BigEndian(nonce, dir);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], seq);
        aad[0] = (byte)type;
        BinaryPrimitives.WriteUInt64BigEndian(aad[1..], seq);
    }

    public ValueTask DisposeAsync()
    {
        _send.Dispose();
        _recv.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
