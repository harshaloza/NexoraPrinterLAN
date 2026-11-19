using System.Diagnostics;
using System.Net;
using Nexora.Client.Core;
using Nexora.Discovery;
using Nexora.PrinterIntegration;
using Nexora.PrintPipeline;
using Nexora.Receiver.Core;
using Nexora.Shared;
using Nexora.Shared.Config;
using Nexora.Shared.IO;
using Nexora.Shared.Jobs;
using Nexora.Shared.Logging;
using Nexora.Shared.Pdf;
using Nexora.Shared.Security;
using Nexora.Transport;

// nexora — command-line companion for diagnostics, headless operation and tests.
var argv = args.ToList();
if (argv.Count == 0 || argv[0] is "-h" or "--help" or "help") return Usage();
var cmd = argv[0].ToLowerInvariant();
argv.RemoveAt(0);

string? Opt(string name)
{
    int i = argv.IndexOf(name);
    if (i < 0 || i + 1 >= argv.Count) return null;
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => argv.Remove(name);

try
{
    switch (cmd)
    {
        case "receiver": return await RunReceiverAsync();
        case "client": return await RunClientAsync();
        case "discover": return await DiscoverAsync();
        case "send": return await SendAsync();
        case "info": return await InfoAsync();
        case "status": return await StatusAsync();
        case "testpdf": return TestPdf();
        case "pairing": return Pairing();
        case "print": return await PrintAsync();
        case "printers": return Printers();
        case "pdfinfo": return PdfInfo();
        case "xpsinfo": return XpsInfoCmd();
        case "xpscapture": return await XpsCaptureAsync();
        case "queue": return Queue();
        default:
            Console.Error.WriteLine($"Unknown command '{cmd}'.");
            return Usage();
    }
}
catch (ProtocolException e)
{
    Console.Error.WriteLine($"FAILED [{e.Code}] {e.Message}");
    return 2;
}
catch (Exception e) when (e is InvalidOperationException or FormatException or FileNotFoundException or IOException)
{
    Console.Error.WriteLine($"FAILED: {e.Message}");
    return 1;
}

int Usage()
{
    Console.WriteLine($"""
        {NexoraInfo.ProductName} command-line tool {NexoraInfo.AppVersion}

          nexora receiver [--data DIR] [--loopback] [--bind IP] [--port N] [--udp N] [--print PRINTER] [--test-mode]
              Run the receiver headless (Ctrl+C to stop). Prints the pairing code on first start.
          nexora client [--data DIR] [--no-capture] [--pair CODE] [--manual HOST[:PORT]] [--no-discovery] [--submit FILE.pdf]...
              Run the client engine headless (pipe capture + outbox + sender).
          nexora discover [--key CODE] [--seconds N] [--udp PORT] [--target IP]
              Broadcast discovery and list receivers (paired ones are marked when --key matches).
          nexora send FILE.pdf --key CODE [--to HOST[:PORT]] [--copies N] [--id GUID] [--drop-after BYTES]
              Send one PDF directly and print the receiver's verdict. --drop-after simulates a broken connection.
          nexora info --key CODE [--to HOST[:PORT]]
          nexora status JOBID --key CODE [--to HOST[:PORT]]
          nexora testpdf OUT.pdf [--pages N] [--size A4|A3|Letter] [--landscape] [--mb N]
          nexora pairing [--data DIR] [--regenerate]     Show (or regenerate) the receiver pairing code.
          nexora print FILE.pdf|FILE.xps --printer NAME [--to-file OUT] [--scaling auto|actualsize|fittoprintablearea | --actual-size | --fit] [--copies N] [--method auto|vector|raster]
              Print locally through a Windows printer driver (receiver print path test). XPS defaults to vector with
              raster fallback; --to-file applies to the raster path only.
          nexora printers                                 List installed printers and their real status.
          nexora pdfinfo FILE.pdf [--png OUT.png] [--page N] [--dpi N]
              Validate a PDF with the Windows PDF engine; list page sizes; optionally render one page to PNG.
          nexora xpsinfo FILE.xps [--png OUT.png] [--page N] [--dpi N]
              Read an XPS job (Nexora v4 driver): pages, sizes, copies, paper; optionally render a page to PNG.
          nexora xpscapture --pipe NAME --queue PRINTER --out DIR
              Receive jobs from a test queue whose port is \\.\pipe\NAME and save each one to DIR (driver tests).
          nexora queue                                    Show the state of the "{NexoraInfo.PrinterQueueName}" queue.
        """);
    return 64;
}

async Task<int> RunReceiverAsync()
{
    var data = Opt("--data");
    var port = Opt("--port");
    var udp = Opt("--udp");
    var printer = Opt("--print");
    var bind = Opt("--bind");
    bool loopback = Flag("--loopback"), testMode = Flag("--test-mode");
    var paths = new NexoraPaths("Receiver", data);
    var log = new Log(paths.Logs, "receiver") { WriteToConsole = true };
    await using var host = new ReceiverHost(paths, log);
    await host.UpdateConfigAsync(c =>
    {
        if (port is not null) c.TransportPort = int.Parse(port);
        if (udp is not null) c.DiscoveryPort = int.Parse(udp);
        if (loopback) { c.AllowLoopback = true; c.BindAddress = "127.0.0.1"; }
        if (bind is not null) c.BindAddress = bind;
        if (printer is not null) { c.DestinationPrinter = printer; c.TestMode = false; }
        if (testMode) c.TestMode = true;
    });
    host.Start();
    Console.WriteLine($"Receiver '{host.Config.ReceiverName}' running. TCP {host.TransportPort}, UDP {host.DiscoveryPort}.");
    Console.WriteLine($"Mode: {(host.Config.TestMode ? "TEST (save only)" : "PRINT to " + host.Config.DestinationPrinter)}");
    Console.WriteLine($"Pairing code: {host.PairingKey.Display}   (fingerprint {host.PairingKey.Fingerprint})");
    Console.WriteLine($"Received documents: {host.Store.ReceivedFolder}");
    Console.WriteLine("READY");
    await WaitForCtrlC();
    return 0;
}

async Task<int> RunClientAsync()
{
    var paths = new NexoraPaths("Client", Opt("--data"));
    var log = new Log(paths.Logs, "client") { WriteToConsole = true };
    var pair = Opt("--pair");
    var manual = Opt("--manual");
    bool noDiscovery = Flag("--no-discovery");
    await using var engine = new ClientEngine(paths, log) { EnableCapture = !Flag("--no-capture") };
    engine.UpdateConfig(c =>
    {
        if (pair is not null) c.SetPairingKey(PairingKey.Parse(pair));
        if (manual is not null) c.ManualReceiverAddress = manual;
        if (noDiscovery) c.AutoDiscovery = false;
    });
    engine.Start();
    while (Opt("--submit") is { } submit)
    {
        var rec = engine.SubmitPdf(submit);
        Console.WriteLine($"Submitted {submit} as job {rec.Metadata.JobId}");
    }
    if (engine.CaptureError is not null) Console.WriteLine($"Capture disabled: {engine.CaptureError}");
    Console.WriteLine("READY");
    await WaitForCtrlC();
    return 0;
}

async Task<int> DiscoverAsync()
{
    var keyText = Opt("--key");
    var seconds = double.Parse(Opt("--seconds") ?? "3");
    var key = keyText is null ? null : PairingKey.Parse(keyText);
    var target = Opt("--target");
    var found = await DiscoveryClient.DiscoverAsync(int.Parse(Opt("--udp") ?? NexoraInfo.DefaultDiscoveryPort.ToString()), key, "cli-" + Environment.MachineName,
        TimeSpan.FromSeconds(seconds), CancellationToken.None, target is null ? null : [IPAddress.Parse(target)]);
    if (found.Count == 0)
    {
        Console.WriteLine("No Nexora receivers answered. Check that the receiver runs, UDP discovery port is allowed by its firewall, and both PCs are on the same subnet.");
        return 3;
    }
    foreach (var r in found)
    {
        var a = r.Announcement;
        Console.WriteLine($"{a.ServerName,-20} {r.Address}:{a.TcpPort,-6} status={a.Status} testMode={a.TestMode} printer={a.DestinationPrinter ?? "(none)"} ready={a.DestinationPrinterReady} v{a.AppVersion} {(r.Trusted ? "PAIRED" : key is null ? "" : "NOT-PAIRED")}");
    }
    return 0;
}

async Task<IPEndPoint> TargetAsync(PairingKey key)
{
    var to = Opt("--to");
    if (to is not null)
    {
        var parts = to.Split(':');
        var host = parts[0];
        int port = parts.Length > 1 ? int.Parse(parts[1]) : NexoraInfo.DefaultTransportPort;
        var ip = IPAddress.TryParse(host, out var p) ? p : (await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork))[0];
        return new IPEndPoint(ip, port);
    }
    var found = await DiscoveryClient.DiscoverAsync(NexoraInfo.DefaultDiscoveryPort, key, "cli", TimeSpan.FromSeconds(2.5), CancellationToken.None);
    var pick = DiscoveryClient.AutoSelect(found, null) ?? throw new InvalidOperationException(
        found.Count == 0 ? "No receiver discovered; use --to HOST" : "Several or no paired receivers discovered; use --to HOST");
    Console.WriteLine($"Using discovered receiver {pick.Announcement.ServerName} at {pick.TransportEndpoint}");
    return pick.TransportEndpoint;
}

