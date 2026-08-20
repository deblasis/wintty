using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The palette's accessibility decisions are unit-tested in
/// CommandPaletteAnnouncerTests and CommandRowAutomationTests. What those
/// cannot reach is the wiring: the WinUI half never loads in a test host,
/// so whether a handler still calls the decision it was handed is only
/// answerable from the parsed source.
///
/// These guard that wiring. They do not prove a screen reader speaks, and
/// they are not a substitute for the behaviour tests next door.
/// </summary>
public class CommandPaletteWiringTests
{
    private static ShellSource Palette() =>
        ShellSource.Load("Controls.CommandPalette.CommandPaletteControl.xaml.cs");

    private static ShellSource MainWindow() => ShellSource.Load("MainWindow.xaml.cs");

    // -- Status announcement ---------------------------------------------

    [Fact]
    public void StatusChange_IsPublished()
    {
        var call = Palette().Case("OnViewModelPropertyChanged", "StatusText").Call("PublishStatus");
        // Publishing anything other than the announcer's verdict would
        // either speak on every keystroke or on none of them.
        Assert.Equal("_announcer.StatusChanged(_vm.StatusText)", call.Arg(0));
    }

    [Fact]
    public void PublishStatus_RaisesTheLiveRegionOnTheStatusLabel()
    {
        var method = Palette().Method("PublishStatus");
        Assert.Equal("StatusLabel", method.Call("UiaAnnouncer.RaiseLiveRegionChanged").Arg(0));
        // It must not go back to a notification from the search box: those
        // are discarded by the row title raised from there right after.
        Assert.Empty(method.Calls("UiaAnnouncer.Announce"));
    }

    [Fact]
    public void PublishStatus_GuardsOnTheVerdictAndNothingElse()
    {
        // The escape this closes: keeping the call and neutering it with a
        // condition that is never true.
        var conditions = Palette().Method("PublishStatus").DescendantNodes()
            .OfType<IfStatementSyntax>()
            .Select(s => s.Condition.ToString())
            .ToList();
        Assert.Equal(new[] { "_vm is null", "toSpeak is null" }, conditions);
    }

    [Fact]
    public void Open_ArmsTheAnnouncerRatherThanSpeaking()
    {
        var section = Palette().Case("OnViewModelPropertyChanged", "IsOpen");
        Assert.Single(section.Calls("_announcer.Opening"));
        // Speaking here would land before focus reaches the palette, and
        // would bank the count so the post-focus one is suppressed.
        Assert.Empty(section.Calls("PublishStatus"));
    }

    [Fact]
    public void FocusSearchBox_SpeaksTheHeldCountAfterFocusLands()
    {
        var method = Palette().Method("FocusSearchBox");
        Assert.Single(method.Calls("SearchBox.Focus"));
        Assert.Equal("_announcer.Focused(_vm.StatusText)", method.Call("PublishStatus").Arg(0));
        // In a later turn of the queue, not alongside the focus call: a
        // reader flushes what it is holding when focus moves.
        Assert.Single(method.Calls("DispatcherQueue?.TryEnqueue"));
    }

    [Fact]
    public void ToggleCommandPalette_MovesFocusIntoThePaletteOnOpen()
    {
        // FocusSearchBox is what releases the held count, so an open that
        // stops calling it is an open that never speaks.
        Assert.Single(MainWindow().Method("ToggleCommandPalette")
            .Calls("CommandPaletteUI.FocusSearchBox"));
    }

    [Fact]
    public void Announcer_IsPerControl()
    {
        // A static announcer would share its already-spoken state across
        // every window, so opening the palette in a second window would
        // find the count banked and stay silent.
        var (_, field) = Palette().Field("_announcer");
        Assert.DoesNotContain(field.Modifiers, m => m.IsKind(SyntaxKind.StaticKeyword));
        Assert.Contains(field.Modifiers, m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
    }

    // -- Mode label ------------------------------------------------------

    [Fact]
    public void SetModeLabel_WritesBothTheTextAndTheName()
    {
        var method = Palette().Method("SetModeLabel");
        var name = method.Call("AutomationProperties.SetName");
        Assert.Equal("ModeLabel", name.Arg(0));
        Assert.Equal("CommandPaletteAnnouncer.ModeAccessibleName(modeLabel)", name.Arg(1));
        Assert.Contains(
            method.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.ToString() == "ModeLabel.Text = modeLabel");
    }

    [Fact]
    public void ModeChange_GoesThroughSetModeLabel()
    {
        // Assigning ModeLabel.Text directly would leave the stale name in
        // place, which is the original defect.
        var palette = Palette();
        Assert.Single(palette.Case("OnViewModelPropertyChanged", "ModeLabel").Calls("SetModeLabel"));
        Assert.Single(palette.Method("SyncAll").Calls("SetModeLabel"));
        // And nowhere else may write the text on its own.
        Assert.Empty(palette.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString() == "ModeLabel.Text"
                && a.Ancestors().OfType<MethodDeclarationSyntax>()
                    .All(m => m.Identifier.ValueText != "SetModeLabel")));
    }

