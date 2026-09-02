using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The switcher's group field and its selection.
///
/// What the plan decides is executable and is tested outright in
/// TabSwitcherFieldTests. What cannot be: whether the popup PAINTS the
/// plan, whether the wash is composited against the ground instead of
/// handed to XAML translucent, and whether the window supplies the ground,
/// the motion gate and High Contrast at all. The shell does not load into
/// this test host, so those are parsed here.
///
/// These are wiring guards, and the whole file is written against ONE
/// failure mode: a guard that is satisfied by the mutation it exists to
/// catch. `Assert.Contains("cell.IsHead", body)` was satisfied by the seam
/// report's `cell.IsHead` argument fifty lines below the condition it meant
/// to pin, so the head test could be deleted outright and this file stayed
/// green. Every assertion here names a NODE -- a condition, an argument
/// position, a call count -- rather than a substring of a method body.
///
/// That a field is legible on screen is a pixel question, and
/// windows/scripts/switcher-groups.ps1 is the one that asks it.
/// </summary>
public sealed class TabSwitcherFieldWiringTests
{
    private const string PopupSource = "Tabs.TabSwitcherPopup.xaml.cs";
    private const string MainWindowSource = "MainWindow.xaml.cs";

    [Fact]
    public void The_field_is_painted_from_the_plan_and_carries_both_of_its_ends()
    {
        var popup = ShellSource.Load(PopupSource);
        var slot = popup.Method("BuildSlot");

        // The wash. Composited to an opaque value against the ground the
        // window reports, NOT handed to XAML as a translucent brush: the
        // card floats over the window's backdrop, and Mica dilutes a
        // translucent tint by an amount that changes with the wallpaper.
        // FromPackedRgb is the opaque door -- TabColorBrush.From takes a
        // colour with alpha, and reaching for it here is exactly the
        // regression this asserts against.
        var wash = slot.Call("TabColorPalette.FieldBackgroundRgb");
        Assert.Equal("group.Color", wash.Arg(0));
        Assert.Equal("groundRgb", wash.Arg(1));
        // ...and it is the composite the field is PAINTED with, not a value
        // computed beside a translucent brush that does the painting.
        Assert.Equal("fieldGroundRgb",
            wash.Ancestors().OfType<AssignmentExpressionSyntax>().First().Left.ToString());
        var fieldBorder = slot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .First(o => o.Type.ToString() == "Border");
        Assert.Equal("TabColorBrush.FromPackedRgb(fieldGroundRgb)",
            ((ConditionalExpressionSyntax)InitializerFor(fieldBorder, "Background")).WhenFalse.ToString());

        // The head carries the header, and only the head. Asserted as the
        // ternary's CONDITION: the previous form looked for "cell.IsHead"
        // anywhere in BuildSlot, and the seam report at the bottom of the
        // method passes `cell.IsHead` as an argument -- so dropping the head
        // test from this condition, which puts a header on every member of a
        // run, left the guard green.
        var header = slot.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Single(c => c.WhenTrue.ToString().Contains("BuildFieldHeader", StringComparison.Ordinal));
        header.Condition.AssertCallTo("PaintsHeader");
        Assert.True(slot.Calls("BuildFieldHeader").Count == 1,
            "the header is built in one place, under the head test.");

        // ...and the head test itself, where it now lives. A set, not a
        // substring: each of the three answers a different way of getting a
        // header wrong -- on every member of a run, on a chip that already
        // names its group, and on an ungrouped tile.
        Assert.Equal(
            new[] { "cell.Group is not null", "cell.IsHead", "cell.Tab is not null" },
            Conjuncts(popup.Method("PaintsHeader").ExpressionBody!.Expression)
                .Select(o => o.ToString()).OrderBy(s => s, StringComparer.Ordinal).ToArray());

        // The tail carries the end bar. Asserted as the enclosing IF's
        // condition, for the same reason the head is: `cell.IsTail` appears
        // in the seam report too.
        var bar = slot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "Rectangle");
        var barIf = bar.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Contains("cell.IsTail", Conjuncts(barIf.Condition).Select(o => o.ToString()));
        Assert.Contains("TabSwitcherShape.EndBarWidthPx", bar.ToString());

