using System.Collections.Concurrent;
using System.Text.Json;

namespace Nexora.Shared.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Component, string Event, string Message,
    IReadOnlyDictionary<string, object?>? Data, string? Exception);

/// <summary>
/// Structured logger: one JSON object per line in a daily file (nexora-YYYYMMDD.log), plus an in-memory
/// event for the UI. Callers must never pass document contents; only names, sizes, IDs and states.
/// </summary>
public sealed class Log : IDisposable
{
    private readonly string? _directory;
    private readonly string _filePrefix;
    private readonly BlockingCollection<LogEntry> _queue = new(boundedCapacity: 10_000);
    private readonly Thread? _writer;
    private readonly int _retentionDays;

    public LogLevel MinimumLevel { get; set; }
    public bool WriteToConsole { get; set; }
    public event Action<LogEntry>? EntryWritten;

    /// <summary>A logger that only raises events / console output (for tests).</summary>
    public static Log Memory() => new(null, "test", LogLevel.Debug, 0);

    public Log(string? directory, string filePrefix, LogLevel minimumLevel = LogLevel.Info, int retentionDays = 14)
    {
        _directory = directory;
        _filePrefix = filePrefix;
        MinimumLevel = minimumLevel;
        _retentionDays = retentionDays;
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Nexora log writer" };
            _writer.Start();
            PurgeOld();
        }
    }

    public LogScope For(string component) => new(this, component);

    public void Write(LogLevel level, string component, string evt, string message,
        IReadOnlyDictionary<string, object?>? data = null, Exception? ex = null)
    {
        if (level < MinimumLevel) return;
        var e = new LogEntry(DateTimeOffset.Now, level, component, evt, message, data, ex?.ToString());
        if (WriteToConsole) Console.WriteLine($"{e.Timestamp:HH:mm:ss} {Abbrev(level)} [{component}] {message}{(ex is null ? "" : " :: " + ex.Message)}");
        try { EntryWritten?.Invoke(e); } catch { /* UI listeners must not break logging */ }
        if (_writer is not null) _queue.TryAdd(e);
    }

    private static string Abbrev(LogLevel l) => l switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warning => "WRN", _ => "ERR" };

    private void WriteLoop()
    {
        foreach (var e in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(_directory!, $"{_filePrefix}-{e.Timestamp:yyyyMMdd}.log");
                var line = JsonSerializer.Serialize(new
                {
                    ts = e.Timestamp.ToString("O"),
                    level = e.Level.ToString().ToLowerInvariant(),
                    component = e.Component,
                    @event = e.Event,
                    msg = e.Message,
                    data = e.Data,
                    exception = e.Exception,
                }, Json.Options);
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never crash the app (disk full, file locked by a viewer, ...).
            }
        }
    }

    private void PurgeOld()
    {
        if (_retentionDays <= 0) return;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_directory!, $"{_filePrefix}-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-_retentionDays)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer?.Join(TimeSpan.FromSeconds(2));
    }
}

public readonly struct LogScope(Log log, string component)
{
    public Log Root => log;
    public void Debug(string evt, string msg, object? data = null) => log.Write(LogLevel.Debug, component, evt, msg, ToDict(data));
    public void Info(string evt, string msg, object? data = null) => log.Write(LogLevel.Info, component, evt, msg, ToDict(data));
    public void Warn(string evt, string msg, object? data = null, Exception? ex = null) => log.Write(LogLevel.Warning, component, evt, msg, ToDict(data), ex);
    public void Error(string evt, string msg, object? data = null, Exception? ex = null) => log.Write(LogLevel.Error, component, evt, msg, ToDict(data), ex);

    private static IReadOnlyDictionary<string, object?>? ToDict(object? data)
    {
        if (data is null) return null;
        if (data is IReadOnlyDictionary<string, object?> d) return d;
        return data.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(data));
    }
}
