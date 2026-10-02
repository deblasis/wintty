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
        var check = connect.Call("Ghostty.Core.Themes.ThemePreviewTarget.ServerPid");
        Assert.True(find.SpanStart < open.SpanStart && open.SpanStart < check.SpanStart,
            "the server pid can only be read once connected, and must be checked before the pipe is returned");
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
        var read = session.Call("reader.ReadLineAsync");
        Assert.True(
            accept.SpanStart < peer.SpanStart && peer.SpanStart < same.SpanStart && same.SpanStart < read.SpanStart,
            "the client must be identified after it connects and before anything it sent is acted on");
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
