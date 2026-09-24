using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A key a surface consumes to close itself must never reach a pane.
///
/// On Windows a key press is a KeyDown plus a WM_CHAR, and TranslateMessage
/// posts the WM_CHAR before the KeyDown is even dispatched. A surface that
/// handles Enter, Escape or Space in its KeyDown and closes cannot stop the
/// character: it is delivered to whatever holds focus once the surface is
/// gone, which can be the pane the surface returns focus to or, when a popup
/// closes around the focused element, any pane in the window. The pane
/// forwards it, and Enter's '\r' runs the prompt line.
///
/// The fix is one signal (ConsumedCloseKey) that every close-on-key surface
/// raises before its close runs, a window handler that arms every pane, and a
/// one-shot arm in the terminal that drops only the armed key's character and
/// is retired by the terminal's next KeyDown or IME composition.
///
/// These tests pin that wiring on every surface, and the census tests make a
/// new framework dialog or flyout fail until it is watched. The behaviour
/// (arm, drop, one-shot, retire) runs on a live window through the test seam:
/// windows/scripts/seam-consumed-close-key.ps1. The OS queue half, a real
/// WM_CHAR delivered to a focus-reverted surface, needs synthesised input,
/// which this repo does not do.
/// </summary>
public class ConsumedCloseKeyWiringTests
{
    private const string Signal = "ConsumedCloseKey";

    private static ShellSource Terminal() => ShellSource.Load("Controls.TerminalControl.xaml.cs");
    private static ShellSource MainWindow() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");

    /// <summary>
    /// The callee as written, with any namespace qualification in front of
    /// the signal's class folded away, so `Ghostty.Input.ConsumedCloseKey.X`
    /// and `ConsumedCloseKey.X` read the same.
    /// </summary>
    private static string SignalCall(InvocationExpressionSyntax call)
    {
        var text = call.CalleeText();
        var at = text.IndexOf(Signal + ".", StringComparison.Ordinal);
        return at < 0 ? text : text[at..];
    }

    private static InvocationExpressionSyntax? AsCall(StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call } ? call : null;

    /// <summary>
    /// Asserts the method's first statement raises the signal, and names
    /// which raise. First, because the close that follows moves focus inside
    /// the same keystroke: a raise after it arms nothing in time.
    /// </summary>
    private static string AssertRaisesFirst(ShellSource source, string method)
    {
        var body = source.Method(method).Body;
        Assert.True(body is { Statements.Count: > 0 }, $"{method} has no statements");
        var call = AsCall(body!.Statements[0]);
        Assert.True(
            call is not null && SignalCall(call).StartsWith(Signal + ".Raise", StringComparison.Ordinal),
            $"{method} must raise {Signal} as its first statement, before the close moves focus; "
            + $"found `{body.Statements[0]}`");
        return SignalCall(call!);
    }