PairingKey RequireKey() => PairingKey.Parse(Opt("--key") ?? throw new InvalidOperationException("--key CODE is required (pairing code shown by the receiver)"));

async Task<ClientSession> ConnectAsync(PairingKey key)
{
    var ep = await TargetAsync(key);
    return await TransportClient.ConnectAsync(ep, key, new ClientIdentity("cli-" + Environment.MachineName.ToLowerInvariant(), Environment.MachineName),
        TimeSpan.FromSeconds(5), CancellationToken.None);
}

async Task<int> SendAsync()
{
    var key = RequireKey();
    var copies = int.Parse(Opt("--copies") ?? "1");
    var idText = Opt("--id");
    var dropAfter = long.Parse(Opt("--drop-after") ?? "-1");
    var file = argv.FirstOrDefault() ?? throw new InvalidOperationException("FILE.pdf is required");
    var info = new FileInfo(file);
    var inspection = PdfInspector.Inspect(file);
    var meta = new JobMetadata
    {
        JobId = idText is null ? Guid.NewGuid() : Guid.Parse(idText),
        DocumentName = info.Name, Format = NexoraInfo.PdfMediaType, SizeBytes = info.Length, Sha256 = Hashing.Sha256File(file),
        PageCount = inspection.PageCount, Settings = new PrintSettings { Copies = copies }, CreatedUtc = DateTimeOffset.UtcNow,
        ClientId = "cli-" + Environment.MachineName.ToLowerInvariant(), ClientName = Environment.MachineName, SubmittedBy = Environment.UserName,
    };
    await using var s = await ConnectAsync(key);
    Console.WriteLine($"Connected to {s.ReceiverName} (test mode: {s.Session.TestMode}). Sending {info.Length:N0} bytes, job {meta.JobId}");
    var sw = Stopwatch.StartNew();
    long resumed = -1;
    var status = await s.SendJobAsync(meta, file, new Progress<TransferProgress>(p => { if (resumed < 0) resumed = p.ResumedFrom; }), CancellationToken.None, dropAfter);
    Console.WriteLine($"Receiver verdict: {status.State.ToWire()}{(status.ErrorCode is null ? "" : $" [{status.ErrorCode}]")} — {status.Message}");
    Console.WriteLine($"Elapsed {sw.Elapsed.TotalSeconds:0.00}s{(resumed > 0 ? $", resumed from byte {resumed:N0}" : "")}");
    Console.WriteLine($"JOB {meta.JobId} {status.State.ToWire()}");
    return status.State == JobState.Failed ? 4 : 0;
}

