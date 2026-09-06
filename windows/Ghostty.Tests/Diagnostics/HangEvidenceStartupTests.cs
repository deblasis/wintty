using System;
using Ghostty.Core.Diagnostics;
using Xunit;

namespace Ghostty.Tests.Diagnostics;

public class HangEvidenceStartupTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = T0.AddMinutes(5);
    private static readonly DateTimeOffset T2 = T0.AddMinutes(10);

    private const string DumpPath =
        @"C:\Users\alex\AppData\Local\Wintty\hangs\hang-4242-20260901-120000.dmp";

    /// <summary>An entry pair the way the watchdog writes one.</summary>
    private static string Entry(DateTimeOffset at) =>
        $"{at:O} {HangEvidenceStartup.StallMarker}\n" +
        $"The UI thread has not pumped for 21s; capturing {DumpPath} (triage dump)\n\n" +
        $"{at.AddSeconds(2):O} {HangEvidenceStartup.StallMarker} minidump written (1,024 bytes, triage)\n\n";

    [Fact]
    public void MissingLogStaysQuiet()
    {
        var outcome = HangEvidenceStartup.Resolve(null, T0);
        Assert.False(outcome.Notify);
        Assert.Equal(default(DateTimeOffset), outcome.NewestStall);
    }

    [Fact]
    public void EmptyLogStaysQuiet()
    {
        var outcome = HangEvidenceStartup.Resolve("", T0);
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void NewestStallAfterLastLaunchNotifies()
    {
        var log = Entry(T0) + Entry(T1);
        // The newest parseable marker line is the post-dump line of the
        // newest entry, two seconds after its stall start.
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T1.AddSeconds(1));
        Assert.True(outcome.Notify);
        Assert.Equal(T1.AddSeconds(2), outcome.NewestStall);
    }

    [Fact]
    public void StallOlderThanLastLaunchStaysQuiet()
    {
        var log = Entry(T0) + Entry(T1);
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T1.AddMinutes(1));
        Assert.False(outcome.Notify);
        Assert.Equal(default(DateTimeOffset), outcome.NewestStall);
    }

    [Fact]
    public void UnhandledEntriesDoNotCount()
    {
        // Same timestamp shape, different tag: an unhandled exception is
        // already surfaced by its own paths, not this notice.
        var log =
            $"{T1:O} [UI-THREAD UNHANDLED]\nSystem.Exception: boom\n\n" +
            $"{T1.AddSeconds(1):O} [APPDOMAIN UNHANDLED]\nSystem.Exception: boom\n\n";
        var outcome = HangEvidenceStartup.Resolve(log, T0);
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void PartialTrailingFragmentIsSkippedNotFatal()
    {
        // A crash mid-write can leave the newest line cut short; the
        // scan must pass over it and still see the newest whole entry.
        var log = Entry(T0) + $"{T2:O} [UI-THREAD ST";
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T0.AddSeconds(-1));
        Assert.True(outcome.Notify);
        Assert.Equal(T0.AddSeconds(2), outcome.NewestStall);
    }

    [Fact]
    public void PartialFragmentAloneNeverNotifies()
    {
        // A detail line cut off mid-word is newer than the last launch
        // but is not an entry, so there is nothing to report.
        var log = Entry(T0) + "The UI thread has not pumped for 2222";
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T0.AddMinutes(1));
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void MarkerMatchesWhatTheWatchdogWrites()
    {
        // The watchdog writes this literal into crash.log; the resolver
        // reads the constant. One test line keeps the two from drifting.
        Assert.Equal("[UI-THREAD STALL]", HangEvidenceStartup.StallMarker);
    }
}
