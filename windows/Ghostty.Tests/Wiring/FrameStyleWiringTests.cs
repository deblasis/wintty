using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// background-style is the terminal's material, frame-style is the chrome's.
/// They are resolved a few lines apart in one method, and that is exactly
/// where they get swapped: both are strings, both hold the same three
/// values, and a window whose chrome and terminal have traded materials
/// starts, paints and looks deliberate.
///
/// So these follow the value rather than the spelling. Each field is traced
/// back through the locals it was built from to the config property it
/// actually reads; a guard that only checked ApplyBackdropStyle mentions
/// FrameStyle somewhere passes the swap unchanged.
///
/// The other half is the early return that used to cover both. It tested the
/// backdrop and skipped the chrome with it, so a reload that moved the
/// palette but not the style never reached the class brush -- and, once
/// frame-style existed, never reached the frame's own material either.
///
/// Wiring guards. What the chrome looks like is only observable on a live
/// window.
/// </summary>
public sealed class FrameStyleWiringTests
{
    private static ShellSource Window() => ShellSource.Load("MainWindow.xaml.cs");

    private static MethodDeclarationSyntax Backdrop() => Window().Method("ApplyBackdropStyle");

    private static PropertyDeclarationSyntax Property(ShellSource source, string name) =>
        Assert.Single(
            source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>(),
            p => p.Identifier.ValueText == name);

    /// <summary>
    /// The one assignment to <paramref name="field"/> in the method.
    /// </summary>
    private static AssignmentExpressionSyntax Assignment(
        MethodDeclarationSyntax method, string field) =>
        Assert.Single(
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == field);

