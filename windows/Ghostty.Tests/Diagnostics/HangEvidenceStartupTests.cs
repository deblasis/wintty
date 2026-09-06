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

    // The "current launch" instant: comfortably after every entry the
    // fixtures write, so entries land inside the (lastLaunch, now]
    // window the resolver notifies on.
    private static readonly DateTimeOffset Now = T0.AddHours(1);

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
        var outcome = HangEvidenceStartup.Resolve(null, T0, Now);
        Assert.False(outcome.Notify);
        Assert.Equal(default(DateTimeOffset), outcome.NewestStall);
    }

    [Fact]
    public void EmptyLogStaysQuiet()
    {
        var outcome = HangEvidenceStartup.Resolve("", T0, Now);
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void NewestStallAfterLastLaunchNotifies()
    {
        var log = Entry(T0) + Entry(T1);
        // The newest parseable marker line is the post-dump line of the
        // newest entry, two seconds after its stall start.
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T1.AddSeconds(1), Now);
        Assert.True(outcome.Notify);
        Assert.Equal(T1.AddSeconds(2), outcome.NewestStall);
    }

    [Fact]
    public void StallOlderThanLastLaunchStaysQuiet()
    {
        var log = Entry(T0) + Entry(T1);
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T1.AddMinutes(1), Now);
        Assert.False(outcome.Notify);
        Assert.Equal(default(DateTimeOffset), outcome.NewestStall);
    }

    [Fact]
    public void StallDuringThisLaunchIsDeferredToTheNextLaunch()
    {
        // A launch slow enough to trip the watchdog logs a stall during
        // OnLaunched itself; that stall belongs to THIS session and must
        // not raise a "previous session froze" notice about the launch
        // the user is watching. It is still newer than this launch's
        // marker, so the next launch reports it.
        var log = Entry(T2);
        Assert.False(HangEvidenceStartup.Resolve(log, lastLaunch: T1, currentLaunch: T2.AddSeconds(-1)).Notify);
        Assert.True(HangEvidenceStartup.Resolve(log, lastLaunch: T1, Now).Notify);
    }

    [Fact]
    public void UnhandledEntriesDoNotCount()
    {
        // Same timestamp shape, different tag: an unhandled exception is
        // already surfaced by its own paths, not this notice.
        var log =
            $"{T1:O} [UI-THREAD UNHANDLED]\nSystem.Exception: boom\n\n" +
            $"{T1.AddSeconds(1):O} [APPDOMAIN UNHANDLED]\nSystem.Exception: boom\n\n";
        var outcome = HangEvidenceStartup.Resolve(log, T0, Now);
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void PartialTrailingFragmentIsSkippedNotFatal()
    {
        // A crash mid-write can leave the newest line cut short; the
        // scan must pass over it and still see the newest whole entry.
        var log = Entry(T0) + $"{T2:O} [UI-THREAD ST";
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T0.AddSeconds(-1), Now);
        Assert.True(outcome.Notify);
        Assert.Equal(T0.AddSeconds(2), outcome.NewestStall);
    }

    [Fact]
    public void PartialLeadingLineFromTheTailCutIsSkippedNotFatal()
    {
        // The 1 MiB tail read can cut a line at the START of the input
        // instead of the end: an entry's first line arrives with its
        // timestamp half gone. It must fail to parse and fall through
        // to the older whole entry rather than misparse or misnotify.
        var cut =
            ($"{T2:O} {HangEvidenceStartup.StallMarker} minidump written (1,024 bytes, triage)\n\n")[10..];
        var log = cut + Entry(T1);
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T0, Now);
        Assert.True(outcome.Notify);
        Assert.Equal(T1.AddSeconds(2), outcome.NewestStall);

        // And a cut line alone is nothing to report.
        Assert.False(HangEvidenceStartup.Resolve(cut, T0, Now).Notify);
    }

    [Fact]
    public void PartialFragmentAloneNeverNotifies()
    {
        // A detail line cut off mid-word is newer than the last launch
        // but is not an entry, so there is nothing to report.
        var log = Entry(T0) + "The UI thread has not pumped for 2222";
        var outcome = HangEvidenceStartup.Resolve(log, lastLaunch: T0.AddMinutes(1), Now);
        Assert.False(outcome.Notify);
    }

    [Fact]
    public void MarkerMatchesWhatTheWatchdogWrites()
    {
        // The watchdog writes the constant into crash.log and this test
        // pins the constant's spelling to the literal, so rewording the
        // marker is a deliberate, visible act rather than a silent
        // reader-writer split.
        Assert.Equal("[UI-THREAD STALL]", HangEvidenceStartup.StallMarker);
    }
}
