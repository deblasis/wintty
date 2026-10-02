using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The theme CLI's pipe lookup goes through the same-executable selection
/// that ThemePreviewTargetTests pins. A lookup by process name alone still
/// compiles and still finds a window, it is just the wrong install's window
/// whenever a dev build runs beside an installed app.
/// </summary>
public class ThemePreviewTargetWiringTests
{
    [Fact]
    public void Cli_pipe_lookup_selects_by_own_executable()
    {
        var find = ShellSource.Load("Program.cs").Method("FindThemePreviewPipe");

        find.Call("Ghostty.Core.Themes.ThemePreviewTarget.FindPipe");
        Assert.Empty(find.Calls("System.Diagnostics.Process.GetProcessesByName"));
        Assert.Empty(find.Calls("Process.GetProcessesByName"));
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
