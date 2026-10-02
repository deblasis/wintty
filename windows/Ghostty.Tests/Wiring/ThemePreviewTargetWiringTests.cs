using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Both ends of the theme preview pipe go through the same-executable checks
/// that ThemePreviewTargetTests pins. A lookup by process name alone, or a
/// server that serves whoever connects, still compiles and still finds a
/// window; it is just the wrong install's window whenever a dev build runs
/// beside an installed app.
/// </summary>
public class ThemePreviewTargetWiringTests
{
    [Fact]
    public void Cli_selects_by_own_executable_and_checks_the_server_it_reached()
    {
        var connect = ShellSource.Load("Program.cs").Method("ConnectThemePreviewPipe");

        var find = connect.Call("Ghostty.Core.Themes.ThemePreviewTarget.FindTarget");
        var open = connect.Call("pipe.Connect");
        var pid = connect.Call("Ghostty.Core.Themes.ThemePreviewTarget.ServerPid");
        var peer = connect.Call("Ghostty.Core.Themes.ThemePreviewTarget.RunsOwnExecutable");
        var ack = connect.Call("Ghostty.Core.Themes.ThemePreviewTarget.AwaitAck");
        Assert.True(
            find.SpanStart < open.SpanStart && open.SpanStart < pid.SpanStart
                && pid.SpanStart < peer.SpanStart && peer.SpanStart < ack.SpanStart,
            "once connected, the server's pid and then the process behind it are checked, and the "
                + "server's acceptance is awaited, all before the pipe is handed to a caller that writes");
        // A reused pid passes a pid comparison; the identity check must be
        // applied to the server pid read from the pipe, not to the target.
        Assert.Equal("server", peer.Arg(0));
        Assert.Empty(connect.Calls("System.Diagnostics.Process.GetProcessesByName"));
        Assert.Empty(connect.Calls("Process.GetProcessesByName"));
    }

    [Fact]
    public void Both_cli_connect_paths_share_the_checked_connect()
    {
        var program = ShellSource.Load("Program.cs");

        program.Method("RegisterThemeCallback").Call("ConnectThemePreviewPipe");
        program.Method("TrySendListThemesMessage").Call("ConnectThemePreviewPipe");
        Assert.Empty(program.Method("RegisterThemeCallback").Calls("_themePipe.Connect"));
        Assert.Empty(program.Method("TrySendListThemesMessage").Calls("pipe.Connect"));
    }

    [Fact]
    public void Server_rejects_a_client_running_another_executable_before_reading()
    {
        var session = ShellSource.Load("Services.ThemePreviewService.cs").Method("RunOneServerSession");

        var accept = session.Call("server.WaitForConnectionAsync");
        var peer = session.Call("Ghostty.Core.Themes.ThemePreviewTarget.ClientPid");
        var same = session.Call("Ghostty.Core.Themes.ThemePreviewTarget.RunsOwnExecutable");
        var ack = session.Call("server.WriteAsync");
        var read = session.Call("reader.ReadLineAsync");
        Assert.True(
            accept.SpanStart < peer.SpanStart && peer.SpanStart < same.SpanStart
                && same.SpanStart < ack.SpanStart && ack.SpanStart < read.SpanStart,
            "the client must be identified after it connects, and acknowledged only then, before "
                + "anything it sent is acted on");
        Assert.Contains("ThemePreviewTarget.Ack", ack.Arg(0));
    }

    [Fact]
    public void Server_ack_to_a_client_that_left_ends_the_session_without_a_fault()
    {
        // Every CLI run probes the pipe with File.Exists, which connects and
        // closes. If that surfaced as a fault, the loop's fault bound would
        // stand the preview server down for the rest of the session.
        var session = ShellSource.Load("Services.ThemePreviewService.cs").Method("RunOneServerSession");
        var ack = session.Call("server.WriteAsync");

        var guard = ack.Ancestors().OfType<TryStatementSyntax>().First();
        var onGone = Assert.Single(guard.Catches, c => c.Declaration?.Type.ToString() == "IOException");
        Assert.Null(onGone.Filter);
        var exit = Assert.Single(onGone.Block.Statements.OfType<ReturnStatementSyntax>());
        Assert.Equal("PipeLoopOutcome.SessionEnded", exit.Expression?.ToString());
    }

    [Fact]
    public void Server_pipe_name_comes_from_the_shape_the_client_probes()
    {
        // A hand-spelled name on either side drifts silently: the client
        // then finds no pipe and runs the TUI on its own.
        var property = Assert.Single(
            ShellSource.Load("Services.ThemePreviewService.cs").Root
                .DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == "PipeName");

        property.Call("Ghostty.Core.Themes.ThemePreviewTarget.PipeNameFor");
    }
}