async Task<int> InfoAsync()
{
    var key = RequireKey();
    await using var s = await ConnectAsync(key);
    var info = await s.GetInfoAsync(CancellationToken.None);
    Console.WriteLine(Json.ToText(info, indented: true));
    return 0;
}

async Task<int> StatusAsync()
{
    var key = RequireKey();
    var id = Guid.Parse(argv.FirstOrDefault() ?? throw new InvalidOperationException("JOBID required"));
    await using var s = await ConnectAsync(key);
    foreach (var st in await s.QueryStatusAsync([id], CancellationToken.None))
        Console.WriteLine($"{st.JobId} {st.State.ToWire()} {st.ErrorCode} {st.Message}");
    return 0;
}

int TestPdf()
{
    var pages = int.Parse(Opt("--pages") ?? "1");
    var size = (Opt("--size") ?? "A4").ToUpperInvariant() switch
    {
        "A3" => TestPdfBuilder.A3, "LETTER" => TestPdfBuilder.Letter, _ => TestPdfBuilder.A4,
    };
    long pad = (long)(double.Parse(Opt("--mb") ?? "0") * 1024 * 1024);
    bool landscape = Flag("--landscape");
    var outFile = argv.FirstOrDefault() ?? "nexora-test.pdf";
    File.WriteAllBytes(outFile, TestPdfBuilder.Build("NEXORA LAN TEST JOB", pages, size, pad, landscape));
    Console.WriteLine($"Wrote {outFile} ({new FileInfo(outFile).Length:N0} bytes, {pages} page(s)) sha256={Hashing.Sha256File(outFile)}");
    return 0;
}

