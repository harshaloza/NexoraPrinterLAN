using System.Security.Cryptography;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Security;

namespace Nexora.Transport;

/// <summary>
/// NXP/2 mutual authentication (docs/NETWORK_PROTOCOL.md §2):
///   C→S HELLO {clientNonce}            S→C HELLO_REPLY {serverNonce, serverProof}
///   C→S AUTH {clientProof}             S→C AUTH_OK (first encrypted frame)
/// Both proofs are HMACs keyed from the pairing code over both fresh nonces, so neither side learns
/// anything useful from a peer without the code and recorded sessions cannot be replayed.
/// </summary>
public static class Handshake
{
    public const string ProtocolName = "NXP";

    public sealed record ClientResult(SecureChannel Channel, HelloReplyMessage Hello, AuthOkMessage Session);
    public sealed record ServerResult(SecureChannel Channel, HelloMessage Hello);

    public static async Task<ClientResult> ClientAsync(Stream stream, PairingKey key, string clientId, string clientName, CancellationToken ct)
    {
        var cNonce = KeySchedule.NewNonce();
        await FrameCodec.WriteAsync(stream, FrameType.Hello,
            Json.ToBytes(new HelloMessage(ProtocolName, NexoraInfo.ProtocolVersion, clientId, clientName, Convert.ToBase64String(cNonce))), ct);

        var replyFrame = await FrameCodec.ReadAsync(stream, ct);
        var reply = SecureChannel.ExpectJson<HelloReplyMessage>(replyFrame, FrameType.HelloReply);
        if (reply.Version < NexoraInfo.MinSupportedProtocolVersion)
            throw new ProtocolException(ErrorCodes.ProtocolVersion, $"Receiver speaks protocol {reply.Version}; this client needs {NexoraInfo.MinSupportedProtocolVersion}+");
        var sNonce = DecodeNonce(reply.Nonce);
        var expected = KeySchedule.ServerProof(key, cNonce, sNonce, reply.ReceiverId);
        if (!CryptographicOperations.FixedTimeEquals(expected, DecodeB64(reply.Proof)))
            throw new ProtocolException(ErrorCodes.AuthFailed, $"Receiver '{reply.ReceiverName}' does not have this client's pairing code. Re-enter the code shown on the receiver.");

        var proof = KeySchedule.ClientProof(key, cNonce, sNonce, clientId);
        await FrameCodec.WriteAsync(stream, FrameType.Auth, Json.ToBytes(new AuthMessage(Convert.ToBase64String(proof))), ct);

        var (c2s, s2c) = KeySchedule.SessionKeys(key, cNonce, sNonce);
        var channel = new SecureChannel(stream, c2s, s2c, isClient: true);
        try
        {
            var ok = await channel.ReceiveJsonAsync<AuthOkMessage>(FrameType.AuthOk, ct);
            return new ClientResult(channel, reply, ok);
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.AuthFailed)
        {
            await channel.DisposeAsync();
            throw new ProtocolException(ErrorCodes.AuthFailed, "Receiver rejected this client's pairing code.");
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    /// <summary>Server side. <paramref name="authOk"/> is sent encrypted once the client proved knowledge of the key.</summary>
    public static async Task<ServerResult> ServerAsync(Stream stream, PairingKey key, string receiverId, string receiverName,
        Func<HelloMessage, AuthOkMessage> authOk, CancellationToken ct)
    {
        var helloFrame = await FrameCodec.ReadAsync(stream, ct);
        if (helloFrame.Type != FrameType.Hello) throw new ProtocolException(ErrorCodes.BadRequest, "Expected HELLO");
        HelloMessage hello;
        try { hello = Json.Parse<HelloMessage>(helloFrame.Payload.Span); }
        catch (System.Text.Json.JsonException) { throw new ProtocolException(ErrorCodes.BadRequest, "Malformed HELLO"); }

        if (hello.Protocol != ProtocolName || hello.Version < NexoraInfo.MinSupportedProtocolVersion)
        {
            await WritePlainError(stream, ErrorCodes.ProtocolVersion, $"Unsupported protocol {hello.Protocol}/{hello.Version}; need {ProtocolName}/{NexoraInfo.MinSupportedProtocolVersion}+", ct);
            throw new ProtocolException(ErrorCodes.ProtocolVersion, "Unsupported client protocol");
        }
        if (string.IsNullOrWhiteSpace(hello.ClientId) || hello.ClientId.Length > 64 || (hello.ClientName?.Length ?? 0) > 128)
        {
            await WritePlainError(stream, ErrorCodes.BadRequest, "Invalid client identity", ct);
            throw new ProtocolException(ErrorCodes.BadRequest, "Invalid client identity");
        }
        var cNonce = DecodeNonce(hello.Nonce);
        var sNonce = KeySchedule.NewNonce();
        var proof = KeySchedule.ServerProof(key, cNonce, sNonce, receiverId);
        await FrameCodec.WriteAsync(stream, FrameType.HelloReply,
            Json.ToBytes(new HelloReplyMessage(NexoraInfo.ProtocolVersion, receiverId, receiverName, Convert.ToBase64String(sNonce), Convert.ToBase64String(proof))), ct);

        var authFrame = await FrameCodec.ReadAsync(stream, ct);
        var auth = SecureChannel.ExpectJson<AuthMessage>(authFrame, FrameType.Auth);
        var expected = KeySchedule.ClientProof(key, cNonce, sNonce, hello.ClientId);
        if (!CryptographicOperations.FixedTimeEquals(expected, DecodeB64(auth.Proof)))
        {
            await WritePlainError(stream, ErrorCodes.AuthFailed, "Pairing code mismatch", ct);
            throw new ProtocolException(ErrorCodes.AuthFailed, $"Client '{hello.ClientName}' used a wrong pairing code");
        }

        var (c2s, s2c) = KeySchedule.SessionKeys(key, cNonce, sNonce);
        var channel = new SecureChannel(stream, s2c, c2s, isClient: false);
        await channel.SendJsonAsync(FrameType.AuthOk, authOk(hello), ct);
        return new ServerResult(channel, hello);
    }

    private static Task WritePlainError(Stream s, string code, string msg, CancellationToken ct) =>
        FrameCodec.WriteAsync(s, FrameType.Error, Json.ToBytes(new ErrorMessage(code, msg)), ct);

    private static byte[] DecodeNonce(string? b64)
    {
        var n = DecodeB64(b64);
        if (n.Length != KeySchedule.NonceSize) throw new ProtocolException(ErrorCodes.BadRequest, "Invalid nonce");
        return n;
    }

    private static byte[] DecodeB64(string? b64)
    {
        try { return Convert.FromBase64String(b64 ?? ""); }
        catch (FormatException) { throw new ProtocolException(ErrorCodes.BadRequest, "Invalid base64 field"); }
    }
}
