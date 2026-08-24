using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The shader picker's autoplay feed, exercised without a WinUI runtime.
/// The feed writes to a delegate and paces itself through another one, so a
/// test can hand it a recorder and a pacing hook that never waits: a full
/// pass of the demo script then runs synchronously, in no wall-clock time,
/// on the test thread.
///
/// What the pacing hook deliberately does NOT do is observe the token. If it
/// threw on cancellation, every one of these tests would pass on the hook's
/// behaviour rather than the feed's, and the loop's own cancellation checks
/// (the thing that stops a Write landing after Dispose) would go untested.
/// </summary>
public class ShaderPreviewFeedTests
{
    private static Task NoDelay(int milliseconds, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public void BootTextAndPromptLandBeforeAnythingIsTyped()
    {
        var recorder = new Recorder { StopAfter = 3 };
        var feed = NewFeed(recorder);
        feed.Start();

        Assert.Contains("Starting MS-DOS...", recorder.TextOf(0));
        Assert.Contains("Version 6.22", recorder.TextOf(0));
        Assert.Contains("C:\\>", recorder.TextOf(1));
        // The third write is the first typed character, not more boot text.
        Assert.Equal("d", recorder.TextOf(2));
    }

    [Fact]
    public void CommandsAreTypedOneCharacterPerWrite()
    {
        var recorder = new Recorder { StopAfter = 5 };
        var feed = NewFeed(recorder);
        feed.Start();

        // Banner, prompt, then "dir" one keystroke at a time. One byte per
        // write is what makes the preview look hand-keyed rather than pasted.
        Assert.Equal(new[] { "d", "i", "r" }, recorder.Writes.Skip(2).Select(Decode));
        Assert.All(recorder.Writes.Skip(2), w => Assert.Single(w));
    }

    [Fact]
    public void ScriptPlaysEveryBeatInOrder()
    {
        var recorder = new Recorder { StopAfter = 400 };
        var feed = NewFeed(recorder);
        feed.Start();

        var text = recorder.Text;
        var banner = IndexOf(text, "Starting MS-DOS...");
        var listing = IndexOf(text, "Volume in drive C is WINTTY");
        var autoexec = IndexOf(text, "LH C:\\WINTTY\\SHADERLAB.EXE /GALLERY");
        var version = IndexOf(text, "wintty shader gallery, live preview");
        var echo = IndexOf(text, "shaders make terminals fun");

        Assert.True(banner < listing, "boot banner must precede the dir listing");
        Assert.True(listing < autoexec, "dir must precede type autoexec.bat");
        Assert.True(autoexec < version, "type autoexec.bat must precede ver");
        Assert.True(version < echo, "ver must precede the closing echo");

        // The cursor-shape flips are the whole reason the script exists: they
        // are the event the mode-change cursor shaders animate on.
        Assert.Contains("\x1b[5 q", text, StringComparison.Ordinal);
        Assert.Contains("\x1b[2 q", text, StringComparison.Ordinal);
        Assert.Contains("\x1b[4 q", text, StringComparison.Ordinal);

        // Foregrounds only, never a background: a background SGR (40-49)
        // would stop fullscreen shaders at a palette-resolved cell. The only
        // other sequence the script emits starting "ESC [ 4" is the DECSCUSR
        // underline flip, so drop that and nothing beginning in 4 is left.
        Assert.DoesNotContain(
            "\x1b[4",
            text.Replace("\x1b[4 q", "", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeStopsTheFeed()
    {
        var recorder = new Recorder { StopAfter = 10 };
        var feed = NewFeed(recorder);
        feed.Start();

        // Start returns only once the loop has stopped, which it can only do
        // by observing the cancellation the recorder triggered on write 10.
        // The upper bound is slack for the three writes a command beat emits
        // back to back after its single check.
        Assert.InRange(recorder.Writes.Count, 10, 12);
    }

    [Fact]
    public void StartAfterDisposeIsANoOp()
    {
        var recorder = new Recorder();
        var feed = NewFeed(recorder);

        feed.Dispose();
        feed.Start();

        // Without the disposed latch this hangs rather than fails: Dispose
        // nulls the token source, so Start would hand the loop a brand new,
        // never-cancelled session.
        Assert.Empty(recorder.Writes);
    }

    private static ShaderPreviewFeed NewFeed(Recorder recorder)
    {
        var feed = new ShaderPreviewFeed(
            recorder.Write, NullLogger<ShaderPreviewFeed>.Instance, NoDelay);
        recorder.Feed = feed;
        return feed;
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static int IndexOf(string haystack, string needle)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0, $"feed never wrote {needle}");
        return index;
    }

    /// <summary>
    /// Collects every write and stops the feed once it has seen enough. The
    /// feed loops forever by design, so a bound is not optional here: the
    /// recorder disposing the feed from inside a write is exactly the
    /// "picker closed mid-session" path.
    /// </summary>
    private sealed class Recorder
    {
        private readonly List<byte[]> _writes = new();

        public int StopAfter { get; init; } = int.MaxValue;
        public ShaderPreviewFeed? Feed { get; set; }

        public IReadOnlyList<byte[]> Writes => _writes;

        public string Text => string.Concat(_writes.Select(Encoding.UTF8.GetString));

        public string TextOf(int index) => Encoding.UTF8.GetString(_writes[index]);

        public void Write(ReadOnlySpan<byte> bytes)
        {
            _writes.Add(bytes.ToArray());
            if (_writes.Count >= StopAfter) Feed?.Dispose();
        }
    }
}