int Pairing()
{
    var paths = new NexoraPaths("Receiver", Opt("--data"));
    var store = new ConfigStore<ReceiverConfig>(paths.ConfigFile);
    var cfg = store.Load();
    cfg.Upgrade();
    var key = cfg.GetOrCreatePairingKey(out _);
    if (Flag("--regenerate"))
    {
        key = PairingKey.Generate();
        cfg.SetPairingKey(key);
    }
    store.Save(cfg);
    Console.WriteLine($"Pairing code: {key.Display}   (fingerprint {key.Fingerprint})");
    return 0;
}

async Task<int> PrintAsync()
{
    var printer = Opt("--printer") ?? throw new InvalidOperationException("--printer NAME is required");
    var toFile = Opt("--to-file");
    var copies = int.Parse(Opt("--copies") ?? "1");
    var method = Enum.Parse<XpsPrintMethod>(Opt("--method") ?? "Auto", ignoreCase: true);
    var scaling = Flag("--actual-size") ? PrintScaling.ActualSize
        : Flag("--fit") ? PrintScaling.FitToPrintableArea
        : Enum.Parse<PrintScaling>(Opt("--scaling") ?? "Auto", ignoreCase: true);
    var file = argv.FirstOrDefault() ?? throw new InvalidOperationException("FILE.pdf or FILE.xps is required");
    var outcome = await new DocumentPrinter().PrintAsync(new PrintRequest
    {
        JobId = Guid.NewGuid(), DocumentPath = Path.GetFullPath(file), DocumentName = Path.GetFileName(file), PrinterName = printer,
        Settings = new PrintSettings { Copies = copies }, Scaling = scaling, XpsMethod = method,
        PrintToFile = toFile is null ? null : Path.GetFullPath(toFile), TrackTimeout = TimeSpan.FromMinutes(2),
    }, (s, m) => Console.WriteLine($"  {s.ToWire()}: {m}"), CancellationToken.None);
    Console.WriteLine($"RESULT {outcome.State.ToWire()} {outcome.ErrorCode} {outcome.Message} (pages {outcome.PagesSent})");
    return outcome.State == JobState.Failed ? 4 : 0;
}

int XpsInfoCmd()
{
    var png = Opt("--png");
    int page = int.Parse(Opt("--page") ?? "1") - 1;
    int dpi = int.Parse(Opt("--dpi") ?? "72");
    var file = Path.GetFullPath(argv.FirstOrDefault() ?? throw new InvalidOperationException("FILE.xps is required"));
    var info = XpsInspector.Inspect(file, png, page, dpi);
    if (info.Problem is not null) { Console.WriteLine($"Not a readable XPS document: {info.Problem}"); return 4; }
    Console.WriteLine($"XPS: {info.PageCount} page(s), copies={info.Copies?.ToString() ?? "-"}, paper={info.MediaSize ?? "-"}, orientation={info.Orientation ?? "-"}");
    for (int i = 0; i < info.Pages.Count; i++)
    {
        var p = info.Pages[i];
        Console.WriteLine($"  page {i + 1}: {p.WidthMm:0} x {p.HeightMm:0} mm {(p.WidthMm > p.HeightMm ? "landscape" : "portrait")}");
    }
    if (png is not null) Console.WriteLine($"Rendered page {page + 1} at {dpi} dpi to {png}");
    return 0;
}

