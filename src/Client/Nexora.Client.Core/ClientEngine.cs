using System.Net;
using System.Net.Sockets;
using Nexora.Discovery;
using Nexora.PrintPipeline;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Pdf;
using Nexora.Shared.Security;
using Nexora.Transport;

namespace Nexora.Client.Core;

/// <summary>Live receiver connection state. Only reports what the last real contact showed.</summary>
public sealed record ReceiverHealth(string? ReceiverId, string? Name, IPEndPoint? Endpoint, bool? Reachable, DateTimeOffset? LastContactUtc,
    ReceiverInfoMessage? Info, string? Error, string? ErrorCode);

/// <summary>
/// Client composition root: captures jobs from the Windows queue, keeps them in a durable outbox, finds the
/// receiver (discovery or manual address), sends with resume and retry, and tracks receiver-side status.
/// </summary>
public sealed class ClientEngine : IAsyncDisposable
{
    private readonly ConfigStore<ClientConfig> _configStore;
    private ClientConfig _cfg;
    private readonly LogScope _log;
    private PipeCaptureServer? _capture;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly List<Task> _loops = [];
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _inflight = new();
    private IReadOnlyList<DiscoveredReceiver> _discovered = [];
    private ReceiverHealth _health = new(null, null, null, null, null, null, null, null);

    public NexoraPaths Paths { get; }
    public Log Log { get; }
    public Outbox Outbox { get; }
    public bool EnableCapture { get; init; } = true;

    public event Action? StatusChanged;
    public event Action<ClientJobRecord>? JobChanged;

    public ClientEngine(NexoraPaths paths, Log? log = null)
    {
        Paths = paths;
        _configStore = new ConfigStore<ClientConfig>(paths.ConfigFile);
        _cfg = _configStore.Load();
        Log = log ?? new Log(paths.Logs, "client", Enum.TryParse<LogLevel>(_cfg.LogLevel, true, out var l) ? l : LogLevel.Info);
        _log = Log.For("client");
        Outbox = new Outbox(paths.Sub("outbox"));
        Outbox.Changed += r => { JobChanged?.Invoke(r); StatusChanged?.Invoke(); };
    }

    public ClientConfig Config => _cfg;
    public IReadOnlyList<DiscoveredReceiver> Discovered => _discovered;
    public ReceiverHealth Health => _health;
    public bool IsDiscovering { get; private set; }
    public DateTimeOffset? LastDiscoveryUtc { get; private set; }
    public bool CaptureListening => _capture?.IsListening ?? false;
    public string? CaptureError { get; private set; }
    public ClientIdentity Identity => new(_cfg.ClientId, _cfg.ClientName);

    public void Start()
    {
        if (EnableCapture)
        {
            try
            {
                _capture = new PipeCaptureServer(_cfg.PipeName, _cfg.QueueName, Paths.Sub("capture"), _cfg.MaxJobBytes, OnCapturedAsync, Log.For("capture"));
                _capture.Start();
            }
            catch (InvalidOperationException e)
            {
                CaptureError = e.Message;
                _capture = null;
            }
        }
        _loops.Add(Task.Run(() => SendLoopAsync(_cts.Token)));
        _loops.Add(Task.Run(() => PollLoopAsync(_cts.Token)));
        _loops.Add(Task.Run(() => DiscoveryLoopAsync(_cts.Token)));
        _log.Info("client.start", $"{NexoraInfo.ProductName} Client {NexoraInfo.AppVersion} started", new { capture = CaptureListening, clientId = _cfg.ClientId });
        StatusChanged?.Invoke();
    }

    // ---------------------------------------------------------------- configuration

    public void UpdateConfig(Action<ClientConfig> change)
    {
        var copy = Json.Parse<ClientConfig>(Json.ToText(_cfg));
        change(copy);
        copy.Validate();
        _configStore.Save(copy);
        bool connectionChanged = copy.ProtectedPairingCode != _cfg.ProtectedPairingCode || copy.ManualReceiverAddress != _cfg.ManualReceiverAddress
            || copy.SelectedReceiverId != _cfg.SelectedReceiverId || copy.LastReceiverAddress != _cfg.LastReceiverAddress;
        _cfg = copy;
        // A new pairing code or receiver may fix the reason queued jobs were failing: retry them now, not after backoff.
        if (connectionChanged)
            foreach (var j in Outbox.All().Where(j => j.State == JobState.Queued && j.NextAttemptUtc is not null))
                Outbox.Update(j.Metadata.JobId, x => x with { NextAttemptUtc = null });
        Wake();
        StatusChanged?.Invoke();
    }

