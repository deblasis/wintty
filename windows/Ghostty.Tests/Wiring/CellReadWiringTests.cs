using System;
using System.IO;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The apprt cell read must stay a reader. ghostty_surface_read_cells once
/// filled its throwaway RenderState with the plain consuming update(),
/// which ate the dirty state the surface's own renderer was about to paint
/// (the hidden-tab-preview blank). readCellsLocked is Zig source this
/// assembly cannot call, so the source is all there is; every assertion is
/// bounded to that one function's body, so a match elsewhere in the file
/// cannot satisfy it, and the closing-brace scan failing is a red, not a
/// silent empty slice.
/// </summary>
public class CellReadWiringTests
{
    private const string Fn = "fn readCellsLocked(";

    private static string ReadCellsLockedBody()
    {
        var root = RepoRoot();
        var zig = File.ReadAllText(Path.Combine(root, "src", "apprt", "embedded.zig"));
        var start = zig.IndexOf(Fn, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{Fn}' not found in src/apprt/embedded.zig");
        // The function is a 4-space-indented member: its body runs to the
        // first line that closes it at the same indentation.
        var end = zig.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "readCellsLocked's closing brace not found");
        return zig[start..end];
    }

    [Fact]
    public void TheCellReadFillsItsStateAsAReader()
    {
        Assert.Contains("state.updateAsReader(", ReadCellsLockedBody());
    }

    [Fact]
    public void TheCellReadNeverCallsTheConsumingUpdate()
    {
        var body = ReadCellsLockedBody();
        Assert.DoesNotContain("state.update(", body);
        Assert.DoesNotContain("state.beginUpdate(", body);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
