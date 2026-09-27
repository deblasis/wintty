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
/// gpu.prev.log, and history is only ever replaced by a newer previous.
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

    [Fact]
    public void Rotate_moves_the_current_log_aside_with_its_bytes()
    {
        File.WriteAllText(Current, "launch-one evidence");

        Assert.True(GpuLogRotation.Rotate(Current));

        var previous = GpuLogRotation.PreviousPathFor(Current);
        Assert.Equal(GpuLogRotation.PreviousFileName, Path.GetFileName(previous));
        Assert.Equal("gpu.prev.log", Path.GetFileName(previous));
        Assert.Equal("launch-one evidence", File.ReadAllText(previous));
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
        var previous = GpuLogRotation.PreviousPathFor(Current);
        File.WriteAllText(previous, "launch-one evidence");

        Assert.True(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-one evidence", File.ReadAllText(previous));
    }

    [Fact]
    public void A_newer_current_replaces_the_stored_previous()
    {
        var previous = GpuLogRotation.PreviousPathFor(Current);
        File.WriteAllText(previous, "launch-one");
        File.WriteAllText(Current, "launch-two");

        Assert.True(GpuLogRotation.Rotate(Current));

        Assert.Equal("launch-two", File.ReadAllText(previous));
    }
}
