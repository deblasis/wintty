using System;
using System.Collections.Generic;
using Ghostty.Core.Themes;
using Xunit;

namespace Ghostty.Tests.Themes;

/// <summary>
/// The theme CLI reaches the Wintty running its own executable file and no
/// other: with a dev build and an installed app both running, the first
/// process with a preview pipe used to win whichever install it belonged to.
/// The readers are injected here; Ghostty.Tests.Windows runs the real ones.
/// </summary>
public class ThemePreviewTargetTests
{
    private const string Installed = @"C:\Program Files\Wintty\Wintty.exe";
    private const string Dev = @"C:\src\wintty\zig-out\bin\Wintty.exe";
    private const int OwnPid = 100;

    private static int? Select(
        string? ownIdentity,
        IReadOnlyDictionary<int, string?> identities,
        ISet<int> piped,
        IList<int>? probed = null,
        IList<int>? foreign = null) =>
        ThemePreviewTarget.Select(
            OwnPid,
            identities.Keys,
            pid => pid == OwnPid ? ownIdentity : identities[pid],
            pid =>
            {
                probed?.Add(pid);
                return piped.Contains(pid);
            },
            (pid, _) => foreign?.Add(pid));

    [Fact]
    public void PicksTheSameExecutableOverAForeignOneListedFirst()
    {
        var ids = new Dictionary<int, string?> { [1] = Installed, [2] = Dev };

        Assert.Equal(2, Select(Dev, ids, new HashSet<int> { 1, 2 }));
        Assert.Equal(1, Select(Installed, ids, new HashSet<int> { 1, 2 }));
    }

    [Fact]
    public void AForeignProcessAloneIsNoTarget()
    {
        // The CLI then runs as if no app were running, rather than
        // previewing into another install's window.
        var ids = new Dictionary<int, string?> { [1] = Installed };

        Assert.Null(Select(Dev, ids, new HashSet<int> { 1 }));
    }

    [Fact]
    public void AForeignProcessPipeIsNeverProbedAndIsReported()
    {
        var ids = new Dictionary<int, string?> { [1] = Installed, [2] = Dev };
        var probed = new List<int>();
        var foreign = new List<int>();

        Select(Dev, ids, new HashSet<int> { 1, 2 }, probed, foreign);

        Assert.Equal(new[] { 2 }, probed);
        Assert.Equal(new[] { 1 }, foreign);
    }

    [Theory]
    [InlineData(@"\\?\C:\Program Files\Wintty\Wintty.exe")]
    [InlineData(@"c:\program files\wintty\WINTTY.EXE")]
    [InlineData("C:/Program Files/Wintty/Wintty.exe")]
    [InlineData(@"C:\Program Files\Wintty\Wintty.exe\")]
    public void SpellingsOfTheSameFinalPathMatch(string spelling)
    {
        var ids = new Dictionary<int, string?> { [1] = spelling };

        Assert.Equal(1, Select(Installed, ids, new HashSet<int> { 1 }));
    }

    [Theory]
    [InlineData(@"\\?\UNC\srv\share\Wintty\Wintty.exe")]
    [InlineData(@"\\.\UNC\srv\share\Wintty\Wintty.exe")]
    [InlineData(@"\\srv\share\Wintty\Wintty.exe")]
    public void ExtendedUncSpellingsMapToThePlainShare(string spelling)
    {
        // Stripping only the four-character prefix would leave
        // "UNC\srv\...", a relative path that names nothing.
        Assert.Equal(@"\\srv\share\Wintty\Wintty.exe", ThemePreviewTarget.NormalizeIdentity(spelling));
    }

    [Theory]
    [InlineData(@"UNC\srv\share\Wintty.exe")]
    [InlineData(@"Wintty.exe")]
    [InlineData(@"\\?\")]
    public void ARelativeIdentityIsUnreadable(string spelling)
    {
        Assert.Null(ThemePreviewTarget.NormalizeIdentity(spelling));
    }

    [Fact]
    public void ASiblingDirectoryWithACommonPrefixDoesNotMatch()
    {
        var ids = new Dictionary<int, string?> { [1] = @"C:\Program Files\Wintty2\Wintty.exe" };

        Assert.Null(Select(Installed, ids, new HashSet<int> { 1 }));
    }

    [Fact]
    public void AnUnreadableCandidateIsSkippedNotTrusted()
    {
        // Access denied or an exited process reads as null: it cannot be
        // shown to be ours, so a readable same-executable process later wins.
        var ids = new Dictionary<int, string?> { [1] = null, [2] = Installed };

        Assert.Equal(2, Select(Installed, ids, new HashSet<int> { 1, 2 }));
        Assert.Null(Select(Installed, new Dictionary<int, string?> { [1] = null }, new HashSet<int> { 1 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnreadableOwnIdentityIsNoTarget(string? own)
    {
        // No fallback to the launch spelling: comparing a spelling with a
        // resolved identity is exactly what misses a junctioned install.
        var ids = new Dictionary<int, string?> { [1] = Installed };

        Assert.Null(Select(own, ids, new HashSet<int> { 1 }));
    }

    [Fact]
    public void OwnProcessIsNeverATarget()
    {
        var ids = new Dictionary<int, string?> { [OwnPid] = Installed };

        Assert.Null(Select(Installed, ids, new HashSet<int> { OwnPid }));
    }

    [Fact]
    public void ASameExecutableProcessWithoutAPipeIsPassedOver()
    {
        var ids = new Dictionary<int, string?> { [1] = Installed, [2] = Installed };

        Assert.Equal(2, Select(Installed, ids, new HashSet<int> { 2 }));
    }

    [Fact]
    public void SeveralSameExecutableProcessesResolveToTheFirstWithAPipe()
    {
        var ids = new Dictionary<int, string?> { [1] = Installed, [2] = Installed };

        Assert.Equal(1, Select(Installed, ids, new HashSet<int> { 1, 2 }));
    }

    [Theory]
    [InlineData(Installed, Installed, true)]
    [InlineData(Installed, @"\\?\c:\program files\wintty\wintty.exe", true)]
    [InlineData(Installed, Dev, false)]
    [InlineData(Installed, null, false)]
    [InlineData(null, null, false)]
    public void SameIdentityNeedsBothSidesReadable(string? a, string? b, bool expected)
    {
        Assert.Equal(expected, ThemePreviewTarget.SameIdentity(a, b));
    }

    [Fact]
    public void PipeNameMatchesTheServerShape()
    {
        Assert.Equal("ghostty-theme-preview-4242", ThemePreviewTarget.PipeNameFor(4242));
    }
}