        // The bar's colour goes through the strips' visibility rule, scored
        // against the surface it is actually painted ON -- which is the
        // field's own wash of this very preset. The raw preset put a Yellow
        // bar on a Yellow field at 1.28:1 on the light theme: the one mark
        // whose job is to be findable was the one that could not be found.
        // TabColorPalette.Border IS that raw preset, so its absence is the
        // assertion.
        var barInk = bar.Call("TabGroupField.TerminalRgbOn");
        Assert.Equal("fieldGroundRgb", barInk.Arg(0));
        Assert.Equal("tailGroup.Color", barInk.Arg(1));
        Assert.DoesNotContain("TabColorPalette.Border", bar.ToString());

        // The rounding is the field's outer corners alone, which is what
        // makes a run read as one band rather than a row of tinted boxes.
        // Positional, because the polarity is the whole content: swapping
        // head and tail rounds a run's inner corners and runs its outer ones
        // square, and every literal stays where it was.
        var corners = popup.Method("FieldCorners")
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "CornerRadius");
        Assert.Equal(
            new[] { "cell.IsHead ? R : 0", "cell.IsTail ? R : 0",
                    "cell.IsTail ? R : 0", "cell.IsHead ? R : 0" },
            corners.ArgumentList!.Arguments.Select(a => a.ToString()).ToArray());
    }

    [Fact]
    public void High_contrast_trades_the_wash_for_an_outline_and_keeps_the_dim_off_the_text()
    {
        var popup = ShellSource.Load(PopupSource);

        // Two questions, two parameters. The motion gate composes
        // animations-off WITH High Contrast, so it answers "may this
        // spring" -- and the popup was reading it for "what may this be
        // made of", which is how a 30% wash ended up under the tab titles
        // of every idle tile in a mode whose whole contract forbids it. The
        // join ring passes the two separately one gesture along.
        var show = popup.Method("Show");
        Assert.Contains("motionOn", show.ParameterList.Parameters.Select(p => p.Identifier.ValueText));
        Assert.Contains("highContrast", show.ParameterList.Parameters.Select(p => p.Identifier.ValueText));

        // A build input like the ground and the font: the mode changes what
        // the card is MADE of, and it can be switched on between two
        // presses of the same cycle.
        Assert.Contains("_builtHighContrast",
            popup.Method("ShouldRebuild").Body!.ToString());

        // The field is a fill or an edge, never both and never neither.
        var slot = popup.Method("BuildSlot");
        var field = slot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .First(o => o.Type.ToString() == "Border");
        var background = InitializerFor(field, "Background");
        var borderBrush = InitializerFor(field, "BorderBrush");
        Assert.True(background is ConditionalExpressionSyntax { Condition.RawKind: (int)SyntaxKind.IdentifierName }
                    && background.ToString().StartsWith("highContrast", StringComparison.Ordinal),
            "the field's fill must be chosen BY the High Contrast flag, not despite it");
        Assert.Equal("null", ((ConditionalExpressionSyntax)background).WhenTrue.ToString());
        Assert.StartsWith("highContrast", borderBrush.ToString(), StringComparison.Ordinal);

        // The dim is NEVER on the card. An opacity there composites the tab
        // title with everything else, and 70% of a caption over a light card
        // measured 4.01:1 against WCAG AA's 4.5 -- a surface the contrast
        // oracle had passing before this treatment existed. It goes on the
        // preview, which is most of a tile's area and carries no text.
        Assert.Empty(popup.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() is "card.Opacity" or "parts.Card.Opacity"));
        var dimWrite = popup.Method("SetSelected")
            .DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "dim.Opacity");
        Assert.Equal("selected ? 1 : TabSwitcherShape.IdleTileOpacity", dimWrite.Right.ToString());

        // ...and in High Contrast there is no dim at all: the ring is the
        // whole answer, as it is for every other affordance the mode
        // flattens. The card is built with a null dim target rather than
        // with a gate at every write site.
        var parts = slot.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "CardParts");
        Assert.Equal("highContrast ? null : preview", parts.ArgumentList!.Arguments[1].ToString());
        // The lift goes too -- a spring in a mode that has animations off.
        Assert.Contains("_builtHighContrast", popup.Method("SetSelected").Body!.ToString());

        // Every held-back count in the card, and there are two -- the field
        // header's and the chip's. A sweep with a pinned count, because the
        // failure this guards is one of them being missed, and an unpinned
        // sweep passes when a call site disappears.
        var dimmedInk = popup.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "Opacity"
                        && a.Right.ToString().Contains("SecondaryInkOpacity", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, dimmedInk.Count);
        Assert.All(dimmedInk, a =>
            Assert.StartsWith("highContrast", a.Right.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void The_header_band_is_reserved_for_every_slot_of_a_card_that_paints_one()
    {
        var popup = ShellSource.Load(PopupSource);
        var build = popup.Method("Build");

        // One flag, decided once over the whole plan and passed to every
        // slot: a run's tiles must sit on one baseline, and so must the
        // ungrouped tiles beside them. Deciding it per slot is how half a
        // card ends up a header taller than the other half.
        //
        // The loop's CONDITION, not the identifier's presence. `headerBand`
        // is declared, passed and read all over Build and BuildSlot, so the
        // substring form survived replacing the whole loop with
        // `var headerBand = false;` -- no card anywhere would show a field
        // header and the guard stayed green.
        var scan = build.DescendantNodes().OfType<ForEachStatementSyntax>()
            .Single(f => f.Statement.ToString().Contains("headerBand = true", StringComparison.Ordinal));
        scan.DescendantNodes().OfType<IfStatementSyntax>().Single()
            .Condition.AssertCallTo("PaintsHeader");

        // The same question the header itself asks. Asking the looser "does
        // any cell have a group" reserved the band across a card whose only
        // field is a CHIP -- which paints no header, so nothing would ever
        // land in it.
        Assert.Contains(
            build.Calls("BuildSlot"),
            c => c.ArgumentList.Arguments.Any(a => a.ToString() == "headerBand"));

        var slot = popup.Method("BuildSlot");
        Assert.Contains("TabSwitcherShape.HeaderHeightPx", slot.Body!.ToString());
    }

    [Fact]
    public void The_selection_moves_the_ring_the_dim_and_the_lift_together()
    {
        var popup = ShellSource.Load(PopupSource);
        var highlight = popup.Method("Highlight");
        var body = highlight.Body!.ToString();

        // The ring alone is what the switcher used to have, and it is not
        // an answer to "which one am I on" when several tiles already carry
        // preset colours. All three cues live in this one method so they
        // cannot drift apart.
        Assert.Contains("_theme.BorderActive", body);
        Assert.Contains("SetSelected", body);

        // TWO animate calls, counted. Unpinned, the guard passed with the
        // outgoing card's animation deleted -- which leaves the tile that
        // just lost the selection lit beside the one that took it.
        Assert.Equal(2, highlight.Calls("Animate").Count);

        // The move is LANDED before the next one begins. A Storyboard that
        // is stopped puts every property it animated back to that property's
        // BASE value, and Animate writes no base, so a stop un-did the move
        // before it: the third press of a cycle re-lit the tile the first
        // press had dimmed, and the card drew two selections.
        //
        // The assertion is "before Begin", not "before Stop". Landing after
        // the stop writes back exactly what the revert undid, so that
        // ordering is equally correct -- a run of switcher-groups.ps1
        // against it is clean, which is how this guard was found asserting
        // something the code does not depend on. Landing after BEGIN is the
        // real failure: the destination becomes the value a To-only track
        // animates FROM, and every move turns into a cut.
        var land = highlight.Calls("LandHighlight").Single();
        var begin = highlight.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Single(c => c.CalleeText() == "_highlightMove.Begin");
        Assert.True(land.SpanStart < begin.SpanStart,
            "LandHighlight must run before the next move begins, or it lands on top of it");

        // ...and what it lands is what Animate recorded, so the two cannot
        // drift. Animate writes no local of its own: writing the
        // destination before Begin would make it the value a To-only track
        // animates FROM, and the move would be a cut.
        Assert.Contains("_highlightLanding.Add", popup.Method("Animate").Body!.ToString());
        Assert.Contains("SetSelected", popup.Method("LandHighlight").Body!.ToString());

        // Polarity, node-level. Swapping the two arms of either conditional
        // dims the active tile and lights every idle one, and leaves every
        // literal this file used to look for exactly where it was.
        AssertSelectedPolarity(popup.Method("SetSelected"), "card.Opacity", "1");
        AssertSelectedPolarity(popup.Method("Animate"), null, "1");

        // Motion off is a cut applied in this same pass, not a zero-length
        // storyboard left to a dispatcher tick: the strips' rule.
        Assert.Contains("TabSwitcherShape.HighlightDuration", body);
        Assert.Contains("TimeSpan.Zero", body);

        // Only the two cards that change are animated, and _activeCard is
        // what says which they are. Asserted as the two guard CONDITIONS:
        // the identifier alone appears in `_activeCard = target;` at the
        // bottom of the method, so the substring form survived deleting
        // both guards.
        var guards = highlight.Calls("Animate")
            .Select(c => c.Ancestors().OfType<IfStatementSyntax>().First().Condition.ToString())
            .ToList();
        Assert.All(guards, g => Assert.Contains("_activeCard", g));
    }

    [Fact]
    public void A_repeat_press_reuses_the_card_so_the_highlight_glides()
    {
        var popup = ShellSource.Load(PopupSource);
        var show = popup.Method("Show");
        var body = show.Body!.ToString();

        // The rebuild is conditional, and the else-path is a Highlight with
        // motion ON -- that IS the glide. A Show that rebuilt every press
        // would tear the card down under the eye on every Ctrl+Tab, which
        // is the jank this exists to remove.
        Assert.True(show.Calls("ShouldRebuild").Count == 1,
            "Show must ask once whether the card on screen still stands.");
        Assert.True(show.Calls("Build").Count == 1, "one build call, under the rebuild test.");
        Assert.Equal(2, show.Calls("Highlight").Count);
        Assert.Contains(show.Calls("Highlight"),
            c => c.ArgumentList.ToString().Contains("motionOn: false", StringComparison.Ordinal));
        Assert.Contains(show.Calls("Highlight"),
            c => c.ArgumentList.Arguments.Count == 2
                 && c.ArgumentList.Arguments[1].ToString() == "motionOn");

        // The grid's size and the entrance are OUTSIDE the rebuild. Neither
        // is a function of the card's contents, which is all ShouldRebuild
        // compares: the column count and the scroll cap follow the WINDOW,
        // which can be resized between two opens of an unchanged tab set,
        // and the entrance follows whether the popup was down a moment ago,
        // which is what `fresh` carries and no key can. Inside, a card built
        // while the window was maximised kept its wide column count after a
        // restore and hung off both edges of the shrunken window, and a
        // second open arrived with no entrance at all. The predecessor could
        // have neither bug because it rebuilt on every press.
        var rebuild = show.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("ShouldRebuild", StringComparison.Ordinal));
        foreach (var name in new[] { "SizeGrid", "RunEnter" })
        {
            var call = show.Calls(name).Single();
            Assert.False(rebuild.Span.Contains(call.Span),
                $"{name} must not sit inside the rebuild branch: it is not a function of the card's contents");
        }
        // ...and the sizing runs before the highlight, which ends by
        // bringing the selection into view. A scroll reset behind it takes
        // the tile straight back off screen.
        Assert.True(show.Calls("SizeGrid").Single().SpanStart < rebuild.SpanStart,
            "SizeGrid must run before the highlight, which scrolls the selection into view");

        // Everything a slot is painted from has to be in the comparison, or
        // a stale card freezes on screen. Naming them here is what makes a
        // forgotten input a red test rather than a rendering bug.
        var should = popup.Method("ShouldRebuild").Body!.ToString();
        foreach (var input in new[]
                 { "_cards.Count", "_theme", "_builtFontFamily", "_builtGroundRgb", "_builtHighContrast" })
            Assert.Contains(input, should);
        // The element-wise comparison, not just the count. `_keys` as a
        // substring was satisfied by the count check alone, so deleting the
        // loop left every count-preserving change -- a retitle, a recolour,
        // a head/tail move -- painting a stale card.
        Assert.Contains(
            should.Contains("keys[i].Equals(_keys[i])", StringComparison.Ordinal),
            new[] { true });

        // The key's fields, POSITIONALLY. Every one of these is satisfied as
        // a substring by a DIFFERENT field of the same record: "Title" by
        // EffectiveTitle, "Color" by the tab's tint, "cell.Tab" by
        // cell.Tab?.EffectiveTitle. Dropping the group's title froze a
        // renamed group's header; dropping cell.Tab made every same-titled
        // tab compare equal, so closing one and opening another lit no tile
        // at all and left a dead tab's preview on screen.
        var key = popup.Method("KeysFor")
            .DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "CellKey");
        Assert.Equal(
            new[]
            {
                "cell.Tab",
                "cell.Group",
                "cell.IsHead",
                "cell.IsTail",
                "cell.Tab?.EffectiveTitle ?? string.Empty",
                "cell.Group?.Title ?? string.Empty",
                "cell.Tab?.Color ?? TabColor.None",
                "cell.Group?.Color ?? TabColor.None",
                "cell.Group is { } group ? manager.MembersOf(group).Count : 0",
            },
            key.ArgumentList!.Arguments.Select(a => a.ToString()).ToArray());

        // The entrance runs on a FRESH open only.
        Assert.Contains("fresh", body);
    }

    [Fact]
    public void A_dismissed_popup_lets_go_of_the_card_it_was_showing()
    {
        var popup = ShellSource.Load(PopupSource);
        var dismissed = popup.Method("Dismissed").Body!.ToString();

        // Held to the next press, a dismissed card keeps every TabModel it
        // drew -- including tabs closed since -- and every pane preview's
        // visual tree alive for as long as the window lives. Both clocks go
        // too: a Storyboard left running on an unloaded element is the leak
        // VerticalTabStrip.StopAllFieldMotion exists to close.
        foreach (var released in new[]
                 { "_highlightMove.Stop()", "_enter.Stop()", "CandidateRow.Children.Clear()",
                   "_cellByTab.Clear()", "_idleBorderByTab.Clear()", "_cards.Clear()",
                   "_slots.Clear()", "_activeCard = null" })
            Assert.Contains(released, dismissed);

        // On the popup's Closed, not beside the timer's own IsOpen = false:
        // the timer is one way it goes down and not the only one.
        var wire = ShellSource.Load(MainWindowSource).Method("WireTabSwitcher").Body!.ToString();
        Assert.Contains("TabSwitcherPopupHost.Closed", wire);
        Assert.Contains("TabSwitcherPopupUI.Dismissed()", wire);
    }

    [Fact]
    public void The_window_supplies_the_motion_gate_the_contrast_mode_and_the_freshness()
    {
        var cycle = ShellSource.Load(MainWindowSource).Method("CycleTab");
        var show = cycle.Call("TabSwitcherPopupUI.Show");

        // The strips' gate, asked at the press rather than cached:
        // UISettings can throw in packaged contexts and the answer can
        // change under the user mid-session. Spelled as a call so an edit
        // to a cached bool fails here.
        var gate = show.ArgExpression(3).AssertCallTo("TabStripMotion.Enabled");
        Assert.Equal("SystemAnimationsEnabled()", gate.Arg(0));
        Assert.Equal("HighContrastChromeActive", gate.Arg(1));

        // High Contrast BESIDE the gate, not only inside it. Inside, it is
        // composed with animations-off and answers only "may this spring";
        // the popup also needs "what may this be made of", and reading the
        // composed answer for both is what put a translucent wash under
        // every idle tile's title in the one mode that forbids it.
        Assert.Equal("HighContrastChromeActive", show.Arg(4));

        // Only the window can say whether this press opened the popup or
        // landed on one already up, and the polarity is load-bearing: the
        // negation is what makes a repeat press skip the entrance.
        Assert.Equal("fresh: !TabSwitcherPopupHost.IsOpen", show.Arg(5));

        // The ground is NOT the window's to supply. Its backdrop estimate
        // answers for the chrome, which follows the OS theme, while the
        // popup's card follows the app's -- a dark-themed app on a light
        // desktop is where the two diverge, and a pale field on a dark card
        // is what handing the window's answer to the popup looked like.
        Assert.DoesNotContain("EstimatedBackdropGround", show.ArgumentList.ToString());
    }

    [Fact]
    public void The_wash_is_composited_against_the_cards_own_fill()
    {
        var ground = ShellSource.Load(PopupSource).Root
            .DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "CardGroundRgb")
            .ToString();

        // Read off the card, not assumed: an acrylic brush carries the
        // opaque colour it tints toward, a solid one carries its own, and
        // only a card painted with neither falls back to a constant.
        Assert.Contains("Card.Background", ground);
        Assert.Contains("AcrylicBrush", ground);
        Assert.Contains("SolidColorBrush", ground);

        // The fallback follows the theme the CARD renders in. ActualTheme,
        // not the OS read and not the window's estimate: those answer for a
        // different surface.
        Assert.Contains("ActualTheme", ground);
        Assert.DoesNotContain("OsTheme", ground);
    }

    [Fact]
    public void The_seam_reports_the_cells_it_reports_because_UIA_cannot_see_them()
    {
        var seam = ShellSource.Load("Testing.TestSeam.cs");
        var section = seam.Case("ExecuteOnUiThreadAsync", "\"switcher-cells\"");

        // A closed popup is a REFUSAL, not an empty card: a driver that read
        // zero cells off a dismissed popup would call every group assertion
        // vacuously true. The if and its Error are the assertion -- a bare
        // read of the property satisfies a substring check and refuses
        // nothing.
        var refusal = section.DescendantNodes().OfType<IfStatementSyntax>()
            .Single(i => i.Condition.ToString().Contains("TestSeamSwitcherOpen", StringComparison.Ordinal));
        Assert.True(refusal.Condition.IsKind(SyntaxKind.LogicalNotExpression),
            "the refusal fires when the popup is NOT up");
        Assert.Contains("Error(", refusal.Statement.ToString());
        Assert.Contains("TestSeamWriteSwitcherCells", section.ToString());

        var write = ShellSource.Load(PopupSource).Method("TestSeamWriteCells").Body!.ToString();
        // The active flag is read off the SAME field the highlight writes,
        // so "exactly one cell is active" is an assertion about the
        // selection the popup actually applied rather than about a second
        // opinion the seam formed on its own.
        Assert.Contains("_activeCard", write);
        Assert.DoesNotContain("ActiveTab", write);
        // The three rects a pixel oracle needs, none of which UIA can find.
        foreach (var name in new[] { "\"card\"", "\"header\"", "\"preview\"" })
            Assert.Contains(name, write);
        foreach (var name in new[] { "\"group\"", "\"head\"", "\"tail\"", "\"active\"" })
            Assert.Contains(name, write);
    }

    /// <summary>
    /// The value an object initializer gives <paramref name="name"/>.
    /// </summary>
    private static ExpressionSyntax InitializerFor(ObjectCreationExpressionSyntax creation, string name)
        => creation.Initializer!.Expressions.OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == name).Right;

    /// <summary>
    /// Every `selected ? x : y` in <paramref name="method"/> puts the ACTIVE
    /// value in the true arm.
    ///
    /// The polarity is the whole content of these conditionals, and it is
    /// invisible to any assertion made of the constants they name: swapping
    /// the arms dims the active tile and lights every idle one while leaving
    /// IdleTileOpacity and ActiveTileScale exactly where they were. The
    /// harness's `moves` leg cannot see it either -- it asserts the two tiles
    /// diverge, not which way.
    /// </summary>
    private static void AssertSelectedPolarity(
        MethodDeclarationSyntax method, string? opacityTarget, string activeOpacity)
    {
        var conditionals = method.DescendantNodes().OfType<ConditionalExpressionSyntax>()
            .Where(c => c.Condition.ToString().Contains("selected", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(conditionals);
        foreach (var c in conditionals)
        {
            var lit = c.WhenTrue.ToString();
            var idle = c.WhenFalse.ToString();
            Assert.True(
                lit == activeOpacity || lit == "TabSwitcherShape.ActiveTileScale",
                $"the SELECTED arm must carry the lit value, not '{lit}'");
            Assert.True(
                idle == "TabSwitcherShape.IdleTileOpacity" || idle == "1",
                $"the unselected arm must carry the idle value, not '{idle}'");
        }
        if (opacityTarget is not null)
            Assert.Contains("TabSwitcherShape.IdleTileOpacity", method.Body!.ToString());
    }

    /// <summary>
    /// The operands of an &amp;&amp; chain, flattened.
    ///
    /// Only &amp;&amp; is walked through. Every other expression is a leaf --
    /// recursing into any BinaryExpressionSyntax would split
    /// `cell.Tab is not null` and report operands nobody wrote, which is
    /// what the first draft of this helper's twin in
    /// ActiveTabFieldWiringTests did.
    /// </summary>
    private static IEnumerable<ExpressionSyntax> Conjuncts(ExpressionSyntax condition)
    {
        if (condition is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.LogicalAndExpression))
        {
            foreach (var left in Conjuncts(binary.Left)) yield return left;
            foreach (var right in Conjuncts(binary.Right)) yield return right;
            yield break;
        }
        yield return condition;
    }
}