    public void SetPairingCode(string code) => UpdateConfig(c => c.SetPairingKey(PairingKey.Parse(code)));

    public void SelectReceiver(DiscoveredReceiver r) => UpdateConfig(c =>
    {
        c.SelectedReceiverId = r.Announcement.ReceiverId;
        c.SelectedReceiverName = r.Announcement.ServerName;
        c.LastReceiverAddress = r.Address.ToString();
        c.LastReceiverPort = r.Announcement.TcpPort;
    });

    // ---------------------------------------------------------------- job intake

    private async Task OnCapturedAsync(CapturedJob job)
    {
        var id = Guid.NewGuid();
        if (job.Problem is not null)
        {
            var meta = BuildMeta(id, job.DocumentName, job.SizeBytes, new string('0', 64), job.PageCount, job.Settings, job.UserName);
            Outbox.Add(meta, job.FilePath, job.Problem, JobState.Failed, ErrorCodes.InvalidDocument);
            _log.Warn("job.invalid", $"Captured job '{job.DocumentName}' is not sendable: {job.Problem}");
            return;
        }
        var sha = await Task.Run(() => Hashing.Sha256File(job.FilePath));
        var m = BuildMeta(id, job.DocumentName, job.SizeBytes, sha, job.PageCount, job.Settings, job.UserName);
        Outbox.Add(m, job.FilePath);
        _log.Info("job.queued", $"Queued '{job.DocumentName}' for sending", new { jobId = id, job.SizeBytes, copies = job.Settings.Copies });
        Wake();
    }

