using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Ghostty.Tests.Shell;
using Xunit;

namespace Ghostty.Tests.Notifications;

/// <summary>
/// Guards the child-exited toast's wiring by reading the shell source as
/// text, the way <c>ToastActivationWiringTests</c> does: this project
/// deliberately does not reference Ghostty.csproj, so the shell half of
/// deblasis/wintty#1193 (the surface's command reaching the notification
/// policy) can only be asserted at its call sites.
///
/// The policy itself (what may appear in the toast, the cap, the redaction)
/// is unit-tested in <see cref="NotificationPolicyTests"/>; what these can
/// catch is the plumbing dropping the command, which compiles, runs, and
/// silently degrades every toast back to "The shell exited ...".
/// </summary>
public class ChildExitedToastWiringTests
{
    // The host routes the native child-exit action through the policy; the
    // surface's own command text has to be one of the inputs, next to the
    // exit code. Passing null instead compiles cleanly and is exactly how
    // the feature would silently disappear again.
    [Fact]
    public void HostFeedsTheSurfaceCommandIntoTheChildExitedPolicy()
    {
        var handler = Member("GhosttyHost.cs", "case GhosttyActionTag.ShowChildExited:");

        var policy = CSharpSourceText.RequireIndex(
            handler, "NotificationPolicy.ChildExited(",
            "the host no longer routes child exits through the notification policy");
        var code = CSharpSourceText.RequireIndex(
            handler, "info.ExitCode",
            "the child-exited toast no longer carries the exit code");
        var command = CSharpSourceText.RequireIndex(
            handler, "c.SurfaceCommandText",
            "the host no longer hands the surface's command to the child-exited toast");

        Assert.True(policy < code, "the exit code is an argument of the policy call");
        Assert.True(code < command, "the command travels with the exit code in the same call");

        // Both trailing inputs are strings, so a positional swap compiles
        // and every policy unit test still passes; pin each one inside the
        // call's own argument list, in the order the signature declares.
        var args = CallArguments(handler, policy);
        var commandArg = CSharpSourceText.RequireIndex(
            args, "c.SurfaceCommandText",
            "the surface's command is no longer an argument of the policy call");
        var keyArg = CSharpSourceText.RequireIndex(
            args, "c.ToastSurfaceKey",
            "the surface key is no longer an argument of the policy call");
        Assert.True(commandArg < keyArg, "the command precedes the surface key in the policy call");
    }

    // The text between the policy call's opening paren and its matching
    // close: the argument list itself, not the whole case block.
    private static string CallArguments(string member, int callStart)
    {
        var open = member.IndexOf('(', callStart);
        Assert.True(open >= 0, "the policy call has an argument list");
        var depth = 0;
        for (var i = open; i < member.Length; i++)
        {
            if (member[i] == '(') depth++;
            else if (member[i] == ')' && --depth == 0) return member[open..(i + 1)];
        }

        Assert.Fail("the policy call is never closed");
        return string.Empty;
    }

    // The control remembers the command it was created with, at the moment
    // it hands that text to libghostty: one evaluation, so the toast names
    // the command the surface actually ran and cannot drift from it.
    [Fact]
    public void SurfaceCommandTextIsLatchedWhereTheSurfaceIsCreated()
    {
        var create = Member("TerminalControl.xaml.cs", "private bool TryCreateSurface()");

        var latch = CSharpSourceText.RequireIndex(
            create, "_surfaceCommandText = surfaceCommand;",
            "the control no longer latches the command text for the child-exited toast");
        var config = CSharpSourceText.RequireIndex(
            create, "surfaceConfig.Command = _commandUtf8",
            "the surface no longer receives the command");

        Assert.True(latch < config, "the latch happens at surface creation, before the surface is created");
    }

    // The toast reads the latched text through this member; if it is
    // renamed or removed the host call above loses its input.
    [Fact]
    public void TheControlExposesTheLatchedCommand()
    {
        var source = CSharpSourceText.Strip(ReadEmbedded("TerminalControl.xaml.cs"));
        CSharpSourceText.RequireIndex(
            source, "internal string? SurfaceCommandText",
            "the control no longer exposes the latched command text");
    }

    private static string Member(string fileSuffix, string declaration)
        => CSharpSourceText.Member(ReadEmbedded(fileSuffix), declaration);

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
