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

    // 0 = live, 1 = disposed. Guarded via Interlocked so Dispose and
    // DisposeAsync can be invoked in any order or concurrently without
    // double-completing the channel or double-disposing _cts. Both paths
    // run during normal shutdown: App.OnAnyWindowClosedInternal calls
    // DisposeAsync explicitly, then LoggerFactory.Dispose later calls
    // Dispose() on every registered provider.
    private int _disposed;

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

    public bool TryWrite(LogRecord record)
    {
        if (_writer.TryWrite(record))
            return true;

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    // Process shutdown calls Dispose() (sync) via LoggerFactory.Dispose;
    // app teardown also calls DisposeAsync() directly. Both must drain
    // the channel and dispose the underlying FileStream before returning,
    // otherwise records emitted in the last few hundred ms of the
    // process lifetime never reach disk. The synchronous path uses
    // Task.Wait rather than sync-over-async so it cannot deadlock under
    // a UI SynchronizationContext, and avoids the ValueTask -> Task
    // allocation on the hot shutdown path.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _writer.TryComplete();
        try
        {
            if (!_writerTask.Wait(TimeSpan.FromSeconds(2)))
            {
                // Writer stuck (e.g. disk hung); cancel and let it unwind
                // so the CTS Dispose below doesn't race a still-propagating
                // OperationCanceledException. The inner catch handles the
                // expected OperationCanceledException-wrapped-in-
                // AggregateException case; the outer catch below only
                // fires on a genuine writer fault from the initial Wait,
                // before we ever asked the writer to cancel.
                _cts.Cancel();
                try { _writerTask.Wait(); }
                catch { /* expected: cancellation propagating out */ }
            }
        }
        catch (AggregateException) { /* writer faulted before timeout; nothing useful we can do here */ }
        _cts.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _writer.TryComplete();
        try
        {
            await _writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _cts.Cancel();
            try { await _writerTask.ConfigureAwait(false); }
            catch { /* expected OperationCanceledException */ }
        }
        catch { /* writer faulted; nothing useful we can do here */ }
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

    /// <summary>
    /// Deletes oldest <c>ghostty-*.log</c> files until the combined size of
    /// the directory is within <see cref="FileLoggerOptions.MaxTotalBytes"/>.
    /// Called on every rollover (no file handle is open at those points, so
    /// deletion is safe). This is the source-agnostic backstop that bounds
    /// disk use even when <see cref="SweepRetention"/>'s date cutoff cannot
    /// (a same-day storm produces thousands of today-dated files).
    /// </summary>
    private void EnforceTotalSizeBudget()
    {
        if (!_fs.DirectoryExists(_opts.Directory))
            return;

        var files = new System.Collections.Generic.List<(string Path, long Length, long Order)>();
        long total = 0;
        foreach (var path in _fs.EnumerateFiles(_opts.Directory, "ghostty-*.log"))
        {
            long len;
            try { len = _fs.FileLength(path); }
            catch (IOException) { continue; }   // vanished between enumerate and stat
            total += len;
            files.Add((path, len, RollSortKey(path)));
        }

        if (total <= _opts.MaxTotalBytes)
            return;

        // Oldest-first by the logger's own naming (date, then numeric roll
        // counter) so we evict the least useful logs and keep the newest.
        files.Sort((a, b) => a.Order.CompareTo(b.Order));
        foreach (var f in files)
        {
            if (total <= _opts.MaxTotalBytes)
                break;
            try
            {
                _fs.DeleteFile(f.Path);
                total -= f.Length;
            }
            catch (IOException) { /* locked/in use: skip, try the next */ }
        }
    }

    /// <summary>
    /// Chronological sort key parsed from a <c>ghostty-YYYYMMDD[-N].log</c>
    /// name: date major, numeric roll counter minor. Filesystem-timestamp
    /// independent so behavior is deterministic and testable. Unrecognized
    /// names sort last (pruned only as a last resort).
    /// </summary>
    private static long RollSortKey(string path)
    {
        const string prefix = "ghostty-";
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            name.Length < prefix.Length + 8)
            return long.MaxValue;

        if (!long.TryParse(
                name.AsSpan(prefix.Length, 8), NumberStyles.None,
                CultureInfo.InvariantCulture, out var date))
            return long.MaxValue;

        long counter = 0;
        var rest = name.AsSpan(prefix.Length + 8); // "" or "-N"
        if (rest.Length > 1 && rest[0] == '-')
            long.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out counter);

        // date (yyyyMMdd, already monotonic) dominates; counter breaks ties.
        return date * 100_000_000L + counter;
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

                            // Bound total on-disk size before opening the next
                            // file. Date-based retention cannot prune same-day
                            // files, so a storm would otherwise grow unbounded;
                            // this deletes oldest files regardless of date.
                            try { EnforceTotalSizeBudget(); } catch { /* best-effort */ }

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
                                // Prune oldest files to honor the byte budget
                                // before the next roll file is created. No
                                // handle is open here, so deleting is safe.
                                try { EnforceTotalSizeBudget(); } catch { /* best-effort */ }
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
            // Force the OS write cache to commit before releasing the
            // handle. Without this, FileStream.Dispose returns once the
            // managed buffer is in the OS cache, but the OS may take
            // hundreds of ms to push to disk. Cross-process readers that
            // open the file the instant Wintty.exe exits (validate-
            // transport smoke runner, third-party log tailers) then see
            // a truncated view. Flush(true) calls FlushFileBuffers on
            // Windows which is synchronous and forces commit. We only
            // pay it once per writer-task lifetime so the cost is
            // negligible compared to the per-batch Flush() that already
            // runs in the steady-state loop.
            // Cast guard: IFileSystem.OpenAppend's return type is the
            // base Stream so test fakes can plug in MemoryStream, but
            // the production RealFileSystem hands back a FileStream
            // whose Flush(bool) overload calls FlushFileBuffers when
            // flushToDisk=true. Fall back to the parameterless Flush
            // for fakes; in-memory writes are already disk-equivalent
            // for them.
            try
            {
                if (stream is FileStream fs) fs.Flush(flushToDisk: true);
                else stream?.Flush();
            }
            catch (IOException) { /* drop */ }
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
            // LogRecord.Timestamp is DateTime; IClock.UtcNow is
            // DateTimeOffset post-widening, so unwrap via UtcDateTime.
            Timestamp: _clock.UtcNow.UtcDateTime,
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
                // LogRecord.Timestamp is DateTime; IClock.UtcNow is
                // DateTimeOffset post-widening, so unwrap via UtcDateTime.
                Timestamp: _parent._clock.UtcNow.UtcDateTime,
                Level: logLevel,
                EventId: eventId,
                Category: _category,
                Message: formatter(state, exception),
                Exception: exception));
        }
    }
}
