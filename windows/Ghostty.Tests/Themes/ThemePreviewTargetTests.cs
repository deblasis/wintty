using System;
using System.Collections.Generic;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The theme CLI reaches the Wintty started from its own executable and no
/// other: with a dev build and an installed app both running, the first
/// process with a preview pipe used to win whichever install it belonged to.
/// </summary>
public class ThemePreviewTargetTests
{
    private const string Installed = @"C:\Program Files\Wintty\Wintty.exe";
    private const string Dev = @"C:\src\wintty\zig-out\bin\Wintty.exe";
    private const int OwnPid = 100;

    private static int? Select(
        string? ownPath,
        IReadOnlyDictionary<int, string?> paths,
        ISet<int> piped,
        IList<int>? probed = null) =>
        ThemePreviewTarget.Select(
            ownPath,
            OwnPid,
            paths.Keys,
            pid => paths[pid],
            pid =>
            {
                probed?.Add(pid);
                return piped.Contains(pid);
            });

    [Fact]
    public void PicksTheSamePathProcessOverAForeignOneListedFirst()
    {
        var paths = new Dictionary<int, string?> { [1] = Installed, [2] = Dev };

        Assert.Equal(2, Select(Dev, paths, new HashSet<int> { 1, 2 }));
        Assert.Equal(1, Select(Installed, paths, new HashSet<int> { 1, 2 }));
    }

    [Fact]
    public void AForeignProcessAloneIsNoTarget()
    {
        // The CLI then runs as if no app were running, rather than
        // previewing into another install's window.
        var paths = new Dictionary<int, string?> { [1] = Installed };

        Assert.Null(Select(Dev, paths, new HashSet<int> { 1 }));
    }

    [Fact]
    public void AForeignProcessPipeIsNeverProbed()
    {
        var paths = new Dictionary<int, string?> { [1] = Installed, [2] = Dev };
        var probed = new List<int>();

        Select(Dev, paths, new HashSet<int> { 1, 2 }, probed);

        Assert.Equal(new[] { 2 }, probed);
    }

    [Theory]
    [InlineData(@"c:\program files\wintty\WINTTY.EXE")]
    [InlineData("C:/Program Files/Wintty/Wintty.exe")]
    [InlineData(@"\\?\C:\Program Files\Wintty\Wintty.exe")]
    [InlineData(@"C:\Program Files\Wintty\Wintty.exe\")]
    [InlineData(@"C:\Program Files\Other\..\Wintty\Wintty.exe")]
    public void SpellingsOfTheSamePathMatch(string spelling)
    {
        var paths = new Dictionary<int, string?> { [1] = spelling };

        Assert.Equal(1, Select(Installed, paths, new HashSet<int> { 1 }));
    }

    [Fact]
    public void ASiblingDirectoryWithACommonPrefixDoesNotMatch()
    {
        var paths = new Dictionary<int, string?> { [1] = @"C:\Program Files\Wintty2\Wintty.exe" };

        Assert.Null(Select(Installed, paths, new HashSet<int> { 1 }));
    }

    [Fact]
    public void AnUnreadablePathIsSkippedNotTrusted()
    {
        // Access denied or an exited process reads as null: it cannot be
        // shown to be ours, so a readable same-path process later wins.
        var paths = new Dictionary<int, string?> { [1] = null, [2] = Installed };

        Assert.Equal(2, Select(Installed, paths, new HashSet<int> { 1, 2 }));
        Assert.Null(Select(Installed, new Dictionary<int, string?> { [1] = null }, new HashSet<int> { 1 }));
    }

    [Fact]
    public void OwnProcessIsNeverATarget()
    {
        var paths = new Dictionary<int, string?> { [OwnPid] = Installed };

        Assert.Null(Select(Installed, paths, new HashSet<int> { OwnPid }));
    }

    [Fact]
    public void ASamePathProcessWithoutAPipeIsPassedOver()
    {
        var paths = new Dictionary<int, string?> { [1] = Installed, [2] = Installed };

        Assert.Equal(2, Select(Installed, paths, new HashSet<int> { 2 }));
    }

    [Fact]
    public void SeveralSamePathProcessesResolveToTheFirstWithAPipe()
    {
        var paths = new Dictionary<int, string?> { [1] = Installed, [2] = Installed };

        Assert.Equal(1, Select(Installed, paths, new HashSet<int> { 1, 2 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoOwnPathIsNoTarget(string? ownPath)
    {
        var paths = new Dictionary<int, string?> { [1] = Installed };

        Assert.Null(Select(ownPath, paths, new HashSet<int> { 1 }));
    }

    [Fact]
    public void PipeNameMatchesTheServerShape()
    {
        Assert.Equal("ghostty-theme-preview-4242", ThemePreviewTarget.PipeNameFor(4242));
    }
}
