using System.Net;
using System.Net.Sockets;
using Nexora.Shared;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Security;
using Nexora.Transport;

namespace Nexora.Receiver.Core;

/// <summary>TCP listener for NXP/2 sessions (job uploads, status queries, receiver info).</summary>
public sealed class ReceiverServer : IAsyncDisposable
{
    private readonly ReceiverConfig _cfg;
    private readonly Func<PairingKey> _key;
    private readonly JobStore _store;
    private readonly AddressFilter _filter;
    private readonly Func<ReceiverInfoMessage> _info;
    private readonly Action<Guid> _onValidated;
    private readonly Func<Guid, CancelReplyMessage> _cancel;
    private readonly LogScope _log;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _active;

    public ReceiverServer(ReceiverConfig cfg, Func<PairingKey> key, JobStore store, AddressFilter filter,
        Func<ReceiverInfoMessage> info, Action<Guid> onValidated, Func<Guid, CancelReplyMessage> cancel, LogScope log)
    {
        _cfg = cfg;
        _key = key;
        _store = store;
        _filter = filter;
        _info = info;
        _onValidated = onValidated;
        _cancel = cancel;
        _log = log;
        _slots = new SemaphoreSlim(Math.Max(1, cfg.MaxConcurrentConnections));
    }

    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _cfg.TransportPort;
    public int ActiveConnections => Volatile.Read(ref _active);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Parse(_cfg.BindAddress), _cfg.TransportPort);
        _listener.Server.ExclusiveAddressUse = true;
        try
        {
            _listener.Start(32);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException($"TCP port {_cfg.TransportPort} is already in use. Stop the older test receiver or change the transport port.", e);
        }
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info("server.listening", $"Receiver listening on TCP {Port}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException e) { _log.Warn("server.accept", "Accept failed", ex: e); continue; }
            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
        var remoteIp = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        var stream = client.GetStream();
        client.NoDelay = true;

        if (!_filter.IsAllowed(remoteIp, out var reason))
        {
            _log.Warn("server.denied", $"Rejected connection from {remoteIp}: {reason}", new { client = remoteIp.ToString() });
            await RefuseAsync(client, ErrorCodes.NotAllowed, "This receiver does not accept connections from your address", ct);
            return;
        }
        if (!await _slots.WaitAsync(0, ct))
        {
            await RefuseAsync(client, ErrorCodes.Busy, "Receiver is busy; retry shortly", ct);
            return;
        }
        Interlocked.Increment(ref _active);
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(15));
            Handshake.ServerResult hs;
            try
            {
                hs = await Handshake.ServerAsync(stream, _key(), _cfg.ReceiverId, _cfg.ReceiverName,
                    _ => new AuthOkMessage(_cfg.ReceiverId, NexoraInfo.AppVersion, _cfg.TestMode, _cfg.MaxJobBytes, TransportClient.DefaultChunkSize), hsCts.Token);
            }
            catch (ProtocolException e)
            {
                _log.Warn("server.handshake", $"Handshake with {remoteIp} failed: {e.Message}", new { client = remoteIp.ToString(), code = e.Code });
                return;
            }
            await using var ch = hs.Channel;
            ch.ReadTimeout = TimeSpan.FromSeconds(_cfg.IdleTimeoutSeconds);
            _log.Info("server.session", $"Authenticated session from {hs.Hello.ClientName} ({remoteIp})", new { client = remoteIp.ToString(), clientId = hs.Hello.ClientId });
            await SessionLoopAsync(ch, hs.Hello, remoteIp.ToString(), ct);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or SocketException or OperationCanceledException)
        {
            _log.Debug("server.disconnect", $"Connection from {remoteIp} ended: {e.Message}");
        }
        catch (ProtocolException e)
        {
            _log.Warn("server.protocol", $"Protocol error from {remoteIp}: {e.Message}", new { code = e.Code });
        }
        catch (Exception e)
        {
            _log.Error("server.crash", $"Unexpected error serving {remoteIp}", ex: e);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _slots.Release();
        }
    }

    private async Task SessionLoopAsync(SecureChannel ch, HelloMessage hello, string clientIp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Frame f;
            try { f = await ch.ReceiveAsync(ct); }
            catch (EndOfStreamException) { return; }
            catch (ProtocolException e) when (e.Code == ErrorCodes.Timeout) { return; }

            switch (f.Type)
            {
                case FrameType.InfoQuery:
                    await ch.SendJsonAsync(FrameType.InfoReply, _info(), ct);
                    break;
                case FrameType.StatusQuery:
                {
                    var q = SecureChannel.ExpectJson<StatusQueryMessage>(f, FrameType.StatusQuery);
                    var list = q.JobIds.Take(500).Select(id => _store.Get(id)?.ToStatus()
                        ?? new JobStatus { JobId = id, State = JobState.Failed, ErrorCode = ErrorCodes.NotFound, Message = "Receiver has no record of this job" }).ToList();
                    await ch.SendJsonAsync(FrameType.StatusReply, new StatusReplyMessage(list), ct);
                    break;
                }
                case FrameType.Cancel:
                {
                    var c = SecureChannel.ExpectJson<CancelMessage>(f, FrameType.Cancel);
                    await ch.SendJsonAsync(FrameType.CancelReply, _cancel(c.JobId), ct);
                    break;
                }
                case FrameType.JobOffer:
                    await ReceiveJobAsync(ch, SecureChannel.ExpectJson<JobMetadata>(f, FrameType.JobOffer), hello, clientIp, ct);
                    break;
                default:
                    await ch.SendErrorAsync(ErrorCodes.BadRequest, $"Unexpected {f.Type}", ct);
                    return;
            }
        }
    }

    private string? ValidateOffer(JobMetadata m, HelloMessage hello, out string code)
    {
        code = ErrorCodes.BadRequest;
        if (m.JobId == Guid.Empty) return "Missing job id";
        if (m.ClientId != hello.ClientId) return "Job client id does not match the authenticated client";
        if (string.IsNullOrWhiteSpace(m.DocumentName) || m.DocumentName.Length > 512) return "Invalid document name";
        if (!Hashing.IsSha256Hex(m.Sha256)) return "Invalid SHA-256";
        if (m.SizeBytes <= 0) return "Empty job";
        if (m.Settings.Copies is < 1 or > 999) return "Copies must be 1-999";
        if (m.SizeBytes > _cfg.MaxJobBytes) { code = ErrorCodes.TooLarge; return $"Job is {m.SizeBytes:N0} bytes; this receiver accepts up to {_cfg.MaxJobBytes:N0}"; }
        if (!NexoraInfo.IsSupportedFormat(m.Format)) { code = ErrorCodes.UnsupportedFormat; return $"Format '{m.Format}' is not supported (only {NexoraInfo.PdfMediaType} and {NexoraInfo.XpsMediaType})"; }
        return null;
    }

    private async Task ReceiveJobAsync(SecureChannel ch, JobMetadata meta, HelloMessage hello, string clientIp, CancellationToken ct)
    {
        var problem = ValidateOffer(meta, hello, out var code);
        if (problem is not null)
        {
            _log.Warn("job.rejected", $"Rejected job '{meta.DocumentName}': {problem}", new { meta.JobId, code });
            await ch.SendJsonAsync(FrameType.OfferReply, new OfferReplyMessage { Decision = OfferDecision.Reject, ErrorCode = code, Message = problem }, ct);
            return;
        }

        IncomingUpload? up;
        ReceiverJobRecord? existing;
        BeginOutcome outcome;
        try
        {
            outcome = _store.Begin(meta, out up, out existing);
        }
        catch (IOException e)
        {
            _log.Error("job.storage", "Could not open job storage", new { meta.JobId }, e);
            await ch.SendJsonAsync(FrameType.OfferReply, new OfferReplyMessage { Decision = OfferDecision.Reject, ErrorCode = ErrorCodes.StorageError, Message = "Receiver could not write to its job folder" }, ct);
            return;
        }
        if (outcome == BeginOutcome.Duplicate)
        {
            _log.Info("job.duplicate", $"Duplicate offer for '{meta.DocumentName}' ignored", new { meta.JobId, state = existing!.State.ToWire() });
            await ch.SendJsonAsync(FrameType.OfferReply, new OfferReplyMessage { Decision = OfferDecision.Duplicate, Existing = existing.ToStatus() }, ct);
            return;
        }
        if (outcome == BeginOutcome.Busy)
        {
            await ch.SendJsonAsync(FrameType.OfferReply, new OfferReplyMessage { Decision = OfferDecision.Reject, ErrorCode = ErrorCodes.Busy, Message = "This job is already being uploaded on another connection" }, ct);
            return;
        }

        using (up)
        {
            _log.Info("job.transfer", $"Receiving '{meta.DocumentName}' ({meta.SizeBytes:N0} bytes) from {clientIp}" + (up!.ResumeOffset > 0 ? $", resuming at {up.ResumeOffset:N0}" : ""),
                new { meta.JobId, meta.SizeBytes, resume = up.ResumeOffset });
            await ch.SendJsonAsync(FrameType.OfferReply, new OfferReplyMessage { Decision = OfferDecision.Accept, ResumeOffset = up.ResumeOffset }, ct);

            using var transferCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            transferCts.CancelAfter(TimeSpan.FromSeconds(_cfg.TransferTimeoutSeconds));
            try
            {
                while (true)
                {
                    var f = await ch.ReceiveAsync(transferCts.Token);
                    if (f.Type == FrameType.Data) { await up.WriteAsync(f.Payload, transferCts.Token); continue; }
                    if (f.Type == FrameType.DataEnd) break;
                    throw new ProtocolException(ErrorCodes.BadRequest, $"Unexpected {f.Type} during transfer");
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProtocolException(ErrorCodes.Timeout, $"Transfer exceeded {_cfg.TransferTimeoutSeconds}s");
            }

            ReceiverJobRecord record;
            try
            {
                record = up.Complete(clientIp);
            }
            catch (ProtocolException e)
            {
                await ch.SendErrorAsync(e.Code, e.Message, ct);
                throw;
            }
            catch (IOException e)
            {
                _log.Error("job.storage", "Failed to finalize job", new { meta.JobId }, e);
                await ch.SendErrorAsync(ErrorCodes.StorageError, "Receiver failed to save the job", ct);
                return;
            }

            var level = record.State == JobState.Failed ? Shared.Logging.LogLevel.Warning : Shared.Logging.LogLevel.Info;
            _log.Root.Write(level, "server", "job.received", $"'{meta.DocumentName}' → {record.State.ToWire()}: {record.Message}",
                new Dictionary<string, object?> { ["jobId"] = meta.JobId, ["state"] = record.State.ToWire(), ["file"] = record.StoredFile });
            // Route to test-mode save or the print queue before answering, so the reported status is already accurate.
            if (record.State == JobState.Validated)
            {
                _onValidated(meta.JobId);
                record = _store.Get(meta.JobId) ?? record;
            }
            await ch.SendJsonAsync(FrameType.JobResult, new JobResultMessage(record.ToStatus()), ct);
        }
    }

    /// <summary>
    /// Sends a plaintext refusal. The client's HELLO is read first and the send side is shut down gracefully:
    /// closing a socket with unread data makes Windows send RST, which would discard the error before the client reads it.
    /// </summary>
    private static async Task RefuseAsync(TcpClient client, string code, string msg, CancellationToken ct)
    {
        var s = client.GetStream();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try { await FrameCodec.ReadAsync(s, cts.Token); } catch { /* not a HELLO or nothing sent: refuse anyway */ }
            await FrameCodec.WriteAsync(s, FrameType.Error, Json.ToBytes(new ErrorMessage(code, msg)), ct);
            client.Client.Shutdown(SocketShutdown.Send);
            await Task.Delay(200, ct);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
        _cts.Dispose();
    }
}
