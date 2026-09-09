using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The arbiter is the only writer of the overlay slot. A second
/// SetOverlayIcon caller anywhere in the shell reintroduces the unexplained
/// dot this guards against.
/// </summary>
public sealed class TaskbarBadgeWiringTests
{
    [Fact]
    public void OnlyTheOverlayFacade_CallsSetOverlayIcon()
    {
        var callers = ShellSource.AllFiles()
            .Where(f => f.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Any(i => i.Expression.ToString().EndsWith("SetOverlayIcon")))
            .Select(f => f.Name)
            .ToList();
        Assert.Equal(["Taskbar.TaskbarOverlayFacade.cs"], callers);
    }
}
