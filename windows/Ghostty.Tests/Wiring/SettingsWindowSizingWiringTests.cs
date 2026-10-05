using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That the two sized windows size themselves through the one DPI-scaled,
/// work-area-clamped placement, rather than each multiplying its own
/// constants out of a constructor.
///
/// The math itself is pure and lives in <c>Ghostty.Core</c>, where
/// <c>DpiScaledWindowPlacementTests</c> drives it against real numbers. What
/// no unit test can see is whether a window still asks for it: the WinUI
/// assembly cannot be loaded into a test host, so the source is all there
/// is, and a window that computes a correct rect and then hands the OS its
/// unscaled constant is a green suite over a window that still opens at
/// 733x500 on a 150% monitor.
///
/// Four joints carry the fix, and each is separately defeatable:
///
///   - The scale. A literal, a system DPI, or a call that reads the window's
///     own DPI are three different windows; only the third is the fix.
///   - The design size. It has to stay declared as a constant the call
///     names, or the 1100x750 that reads as the design intent drifts into
///     the arithmetic and the scale silently applies to nothing.
///   - The work area. The clamp needs one, and a size computed without it is
///     the off-screen-start bug in its other half.
///   - The application. A rect that is computed and dropped is the whole
///     defect wearing a passing test, so the four members are pinned to the
///     MoveAndResize arguments in order: a swapped pair would pass a
///     "does it resize" check.
///
/// What this cannot prove: what GetDpiForWindow returns, or what the window
/// looks like. Both need a real window on a real monitor.
/// </summary>
public class SettingsWindowSizingWiringTests
{
    /// <summary>The settings shell: the 1100x750 window.</summary>
    private const string SettingsFile = "Settings.SettingsWindow.xaml.cs";

    /// <summary>The about panel: the 420x560 one, same defect.</summary>
    private const string AboutFile = "Dialogs.AboutWindow.xaml.cs";

    /// <summary>The one placement call both windows must make.</summary>
    private const string Compute = "DpiScaledWindowPlacement.Compute";

    /// <summary>The window read that supplies the scale.</summary>
    private const string DpiCall = "PInvoke.GetDpiForWindow";

    [Theory]
    [InlineData(SettingsFile)]
    [InlineData(AboutFile)]
    public void SizedWindow_ScalesItsDesignSizeAndClampsItToTheWorkArea(string file)
    {
        var ctor = Constructor(ShellSource.Load(file));

        // Exactly one: two calls are two placements racing for the window,
        // and the second one is a second policy about which wins.
        var compute = Assert.Single(ctor.Calls(Compute));

        Assert.Equal(4, compute.ArgumentList.Arguments.Count);
        Assert.True(
            compute.ArgExpression(3).ToString().Contains(
                "WindowHelper.WorkAreaFor", StringComparison.Ordinal),
            "the placement must be clamped against a work area resolved for this "
            + $"window; found `{compute.Arg(3)}`. A size computed with nothing to "
            + "clamp it against is how a 750-tall window starts above the top of a "
            + "768-tall work area");

        // The scale is this window's own monitor, read once, and handed on
        // under the name the placement reads it by.
        var dpi = Assert.Single(ctor.Calls(DpiCall));
        Assert.True(
            dpi.ArgumentList.Arguments.Count == 1,
            $"{DpiCall} takes the window handle and nothing else; found "
            + $"{dpi.ArgumentList.Arguments.Count} arguments, so this test is not "
            + "reading the scale the window will use");

        var scale = Assert.IsType<IdentifierNameSyntax>(compute.ArgExpression(2));
        var declared = Assert.Single(
            ctor.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
            v => v.Identifier.ValueText == scale.Identifier.ValueText);
        var initializer = Assert.IsType<InvocationExpressionSyntax>(
            declared.Initializer!.Value);
        Assert.True(
            initializer.CalleeText().EndsWith(DpiCall, StringComparison.Ordinal),
            $"the `{scale}` the placement scales by must be read by {DpiCall}, found "
            + $"`{initializer.CalleeText()}`. The system DPI is the wrong number on a "
            + "per-monitor-scaled setup, which is exactly where this bug was reported");

        // The design size stays a named constant the call names. Written into
        // the argument list instead, the constants still read as the design
        // size while nothing scales them.
        foreach (var index in new[] { 0, 1 })
        {
            var arg = Assert.IsType<IdentifierNameSyntax>(compute.ArgExpression(index));
            var local = Assert.Single(
                ctor.DescendantNodes().OfType<VariableDeclaratorSyntax>(),
                v => v.Identifier.ValueText == arg.Identifier.ValueText);
            var statement = Assert.IsType<LocalDeclarationStatementSyntax>(local.Parent!.Parent);
            Assert.True(
                statement.Modifiers.Any(m => m.ValueText == "const"),
                $"`{arg}` must stay a `const int` design size declared in this "
                + $"constructor; found `{statement}`. The scale multiplies whatever "
                + "the call is handed, so a size computed elsewhere has already had "
                + "its pixels fixed before the DPI is read");
        }

        // And the result is what reaches the OS, all four members, in order.
        var placement = Assert.IsType<VariableDeclaratorSyntax>(
            compute.FirstAncestorOrSelf<VariableDeclaratorSyntax>());
        var resize = Assert.Single(ctor.Calls("appWindow.MoveAndResize"));
        var rect = Assert.IsType<ObjectCreationExpressionSyntax>(
            resize.ArgumentList.Arguments[0].Expression);
        Assert.Equal(4, rect.ArgumentList!.Arguments.Count);
        foreach (var (member, index) in new[]
                 { ("X", 0), ("Y", 1), ("Width", 2), ("Height", 3) })
        {
            Assert.Equal(
                $"{placement.Identifier.ValueText}.{member}",
                rect.ArgumentList.Arguments[index].ToString());
        }
    }

    [Fact]
    public void OnlyTheseTwoWindowsPlaceThemselves()
    {
        // The in-repo precedent (the shader gallery picker) still carries its
        // own inline multiply and is left alone by this batch; naming the two
        // here rather than sweeping every window is what makes adding a THIRD
        // one with a fresh copy of this arithmetic a decision somebody argues
        // for.
        var placing = ShellSource.AllFiles()
            .Where(f => f.Root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Any(i => i.CalleeText() == Compute))
            .Select(f => f.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[] { AboutFile, SettingsFile },
            placing);
    }

    /// <summary>The one constructor of a window file.</summary>
    private static ConstructorDeclarationSyntax Constructor(ShellSource source)
    {
        var found = source.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .ToList();
        Assert.Single(found);
        return found[0];
    }
}
