using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging;

/// <summary>
/// Rolling-file sink for the Windows tree's <see cref="ILoggerFactory"/>.
/// Producer side is lock-free; writes go onto a bounded
/// <see cref="Channel{T}"/> with drop-oldest semantics so a logging storm
/// never blocks UI or termio threads. A single background task drains
/// the channel in batches, formats each record as one pipe-separated
/// line, rotates on UTC day change and on 16 MB size, and prunes files
/// older than 14 days at startup.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private readonly FileLoggerOptions _opts;
    private readonly IClock _clock;
    private readonly IFileSystem _fs;

    private readonly Channel<LogRecord> _channel;
    private readonly ChannelWriter<LogRecord> _writer;
    private readonly ChannelReader<LogRecord> _reader;

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;

    private long _droppedCount;

    // Reused across FormatRecord calls on the single writer task. Not
    // thread-safe, which is fine: only WriterLoopAsync touches it.
    // Caching it removes the per-record StringBuilder allocation the
    // previous implementation paid.
    private readonly StringBuilder _formatBuilder = new(256);

    public FileLoggerProvider(FileLoggerOptions options)
        : this(options, SystemClock.Instance, RealFileSystem.Instance) { }

    internal FileLoggerProvider(FileLoggerOptions options, IClock clock, IFileSystem fs)
    {
        _opts = options;
        _clock = clock;
        _fs = fs;

        // DropOldest causes TryWrite to always return true (the oldest
        // queued record is silently evicted to make room). To surface
        // those evictions we count them in the ItemDropped callback;
        // the writer loop later flushes the count as one synthetic
        // "LogRecordsDropped" warning record per batch.
        _channel = Channel.CreateBounded<LogRecord>(
            new BoundedChannelOptions(options.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _droppedCount));
        _writer = _channel.Writer;
        _reader = _channel.Reader;

        // Best-effort directory prep + startup retention sweep. A locked
        // stale log file (second Ghostty instance running, revoked ACLs)
        // would throw IOException/UnauthorizedAccessException out of the
        // ctor and crash App.OnLaunched for a non-critical cleanup task.
        // Log retention is not worth failing app startup for; mirror the
        // writer-loop rollover sweep which is already try/catched.
        try { _fs.CreateDirectory(_opts.Directory); } catch { /* best-effort */ }
        try { SweepRetention(); } catch { /* best-effort */ }
        _writerTask = Task.Run(WriterLoopAsync);
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, this);

    /// <summary>
    /// Public writer entry so follow-up work (crash-log path convergence)
    /// can drive records straight onto the channel without going through
    /// <see cref="ILogger"/>. Not part of this PR's consumer surface;
    /// kept public to avoid a breaking change when the follow-up lands.
    /// </summary>
    public bool TryWrite(LogRecord record)
    {
        if (_writer.TryWrite(record))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Writer stuck; force-cancel and wait for unwind so _cts.Dispose()
            // below doesn't race the still-propagating OperationCanceledException.
            _cts.Cancel();
            try { await _writerTask.ConfigureAwait(false); }
            catch { /* expected OperationCanceledException */ }
        }
        _cts.Dispose();
    }

    private void SweepRetention()
    {
        if (!_fs.DirectoryExists(_opts.Directory))
            return;

        var cutoff = _clock.UtcToday.AddDays(-_opts.RetentionDays);
        foreach (var path in _fs.EnumerateFiles(_opts.Directory, "ghostty-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var datePart = name.Length >= "ghostty-YYYYMMDD".Length
                ? name.Substring("ghostty-".Length, 8)
                : null;
            if (datePart is null)
                continue;
            if (!DateOnly.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var fileDate))
                continue;
            if (fileDate < cutoff)
                _fs.DeleteFile(path);
        }
    }

    private async Task WriterLoopAsync()
    {
        DateOnly openDate = default;
        int rollCounter = 0;
        Stream? stream = null;

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (await _reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                var batch = DrainBatch();
                if (batch.Count == 0)
                    continue;

                var dropped = Interlocked.Exchange(ref _droppedCount, 0);
                if (dropped > 0)
                    batch.Insert(0, SyntheticDroppedRecord(dropped));

                foreach (var record in batch)
                {
                    try
                    {
                        var today = DateOnly.FromDateTime(record.Timestamp);
                        if (stream is null || today != openDate)
                        {
                            try { stream?.Flush(); } catch (IOException) { /* drop */ }
                            stream?.Dispose();

                            // On UTC-day rollover (not the first-open case), sweep
                            // retention so long-running sessions don't accumulate
                            // stale files past the retention window. Startup sweep
                            // already ran in the ctor, so guard on openDate.
                            var previousDate = openDate;
                            openDate = today;
                            rollCounter = 0;
                            if (previousDate != default)
                            {
                                try { SweepRetention(); } catch { /* best-effort */ }
                            }

                            try
                            {
                                stream = _fs.OpenAppend(PathFor(openDate, rollCounter));
                            }
                            catch (IOException)
                            {
                                // Path gone, ACL revoked, etc. Skip this record;
                                // the next batch will retry.
                                stream = null;
                                continue;
                            }
                        }

                        var len = FormatRecord(record, buffer);
                        try
                        {
                            stream.Write(buffer, 0, len);
                        }
                        catch (IOException)
                        {
                            // Disk full, ACL revoked, handle invalidated: drop
                            // record rather than letting the writer task die.
                            continue;
                        }

                        if (stream.Position >= _opts.MaxBytesPerFile)
                        {
                            try
                            {
                                stream.Flush();
                                stream.Dispose();
                                rollCounter++;
                                stream = _fs.OpenAppend(PathFor(openDate, rollCounter));
                            }
                            catch (IOException)
                            {
                                stream = null;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Shutdown signal: let it propagate to the outer handler.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Last-resort guard: a record-level failure (broken
                        // Exception.Message getter, formatter bug, etc.) must
                        // never kill the writer loop. We can't log through
                        // ILogger here without recursing back into ourselves,
                        // but Debug.WriteLine surfaces the detail when a
                        // debugger is attached so infra failures aren't 100%
                        // silent during development.
                        System.Diagnostics.Debug.WriteLine(
                            $"[FileLoggerProvider] record-level failure: {ex}");
                    }
                }
                try { stream?.Flush(); } catch (IOException) { /* drop */ }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            stream?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private System.Collections.Generic.List<LogRecord> DrainBatch()
    {
        var list = new System.Collections.Generic.List<LogRecord>(_opts.BatchMaxRecords);
        while (list.Count < _opts.BatchMaxRecords && _reader.TryRead(out var rec))
            list.Add(rec);
        return list;
    }

    private LogRecord SyntheticDroppedRecord(long count)
        => new(
            Timestamp: _clock.UtcNow,
            Level: LogLevel.Warning,
            EventId: new EventId(0, "LogRecordsDropped"),
            Category: "Ghostty.Core.Logging",
            Message: $"{count} log record(s) dropped due to channel overflow",
            Exception: null);

    private string PathFor(DateOnly date, int rollCounter)
    {
        var name = rollCounter == 0
            ? $"ghostty-{date:yyyyMMdd}.log"
            : $"ghostty-{date:yyyyMMdd}-{rollCounter}.log";
        return Path.Combine(_opts.Directory, name);
    }

    private int FormatRecord(in LogRecord r, byte[] buffer)
    {
        // 2026-04-17T14:23:17.042Z | Warn  | 2100 | Category | Message\r\n
        //   [indented stack lines on exception]
        var sb = _formatBuilder;
        sb.Clear();
        // AppendFormat writes the timestamp directly into sb's buffer,
        // skipping the intermediate DateTime.ToString() allocation the
        // previous implementation paid per record.
        sb.AppendFormat(CultureInfo.InvariantCulture, "{0:yyyy-MM-ddTHH:mm:ss.fffZ}", r.Timestamp)
          .Append(" | ")
          .Append(LevelTag(r.Level))
          .Append(" | ")
          .Append(r.EventId.Id)
          .Append(" | ")
          .Append(r.Category)
          .Append(" | ")
          .Append(r.Message)
          .Append('\n');

        if (r.Exception is not null)
        {
            // Write the exception type + message, then up to 10 Ghostty.* frames.
            sb.Append("  ").Append(r.Exception.GetType().FullName)
              .Append(": ").Append(r.Exception.Message).Append('\n');

            var trace = r.Exception.StackTrace;
            if (trace is not null)
            {
                int frame = 0;
                foreach (var line in trace.Split('\n'))
                {
                    if (frame >= 10) break;
                    var trimmed = line.TrimEnd();
                    if (trimmed.Length == 0) continue;
                    sb.Append("    ").Append(trimmed).Append('\n');
                    frame++;
                }
            }
        }

        var text = sb.ToString();

        // Guard against pathological record sizes that would overflow the
        // rented buffer. UTF-8 worst case is 4 bytes per char, so cap the
        // character count to buffer.Length / 4 and append a truncation
        // marker. Keeps one record from killing the writer task.
        var maxChars = buffer.Length / 4;
        if (text.Length > maxChars)
        {
            const string suffix = "...[truncated]\n";
            text = string.Concat(text.AsSpan(0, maxChars - suffix.Length), suffix);
        }

        return Encoding.UTF8.GetBytes(text, buffer);
    }

    private static string LevelTag(LogLevel l) => l switch
    {
        LogLevel.Trace       => "Trce ",
        LogLevel.Debug       => "Dbug ",
        LogLevel.Information => "Info ",
        LogLevel.Warning     => "Warn ",
        LogLevel.Error       => "Err  ",
        LogLevel.Critical    => "Crit ",
        _                    => "None ",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly FileLoggerProvider _parent;

        public FileLogger(string category, FileLoggerProvider parent)
        {
            _category = category;
            _parent = parent;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Per-category level filtering is applied upstream by the
        // LoggerFactory filter delegate wired in LoggingBootstrap.Build.
        // By the time this is called, the call has already passed the
        // configured threshold, so we only reject the explicit off level.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _parent.TryWrite(new LogRecord(
                Timestamp: _parent._clock.UtcNow,
                Level: logLevel,
                EventId: eventId,
                Category: _category,
                Message: formatter(state, exception),
                Exception: exception));
        }
    }
}