    /// <summary>
    /// Every expression <paramref name="root"/> is built from, following
    /// locals declared in the same method one hop at a time.
    ///
    /// Both the reads and the conditions worth asserting on are a local or
    /// two away from where they matter: the backdrop's style comes through
    /// `configStyle` and then `style`, and its memoisation reaches the switch
    /// as a bare `backdropChanged`. Matching on the text at the use site
    /// would see neither.
    /// </summary>
    private static IReadOnlyList<ExpressionSyntax> Behind(
        MethodDeclarationSyntax method, ExpressionSyntax root)
    {
        var reached = new List<ExpressionSyntax>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<ExpressionSyntax>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var expression = pending.Dequeue();
            reached.Add(expression);
            foreach (var identifier in expression.DescendantNodesAndSelf()
                .OfType<IdentifierNameSyntax>())
            {
                var name = identifier.Identifier.ValueText;
                if (!seen.Add(name)) continue;
                var local = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.ValueText == name);
                if (local?.Initializer is { } initializer) pending.Enqueue(initializer.Value);
            }
        }

        return reached;
    }

    /// <summary>
    /// The names of the <c>_configService</c> properties an expression is
    /// built from. Matched on the member access rather than on text, so
    /// <c>_currentFrameStyle</c> is not mistaken for a read of
    /// <c>FrameStyle</c>.
    /// </summary>
    private static IReadOnlyList<string> ConfigReadsBehind(
        MethodDeclarationSyntax method, string field) =>
        Behind(method, Assignment(method, field).Right)
            .SelectMany(e => e.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
            .Where(a => a.Expression.ToString() == "_configService")
            .Select(a => a.Name.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool ReadsAnyOf(
        MethodDeclarationSyntax method, ExpressionSyntax expression, params string[] names) =>
        Behind(method, expression)
            .SelectMany(e => e.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            .Any(n => names.Contains(n.Identifier.ValueText, StringComparer.Ordinal));

    /// <summary>
    /// The whole point of the key. Swap the two config reads and both halves
    /// of this go red, which a "mentions FrameStyle" check does not.
    /// </summary>
    [Fact]
    public void The_backdrop_reads_background_style_and_the_frame_reads_frame_style()
    {
        var method = Backdrop();

        var backdrop = ConfigReadsBehind(method, "_currentBackdropStyle");
        Assert.Contains("BackgroundStyle", backdrop);
        Assert.DoesNotContain("FrameStyle", backdrop);

        var frame = ConfigReadsBehind(method, "_currentFrameStyle");
        Assert.Contains("FrameStyle", frame);
        Assert.DoesNotContain("BackgroundStyle", frame);
    }

    /// <summary>
    /// Low power flattens the chrome for the same reason it flattens the
    /// backdrop: the composition cost is in the translucency, not in which
    /// surface is carrying it. Asserted on the conditional's own arms,
    /// because an inverted one forces translucency on exactly the machines
    /// that asked for less of it.
    /// </summary>
    [Fact]
    public void Low_power_forces_the_frame_solid()
    {
        var method = Backdrop();
        var source = Assert.Single(
            Behind(method, Assignment(method, "_currentFrameStyle").Right)
                .OfType<ConditionalExpressionSyntax>());

        Assert.Equal("lowPowerActive", source.Condition.ToString());
        Assert.Equal("BackdropStyles.Solid", source.WhenTrue.ToString());
        Assert.Equal("_configService.FrameStyle", source.WhenFalse.ToString());
    }

    /// <summary>
    /// The deferred half of this task. ApplyBackdropStyle used to open with
    /// `if (style == _currentBackdropStyle && SystemBackdrop is not null)
    /// return;`, and everything else in the method sat below it -- the class
    /// brush included. The brush colour is palette-derived, so a reload that
    /// repainted the terminal without moving the style left GDI on the colour
    /// the window started with, and the frame's own material never landed at
    /// all.
    ///
    /// Both halves are asserted through <see cref="Behind"/>: moving the
    /// class brush under a bare `if (backdropChanged)` is the same defect
    /// with none of the same words in it.
    /// </summary>
    [Fact]
    public void The_chrome_is_not_skipped_by_the_backdrops_own_memoisation()
    {
        var method = Backdrop();

        Assert.Empty(method.DescendantNodes().OfType<ReturnStatementSyntax>());

        void MustNotBeGatedOnTheBackdropMemo(SyntaxNode node, string what)
        {
            foreach (var gate in node.Ancestors().OfType<IfStatementSyntax>())
            {
                Assert.False(
                    ReadsAnyOf(method, gate.Condition, "_currentBackdropStyle", "SystemBackdrop"),
                    $"{what} sits under `if ({gate.Condition})`, which tests whether the "
                        + "backdrop material moved. The chrome colour moves without it.");
            }
        }

        var brushes = method.Calls("ApplyWindowClassBrush");
        Assert.NotEmpty(brushes);
        foreach (var brush in brushes)
            MustNotBeGatedOnTheBackdropMemo(brush, "the class brush");

        MustNotBeGatedOnTheBackdropMemo(
            Assignment(method, "_currentFrameStyle"), "the frame's material");
    }

    /// <summary>
    /// The other side of the split: the SystemBackdrop swap is still skipped
    /// when the material has not moved. Rebuilding an AcrylicBackdrop on
    /// every reload is what the early return was there for.
    /// </summary>
    [Fact]
    public void The_backdrop_swap_is_still_memoised()
    {
        var method = Backdrop();
        var swaps = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "SystemBackdrop")
            .ToList();

        Assert.NotEmpty(swaps);
        foreach (var swap in swaps)
        {
            Assert.Contains(
                swap.Ancestors().OfType<IfStatementSyntax>(),
                gate => ReadsAnyOf(method, gate.Condition, "_currentBackdropStyle"));
        }
    }

    /// <summary>
    /// The chrome's fill is resolved from the frame's material, from the
    /// palette, and from the desktop the window is actually on.
    ///
    /// The shell-theme argument used to be pinned false, on the reasoning
    /// that window-theme owns the hue and frame-style owns the material. The
    /// reasoning is right and the pin was not: it made the resolver answer
    /// "solid" for every frame the palette was driving, so frosted, crystal
    /// and solid produced one identical opaque window under
    /// window-theme=wintty -- the one combination the key exists to create.
    /// Asserted on the argument being the live read, since a literal `true`
    /// resolves the other three rows wrong just as quietly.
    /// </summary>
    [Fact]
    public void The_chrome_fill_comes_from_the_frames_material_and_the_palette()
    {
        var resolve = Property(Window(), "ChromeFillArgb")
            .Call("RootBackgroundResolver.Resolve");

        Assert.Equal("EffectiveFrameStyle", resolve.Arg(0));
        Assert.Equal("_shellTheme.IsEnabled", resolve.Arg(1));

        // The tab bar's shade, not the title bar's. Both are palette-derived
        // and only one of them is what the row, the caption lane and the
        // strips have always been painted; RootGrid keeps the other.
        Assert.Equal("ShellThemeChromeArgb", resolve.Arg(2));

        // Node, not text: `!OsTheme.IsDark(...)` reads the same to a
        // substring match and hands the light desktop the dark colour.
        var polarity = resolve.ArgExpression(3).AssertCallTo("OsTheme.IsDark");
        Assert.Equal("_systemUiSettings", polarity.Arg(0));
    }

    /// <summary>
    /// Two places pin the frame solid, and both live in this one expression
    /// rather than being re-tested at each painter.
    ///
    /// High Contrast is the first: translucency over a backdrop nobody
    /// controls is what that mode exists to remove. Asserted on the arms,
    /// because swapping them makes High Contrast the only mode that goes
    /// translucent and leaves every other mode unable to.
    ///
    /// A solid background is the second, and it is the fold below. Dropping
    /// it renders a window with no chrome in it: there is one backdrop per
    /// window, so a frame resolved transparent over a solid one exposes
    /// RootGrid, which under window-theme=wintty is the terminal's own
    /// colour. Asserted through the call rather than on the property's text,
    /// because the two arguments are both style strings and the fold accepts
    /// them either way round.
    /// </summary>
    [Fact]
    public void The_frame_is_pinned_solid_where_translucency_has_nothing_behind_it()
    {
        var body = Property(Window(), "EffectiveFrameStyle").ExpressionBody?.Expression;
        var choice = Assert.IsType<ConditionalExpressionSyntax>(body);

        Assert.Equal("HighContrastChromeActive", choice.Condition.ToString());
        Assert.Equal("BackdropStyles.Solid", choice.WhenTrue.ToString());

        var fold = choice.WhenFalse.AssertCallTo("BackdropStyles.FrameOver");
        Assert.Equal("_currentFrameStyle", fold.Arg(0));
        Assert.Equal("_currentBackdropStyle", fold.Arg(1));
    }

    /// <summary>
    /// "The palette is painting the chrome" is both keys agreeing, and every
    /// painter branches on it. Dropping either half is a whole table row:
    /// without the frame test the palette paints a frosted window opaque
    /// again, and without the window-theme test a solid frame claims a
    /// palette that was never asked for.
    ///
    /// The frame half is asserted as a `!=` against transparent rather than
    /// on the words, because the inverted test compiles and reads fine.
    /// </summary>
    [Fact]
    public void The_palette_paints_only_while_the_frame_is_solid_enough()
    {
        var body = Property(Window(), "ChromePaintedFromPalette").ExpressionBody?.Expression;
        var conjunction = Assert.IsType<BinaryExpressionSyntax>(body);
        Assert.True(
            conjunction.IsKind(SyntaxKind.LogicalAndExpression),
            $"the test reads `{conjunction}`; it has to require both keys, not either.");

        var operands = new[] { conjunction.Left, conjunction.Right };
        Assert.Contains(operands, o => o.ToString() == "_shellTheme.IsEnabled");

        var frame = Assert.Single(operands.OfType<BinaryExpressionSyntax>());
        Assert.True(
            frame.IsKind(SyntaxKind.NotEqualsExpression),
            $"the frame test reads `{frame}`; an equality there paints the palette "
                + "onto exactly the frames that asked for the backdrop.");
        Assert.Equal("ChromeFillArgb", frame.Left.ToString());
        Assert.Equal("RootBackgroundResolver.TransparentArgb", frame.Right.ToString());
    }

    /// <summary>
    /// frame-style can cover the backdrop, never replace it. A solid frame is
    /// its own ground; anything else leaves whatever background-style put
    /// there showing, so the ink is scored against the backdrop's estimate.
    /// Scoring a bare crystal row against an acrylic estimate is what reading
    /// one style for both would do the moment the two keys disagree.
    /// </summary>
    [Fact]
    public void The_ground_under_the_chrome_falls_back_to_the_backdrop()
    {
        var body = Property(Window(), "ChromeGroundStyle").ExpressionBody?.Expression;
        var choice = Assert.IsType<ConditionalExpressionSyntax>(body);

        var test = Assert.IsType<BinaryExpressionSyntax>(choice.Condition);
        Assert.True(
            test.IsKind(SyntaxKind.EqualsExpression),
            $"the ground reads `{test}`; a negated test hands the solid frame the "
                + "backdrop's estimate and the bare frame its own fill.");
        Assert.Equal("_currentFrameStyle", test.Left.ToString());
        Assert.Equal("BackdropStyles.Solid", test.Right.ToString());

        Assert.Equal("BackdropStyles.Solid", choice.WhenTrue.ToString());
        Assert.Equal("_currentBackdropStyle", choice.WhenFalse.ToString());
    }

    /// <summary>
    /// High Contrast ignores frame-style entirely: the row is painted from
    /// Windows' own colours whatever the config says. Asserted on the
    /// conditional's arms in both painters, because the failure worth
    /// catching is the two being swapped -- which makes High Contrast the one
    /// mode that goes translucent.
    /// </summary>
    [Theory]
    [InlineData("ApplyVerticalTitleBarChrome")]
    [InlineData("ApplyCaptionButtonChrome")]
    public void High_contrast_still_wins_over_the_frames_material(string painter)
    {
        var choice = Assert.Single(
            Window().Method(painter).DescendantNodes().OfType<ConditionalExpressionSyntax>(),
            c => c.Condition.ToString() == "HighContrastChromeActive");

        Assert.Contains("UnpackTerminalColor", choice.WhenTrue.ToString());
        Assert.Contains("ChromeFillArgb", choice.WhenFalse.ToString());
    }

    /// <summary>
    /// Both strips take the same fill from the same expression. Two pushes
    /// resolved separately is how the horizontal strip ends up a different
    /// material from the vertical one for the same config.
    /// </summary>
    [Fact]
    public void Both_strips_are_pushed_the_same_fill()
    {
        var method = Window().Method("ApplyStripChromeFill");
        var pushes = method.Calls("_verticalTabHost.SetChromeFill")
            .Concat(method.Calls("_horizontalTabHost.SetChromeFill"))
            .ToList();

        Assert.Equal(2, pushes.Count);
        var argument = Assert.Single(pushes.Select(p => p.Arg(0)).Distinct(StringComparer.Ordinal));
        Assert.True(
            ReadsAnyOf(method, pushes[0].ArgExpression(0), "ChromeStripFill"),
            $"the strips are pushed `{argument}`, which is not the frame's fill.");

        // One path yields null, and it is High Contrast without the palette:
        // that surface comes from an HC-overridable theme resource the window
        // cannot name. window-theme=wintty must NOT yield null -- it names a
        // shade, and dropping it here is what left the strips opaque under
        // every frame-style. Asserted on the operands rather than on the
        // words: an `||` between the same two names is the old behaviour back.
        var choice = Assert.Single(
            Behind(method, pushes[0].ArgExpression(0)).OfType<ConditionalExpressionSyntax>());
        Assert.True(
            choice.WhenTrue.IsKind(SyntaxKind.NullLiteralExpression),
            $"the opted-out path must yield `null`, not `{choice.WhenTrue}`.");

        var gate = Assert.IsType<BinaryExpressionSyntax>(choice.Condition);
        Assert.True(
            gate.IsKind(SyntaxKind.LogicalAndExpression),
            $"the strips opt out on `{gate}`; either-of makes window-theme opt out "
                + "again, which is the bug this key could not be seen through.");

        var operands = new[] { gate.Left, gate.Right };
        Assert.Contains(operands, o => o.ToString() == "HighContrastChromeActive");
        var palette = Assert.Single(operands.OfType<PrefixUnaryExpressionSyntax>());
        Assert.True(
            palette.IsKind(SyntaxKind.LogicalNotExpression),
            $"the window-theme half reads `{palette}`; unnegated it opts the palette out.");
        Assert.Equal("_shellTheme.IsEnabled", palette.Operand.ToString());
    }

    /// <summary>
    /// And the strips have to accept it. Both used to refuse a fill while the
    /// palette was on -- an early return on one side, a one-armed `if` on the
    /// other -- which is where a correctly resolved bare fill went to die.
    ///
    /// Asserted on the shapes that did the refusing, because either one comes
    /// back as a plausible-looking guard against the palette being overwritten.
    /// </summary>
    [Fact]
    public void The_strips_accept_the_fill_while_the_palette_is_on()
    {
        var host = ShellSource.Load("Tabs.TabHost.xaml.cs").Method("SetChromeFill");
        foreach (var bail in host.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            var gate = Assert.IsType<IfStatementSyntax>(bail.Parent);
            Assert.DoesNotContain("_shellActiveTextBrush", gate.Condition.ToString());
        }

        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs").Method("SetChromeFill");
        var branch = Assert.Single(
            strip.DescendantNodes().OfType<IfStatementSyntax>(),
            i => i.Condition.ToString().Contains("_shellThemeActive"));
        Assert.NotNull(branch.Else);
    }

    /// <summary>
    /// The selected tab stays opaque in every combination: the seam cover
    /// takes its fill from it, and a translucent one reopens the folder join
    /// the tab chrome exists to make. So the frame's fill reaches the strip's
    /// own surface and nothing else -- asserted on what the fill is allowed
    /// to be written into, since routing it into the selected row is a change
    /// that compiles and looks like a tidy-up.
    /// </summary>
    [Fact]
    public void The_strips_fill_never_reaches_the_selected_row()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");

        var written = strip.Method("SetChromeFill")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Select(a => a.Left.ToString())
            .ToList();
        Assert.Equal(new[] { "_chromeFillRgb" }, written);

        var surface = strip.Method("ApplyDefaultPaneChrome");
        var reads = surface.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Where(i => i.Identifier.ValueText == "_chromeFillRgb")
            .ToList();
        Assert.NotEmpty(reads);
        foreach (var read in reads)
        {
            Assert.Contains(
                read.Ancestors().OfType<AssignmentExpressionSyntax>(),
                a => a.Left.ToString() == "Background");
        }

        // Same rule on the horizontal side, where the selected header has its
        // own resource sitting next to the strip's.
        var host = ShellSource.Load("Tabs.TabHost.xaml.cs");
        var keys = host.Method("SetChromeFill")
            .DescendantNodes().OfType<ElementAccessExpressionSyntax>()
            .Select(e => e.ToString())
            .ToList();
        Assert.All(keys, key => Assert.Contains("\"TabViewBackground\"", key));
    }
}
