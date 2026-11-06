using System.Net;
using System.Net.Sockets;
using Nexora.Shared;
using Nexora.Shared.Jobs;
using Nexora.Shared.Security;

namespace Nexora.Transport;

public sealed record ClientIdentity(string ClientId, string ClientName);

public sealed record TransferProgress(Guid JobId, long BytesSent, long TotalBytes, long ResumedFrom);

/// <summary>Opens authenticated NXP/2 sessions to a receiver.</summary>
public static class TransportClient
{
    public const int DefaultChunkSize = 256 * 1024;

    public static async Task<ClientSession> ConnectAsync(IPEndPoint endpoint, PairingKey key, ClientIdentity identity,
        TimeSpan connectTimeout, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(connectTimeout);
                try
                {
                    await tcp.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new ProtocolException(ErrorCodes.Unreachable, $"Timed out connecting to {endpoint}");
                }
                catch (SocketException e)
                {
                    throw new ProtocolException(ErrorCodes.Unreachable, $"Cannot connect to {endpoint}: {e.SocketErrorCode}");
                }
            }
            var stream = tcp.GetStream();
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(15));
            Handshake.ClientResult hs;
            try
            {
                hs = await Handshake.ClientAsync(stream, key, identity.ClientId, identity.ClientName, hsCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProtocolException(ErrorCodes.Timeout, "Receiver did not complete the handshake");
            }
            catch (IOException e)
            {
                throw new ProtocolException(ErrorCodes.Unreachable, $"Connection to {endpoint} dropped during handshake: {e.Message}");
            }
            return new ClientSession(tcp, hs);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }
}

public sealed class ClientSession : IAsyncDisposable
{
    private readonly TcpClient _tcp;
    private readonly SecureChannel _ch;

    internal ClientSession(TcpClient tcp, Handshake.ClientResult hs)
    {
        _tcp = tcp;
        _ch = hs.Channel;
        ReceiverId = hs.Hello.ReceiverId;
        ReceiverName = hs.Hello.ReceiverName;
        Session = hs.Session;
    }

    public string ReceiverId { get; }
    public string ReceiverName { get; }
    public AuthOkMessage Session { get; }

    public async Task<ReceiverInfoMessage> GetInfoAsync(CancellationToken ct)
    {
        await _ch.SendAsync(FrameType.InfoQuery, ReadOnlyMemory<byte>.Empty, ct);
        return await _ch.ReceiveJsonAsync<ReceiverInfoMessage>(FrameType.InfoReply, ct);
    }

    public async Task<IReadOnlyList<JobStatus>> QueryStatusAsync(IReadOnlyList<Guid> jobIds, CancellationToken ct)
    {
        await _ch.SendJsonAsync(FrameType.StatusQuery, new StatusQueryMessage(jobIds), ct);
        return (await _ch.ReceiveJsonAsync<StatusReplyMessage>(FrameType.StatusReply, ct)).Jobs;
    }

    public async Task<CancelReplyMessage> CancelAsync(Guid jobId, CancellationToken ct)
    {
        await _ch.SendJsonAsync(FrameType.Cancel, new CancelMessage(jobId), ct);
        return await _ch.ReceiveJsonAsync<CancelReplyMessage>(FrameType.CancelReply, ct);
    }

    /// <summary>
    /// Offers a job and streams it. Resumes from the receiver's offset after an interrupted transfer, and
    /// returns the receiver's existing status without resending when the job is a duplicate.
    /// The returned status is the receiver's verdict after it hashed the bytes it wrote to disk.
    /// </summary>
    public async Task<JobStatus> SendJobAsync(JobMetadata meta, string filePath, IProgress<TransferProgress>? progress,
        CancellationToken ct, long stopAfterBytesForTest = -1)
    {
        await _ch.SendJsonAsync(FrameType.JobOffer, meta, ct);
        var reply = await _ch.ReceiveJsonAsync<OfferReplyMessage>(FrameType.OfferReply, ct);
        switch (reply.Decision)
        {
            case OfferDecision.Duplicate:
                return reply.Existing ?? new JobStatus { JobId = meta.JobId, State = JobState.Received, Message = "Already received" };
            case OfferDecision.Reject:
                throw new ProtocolException(reply.ErrorCode ?? ErrorCodes.BadRequest, reply.Message ?? "Receiver rejected the job");
        }

        long offset = reply.ResumeOffset;
        if (offset < 0 || offset > meta.SizeBytes) throw new ProtocolException(ErrorCodes.BadRequest, "Receiver asked for an invalid resume offset");
        int chunk = Math.Clamp(Session.ChunkSize, 16 * 1024, 1024 * 1024);
        var buffer = new byte[chunk];
        await using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, chunk, FileOptions.SequentialScan))
        {
            if (fs.Length != meta.SizeBytes) throw new ProtocolException(ErrorCodes.StorageError, "Spooled job file changed size since it was queued");
            fs.Position = offset;
            long sent = offset;
            progress?.Report(new TransferProgress(meta.JobId, sent, meta.SizeBytes, offset));
            while (sent < meta.SizeBytes)
            {
                int n = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(chunk, meta.SizeBytes - sent)), ct);
                if (n == 0) throw new ProtocolException(ErrorCodes.StorageError, "Unexpected end of spooled job file");
                await _ch.SendAsync(FrameType.Data, buffer.AsMemory(0, n), ct);
                sent += n;
                progress?.Report(new TransferProgress(meta.JobId, sent, meta.SizeBytes, offset));
                if (stopAfterBytesForTest >= 0 && sent >= stopAfterBytesForTest)
                {
                    _tcp.Client.Shutdown(SocketShutdown.Both); // simulate a dropped connection (tests only)
                    throw new IOException("Simulated connection drop");
                }
            }
        }
        await _ch.SendJsonAsync(FrameType.DataEnd, new DataEndMessage(meta.SizeBytes), ct);
        // Verification re-reads the file on the receiver; allow time for large jobs.
        _ch.ReadTimeout = TimeSpan.FromSeconds(Math.Max(60, meta.SizeBytes / (20 * 1024 * 1024)));
        var result = await _ch.ReceiveJsonAsync<JobResultMessage>(FrameType.JobResult, ct);
        _ch.ReadTimeout = TimeSpan.FromSeconds(60);
        return result.Status;
    }

    public async ValueTask DisposeAsync()
    {
        await _ch.DisposeAsync();
        _tcp.Dispose();
    }
}