async Task<int> XpsCaptureAsync()
{
    var pipe = Opt("--pipe") ?? throw new InvalidOperationException("--pipe NAME is required");
    var queue = Opt("--queue") ?? throw new InvalidOperationException("--queue PRINTER is required");
    var outDir = Directory.CreateDirectory(Opt("--out") ?? "xps-capture").FullName;
    var log = new Log(null, "xpscapture") { WriteToConsole = true };
    await using var server = new PipeCaptureServer(pipe, queue, Path.Combine(outDir, ".incoming"), 4L * 1024 * 1024 * 1024, job =>
    {
        // The capture server checks for PDF; XPS jobs arrive flagged "non-PDF", which is expected here.
        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{SafeFileName.Sanitize(job.DocumentName, 60)}";
        bool xps = XpsInspector.LooksLikeXps(job.FilePath);
        var target = Path.Combine(outDir, name + (xps ? ".xps" : ".bin"));
        File.Move(job.FilePath, target, overwrite: true);
        var size = new FileInfo(target).Length;
        Console.WriteLine($"JOB '{job.DocumentName}' {size:N0} bytes -> {target} (format: {(xps ? "XPS" : "unknown")}, ticket copies={job.Settings.Copies}, paper={job.Settings.PaperSize}, orientation={job.Settings.Orientation})");
        if (xps)
        {
            var info = XpsInspector.Inspect(target);
            Console.WriteLine(info.Problem is null
                ? $"    {info.PageCount} page(s): {string.Join(", ", info.Pages.Select(p => $"{p.WidthMm:0}x{p.HeightMm:0}mm"))}; PrintTicket copies={info.Copies}, paper={info.MediaSize}, orientation={info.Orientation}"
                : $"    XPS could not be read: {info.Problem}");
        }
        return Task.CompletedTask;
    }, log.For("capture"));
    server.Start();
    Console.WriteLine($"Listening on \\\\.\\pipe\\{pipe} for queue '{queue}'. Saving to {outDir}");
    Console.WriteLine("READY");
    await WaitForCtrlC();
    return 0;
}

int PdfInfo()
{
    var png = Opt("--png");
    int page = int.Parse(Opt("--page") ?? "1") - 1;
    int dpi = int.Parse(Opt("--dpi") ?? "72");
    var file = argv.FirstOrDefault() ?? throw new InvalidOperationException("FILE.pdf is required");
    var basic = PdfInspector.Inspect(file);
    Console.WriteLine($"Structure: pdf={basic.IsPdf} version={basic.Version} eof={basic.HasEof} pages(est)={basic.PageCount} {basic.Problem}");
    using var r = PdfRenderer.Open(Path.GetFullPath(file));
    Console.WriteLine($"Windows PDF engine: {r.PageCount} page(s)");
    for (int i = 0; i < r.PageCount; i++)
    {
        var sz = r.PageSizePoints(i);
        Console.WriteLine($"  page {i + 1}: {sz.Width:0.#} x {sz.Height:0.#} pt = {sz.Width / 72 * 25.4:0} x {sz.Height / 72 * 25.4:0} mm {(sz.Width > sz.Height ? "landscape" : "portrait")}");
    }
    if (png is not null)
    {
        using var bmp = r.Render(page, dpi);
        bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"Rendered page {page + 1} at {dpi} dpi to {png} ({bmp.Width}x{bmp.Height})");
    }
    return 0;
}

int Printers()
{
    foreach (var p in PrinterCatalog.List())
        Console.WriteLine($"{(p.IsDefault ? "*" : " ")} {p.Name,-32} ready={p.IsReady,-5} {(p.IsVirtual ? "virtual" : "device "),-7} driver={p.DriverName}  port={p.PortName}  status={p.StatusText}");
    return 0;
}

int Queue()
{
    var q = QueueInspector.Inspect();
    Console.WriteLine($"Queue '{NexoraInfo.PrinterQueueName}': {q.Summary}");
    Console.WriteLine($"  exists={q.Exists} port={q.PortName ?? "-"} (ok={q.PortCorrect}) driver={q.DriverName ?? "-"} (ok={q.DriverCorrect}) default={q.IsDefault}");
    Console.WriteLine($"  '{NexoraInfo.SourceDriverName}' driver installed: {q.DriverAvailable}");
    return q.Healthy ? 0 : 5;
}

static Task WaitForCtrlC()
{
    var tcs = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; tcs.TrySetResult(); };
    return tcs.Task;
}