    // -- Row automation --------------------------------------------------

    [Fact]
    public void EveryRow_IsNamedFromTheRowDecision()
    {
        var method = Palette().Method("OnContainerContentChanging");
        var name = method.Call("AutomationProperties.SetName");
        Assert.Equal("args.ItemContainer", name.Arg(0));
        Assert.Equal("row.Name", name.Arg(1));
        Assert.Single(method.Calls("CommandRowAutomation.For"));
    }

    [Fact]
    public void AbsentRowProperties_AreClearedNotBlanked()
    {
        var targets = Palette().Method("OnContainerContentChanging")
            .Calls("SetOrClear").Select(c => c.Arg(1)).ToList();
        Assert.Equal(
            new[]
            {
                "AutomationProperties.HelpTextProperty",
                "AutomationProperties.AcceleratorKeyProperty",
            },
            targets);

        // And SetOrClear has to actually clear. A recycled container keeps
        // the previous row's value otherwise.
        Assert.Single(Palette().Method("SetOrClear").Calls("target.ClearValue"));
    }

    // -- Focus restore ---------------------------------------------------

    [Fact]
    public void FocusedTerminal_WalksUpFromTheFocusedElement()
    {
        // The focused element is the terminal's IME sink, never the
        // TerminalControl, so a cast can only ever yield null. The walk is
        // the fix; a body that just returns null is the regression.
        var method = MainWindow().Method("FocusedTerminal");
        Assert.Single(method.Calls("FocusManager.GetFocusedElement"));
        Assert.Single(method.Calls("VisualTreeHelper.GetParent"));
        Assert.Single(method.DescendantNodes().OfType<WhileStatementSyntax>());
        Assert.Contains(
            method.DescendantNodes().OfType<DeclarationPatternSyntax>(),
            p => p.Type.ToString() == "Controls.TerminalControl");
    }

    [Fact]
    public void PaletteOpen_CapturesTheSurfaceByWalking()
    {
        var main = MainWindow();
        Assert.Single(main.Method("ToggleCommandPalette").Calls("FocusedTerminal"));
        // No cast anywhere may claim the focused element is a terminal.
        Assert.Empty(main.Root.DescendantNodes().OfType<BinaryExpressionSyntax>()
            .Where(b => b.IsKind(SyntaxKind.AsExpression)
                && b.Right.ToString() == "Controls.TerminalControl"));
    }

    [Fact]
    public void RestorePaletteFocus_FocusesTheCapturedSurfaceThenFallsBack()
    {
        var method = MainWindow().Method("RestorePaletteFocus");
        Assert.Single(method.Calls("_previousFocusSurface?.Focus"));
        Assert.Single(method.Calls("FocusActiveLeaf"));
        // Deferred: this runs inside the Popup teardown, where WinUI is
        // still moving focus itself.
        Assert.Single(method.Calls("DispatcherQueue.TryEnqueue"));
    }

    [Fact]
    public void BothClosePaths_GoThroughTheRestore()
    {
        // The toggle and the light-dismiss handler. Neither may go back to
        // poking the captured surface directly, which is how the restore
        // ends up happening in only one of them.
        var main = MainWindow();
        Assert.Equal(2, main.Root.Calls("RestorePaletteFocus").Count);
        Assert.Empty(main.Root.Calls("_previousFocusSurface?.Focus")
            .Where(c => c.Ancestors().OfType<MethodDeclarationSyntax>()
                .All(m => m.Identifier.ValueText != "RestorePaletteFocus")));
    }
}
