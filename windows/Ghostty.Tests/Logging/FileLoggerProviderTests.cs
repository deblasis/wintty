using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class FileLoggerProviderTests : IDisposable
{
    private readonly string _tempDir;

    public FileLoggerProviderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GhosttyLogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task OpensFileUnderExpectedName_OnFirstWrite()
    {
        var clock = new FakeClock(new DateTime(2026, 4, 17, 14, 0, 0, DateTimeKind.Utc));
        await using var sink = new FileLoggerProvider(
            NewOptions(_tempDir), clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("TestCategory");

        logger.LogWarning(new EventId(42, "TestEvent"), "hello");

        await DrainAsync(sink);
        var files = Directory.EnumerateFiles(_tempDir).ToArray();
        Assert.Single(files);
        Assert.Equal("ghostty-20260417.log", Path.GetFileName(files[0]));
        var body = ReadAllTextShared(files[0]);
        Assert.Contains(" | Warn  | 42 | TestCategory | hello", body);
    }

    [Fact]
    public async Task RollsToNewFile_OnUtcDayChange()
    {
        var clock = new FakeClock(new DateTime(2026, 4, 17, 23, 59, 0, DateTimeKind.Utc));
        await using var sink = new FileLoggerProvider(
            NewOptions(_tempDir), clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("TestCategory");

        logger.LogInformation(new EventId(1, "First"), "first day");
        await DrainAsync(sink);

        clock.Set(new DateTime(2026, 4, 18, 0, 1, 0, DateTimeKind.Utc));
        logger.LogInformation(new EventId(2, "Second"), "second day");
        await DrainAsync(sink);

        var files = Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "ghostty-20260417.log", "ghostty-20260418.log" }, files);
    }

    [Fact]
    public async Task RollsToSuffixedFile_WhenSizeCapExceeded()
    {
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        // Tiny cap so one small log line rolls immediately.
        var opts = NewOptions(_tempDir) with { MaxBytesPerFile = 50 };
        await using var sink = new FileLoggerProvider(opts, clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Cat");

        for (int i = 0; i < 5; i++)
            logger.LogWarning(new EventId(i, "E"), "message-{Index}", i);

        await DrainAsync(sink);
        var files = Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Contains("ghostty-20260417.log", files);
        Assert.Contains("ghostty-20260417-1.log", files); // at least one roll happened
    }

    [Fact]
    public async Task EmitsSyntheticDroppedRecord_WhenChannelOverflows()
    {
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        // Capacity=1 so any burst overflows (DropOldest discards the oldest).
        var opts = NewOptions(_tempDir) with { ChannelCapacity = 1, BatchMaxRecords = 1 };
        await using var sink = new FileLoggerProvider(opts, clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Cat");

        for (int i = 0; i < 20; i++)
            logger.LogWarning(new EventId(i, "E"), "burst-{Index}", i);

        await DrainAsync(sink);
        var body = ReadAllTextShared(Path.Combine(_tempDir, "ghostty-20260417.log"));
        // The synthetic "LogRecordsDropped" warning emits category
        // Ghostty.Core.Logging and its message contains the overflow
        // phrase. The format line writes Category and Message but not
        // the EventId name, so we assert on both signals that are
        // actually emitted.
        Assert.Contains("Ghostty.Core.Logging", body);
        Assert.Contains("dropped due to channel overflow", body);
    }

    [Fact]
    public void Dispose_FlushesSingleVerdictRecord_WrittenJustBeforeDispose()
    {
        // Regression: smoke runner spawns Wintty with a one-shot shell
        // (cmd.exe /c exit). Zig writes one "transport resolved:" line,
        // then the shell exits and the WinUI shell calls Dispose() within
        // ~200ms. Without a flush-on-Dispose contract the verdict line
        // can sit unwritten on the bounded channel when the file handle
        // is released; the assertion script then fails with "no verdict
        // line in ghostty-YYYYMMDD.log".
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        var sink = new FileLoggerProvider(NewOptions(_tempDir), clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Ghostty.Zig.validate_transport");

        logger.LogInformation(
            new EventId(1, "Verdict"),
            "transport resolved: shell=\"cmd.exe\" config_mode=auto resolved=conpty");

        // No drain wait: dispose immediately so the test fails unless
        // Dispose synchronously drains the channel and flushes the stream.
        sink.Dispose();

        var body = ReadAllTextShared(Path.Combine(_tempDir, "ghostty-20260417.log"));
        Assert.Contains("transport resolved:", body);
        Assert.Contains("resolved=conpty", body);
    }

    [Fact]
    public void Dispose_DrainsBurst_BeforeReleasingHandle()
    {
        // Heavier variant: many records produced back-to-back, dispose
        // called immediately. If Dispose returns before the writer
        // task has processed the queue, the late records are lost.
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        var sink = new FileLoggerProvider(NewOptions(_tempDir), clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Burst");

        const int count = 500;
        for (int i = 0; i < count; i++)
            logger.LogInformation(new EventId(i, "E"), "record-{Index}", i);

        sink.Dispose();

        var body = ReadAllTextShared(Path.Combine(_tempDir, "ghostty-20260417.log"));
        // Count "record-" occurrences rather than asserting a specific
        // index: BoundedChannel + DropOldest may discard records under
        // burst, but every successfully-enqueued record must reach disk.
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 1, $"expected at least one record on disk, got {lines.Length}");
        Assert.Contains($"record-{count - 1}\n", body); // last record must land
    }

    [Fact]
    public void Dispose_IsIdempotent_WhenInvokedByBothExplicitAndFactoryPath()
    {
        // App.OnAnyWindowClosedInternal calls DisposeAsync explicitly,
        // then LoggerFactory.Dispose (via AddProvider registration)
        // calls Dispose on the same instance. Both paths must succeed
        // without throwing and without corrupting state on the second
        // invocation.
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        var sink = new FileLoggerProvider(NewOptions(_tempDir), clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Idem");

        logger.LogInformation(new EventId(1, "E"), "single-line");

        sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        sink.Dispose(); // second teardown via LoggerFactory.Dispose path
        sink.Dispose(); // tolerate a third pass too

        var body = ReadAllTextShared(Path.Combine(_tempDir, "ghostty-20260417.log"));
        Assert.Contains("single-line", body);
    }

    [Fact]
    public async Task PrunesOldestRollFiles_WhenTotalSizeBudgetExceeded()
    {
        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        // Tiny per-file cap forces a roll on nearly every record; small
        // total budget forces oldest-file pruning. This is the runaway-
        // producer scenario in miniature: without a byte ceiling these
        // 200 writes would leave ~200 same-day files on disk.
        var opts = NewOptions(_tempDir) with { MaxBytesPerFile = 60, MaxTotalBytes = 300 };
        await using var sink = new FileLoggerProvider(opts, clock, RealFileSystem.Instance);
        var logger = sink.CreateLogger("Cat");

        for (int i = 0; i < 200; i++)
            logger.LogWarning(new EventId(i, "E"), "message-{Index}", i);

        await DrainAsync(sink);

        var files = Directory.EnumerateFiles(_tempDir, "ghostty-*.log").ToArray();
        long total = files.Sum(f => new FileInfo(f).Length);

        // The budget is enforced before each new roll file opens, so the
        // on-disk total may transiently exceed it by at most one full file.
        Assert.True(
            total <= opts.MaxTotalBytes + opts.MaxBytesPerFile,
            $"total {total} bytes exceeded budget {opts.MaxTotalBytes} (+1 file {opts.MaxBytesPerFile})");

        // Oldest files were pruned: nowhere near the ~200 a runaway would
        // otherwise leave behind.
        Assert.True(files.Length <= 10, $"expected pruning, found {files.Length} files");
    }

    [Theory]
    // Same day, ascending roll counter: 0 (unsuffixed) is oldest, then 1, 2,
    // 9, 10. A lexical sort would wrongly place "-10" before "-2"; the numeric
    // parse must not. This pins the one subtle property the pruner relies on
    // to evict the genuinely-oldest files.
    [InlineData("ghostty-20260417.log", "ghostty-20260417-1.log")]
    [InlineData("ghostty-20260417-1.log", "ghostty-20260417-2.log")]
    [InlineData("ghostty-20260417-2.log", "ghostty-20260417-10.log")]
    [InlineData("ghostty-20260417-9.log", "ghostty-20260417-10.log")]
    // Older day sorts before newer day regardless of counter.
    [InlineData("ghostty-20260417-99.log", "ghostty-20260418.log")]
    public void RollSortKey_OrdersChronologically_NotLexically(string older, string newer)
    {
        Assert.True(
            FileLoggerProvider.RollSortKey(older) < FileLoggerProvider.RollSortKey(newer),
            $"expected {older} to sort before {newer}");
    }

    [Fact]
    public void RetentionSweep_DeletesFilesOlderThanCutoff_OnConstruction()
    {
        // Today = 2026-04-17. Retention 14 days => cutoff 2026-04-03.
        WriteStub("ghostty-20260301.log"); // very old, should be deleted
        WriteStub("ghostty-20260402.log"); // day before cutoff, should be deleted
        WriteStub("ghostty-20260404.log"); // after cutoff, should remain
        WriteStub("ghostty-20260417.log"); // today, should remain
        WriteStub("not-a-log.txt");       // unrelated, should remain

        var clock = new FakeClock(new DateTime(2026, 4, 17, 12, 0, 0, DateTimeKind.Utc));
        using var sink = new FileLoggerProvider(NewOptions(_tempDir), clock, RealFileSystem.Instance);

        var remaining = Directory.EnumerateFiles(_tempDir).Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.DoesNotContain("ghostty-20260301.log", remaining);
        Assert.DoesNotContain("ghostty-20260402.log", remaining);
        Assert.Contains("ghostty-20260404.log", remaining);
        Assert.Contains("ghostty-20260417.log", remaining);
        Assert.Contains("not-a-log.txt", remaining);
    }

    // ----- helpers -----

    private void WriteStub(string name) => File.WriteAllText(Path.Combine(_tempDir, name), "");

    // Read a file that may still be held open for appending by the
    // writer task. Production side opens with FileShare.Read, so a
    // concurrent reader must pass FileShare.ReadWrite to coexist.
    private static string ReadAllTextShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static FileLoggerOptions NewOptions(string dir) => new()
    {
        Directory = dir,
        BatchMaxRecords = 64,
        RetentionDays = 14,
        ChannelCapacity = 4096,
        MaxBytesPerFile = 16 * 1024 * 1024,
    };

    private static async Task DrainAsync(FileLoggerProvider sink)
    {
        // Give the writer loop one scheduling slice to flush the batch.
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(20);
        }
    }

    private sealed class FakeClock : IClock
    {
        private DateTimeOffset _now;
        // Existing call sites seed with DateTime(..., DateTimeKind.Utc);
        // wrap in DateTimeOffset(TimeSpan.Zero) to preserve the UTC
        // semantics the test setup relies on.
        public FakeClock(DateTime utcNow) => _now = new DateTimeOffset(utcNow, TimeSpan.Zero);
        public void Set(DateTime utcNow) => _now = new DateTimeOffset(utcNow, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now;
    }
}
