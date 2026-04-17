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
        private DateTime _now;
        public FakeClock(DateTime utcNow) => _now = utcNow;
        public void Set(DateTime utcNow) => _now = utcNow;
        public DateTime UtcNow => _now;
    }
}
