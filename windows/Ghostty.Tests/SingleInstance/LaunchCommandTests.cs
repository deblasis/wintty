using System.Collections.Generic;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.SingleInstance;

/// <summary>
/// The command half of a forwarded launch (#1094 review M2): a cold start
/// turns `wintty -e pwsh -NoLogo` into the first surface's command
/// (libghostty's Config.parseManuallyHook consumes everything after -e as
/// a direct argv), so the primary must run the same thing when the launch
/// arrives over the pipe instead. Pure model, no GUI; the shell-side
/// wiring is pinned by <c>LaunchCommandWiringTests</c>.
/// </summary>
public sealed class LaunchCommandTests
{
    [Fact]
    public void ExecuteFlag_ConsumesTheRestOfArgvAsTheCommand()
    {
        Assert.Equal(
            "pwsh -NoLogo",
            LaunchCommand.FromArgs(new[] { "Wintty.exe", "-e", "pwsh", "-NoLogo" }));
    }

    [Fact]
    public void ExecuteFlag_MayAppearAnywhere_ButOnlyOnceMatters()
    {
        // Config.parseManuallyHook scans argv in order and stops at the
        // first -e; everything after it is the command, even a later -e.
        Assert.Equal(
            "ssh host -p 2222",
            LaunchCommand.FromArgs(new[] { "Wintty.exe", "-e", "ssh", "host", "-p", "2222" }));
    }

    [Fact]
    public void ExecuteFlag_WithNoCommandAfterIt_IsNoCommand()
    {
        // A cold start diagnoses "missing command after -e" and still opens
        // a window; a forward has nothing to act on, so the model answers
        // null and the primary opens the default window.
        Assert.Null(LaunchCommand.FromArgs(new[] { "Wintty.exe", "-e" }));
    }

    [Fact]
    public void NoExecuteFlag_IsNoCommand()
    {
        Assert.Null(LaunchCommand.FromArgs(new[] { "Wintty.exe" }));
        Assert.Null(LaunchCommand.FromArgs(
            new[] { "Wintty.exe", "--jumplist-action=new-window" }));
        Assert.Null(LaunchCommand.FromArgs(null));
    }

    /// <summary>
    /// The joined command must survive the native side's own parse: a
    /// shell-form command on Windows is split with a
    /// CommandLineToArgvW-compatible iterator and spawned directly
    /// (termio Exec), so each part is quoted with the same rules and the
    /// string re-parses to the argv that was forwarded.
    /// </summary>
    [Theory]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\path with space\\", "\"C:\\path with space\\\\\"")]
    [InlineData("trailing\\\"quote\"", "\"trailing\\\\\\\"quote\\\"\"")]
    [InlineData("", "\"\"")]
    public void PartsThatNeedQuoting_AreQuotedForTheWindowsArgvRules(
        string part, string expected)
    {
        Assert.Equal(
            "echo " + expected,
            LaunchCommand.FromArgs(new[] { "Wintty.exe", "-e", "echo", part }));
    }

    [Fact]
    public void PlainParts_JoinWithSpacesUnquoted()
    {
        Assert.Equal(
            "echo plain",
            LaunchCommand.FromArgs(new[] { "Wintty.exe", "-e", "echo", "plain" }));
    }

    [Fact]
    public void MixedParts_QuoteOnlyWhereNeeded()
    {
        Assert.Equal(
            "git log \"--pretty=%h %s\"",
            LaunchCommand.FromArgs(new[]
            {
                "Wintty.exe", "-e", "git", "log", "--pretty=%h %s",
            }));
    }
}
