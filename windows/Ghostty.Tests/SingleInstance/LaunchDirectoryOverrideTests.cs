using System;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The rules of <see cref="Ghostty.Core.SingleInstance.LaunchDirectoryOverride"/>:
/// the equals-form flag a forwarded launch carried, and the dialect the CLI
/// speaks for it (trim, quote stripping, -e boundary, last-occurrence, home /
/// inherit / ~/ resolution, relative anchoring, and the spawn policy). The
/// shell-side wiring (the primary reading the flag off the request and applying
/// it to the pane it opens) is pinned by
/// <c>FirstPaneCommandWiringTests</c>.
/// </summary>
public sealed class LaunchDirectoryOverrideTests
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void TheEqualsFormYieldsTheValue()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=C:\\tools" });
        Assert.Equal("C:\\tools", got);
    }

    [Fact]
    public void TheProgramArgumentIsNeverAFlag()
    {
        // argv[0] can spell anything a launcher composed, and the CLI's
        // iterator starts after the program.
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=C:\\tools" });
        Assert.Null(got);
    }

    [Fact]
    public void TheDashETailIsCommandPayloadNotFlags()
    {
        // Everything after the first -e is the command on every cold leg;
        // a flag-shaped word inside it must not move the pane.
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "-e", "cmd", "--working-directory=C:\\x" }));
    }

    [Fact]
    public void TheScanStopsAtTheFirstDashE()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=C:\\a", "-e", "cmd", "--working-directory=C:\\b" });
        Assert.Equal("C:\\a", got);
    }

    [Fact]
    public void TheLastOccurrenceWins()
    {
        // The config cascade applies argv in order, so the last value stands;
        // the GTK client even depends on it, inserting its cwd flag first.
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=C:\\a", "--working-directory=C:\\b" });
        Assert.Equal("C:\\b", got);
    }

    [Fact]
    public void AnEmptyValueIsSkippedNotFatal()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=", "--working-directory=C:\\b" });
        Assert.Equal("C:\\b", got);
    }

    [Fact]
    public void AbsentFlagYieldsNull()
    {
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "-e", "cmd" }));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe" }));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(null));
    }

    [Fact]
    public void NoSpaceSeparatedFormIsInvented()
    {
        // The CLI accepts config values only in the equals form; a bare flag
        // followed by a path is a positional the CLI rejects, so it is no
        // override here either -- consuming it would quietly change what the
        // launch runs.
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory", "C:\\tools" }));
    }

    [Fact]
    public void OnlyAsciiWhitespaceIsTrimmed()
    {
        // parseCLI trims ASCII whitespace; the CLR's Trim() would also strip
        // the NBSP the native parser leaves in place, and the two legs of one
        // command line would disagree.
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=  C:\\tools\t" });
        Assert.Equal("C:\\tools", got);

        var nbsp = "--working-directory=\u00A0C:\\tools";
        var kept = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", nbsp });
        Assert.Equal("\u00A0C:\\tools", kept);
    }

    [Fact]
    public void OneSurroundingQuotePairIsStripped()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "wintty.exe", "--working-directory=\"C:\\Program Files\"" });
        Assert.Equal("C:\\Program Files", got);
    }

    [Fact]
    public void HomeResolvesThroughTheEnvironmentFirst()
    {
        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(profile)) return; // CI shape; the row is inert

        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "home", "C:\\inherited");
        Assert.Equal(profile, got);
    }

    [Fact]
    public void InheritFallsBackToTheInheritedDirectory()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "inherit", "C:\\inherited");
        Assert.Equal("C:\\inherited", got);
    }

    [Fact]
    public void AForwardSlashTildeResolvesAgainstTheProfile()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "~/src", "C:\\inherited");
        Assert.Equal(Home + "\\src", got);
    }

    [Fact]
    public void ABackslashTildeIsALiteralRelativePath()
    {
        // libghostty's Windows home expansion accepts `~/` only and leaves
        // `~\` alone; one dialect is the point of this class.
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "~\\src", "C:\\inherited");
        Assert.Equal("C:\\inherited\\~\\src", got);
    }

    [Fact]
    public void ARelativeValueResolvesAgainstTheInheritedDirectory()
    {
        // The CLI resolves relatives on the client, against the caller's
        // cwd; the wire's working-directory field is that cwd.
        Assert.Equal(
            "C:\\inherited",
            Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(".", "C:\\inherited"));
        Assert.Equal(
            "C:\\base\\sub",
            Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve("sub", "C:\\base"));
        Assert.Equal(
            "C:\\base",
            Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve("..", "C:\\base\\sub"));
    }

    [Fact]
    public void AValueThatCannotResolveIsNoOverride()
    {
        // A NUL cannot arrive through real argv, but it can arrive through a
        // hand-built wire payload, and it is the one byte that makes a
        // relative path unresolvable against its base. An anchored value is
        // taken as given -- the CLI never canonicalizes one either -- and the
        // receiver's own directory check decides it.
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "foo\0bar", "C:\\inherited"));
    }

    [Fact]
    public void ARemoteUncNameIsRefused()
    {
        // The spawn policy: a remote UNC directory names who receives the
        // user's credentials when a shell is spawned there, and a launch
        // flag is not exempt.
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "\\\\evil.example.com\\share", "C:\\inherited"));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "//evil.example.com/share", "C:\\inherited"));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "\\\\.\\C:", "C:\\inherited"));
    }

    [Fact]
    public void ALocalUncNamePasses()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "\\\\localhost\\share", "C:\\inherited");
        Assert.Equal("\\\\localhost\\share", got);
    }

    [Fact]
    public void APlainAnchoredValueIsTakenAsGiven()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "D:\\work\\project", "C:\\inherited");
        Assert.Equal("D:\\work\\project", got);
    }

    [Fact]
    public void NoRequestYieldsNoAnswer()
    {
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            null, "C:\\inherited"));
    }
}
