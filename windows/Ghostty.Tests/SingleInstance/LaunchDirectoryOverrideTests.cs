using System;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The rules of <see cref="Ghostty.Core.SingleInstance.LaunchDirectoryOverride"/>:
/// the equals-form flag a forwarded launch carried, and the special values the
/// CLI accepts for it. The shell-side wiring (the primary reading the flag off
/// the request and applying it to the pane it opens) is pinned by
/// <c>FirstPaneCommandWiringTests</c>.
/// </summary>
public sealed class LaunchDirectoryOverrideTests
{
    private static string Home =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void TheEqualsFormYieldsTheValue()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=C:\\tools" });
        Assert.Equal("C:\\tools", got);
    }

    [Fact]
    public void AbsentFlagYieldsNull()
    {
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "-e", "cmd" }));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            Array.Empty<string>()));
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
            new[] { "--working-directory", "C:\\tools" }));
    }

    [Fact]
    public void TheFirstOccurrenceWins()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=C:\\first", "--working-directory=C:\\second" });
        Assert.Equal("C:\\first", got);
    }

    [Fact]
    public void TheValueIsTrimmedLikeTheCliTrimsIt()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=  C:\\tools  " });
        Assert.Equal("C:\\tools", got);
    }

    [Fact]
    public void AnEmptyValueIsNoOverride()
    {
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=" }));
        Assert.Null(Ghostty.Core.SingleInstance.LaunchDirectoryOverride.FromArgs(
            new[] { "--working-directory=   " }));
    }

    [Fact]
    public void HomeResolvesToTheProfile()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "home", "C:\\inherited");
        Assert.Equal(Home, got);
    }

    [Fact]
    public void InheritFallsBackToTheInheritedDirectory()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "inherit", "C:\\inherited");
        Assert.Equal("C:\\inherited", got);
    }

    [Fact]
    public void ATildePrefixResolvesAgainstTheProfile()
    {
        var got = Ghostty.Core.SingleInstance.LaunchDirectoryOverride.Resolve(
            "~/src", "C:\\inherited");
        Assert.Equal(Home + "\\src", got);
    }

    [Fact]
    public void APlainValueIsTakenAsGiven()
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