    /// <summary>Queues an existing PDF file (test page, drag-and-drop, CLI). The file is copied into the outbox.</summary>
    public ClientJobRecord SubmitPdf(string path, string? documentName = null, PrintSettings? settings = null)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("File not found", path);
        if (info.Length > _cfg.MaxJobBytes) throw new InvalidOperationException($"File is larger than the {_cfg.MaxJobBytes / (1024 * 1024)} MB limit");
        var inspection = PdfInspector.Inspect(path);
        if (!inspection.IsPdf) throw new InvalidOperationException($"Not a PDF: {inspection.Problem}");
        var id = Guid.NewGuid();
        var tmp = Path.Combine(Paths.Sub("capture"), id.ToString("N") + ".capture");
        File.Copy(path, tmp);
        var meta = BuildMeta(id, documentName ?? Path.GetFileName(path), info.Length, Hashing.Sha256File(tmp), inspection.PageCount, settings ?? new PrintSettings(), Environment.UserName);
        var rec = Outbox.Add(meta, tmp);
        Wake();
        return rec;
    }

    public ClientJobRecord SubmitTestPage()
    {
        var tmp = Path.Combine(Paths.Sub("capture"), $"testpage-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(tmp, TestPdfBuilder.Build($"Nexora test page from {_cfg.ClientName}"));
        try { return SubmitPdf(tmp, "Nexora test page.pdf"); }
        finally { File.Delete(tmp); }
    }

    private JobMetadata BuildMeta(Guid id, string doc, long size, string sha, int? pages, PrintSettings settings, string? user) => new()
    {
        JobId = id, DocumentName = doc, Format = NexoraInfo.PdfMediaType, SizeBytes = size, Sha256 = sha, PageCount = pages,
        Settings = settings, CreatedUtc = DateTimeOffset.UtcNow, ClientId = _cfg.ClientId, ClientName = _cfg.ClientName, SubmittedBy = user,
    };

    // ---------------------------------------------------------------- user actions

    public void Wake() => _wake.Release();

    /// <summary>Retry a failed/cancelled job. A job the receiver already has is resubmitted under a new id (an explicit reprint).</summary>
    public ClientJobRecord Retry(Guid id)
    {
        var r = Outbox.Get(id) ?? throw new KeyNotFoundException();
        if (!File.Exists(r.FilePath)) throw new InvalidOperationException("The spooled file for this job was already removed");
        ClientJobRecord result;
        if (r.ReceiverId is null || r.ErrorCode is ErrorCodes.ChecksumMismatch)
            result = Outbox.Update(id, x => x with { State = JobState.Queued, Attempts = 0, NextAttemptUtc = null, ErrorCode = null, Message = "Retry requested" });
        else
        {
            var copy = Path.Combine(Paths.Sub("capture"), Guid.NewGuid().ToString("N") + ".capture");
            File.Copy(r.FilePath, copy);
            result = Outbox.Add(r.Metadata with { JobId = Guid.NewGuid(), CreatedUtc = DateTimeOffset.UtcNow }, copy, $"Resubmission of job {id.ToString()[..8]}");
        }
        Wake();
        return result;
    }

    public async Task<string> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var r = Outbox.Get(id) ?? throw new KeyNotFoundException();
        if (r.State.IsTerminal()) return $"Job is already {r.State.ToDisplay()}";
        if (r.AwaitingTransfer && r.ReceiverId is null)
        {
            lock (_gate) if (_inflight.TryGetValue(id, out var c)) c.Cancel();
            Outbox.Update(id, x => x with { State = JobState.Cancelled, ErrorCode = ErrorCodes.Cancelled, Message = "Cancelled before delivery" });
            return "Cancelled";
        }
        await using var s = await ConnectAsync(ct);
        var reply = await s.CancelAsync(id, ct);
        if (reply.Status is not null) ApplyStatus(reply.Status);
        return reply.Message ?? (reply.Cancelled ? "Cancelled" : "Receiver could not cancel the job");
    }

    public async Task<ReceiverInfoMessage> CheckConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await using var s = await ConnectAsync(ct);
            var info = await s.GetInfoAsync(ct);
            SetHealth(_health with { Reachable = true, LastContactUtc = DateTimeOffset.UtcNow, Info = info, Name = info.ReceiverName, ReceiverId = info.ReceiverId, Error = null, ErrorCode = null });
            return info;
        }
        catch (ProtocolException e)
        {
            SetHealth(_health with { Reachable = e.Code is not (ErrorCodes.Unreachable or ErrorCodes.Timeout or ErrorCodes.NoReceiver), Error = e.Message, ErrorCode = e.Code });
            throw;
        }
    }

    public async Task<IReadOnlyList<DiscoveredReceiver>> DiscoverNowAsync(CancellationToken ct = default)
    {
        IsDiscovering = true;
        StatusChanged?.Invoke();
        try
        {
            var found = await DiscoveryClient.DiscoverAsync(_cfg.DiscoveryPort, _cfg.GetPairingKey(), _cfg.ClientId, TimeSpan.FromSeconds(2.5), ct);
            _discovered = found;
            LastDiscoveryUtc = DateTimeOffset.UtcNow;
            _log.Info("discovery.result", $"Discovery found {found.Count} receiver(s)",
                new { receivers = string.Join(", ", found.Select(f => $"{f.Announcement.ServerName}@{f.Address}{(f.Trusted ? " (paired)" : "")}")) });
            var current = found.FirstOrDefault(f => f.Announcement.ReceiverId == _cfg.SelectedReceiverId);
            if (current is not null && (current.Address.ToString() != _cfg.LastReceiverAddress || current.Announcement.TcpPort != _cfg.LastReceiverPort))
            {
                _log.Info("discovery.moved", $"Receiver '{current.Announcement.ServerName}' is now at {current.Address}");
                SelectReceiver(current);
            }
            else if (current is null && _cfg.ManualReceiverAddress is null && DiscoveryClient.AutoSelect(found, _cfg.SelectedReceiverId) is { } auto)
            {
                _log.Info("discovery.autoselect", $"Auto-selected receiver '{auto.Announcement.ServerName}' at {auto.Address}");
                SelectReceiver(auto);
            }
            return found;
        }
        finally
        {
            IsDiscovering = false;
            StatusChanged?.Invoke();
        }
    }

    // ---------------------------------------------------------------- connection

    private async Task<IPEndPoint> ResolveEndpointAsync(CancellationToken ct)
    {
        if (_cfg.ParseManualAddress() is { } manual)
        {
            if (IPAddress.TryParse(manual.Host, out var ip)) return new IPEndPoint(ip, manual.Port);
            var addrs = await Dns.GetHostAddressesAsync(manual.Host, AddressFamily.InterNetwork, ct);
            if (addrs.Length == 0) throw new ProtocolException(ErrorCodes.Unreachable, $"Cannot resolve '{manual.Host}'");
            return new IPEndPoint(addrs[0], manual.Port);
        }
        if (_cfg.LastReceiverAddress is { } last && IPAddress.TryParse(last, out var lastIp))
            return new IPEndPoint(lastIp, _cfg.LastReceiverPort ?? _cfg.DefaultTransportPort);
        if (_cfg.AutoDiscovery)
        {
            await DiscoverNowAsync(ct);
            if (_cfg.LastReceiverAddress is { } found && IPAddress.TryParse(found, out var foundIp))
                return new IPEndPoint(foundIp, _cfg.LastReceiverPort ?? _cfg.DefaultTransportPort);
        }
        throw new ProtocolException(ErrorCodes.NoReceiver, _discovered.Count > 1
            ? "Several receivers were found; select one on the Devices page"
            : "No paired receiver found. Start Nexora Receiver on the printer PC, enter its pairing code, or set a manual address.");
    }

    private async Task<ClientSession> ConnectAsync(CancellationToken ct)
    {
        var key = _cfg.GetPairingKey() ?? throw new ProtocolException(ErrorCodes.AuthFailed, "No pairing code entered. Enter the code shown on the receiver (Settings → Security).");
        var ep = await ResolveEndpointAsync(ct);
        ClientSession s;
        try
        {
            s = await TransportClient.ConnectAsync(ep, key, Identity, TimeSpan.FromSeconds(_cfg.ConnectTimeoutSeconds), ct);
        }
        catch (ProtocolException e) when (e.Code is ErrorCodes.Unreachable or ErrorCodes.Timeout && _cfg.AutoDiscovery && _cfg.ManualReceiverAddress is null)
        {
            // The receiver's IP may have changed (DHCP): rediscover by receiver id and try once more.
            var before = ep;
            await DiscoverNowAsync(ct);
            ep = await ResolveEndpointAsync(ct);
            if (ep.Equals(before)) { SetHealth(_health with { Endpoint = ep, Reachable = false, Error = e.Message, ErrorCode = e.Code }); throw; }
            s = await TransportClient.ConnectAsync(ep, key, Identity, TimeSpan.FromSeconds(_cfg.ConnectTimeoutSeconds), ct);
        }
        catch (ProtocolException e)
        {
            SetHealth(_health with { Endpoint = ep, Reachable = e.Code is not (ErrorCodes.Unreachable or ErrorCodes.Timeout), Error = e.Message, ErrorCode = e.Code });
            throw;
        }
        if (_cfg.SelectedReceiverId != s.ReceiverId)
            UpdateConfig(c => { c.SelectedReceiverId = s.ReceiverId; c.SelectedReceiverName = s.ReceiverName; });
        SetHealth(_health with { ReceiverId = s.ReceiverId, Name = s.ReceiverName, Endpoint = ep, Reachable = true, LastContactUtc = DateTimeOffset.UtcNow, Error = null, ErrorCode = null });
        return s;
    }

    private void SetHealth(ReceiverHealth h)
    {
        _health = h;
        StatusChanged?.Invoke();
    }

    // ---------------------------------------------------------------- loops

    private async Task SendLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _wake.WaitAsync(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
            var due = Outbox.All().Where(j => j.State == JobState.Queued && (j.NextAttemptUtc is null || j.NextAttemptUtc <= DateTimeOffset.UtcNow))
                .OrderBy(j => j.Metadata.CreatedUtc).ToList();
            foreach (var job in due)
            {
                if (ct.IsCancellationRequested) break;
                await SendOneAsync(job, ct);
            }
        }
    }

    private async Task SendOneAsync(ClientJobRecord job, CancellationToken ct)
    {
        var id = job.Metadata.JobId;
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate) _inflight[id] = jobCts;
        int attempt = job.Attempts + 1;
        try
        {
            Outbox.Update(id, x => x with { State = JobState.Transferring, Attempts = attempt, Message = $"Connecting (attempt {attempt})" });
            await using var session = await ConnectAsync(jobCts.Token);
            Outbox.Update(id, x => x with { ReceiverId = session.ReceiverId, ReceiverName = session.ReceiverName, Message = $"Sending to {session.ReceiverName}" });
            long lastReported = 0;
            var progress = new Progress<TransferProgress>(p =>
            {
                if (p.BytesSent - lastReported < 512 * 1024 && p.BytesSent != p.TotalBytes) return;
                lastReported = p.BytesSent;
                Outbox.Update(id, x => x with { BytesSent = p.BytesSent, Message = $"Sending {p.BytesSent * 100 / Math.Max(1, p.TotalBytes)}%" + (p.ResumedFrom > 0 ? $" (resumed at {p.ResumedFrom / 1024} KB)" : "") });
            });
            var status = await session.SendJobAsync(job.Metadata, job.FilePath, progress, jobCts.Token);
            if (status.State == JobState.Failed && status.ErrorCode == ErrorCodes.ChecksumMismatch)
            {
                ScheduleRetry(id, attempt, status.ErrorCode, status.Message ?? "Checksum mismatch; resending");
                return;
            }
            ApplyStatus(status, destination: $"{session.ReceiverName}");
            _log.Info("job.sent", $"'{job.Metadata.DocumentName}' delivered: {status.State.ToWire()}", new { jobId = id, receiver = session.ReceiverName, status.Message });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Cancelled by the user; CancelAsync recorded the state.
        }
        catch (ProtocolException e) when (ErrorCodes.IsPermanent(e.Code))
        {
            Outbox.Update(id, x => x with { State = JobState.Failed, ErrorCode = e.Code, Message = e.Message, NextAttemptUtc = null });
            _log.Error("job.failed", $"'{job.Metadata.DocumentName}' failed permanently: {e.Message}", new { jobId = id, code = e.Code });
        }
        catch (Exception e) when (e is ProtocolException or IOException or SocketException or System.Text.Json.JsonException)
        {
            var code = (e as ProtocolException)?.Code ?? ErrorCodes.Unreachable;
            ScheduleRetry(id, attempt, code, e.Message);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.Error("job.crash", $"Unexpected error sending '{job.Metadata.DocumentName}'", new { jobId = id }, e);
            ScheduleRetry(id, attempt, ErrorCodes.Internal, e.Message);
        }
        finally
        {
            lock (_gate) _inflight.Remove(id);
        }
    }

    private void ScheduleRetry(Guid id, int attempt, string code, string message)
    {
        var r = Outbox.Get(id)!;
        if (r.State == JobState.Cancelled) return;
        if (DateTimeOffset.UtcNow - r.Metadata.CreatedUtc > TimeSpan.FromHours(_cfg.RetryWindowHours))
        {
            Outbox.Update(id, x => x with { State = JobState.Failed, ErrorCode = code, Message = $"Gave up after {attempt} attempts: {message}", NextAttemptUtc = null });
            _log.Error("job.giveup", $"Gave up on '{r.Metadata.DocumentName}' after {attempt} attempts", new { jobId = id, code });
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(attempt - 1, 6))));
        Outbox.Update(id, x => x with { State = JobState.Queued, ErrorCode = code, Message = $"{message} — retry in {delay.TotalSeconds:0}s", NextAttemptUtc = DateTimeOffset.UtcNow + delay });
        _log.Warn("job.retry", $"'{r.Metadata.DocumentName}' attempt {attempt} failed ({code}); retrying in {delay.TotalSeconds:0}s", new { jobId = id, message });
    }

    private void ApplyStatus(JobStatus s, string? destination = null)
    {
        if (Outbox.Get(s.JobId) is null) return;
        Outbox.Update(s.JobId, x => x with
        {
            State = s.State, ErrorCode = s.ErrorCode, Message = s.Message, ReceiverFinal = s.IsFinal, NextAttemptUtc = null,
            BytesSent = x.Metadata.SizeBytes,
            Destination = destination is null ? (s.PrinterName ?? x.Destination) : (s.PrinterName is null ? destination : $"{destination} → {s.PrinterName}"),
        });
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _cfg.StatusPollSeconds)), ct); } catch (OperationCanceledException) { break; }
            var pending = Outbox.All().Where(j => j.AwaitingReceiverStatus && j.UpdatedUtc > DateTimeOffset.UtcNow.AddDays(-2)).ToList();
            if (pending.Count == 0) continue;
            try
            {
                await using var s = await ConnectAsync(ct);
                foreach (var st in await s.QueryStatusAsync(pending.Select(p => p.Metadata.JobId).ToList(), ct))
                {
                    var before = Outbox.Get(st.JobId);
                    if (before is not null && (before.State != st.State || before.Message != st.Message || before.ReceiverFinal != st.IsFinal))
                    {
                        ApplyStatus(st);
                        _log.Info("job.status", $"'{before.Metadata.DocumentName}' → {st.State.ToWire()}: {st.Message}", new { jobId = st.JobId });
                    }
                }
            }
            catch (Exception e) when (e is ProtocolException or IOException or SocketException)
            {
                _log.Debug("poll.failed", $"Status poll failed: {e.Message}");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _log.Warn("poll.crash", "Status poll failed", ex: e);
            }
        }
    }

    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_cfg.AutoDiscovery)
            {
                try { await DiscoverNowAsync(ct); }
                catch (Exception e) when (!ct.IsCancellationRequested) { _log.Warn("discovery.error", $"Discovery failed: {e.Message}"); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _cfg.DiscoveryIntervalSeconds)), ct); } catch (OperationCanceledException) { break; }
            try { Outbox.Purge(TimeSpan.FromDays(Math.Max(1, _cfg.JobRetentionDays))); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_capture is not null) await _capture.DisposeAsync();
        try { await Task.WhenAll(_loops); } catch { }
        _log.Info("client.stop", "Client stopped");
        Log.Dispose();
        _cts.Dispose();
    }
}
