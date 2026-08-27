using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The Win32 class brush is the fill that lands before XAML composes the
/// first frame, so it is only ever seen at the moment where being wrong is
/// most visible. Five things about it survive a unit test unharmed:
///
/// GDI takes a COLORREF (0x00BBGGRR) where the resolver hands out ARGB
/// (0xAARRGGBB). The constant this replaced was a neutral grey, so the
/// transposition rendered identically; the conversion can be dropped
/// again and nothing looks wrong until a palette colour arrives.
///
/// The memoisation key decides whether a reload reaches GDI at all, and a
/// key that stops covering the colour swallows exactly the reloads the
/// colour was made derivable for.
///
/// And the same is true one level up. ApplyBackdropStyle used to open
/// with a skip guard, and any guard there -- however wide its key --
/// swallows the colour-only reloads before the brush is even reached.
/// The guard is gone: the SystemBackdrop swap gates on its own memo, and
/// the brush's memo is what decides.
///
/// SetClassLongPtr hands back the brush it displaced, and only the ones we
/// allocated may be deleted. A cache that starts changing more often turns
/// a wrong answer there into a GDI leak per reload.
///
/// ShellThemeService has to exist before the brush resolves a colour, and
/// its construction reads as a line that could sit anywhere in a 700 line
/// constructor.
///
/// Reads the source, because the shell assembly cannot be loaded into a
/// test host.
/// </summary>
public sealed class WindowClassBrushWiringTests
{
    private const string ApplyBrush = "ApplyWindowClassBrush";

    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    [Fact]
    public void The_opaque_arm_hands_gdi_a_colorref_not_an_argb()
    {
        var method = MainWindow().Method(ApplyBrush);
        var colour = ColourParameter(method);

        var create = OpaqueArm(method).Call("CreateSolidBrush");
        var conversion = create.ArgExpression(0).AssertCallTo("ToColorRef");

        Assert.Equal(colour, conversion.Arg(0));
    }

    [Fact]
    public void The_cache_is_keyed_on_the_colour_as_well_as_the_kind()
    {
        var method = MainWindow().Method(ApplyBrush);
        var kind = method.ParameterList.Parameters[0].Identifier.ValueText;
        var colour = ColourParameter(method);

        var guard = Assert.Single(
            method.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Statement is ReturnStatementSyntax);

        var condition = Assert.IsType<BinaryExpressionSyntax>(guard.Condition);
        Assert.True(
            condition.IsKind(SyntaxKind.LogicalAndExpression),
            $"the memoisation guard reads '{condition}', which cannot be testing both "
                + "the kind and the colour. A reload that repaints the same kind in a "
                + "new colour never reaches GDI.");

        var named = condition.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .Distinct()
            .ToList();
        Assert.Contains(kind, named);
        Assert.Contains(colour, named);

        // A key that is compared but never written memoises the first call
        // forever, which is the same defect wearing the right shape.
        var written = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .ToList();
        foreach (var field in named.Where(n => n != kind && n != colour))
            Assert.Contains(field, written);
    }