    /// <summary>Every invocation of <paramref name="method"/> under <paramref name="scope"/>.</summary>
    private static List<InvocationExpressionSyntax> CallsTo(SyntaxNode scope, string method) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() == method)
            .ToList();

    // -- The terminal's half --------------------------------------------------

    [Fact]
    public void TheTerminalRetiresTheArmFirstOnEveryKeyDown()
    {
        var first = Terminal().Method("OnKeyDown").Body!.Statements[0];
        Assert.Equal("RetireStaleCharacterSuppress", AsCall(first)?.CalleeText());
    }

    [Fact]
    public void TheTerminalRetiresTheArmFirstWhenAnImeCompositionStarts()
    {
        // A commit's characters arrive with no KeyDown of their own, so an
        // arm standing when a composition starts would eat the first one.
        var first = Terminal().Method("OnImeCompositionStarted").Body!.Statements[0];
        Assert.Equal("RetireStaleCharacterSuppress", AsCall(first)?.CalleeText());
    }

    [Fact]
    public void TheCharacterDecisionConsultsTheArmBeforeAnythingForwards()
    {
        var source = Terminal();
        var routed = source.Method("OnCharacterReceived");
        Assert.Single(CallsTo(routed, "HandleCharacter"));

        // One decision for both one-shot drops, first, then a return before
        // anything forwards. The chord suppress is spent there and nowhere
        // else: a second place that clears it is a second order to get wrong.
        var handle = source.Method("HandleCharacter");
        var statements = handle.Body!.Statements;
        var decide = Assert.Single(CallsTo(statements[0], "ConsumedCloseArm.Decide"));
        Assert.Equal("ref _consumedCloseArm", decide.Arg(0));
        Assert.Equal("ref _suppressNextCharacter", decide.Arg(1));
        var guard = Assert.IsType<IfStatementSyntax>(statements[1]);
        Assert.Contains("CharacterFate.Forward", guard.Condition.ToString(), StringComparison.Ordinal);
        Assert.True(
            guard.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(),
            "a dropped character must return before the forward");
        Assert.Empty(handle.AssignsTo("_suppressNextCharacter"));
        Assert.Empty(CallsTo(handle, "_consumedCloseArm.Consume"));
    }

    [Theory]
    [InlineData("OnKeyUp")]
    [InlineData("OnCharacterReceived")]
    public void TheKeyReleaseAndCharacterPathsAreGatedOnAnOpenPalette(string method)
    {
        // OnKeyDown always had this gate. While the palette's popup is up but
        // its search box has not yet taken focus, releases and characters
        // still land on the pane, and CharacterReceived is raised on its own.
        var gated = Terminal().Method(method).Body!.Statements
            .OfType<IfStatementSyntax>()
            .Any(i => i.Condition.ToString() == "CommandPaletteIsOpen"
                      && i.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any());
        Assert.True(gated, $"{method} must return while CommandPaletteIsOpen");
    }

    // -- The window's half ----------------------------------------------------

    [Fact]
    public void TheWindowArmsEveryPaneItHolds()
    {
        var source = MainWindow();
        var subscribed = source.Root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression)
                        && a.Left.ToString().EndsWith(Signal + ".Raised", StringComparison.Ordinal))
            .ToList();
        var wire = Assert.Single(subscribed);
        Assert.Equal("OnConsumedCloseKey", wire.Right.ToString());

        // Every leaf of every tab: which pane the character lands on is
        // decided by where focus falls, not by which leaf is active.
        var handler = source.Method("OnConsumedCloseKey");
        var arm = Assert.Single(CallsTo(handler, "leaf.Terminal().ArmConsumedClose"));
        var loops = arm.Ancestors().OfType<ForEachStatementSyntax>().ToList();
        Assert.Contains(loops, l => l.Expression.ToString().Contains("PaneTree.Leaves", StringComparison.Ordinal));
        Assert.Contains(loops, l => l.Expression.ToString() == "_tabManager.Tabs");
    }

    [Fact]
    public void TheWindowTakesTheStaticSubscriptionBackOnClose()
    {
        var close = MainWindow().Method("OnClosedAsync");
        Assert.Contains(
            close.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.IsKind(SyntaxKind.SubtractAssignmentExpression)
                 && a.Left.ToString().EndsWith(Signal + ".Raised", StringComparison.Ordinal)
                 && a.Right.ToString() == "OnConsumedCloseKey");
    }

    // -- The surfaces ---------------------------------------------------------

    [Fact]
    public void Palette_EscapeRaisesBeforeItCloses()
    {
        var palette = ShellSource.Load("Controls.CommandPalette.CommandPaletteControl.xaml.cs");
        var section = palette.Case("HandleSearchKey", "VirtualKey.Escape");
        var first = AsCall(section.Statements[0]);
        Assert.True(first is not null, "Escape's first statement must be the raise");
        Assert.Equal(Signal + ".Raise", SignalCall(first!));
        Assert.Equal("VirtualKey.Escape", first!.Arg(0));
    }

    [Fact]
    public void Palette_EnterAndAnInvokedRowGoThroughTheActivationAct()
    {
        var palette = ShellSource.Load("Controls.CommandPalette.CommandPaletteControl.xaml.cs");
        Assert.Equal(Signal + ".RaiseFor", AssertRaisesFirst(palette, "Activate"));

        var enter = palette.Case("HandleSearchKey", "VirtualKey.Enter");
        var call = AsCall(enter.Statements[0]);
        Assert.Equal("Activate", call?.CalleeText());
        Assert.Equal("VirtualKey.Enter", call!.Arg(0));

        Assert.Single(CallsTo(palette.Method("OnItemClick"), "Activate"));

        // The act is the only thing that runs a command, so no entrance can
        // run one without the raise.
        var runs = palette.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith("ExecuteSelectedCommand", StringComparison.Ordinal))
            .ToList();
        var run = Assert.Single(runs);
        Assert.Equal("Activate", run.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText);
    }

    [Fact]
    public void SearchBar_EveryCloseTheUserAsksForRaisesFirst()
    {
        var bar = ShellSource.Load("Controls.Search.SearchBarControl.xaml.cs");
        Assert.Equal(Signal + ".RaiseFor", AssertRaisesFirst(bar, "CloseFromKey"));

        Assert.Single(CallsTo(bar.Method("OnControlKeyDown"), "CloseFromKey"));
        Assert.Single(CallsTo(bar.Case("HandleNeedleKey", "VirtualKey.Escape"), "CloseFromKey"));
        Assert.Single(CallsTo(bar.Method("OnCloseClick"), "CloseFromKey"));

        // RaiseClosed is what hands focus back to the pane. Reached only
        // through CloseFromKey, a new close path cannot skip the raise.
        var closers = CallsTo(bar.Root, "RaiseClosed")
            .Select(c => c.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText)
            .ToList();
        Assert.Equal(new[] { "CloseFromKey" }, closers);
    }

    [Fact]
    public void TabOverview_BothWaysOutRaiseFirst()
    {
        var overview = ShellSource.Load("Tabs.TabOverviewControl.xaml.cs");
        Assert.Equal(Signal + ".RaiseFor", AssertRaisesFirst(overview, "Choose"));
        Assert.Equal(Signal + ".Raise", AssertRaisesFirst(overview, "Dismiss"));

        Assert.Single(CallsTo(overview.Case("HandleKey", "VirtualKey.Escape"), "Dismiss"));
        Assert.Single(CallsTo(overview.Case("HandleKey", "VirtualKey.Enter"), "Choose"));
        Assert.Single(CallsTo(overview.Method("OnTileClick"), "Choose"));

        // The events that close the overview, raised only from the two acts,
        // plus the scrim tap: a pointer, with no key behind it.
        var raisers = overview.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.CalleeText() is "TabChosen?.Invoke" or "Dismissed?.Invoke")
            .Select(i => i.Ancestors().OfType<MethodDeclarationSyntax>().First().Identifier.ValueText)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new[] { "Choose", "Dismiss", "OnScrimTapped" }, raisers);
    }

    [Fact]
    public void VerticalStrip_APinnedRowsEnterOrSpaceRaisesBeforeItActivates()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        Assert.Equal(Signal + ".Raise", AssertRaisesFirst(strip, "ActivateShelfFromKey"));
        var section = strip.Case("OnPinnedRowKeyDown", "VirtualKey.Enter");
        Assert.Single(CallsTo(section, "ActivateShelfFromKey"));
        Assert.Empty(CallsTo(section, "ActivateFromShelf"));
    }

    [Fact]
    public void VerticalStrip_ABodyRowsSelectionRaisesBeforeItActivates()
    {
        var strip = ShellSource.Load("Tabs.VerticalTabStrip.xaml.cs");
        Assert.Equal(Signal + ".RaiseForHeldKeys", AssertRaisesFirst(strip, "ActivateFromNavSelection"));
        Assert.Single(CallsTo(strip.Method("OnNavSelectionChanged"), "ActivateFromNavSelection"));
        Assert.Empty(CallsTo(strip.Method("OnNavSelectionChanged"), "_manager.Activate"));
    }

    [Fact]
    public void HorizontalStrip_AnItemsSelectionRaisesBeforeItActivates()
    {
        var host = ShellSource.Load("Tabs.TabHost.xaml.cs");
        Assert.Equal(Signal + ".RaiseForHeldKeys", AssertRaisesFirst(host, "ActivateFromSelection"));
        Assert.Single(CallsTo(host.Method("OnSelectionChanged"), "ActivateFromSelection"));
        Assert.Empty(CallsTo(host.Method("OnSelectionChanged"), "_manager.Activate"));
    }

    [Fact]
    public void NoticeBar_ItsOwnKeysAndAFocusedDismissRaise()
    {
        var host = ShellSource.Load("Controls.Notifications.NotificationHost.xaml.cs");
        Assert.Equal(Signal + ".Raise", AssertRaisesFirst(host, "DismissFromKey"));

        // The bar's KeyDown lambda answers through the act.
        var addBar = host.Method("AddBar");
        var keyLambda = addBar.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.IsKind(SyntaxKind.AddAssignmentExpression) && a.Left.ToString() == "bar.KeyDown");
        Assert.Single(CallsTo(keyLambda, "DismissFromKey"));
        Assert.Empty(CallsTo(keyLambda, "_service?.Dismiss"));

        // Any removal of a bar that holds focus (an action button, the close
        // button) raises for the held keys before the bar leaves the tree.
        var statements = host.Method("RemoveBar").DescendantNodes().OfType<StatementSyntax>().ToList();
        var raise = statements.FindIndex(s => s is IfStatementSyntax i
            && i.Statement.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .Any(c => SignalCall(c) == Signal + ".RaiseForHeldKeys"));
        var remove = statements.FindIndex(s => AsCall(s)?.CalleeText() == "Stack.Children.Remove");
        Assert.True(raise >= 0, "RemoveBar must raise for held keys when the bar had focus");
        Assert.True(remove >= 0, "expected RemoveBar to remove the bar");
        Assert.True(raise < remove, "the raise must come before the bar leaves the tree and focus moves");
    }

    [Fact]
    public void NewTabButton_WatchesItsMenuAndRaisesBeforeOpeningATab()
    {
        var button = ShellSource.Load("Tabs.NewTabSplitButton.xaml.cs");
        var ctor = button.Root.DescendantNodes().OfType<ConstructorDeclarationSyntax>().Single();
        Assert.Contains(
            ctor.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            c => SignalCall(c) == Signal + ".Watch" && c.Arg(0) == "ProfileMenu");

        var primary = button.Method("OnPrimaryClick").Body!.Statements.ToList();
        var raise = primary.FindIndex(s => AsCall(s) is { } c && SignalCall(c) == Signal + ".RaiseForHeldKeys");
        var open = primary.FindIndex(s => AsCall(s)?.CalleeText() == "Owner.OpenProfile");
        Assert.True(raise >= 0 && open >= 0 && raise < open,
            "OnPrimaryClick must raise for held keys before it opens the tab that takes focus");
    }

    [Fact]
    public void TheDialogTrackerWatchesEveryDialogItTracks()
    {
        var tracker = ShellSource.Load("Dialogs.DialogTracker.cs");
        var first = AsCall(tracker.Method("Track").Body!.Statements[0]);
        Assert.True(first is not null && SignalCall(first) == Signal + ".Watch",
            "Track must watch the dialog as its first statement");
    }

    // -- The census: framework dialogs and flyouts ----------------------------

    /// <summary>
    /// Files that only run inside the Settings window (or another top-level
    /// window with no panes). A key's WM_CHAR is posted to the window that
    /// took the KeyDown, so a dialog or flyout closing there cannot hand its
    /// character to a pane in a terminal window.
    /// </summary>
    /// An entry ending in a dot is a folder: every file under it is exempt.
    private static readonly (string File, string Why)[] NoPaneWindow =
    {
        ("Settings.Pages.", "Settings pages: hosted only by the Settings window, their menus and dialogs close there"),
        ("Settings.ProfileIconPickerControl.xaml.cs", "a Settings control: its dialog lives in the Settings window"),
    };

    private static bool Covers(string entry, string name) =>
        entry.EndsWith('.')
            ? name.StartsWith(entry, StringComparison.Ordinal) || name.Contains("." + entry, StringComparison.Ordinal)
            : name.EndsWith(entry, StringComparison.Ordinal);

    private static bool Exempt(ShellSource file) => NoPaneWindow.Any(x => Covers(x.File, file.Name));

    private static SyntaxNode Scope(SyntaxNode node) =>
        (SyntaxNode?)node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
        ?? node.Ancestors().OfType<PropertyDeclarationSyntax>().First();

    /// <summary>
    /// XAML files that belong to the Settings window: their flyouts close
    /// there, never onto a pane. An entry ending in a dot is a folder.
    /// </summary>
    private static readonly (string File, string Why)[] NoPaneWindowXaml =
    {
        ("Settings.", "the Settings window's pages and dialogs"),
        ("Controls.Settings.", "controls only the Settings window's pages host"),
    };

    /// <summary>`this.x` and `x` name the same thing.</summary>
    private static string Plain(string expression)
    {
        var text = expression.Trim();
        return text.StartsWith("this.", StringComparison.Ordinal) ? text["this.".Length..] : text;
    }

    /// <summary>The receiver of a `ShowAsync` call, whatever its shape, or null.</summary>
    private static string? ShowAsyncReceiver(InvocationExpressionSyntax call) => call.Expression switch
    {
        // dlg.ShowAsync(), this._dlg.ShowAsync(), new X().ShowAsync(),
        // and every overload (ShowAsync(ContentDialogPlacement.InPlace)).
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ShowAsync" } access => access.Expression.ToString(),
        // dlg?.ShowAsync()
        MemberBindingExpressionSyntax { Name.Identifier.ValueText: "ShowAsync" }
            when call.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } conditional
            => conditional.Expression.ToString(),
        _ => null,
    };

    /// <summary>
    /// Classes in the corpus that declare a static ShowAsync of their own
    /// (CheatSheetLauncher): a call through the class name is that helper,
    /// whose body the census reads on its own, not a dialog.
    /// </summary>
    private static HashSet<string> StaticShowAsyncHelpers(IEnumerable<ShellSource> files) =>
        files.SelectMany(f => f.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            .Where(m => m.Identifier.ValueText == "ShowAsync"
                        && m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword)))
            .Select(m => m.Ancestors().OfType<ClassDeclarationSyntax>().First().Identifier.ValueText)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The first argument of every Watch and Track call under <paramref name="scope"/>.</summary>
    private static HashSet<string> WatchedIn(SyntaxNode scope, bool includeTrack) =>
        scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => SignalCall(c) == Signal + ".Watch"
                        || (includeTrack && c.CalleeText().EndsWith(".Track", StringComparison.Ordinal)))
            .Where(c => c.ArgumentList.Arguments.Count > 0)
            .Select(c => Plain(c.ArgumentList.Arguments[0].Expression.ToString()))
            .ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void EveryDialogShownOverAPaneIsWatched()
    {
        var files = ShellSource.AllFiles().ToList();
        var helpers = StaticShowAsyncHelpers(files);
        var shows = new List<(ShellSource File, InvocationExpressionSyntax Call, string Receiver)>();
        foreach (var file in files)
        {
            foreach (var call in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (ShowAsyncReceiver(call) is not { } receiver) continue;
                var last = receiver.Split('.')[^1];
                if (helpers.Contains(last)) continue;
                shows.Add((file, call, Plain(receiver)));
            }
        }

        // Non-vacuous: the dialogs this was written against are still found.
        Assert.True(shows.Count >= 8, $"expected at least 8 dialog ShowAsync calls, found {shows.Count}");

        // The watch has to name the dialog being shown: any Watch or Track
        // elsewhere in the method is not one.
        var unwatched = shows
            .Where(s => !Exempt(s.File))
            .Where(s => !WatchedIn(Scope(s.Call), includeTrack: true).Contains(s.Receiver))
            .Select(s => $"{s.File.Name}: {s.Receiver}.ShowAsync")
            .ToList();
        Assert.True(unwatched.Count == 0,
            "these dialogs close on Enter or Escape back onto a pane without "
            + $"{Signal}.Watch or DialogTracker.Track on the dialog shown, so the key's character "
            + "reaches the pane: " + string.Join("; ", unwatched));
    }

    private static readonly string[] FlyoutTypes = { "MenuFlyout", "Flyout", "CommandBarFlyout" };

    [Fact]
    public void EveryFlyoutBuiltForATerminalWindowIsWatched()
    {
        var made = new List<(ShellSource File, ObjectCreationExpressionSyntax New, string Variable)>();
        foreach (var file in ShellSource.AllFiles())
        {
            foreach (var creation in file.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            {
                if (!FlyoutTypes.Contains(creation.Type.ToString())) continue;
                // The name the flyout is held under: a local it is declared
                // into, or a field, local or property it is assigned to. A
                // flyout returned straight from a helper or passed inline has
                // no name to watch, and fails: hold it in a local and watch it.
                var variable = creation.Parent switch
                {
                    EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } => declarator.Identifier.ValueText,
                    AssignmentExpressionSyntax assignment => Plain(assignment.Left.ToString()),
                    _ => "",
                };
                made.Add((file, creation, variable));
            }
        }

        Assert.True(made.Count >= 8, $"expected at least 8 flyout creations, found {made.Count}");

        var unwatched = made
            .Where(m => !Exempt(m.File))
            .Where(m => m.Variable.Length == 0
                        || !WatchedIn(Scope(m.New), includeTrack: false).Contains(m.Variable))
            .Select(m => $"{m.File.Name}: new {m.New.Type} ({(m.Variable.Length > 0 ? m.Variable : "not held in a local")})")
            .ToList();
        Assert.True(unwatched.Count == 0,
            "these flyouts close on Escape or Enter back onto a pane without "
            + $"{Signal}.Watch, so the key's character reaches the pane: "
            + string.Join("; ", unwatched));
    }

    [Fact]
    public void NoTeachingTipOpensOverAPane()
    {
        // A TeachingTip closes on Escape and hands focus back like a flyout,
        // but it is neither a FlyoutBase nor a ContentDialog, so there is no
        // Watch for it. The first one near a pane needs one.
        var tips = ShellSource.AllFiles()
            .Where(f => !Exempt(f))
            .SelectMany(f => f.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(c => c.Type.ToString() == "TeachingTip")
                .Select(_ => f.Name))
            .Concat(PaneWindowXaml()
                .Where(x => x.Root.Descendants().Any(e => e.Name.LocalName == "TeachingTip"))
                .Select(x => x.Name))
            .ToList();
        Assert.True(tips.Count == 0,
            "a TeachingTip in a pane window has no consumed-close Watch overload yet; add one "
            + "to ConsumedCloseKey and watch it: " + string.Join(", ", tips));
    }

    private const string XamlPrefix = "Ghostty.Tests.XamlCensus.";

    /// <summary>
    /// Every embedded XAML file's dotted name. Most of the shell's XAML is
    /// embedded under XamlCensus with its path ("Tabs.NewTabSplitButton.xaml");
    /// the handful other tests already embed keep their own logical names
    /// ("Tabs.TabHost.xaml", "Settings.Pages.KeybindingsPage.xaml"), which
    /// end in the same file name. See Ghostty.Tests.csproj.
    /// </summary>
    private static IEnumerable<(string Resource, string Name)> AllXaml()
    {
        foreach (var resource in typeof(ShellSource).Assembly.GetManifestResourceNames())
        {
            var dotted = resource.Replace('\\', '.').Replace('/', '.');
            if (!dotted.EndsWith(".xaml", StringComparison.Ordinal)) continue;
            var name = dotted.StartsWith(XamlPrefix, StringComparison.Ordinal)
                ? dotted[XamlPrefix.Length..]
                : dotted.StartsWith("Ghostty.Tests.", StringComparison.Ordinal) ? dotted["Ghostty.Tests.".Length..] : dotted;
            yield return (resource, name);
        }
    }

    /// <summary>"CommandPaletteControl.xaml" out of any dotted XAML name.</summary>
    private static string XamlFileName(string name)
    {
        var parts = name.Split('.');
        return parts[^2] + "." + parts[^1];
    }

    /// <summary>Every XAML file outside the Settings window, parsed.</summary>
    private static List<(string Name, XElement Root)> PaneWindowXaml()
    {
        var asm = typeof(ShellSource).Assembly;
        var found = new List<(string, XElement)>();
        foreach (var (resource, name) in AllXaml())
        {
            if (NoPaneWindowXaml.Any(x => Covers(x.File, name))) continue;
            using var stream = asm.GetManifestResourceStream(resource)!;
            found.Add((name, XDocument.Load(stream).Root!));
        }
        return found;
    }

    [Fact]
    public void EveryFlyoutDeclaredInPaneWindowXamlIsWatched()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = PaneWindowXaml();
        Assert.True(xaml.Count >= 20, $"expected the shell's XAML under {XamlPrefix}, found {xaml.Count} files");

        var files = ShellSource.AllFiles().ToList();
        var declared = new List<string>();
        var unwatched = new List<string>();
        foreach (var (name, root) in xaml)
        {
            foreach (var flyout in root.Descendants().Where(e => FlyoutTypes.Contains(e.Name.LocalName)))
            {
                var id = (string?)flyout.Attribute(x + "Name");
                declared.Add($"{name}:{id}");
                var codeBehind = files.FirstOrDefault(f => f.Name.EndsWith("." + XamlFileName(name) + ".cs", StringComparison.Ordinal));
                if (id is null || codeBehind is null || !WatchedIn(codeBehind.Root, includeTrack: false).Contains(id))
                    unwatched.Add($"{name}: <{flyout.Name.LocalName}> {(id is null ? "(no x:Name)" : id)}");
            }
        }

        // Non-vacuous: the new-tab button's profile menu is declared in XAML.
        Assert.Contains(declared, d => d.EndsWith(":ProfileMenu", StringComparison.Ordinal));
        Assert.True(unwatched.Count == 0,
            "these XAML flyouts close on Escape or Enter back onto a pane: give each an x:Name "
            + $"and call {Signal}.Watch(<name>) in its code-behind: " + string.Join("; ", unwatched));
    }

    [Fact]
    public void EveryExemptionStillCoversADialogOrFlyout()
    {
        // An exemption for a file that no longer shows a dialog or builds a
        // flyout is a pass nobody is checking, waiting for the next one.
        var files = ShellSource.AllFiles().ToList();
        foreach (var (file, why) in NoPaneWindow)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            var sources = files.Where(f => Covers(file, f.Name)).ToList();
            Assert.NotEmpty(sources);
            var covers = sources.Any(source => source.Root.DescendantNodes().Any(n =>
                n is ObjectCreationExpressionSyntax { Type: var type } && FlyoutTypes.Contains(type.ToString())
                || n is InvocationExpressionSyntax call && ShowAsyncReceiver(call) is not null));
            Assert.True(covers, $"{file} is exempt but shows no dialog and builds no flyout; drop the exemption");
        }

        var allXaml = AllXaml().Select(x => x.Name).ToList();

        foreach (var (file, why) in NoPaneWindowXaml)
        {
            Assert.False(string.IsNullOrWhiteSpace(why));
            Assert.Contains(allXaml, n => Covers(file, n));
        }
    }
}
