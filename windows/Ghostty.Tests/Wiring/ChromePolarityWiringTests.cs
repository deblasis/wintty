using System;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The chrome's ink and its solid fill must read their polarity from ONE
/// source: the window theme manager, which holds the explicit window-theme
/// override when there is one and follows the system otherwise. A fill or
/// an estimate that reads the OS desktop directly disagrees with the window
/// the user asked for - a light-themed window on a dark desktop then paints
/// a dark frame around light content, and the title ink is scored against a
/// ground that is not on screen. Both disagreement diseases on record
/// (a fill from the desktop scored by the manager, and the reverse) came
/// from the two sites below drifting apart.
/// </summary>
public sealed class ChromePolarityWiringTests
{
    [Fact]
    public void TheSolidChromeFillAndItsEstimateReadTheWindowThemeNotTheDesktop()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs").Root;

        foreach (var property in new[] { "ChromeFillArgb", "EstimatedBackdropGround" })
        {
            var decl = source.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                .Single(p => p.Identifier.Text == property);
            var text = decl.ToString();

            // Non-vacuity: the property still answers a polarity question.
            Assert.Contains("IsDarkMode", text);

            Assert.DoesNotContain("OsTheme.IsDark", text);
        }
    }

    [Fact]
    public void TheDesktopReadStillAnswersTheSystemFlipNotTheChromePaint()
    {
        var source = ShellSource.Load("MainWindow.xaml.cs").Root;

        // The system-theme handler still classifies its own event sender, and
        // the startup scheme still needs a value before the manager exists;
        // those are the desktop read's remaining jobs. The chrome paint path
        // (the fill and the estimate above) is not one of them.
        var sites = source.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().Contains("OsTheme.IsDark"))
            .ToList();
        Assert.Contains(sites, i => i.Ancestors().OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "OnSystemColorValuesChanged"));
    }
}
