using System;
using System.IO;
using Ghostty.Core.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

/// <summary>
/// The rotation that keeps one previous gpu.log alive across a relaunch
/// (#968). Program.cs opens the new log right after calling this, so what
/// these tests pin is the contract the open relies on: the current path is
/// free after a successful rotate, the bytes that were there now live at
/// gpu.prev.log, and history is only ever replaced by a newer previous -
/// never deleted on the way to nothing.
///
/// Expected paths are spelled as literals, not computed through
/// <see cref="GpuLogRotation.PreviousPathFor"/>: a helper that drifted
/// would otherwise drag the tests' expectations with it.
/// </summary>
public class GpuLogRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "GpuLogRotationTests_" + Guid.NewGuid().ToString("N"));

    public GpuLogRotationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Current => Path.Combine(_dir, "gpu.log");

    /// <summary>The literal path the rotation must land on.</summary>
    private string Previous => Path.Combine(_dir, "gpu.prev.log");

    [Fact]
    public void The_previous_name_is_the_issue_named_file()
    {
        Assert.Equal("gpu.prev.log", GpuLogRotation.PreviousFileName);
        Assert.Equal(
            Path.Combine(Path.GetTempPath(), "gpu.prev.log"),
            GpuLogRotation.PreviousPathFor(Path.Combine(Path.GetTempPath(), "gpu.log")));
    }

    [Fact]
    public void Rotate_moves_the_current_log_aside_with_its_bytes()
    {
        File.WriteAllText(Current, "launch-one evidence");

        Assert.True(GpuLogRotation.Rotate(Current));

        Assert.Equal("gpu.prev.log", Path.GetFileName(Previous));
        Assert.Equal("launch-one evidence", File.ReadAllText(Previous));
        Assert.False(File.Exists(Current),
            "the current path must be free for the new launch's open");
    }

    [Fact]
    public void A_launch_with_no_current_log_keeps_the_stored_previous()
    {
        // A first launch (or one whose redirect never ran) has nothing to
        // carry aside. Deleting the stored previous on the way to nothing
        // is exactly the evidence loss this rotation exists to stop, in a
        // cheaper coat.
        File.WriteAllText(Previous, "launch-one evidence");

        Assert.True(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-one evidence", File.ReadAllText(Previous));
    }

    [Fact]
    public void A_newer_current_replaces_the_stored_previous()
    {
        File.WriteAllText(Previous, "launch-one");
        File.WriteAllText(Current, "launch-two");

        Assert.True(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-two", File.ReadAllText(Previous));
    }

    [Fact]
    public void A_held_current_log_leaves_the_stored_previous_untouched()
    {
        // The second-launch shape: RedirectStderrToFile runs before the
        // single-instance election, so a launch that arrives while a
        // primary is running rotates against a gpu.log the primary holds
        // for the life of its process (no FILE_SHARE_DELETE). The rotation
        // must bounce off the held handle WITHOUT having deleted the
        // stored previous - deleting first is what turned this case into
        // evidence loss.
        File.WriteAllText(Previous, "launch-one evidence");
        File.WriteAllText(Current, "held by another instance");
        using var hold = new FileStream(
            Current, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-one evidence", File.ReadAllText(Previous));
        Assert.Equal("held by another instance", File.ReadAllText(Current));
        Assert.False(File.Exists(Previous + ".tmp"), "no scratch file is part of this design");
    }

    [Fact]
    public void A_held_stored_previous_degrades_without_loss()
    {
        // A tailer holding gpu.prev.log open must not cost the current
        // launch's evidence either: the replace-move fails outright and
        // both files stay as they were; the caller appends to gpu.log
        // instead.
        File.WriteAllText(Previous, "launch-one evidence");
        File.WriteAllText(Current, "launch-two evidence");
        using var hold = new FileStream(
            Previous, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-one evidence", File.ReadAllText(Previous));
        Assert.Equal("launch-two evidence", File.ReadAllText(Current));
        Assert.False(File.Exists(Previous + ".tmp"), "no scratch file is part of this design");
    }
}