    [Fact]
    public void The_displaced_brush_is_deleted_only_when_we_allocated_it()
    {
        var method = MainWindow().Method(ApplyBrush);
        var delete = method.Call("Win32Interop.DeleteObject");

        var guard = Assert.Single(delete.Ancestors().OfType<IfStatementSyntax>());
        var condition = Assert.IsType<BinaryExpressionSyntax>(guard.Condition);
        Assert.True(condition.IsKind(SyntaxKind.LogicalAndExpression), condition.ToString());
        Assert.Contains(
            new[] { condition.Left, condition.Right },
            operand => operand is IdentifierNameSyntax id
                && id.Identifier.ValueText == "_classBrushOwned");

        var write = Assert.Single(
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_classBrushOwned");
        Assert.True(
            write.SpanStart > delete.SpanStart,
            "_classBrushOwned must be updated after the previous brush is disposed of, "
                + "or it describes the brush being installed rather than the one installed.");
    }

    [Fact]
    public void The_brush_colour_comes_from_the_resolver_and_not_from_a_literal()
    {
        var apply = MainWindow().Method("ApplyBackdropStyle");
        var resolve = apply.Call("RootBackgroundResolver.Resolve");

        Assert.Equal("_currentBackdropStyle", resolve.Arg(0));
        var polarity = resolve.ArgExpression(3).AssertCallTo("OsTheme.IsDark");
        Assert.Equal("_systemUiSettings", polarity.Arg(0));

        var resolved = Assert.Single(
            resolve.Ancestors().OfType<VariableDeclaratorSyntax>()).Identifier.ValueText;

        var calls = apply.Calls(ApplyBrush);
        Assert.NotEmpty(calls);
        foreach (var call in calls)
            Assert.Equal(resolved, call.Arg(1));
    }

    /// <summary>
    /// ApplyBackdropStyle used to open with a skip guard, and a guard
    /// there swallows the colour-only reloads this file exists for: with
    /// background-style = solid the style never moves, so an OS dark/light
    /// flip or a palette reload early-returned before the brush was
    /// reached. Widening the guard's key cannot fix it -- the colour has to
    /// be resolved before it can be tested, and any resolve-then-test
    /// answers "unchanged" for exactly the reloads the colour moves on --
    /// so the guard is gone entirely and the SystemBackdrop swap gates on
    /// its own memo instead.
    ///
    /// The first half mirrors
    /// <see cref="FrameStyleWiringTests.The_chrome_is_not_skipped_by_the_backdrops_own_memoisation"/>
    /// so the two tripwires cannot drift apart. The second half pins where
    /// the skipping actually lives now: the brush's own memo, which keys
    /// on both the kind and the colour, so a colour-only change still
    /// reaches GDI while an unchanged pair no-ops.
    /// </summary>
    [Fact]
    public void The_brush_is_not_skipped_above_and_memoised_on_kind_and_colour_below()
    {
        var apply = MainWindow().Method("ApplyBackdropStyle");

        // No early return anywhere in the method: one above the resolve or
        // the brush calls is the old guard back, whatever its key tests.
        Assert.Empty(apply.DescendantNodes().OfType<ReturnStatementSyntax>());

        var brush = MainWindow().Method(ApplyBrush);
        var kind = brush.ParameterList.Parameters[0].Identifier.ValueText;
        var colour = ColourParameter(brush);

        var memo = Assert.Single(
            brush.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Statement is ReturnStatementSyntax);

        var condition = Assert.IsType<BinaryExpressionSyntax>(memo.Condition);
        Assert.True(
            condition.IsKind(SyntaxKind.LogicalAndExpression),
            $"the memoisation guard reads '{condition}', which cannot be testing both "
                + "the kind and the colour. With no skip guard above, this memo is "
                + "the only thing standing between a reload and GDI.");

        var named = condition.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .Distinct()
            .ToList();
        Assert.Contains(kind, named);
        Assert.Contains(colour, named);
    }

    [Fact]
    public void The_shell_theme_exists_before_the_backdrop_resolves_a_colour()
    {
        var ctor = Assert.Single(
            MainWindow().Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>(),
            c => c.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Any(a => a.Left.ToString() == "_shellTheme"));

        var construction = Assert.Single(
            ctor.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_shellTheme");

        Assert.True(
            construction.SpanStart < ctor.Call("ApplyBackdropStyle").SpanStart,
            "_shellTheme is read while resolving the class brush colour, so constructing "
                + "it further down the constructor throws on the first window.");
    }

    private static string ColourParameter(MethodDeclarationSyntax method)
    {
        Assert.True(
            method.ParameterList.Parameters.Count == 2,
            $"{ApplyBrush} takes {method.ParameterList.Parameters.Count} parameters; "
                + "it is meant to be told both the kind and the colour.");
        return method.ParameterList.Parameters[1].Identifier.ValueText;
    }

    private static SwitchExpressionArmSyntax OpaqueArm(MethodDeclarationSyntax method)
    {
        var arms = method.DescendantNodes().OfType<SwitchExpressionArmSyntax>()
            .Where(a => a.Pattern.ToString()
                .Contains("ClassBrushKind.Opaque", StringComparison.Ordinal))
            .ToList();
        Assert.True(arms.Count == 1, $"expected one Opaque arm, found {arms.Count}");
        return arms[0];
    }
}
