using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The palette theme mode's joints to the shell. The decisions (throttle,
/// exact restore, one commit, the shared snapshot slot) are unit-tested
/// against the real <c>PaletteThemeBrowse</c>; what those tests cannot see is
/// whether the WinUI half still asks it, which only the source can show. The
/// seam harness (windows/scripts/seam-palette-theme.ps1) is what proves the
/// behaviour on a running window.
/// </summary>
public class PaletteThemeWiringTests
{
    private static ShellSource ViewModel() => ShellSource.Load("Commands.CommandPaletteViewModel.cs");
    private static ShellSource Palette() => ShellSource.Load("Controls.CommandPalette.CommandPaletteControl.xaml.cs");
    private static ShellSource Window() => ShellSource.Load("Ghostty.MainWindow.xaml.cs");
    private static ShellSource Target() => ShellSource.Load("Services.PaletteThemeTarget.cs");

    /// <summary>
    /// Invocations whose callee, as written, ends with <paramref name="tail"/>.
    /// Covers `_themeMode?.Browse.Cancel()`, whose callee the shared helper
    /// reads as `.Browse.Cancel`.
    /// </summary>
    private static List<InvocationExpressionSyntax> CallsEndingWith(SyntaxNode node, string tail) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression.ToString().EndsWith(tail, System.StringComparison.Ordinal))
            .ToList();

    private static bool IsThemeModeTest(ExpressionSyntax condition) =>
        condition.ToString() == "Mode == PaletteMode.Theme";

    /// <summary>
    /// Every close of the palette undoes an unconfirmed browse, and does it
    /// first. Escape, the toggle chord, a click outside and the window going
    /// away all reach the view model's Close; a cancel anywhere else would
    /// miss at least one of them and leave a preview on screen that nothing
    /// will ever take back.
    /// </summary>
    [Fact]
    public void CloseCancelsTheBrowseBeforeAnythingElse()
    {
        var close = ViewModel().Method("Close");
        var first = Assert.IsType<IfStatementSyntax>(close.Body!.Statements[0]);
        Assert.True(IsThemeModeTest(first.Condition), $"Close must open with the theme-mode test, found `{first.Condition}`");
        Assert.Single(CallsEndingWith(first.Statement, "Browse.Cancel"));
        Assert.Single(first.Statement.Calls("LeaveThemeMode"));
    }

    /// <summary>
    /// Enter in the theme list confirms, and only there: the command path
    /// below it would execute a theme row as a command and close the palette
    /// with the browse still live.
    /// </summary>
    [Fact]
    public void EnterInTheThemeListConfirmsExactlyOnce()
    {
        var vm = ViewModel();
        var execute = vm.Method("ExecuteSelectedCommand");
        var first = Assert.IsType<IfStatementSyntax>(execute.Body!.Statements[0]);
        Assert.True(IsThemeModeTest(first.Condition));
        Assert.Single(first.Statement.Calls("ConfirmSelectedTheme"));
        Assert.True(first.Statement.DescendantNodesAndSelf().OfType<ReturnStatementSyntax>().Any(),
            "the theme-mode branch of ExecuteSelectedCommand must return before the command path");

        var confirm = vm.Method("ConfirmSelectedTheme");
        Assert.Single(CallsEndingWith(confirm, "Browse.Confirm"));
        Assert.Single(confirm.Calls("Close"));
        Assert.Empty(CallsEndingWith(confirm, "Browse.Cancel"));
    }

    /// <summary>
    /// Moving the highlight is the preview: the selection setter hands the
    /// highlighted theme to the browse, guarded only by being in the theme
    /// list and not placing the list's own first highlight.
    /// </summary>
    [Fact]
    public void MovingTheHighlightPreviews()
    {
        var property = ViewModel().Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "SelectedCommand");
        var setter = property.AccessorList!.Accessors.Single(a => a.IsKind(SyntaxKind.SetAccessorDeclaration));
        var select = Assert.Single(CallsEndingWith(setter, "Browse.Select"));
        var guard = select.Ancestors().OfType<IfStatementSyntax>().First();
        Assert.Equal("Mode == PaletteMode.Theme && !_placingThemeSelection", guard.Condition.ToString());
    }

    /// <summary>
    /// The keyboard and the seam share one key decision, so the seam cannot
    /// drive a palette the keyboard does not.
    /// </summary>
    [Fact]
    public void TheKeyboardAndTheSeamShareOneKeyHandler()
    {
        var palette = Palette();
        Assert.Single(palette.Method("OnSearchKeyDown").Calls("HandleSearchKey"));
        Assert.Empty(palette.Method("OnSearchKeyDown").DescendantNodes().OfType<SwitchStatementSyntax>());
        Assert.Single(palette.Method("TestSeamKey").Calls("HandleSearchKey"));
        Assert.Single(palette.Method("HandleSearchKey").DescendantNodes().OfType<SwitchStatementSyntax>());
    }

    /// <summary>
    /// The window's browse records against the process's one snapshot slot,
    /// the one the inline +list-themes picker and the preview pipe share. A
    /// slot of its own is how two overlapping browses restore a theme nobody
    /// had.
    /// </summary>
    [Fact]
    public void TheBrowseSharesTheProcessSnapshotSlot()
    {
        var creations = Window().Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Where(n => n.Type.ToString().EndsWith("PaletteThemeBrowse", System.StringComparison.Ordinal))
            .ToList();
        var creation = Assert.Single(creations);
        Assert.Equal("Ghostty.App.ThemePreviewSession", creation.ArgumentList!.Arguments[1].ToString());
    }

    /// <summary>
    /// A window closing mid-browse takes its preview with it. The palette's
    /// own Close is not reached on that path, and the preview is the app's,
    /// so without this every other window would stay on it.
    /// </summary>
    [Fact]
    public void AClosingWindowCancelsItsBrowse()
    {
        var teardown = Window().Method("OnClosedAsync");
        Assert.Contains(CallsEndingWith(teardown, "Cancel"), c =>
            c.Ancestors().OfType<IfStatementSyntax>()
                .Any(guard => guard.Condition.ToString().Contains("_paletteThemeBrowse", System.StringComparison.Ordinal)));
    }

    /// <summary>
    /// A confirm is one write of the `theme` key, through the writer that
    /// suppresses the watcher, catches disk errors and reloads, and through
    /// the editor whose write boundary carries the test-config guard.
    /// </summary>
    [Fact]
    public void ACommitIsOneThemeWriteThroughTheGuardedWriter()
    {
        var commit = Target().Method("Commit");
        var write = Assert.Single(commit.Calls("_writer.Write"));
        var set = Assert.Single(commit.Calls("_editor.SetValue"));
        Assert.Equal("ThemeKey", set.Arg(0));
        Assert.Contains(set.Ancestors(), a => a == write);
        Assert.Empty(commit.Calls("_editor.WriteRaw"));
    }

    // -- One theme list --------------------------------------------------

    /// <summary>
    /// The palette, the Settings Colors page and the chrome's theme lookup
    /// all read ThemeProvider.Directories, and it names the bundled themes
    /// after the user's directories. Dropping the bundled directory from it
    /// would hide every theme the app ships from all three at once.
    /// </summary>
    [Fact]
    public void EveryThemeReaderSharesOneListThatIncludesTheBundledThemes()
    {
        var provider = ShellSource.Load("Services.ThemeProvider.cs");
        var directories = provider.Method("Directories");
        var search = Assert.Single(directories.Calls("ThemeSearchPath.Directories"));
        Assert.Equal("ThemeSearchPath.BundledDirectoryForThisProcess()", search.Arg(2));
        Assert.Single(provider.Method("Refresh").Calls("Directories"));

        var resolve = ShellSource.Load("Services.ConfigService.cs").Method("ResolveThemePath");
        Assert.Single(resolve.Calls("ThemeProvider.Directories"));
        Assert.Empty(resolve.Calls("ThemeSearchPath.UserDirectories"));

        // The palette's list and its swatches.
        Assert.Equal(2, Window().Root.Calls("Services.ThemeProvider.Directories").Count);
    }

    // -- Filter debounce -------------------------------------------------

    /// <summary>
    /// Typing in the theme list refilters through the debounce, never
    /// directly: a direct refilter moves the highlight, and so the preview,
    /// on every keystroke. PaletteFilterDebounceTests pins the timing; this
    /// pins that the view model asks it.
    /// </summary>
    [Fact]
    public void TypingInTheThemeListRefiltersThroughTheDebounce()
    {
        var changed = ViewModel().Method("OnSearchTextChanged");
        var theme = Assert.Single(changed.DescendantNodes().OfType<IfStatementSyntax>(), s => IsThemeModeTest(s.Condition));
        var request = Assert.Single(CallsEndingWith(theme.Statement, "Filter.Request"));
        Assert.Single(request.ArgumentList.Arguments[0].Calls("ApplyThemeFilter"));

        // The only other refilter is the fallback for a palette with no theme
        // mode wired, under an explicit null test.
        var direct = theme.Statement.Calls("ApplyThemeFilter")
            .Where(c => !c.Ancestors().Contains(request))
            .ToList();
        var fallback = Assert.Single(direct);
        var guard = Assert.IsType<IfStatementSyntax>(fallback.Ancestors().OfType<IfStatementSyntax>().First());
        Assert.Equal("_themeMode is null", guard.Condition.ToString());
    }

    /// <summary>
    /// A key that acts on the list acts on the list the typed text describes:
    /// every mover and Enter apply a waiting filter before anything else.
    /// </summary>
    [Theory]
    [InlineData("MoveSelectionUp")]
    [InlineData("MoveSelectionDown")]
    [InlineData("MoveSelectionBy")]
    [InlineData("ConfirmSelectedTheme")]
    public void ListKeysApplyAWaitingFilterFirst(string method)
    {
        var body = ViewModel().Method(method).Body!;
        Assert.Single(body.Statements[0].Calls("FlushThemeFilter"));
        Assert.Single(CallsEndingWith(ViewModel().Method("FlushThemeFilter"), "Filter.Flush"));
    }

    /// <summary>
    /// Entering and leaving the theme list drop a waiting filter, so one
    /// never lands on the next list the palette shows.
    /// </summary>
    [Theory]
    [InlineData("EnterThemeMode")]
    [InlineData("LeaveThemeMode")]
    public void EnteringAndLeavingTheThemeListDropAWaitingFilter(string method)
        => Assert.Single(CallsEndingWith(ViewModel().Method(method), "Filter.Cancel"));

    /// <summary>
    /// The debounce has a timer of its own. Sharing the browse's one-slot
    /// timer would let a keystroke replace the browse's pending cooldown, or
    /// the cooldown a keystroke's wait.
    /// </summary>
    [Fact]
    public void TheFilterWaitsOnItsOwnTimer()
    {
        var window = Window();
        Assert.Contains(window.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>(),
            o => o.Type.ToString().EndsWith("PaletteFilterDebounce", System.StringComparison.Ordinal)
                 && o.ArgumentList?.Arguments.Single().ToString() == "SchedulePaletteFilterWork");
        var schedule = window.Method("SchedulePaletteFilterWork");
        Assert.DoesNotContain(schedule.DescendantNodes().OfType<IdentifierNameSyntax>(),
            n => n.Identifier.ValueText.StartsWith("_paletteThemeTimer", System.StringComparison.Ordinal));
    }

    // -- Look and feel ---------------------------------------------------

    /// <summary>
    /// Every preview re-resolves the window's light/dark variant from the
    /// previewed background. A browse holds the palette on the variant it
    /// opened in, or a held arrow key across light and dark themes flips
    /// the whole palette surface at every boundary; it lets go when the
    /// browse ends, whichever way it ends. The seam harness's fast arrow run
    /// is the pixel proof; this pins where the hold lives.
    /// </summary>
    [Fact]
    public void ABrowseHoldsThePaletteOnTheVariantItOpenedIn()
    {
        var palette = Palette();
        var changed = palette.Method("OnThemeChanged");
        var hold = Assert.IsType<IfStatementSyntax>(changed.Body!.Statements[0]);
        Assert.Contains("PaletteMode.Theme", hold.Condition.ToString(), System.StringComparison.Ordinal);
        Assert.Contains(hold.Statement.DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.ToString() == "_themeSyncDeferred = true");
        Assert.True(hold.Statement.DescendantNodes().OfType<ReturnStatementSyntax>().Any(),
            "the hold must return before ApplyTheme");
        Assert.Single(changed.Calls("ApplyTheme"));

        Assert.Single(palette.Case("OnViewModelPropertyChanged", "ModeLabel").Calls("ResumeThemeSync"));
        Assert.Single(palette.Case("OnViewModelPropertyChanged", "IsOpen").Calls("ResumeThemeSync"));
        Assert.Single(palette.Method("ResumeThemeSync").Calls("ApplyTheme"));
    }

    /// <summary>
    /// Closing the palette's Popup unloads the palette, and Unloaded disposes
    /// its theme manager. Loaded has to build a new one, or every open after
    /// the first keeps the variant the first close left behind: the hold
    /// above would have nothing to catch up from, and a system light/dark
    /// flip would never reach the palette again.
    /// </summary>
    [Fact]
    public void EveryOpenFollowsTheWindowThemeAgain()
    {
        var palette = Palette();
        var unloaded = palette.Method("OnPaletteUnloaded");
        Assert.Single(unloaded.Calls("_themeManager.Dispose"));

        var loaded = palette.Method("OnPaletteLoaded");
        var rebuild = Assert.Single(loaded.DescendantNodes().OfType<IfStatementSyntax>(),
            s => s.Condition.ToString().Contains("_themeManager is null", System.StringComparison.Ordinal));
        Assert.Single(rebuild.Statement.Calls("SubscribeThemeManager"));
        Assert.Single(loaded.Calls("ApplyTheme"));

        Assert.Single(palette.Method("Configure").Calls("SubscribeThemeManager"));
        Assert.Contains(palette.Method("Configure").DescendantNodes().OfType<AssignmentExpressionSyntax>(),
            a => a.ToString() == "_configService = configService");
    }

    /// <summary>
    /// A theme row's accessible name comes from ThemeRowPresentation, which
    /// says "current theme", through the same one SetName every row takes.
    /// </summary>
    [Fact]
    public void ThemeRowsAreNamedByThemeRowPresentation()
    {
        var method = Palette().Method("OnContainerContentChanging");
        var row = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "row");
        Assert.Contains(row.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.CalleeText() == "ThemeRowPresentation.Automation");
    }

    /// <summary>
    /// A long theme list stays responsive: phase 0 paints only a swatch that
    /// is already cached (so a refilter or a scroll never blinks) and reads
    /// no theme file; an unread swatch is read in phase 1, and a container
    /// already on its way to the recycle queue reads nothing.
    /// </summary>
    [Fact]
    public void ARowNeverReadsAThemeFileInPhaseZero()
    {
        var palette = Palette();
        var phase0 = palette.Method("OnContainerContentChanging");
        Assert.Single(phase0.Calls("_vm.TryGetCachedThemeSwatch"));
        Assert.Empty(phase0.Calls("_vm.LoadThemeSwatch"));
        var register = Assert.Single(phase0.Calls("args.RegisterUpdateCallback"));
        Assert.Equal("OnThemeRowSwatchPhase", register.Arg(0));

        var phase1 = palette.Method("OnThemeRowSwatchPhase");
        Assert.Single(phase1.Calls("_vm.LoadThemeSwatch"));
        Assert.Contains("args.InRecycleQueue", phase1.Body!.Statements[0].ToString(), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The theme list's footer reads like the other modes' ("verb" per key),
    /// and the search box and list are named for what they hold there.
    /// </summary>
    [Fact]
    public void TheThemeListSaysApplyAndCancelAndFilterThemes()
    {
        var chrome = Palette().Method("UpdateModeChrome");
        var literals = chrome.DescendantNodes().OfType<LiteralExpressionSyntax>()
            .Select(l => l.Token.ValueText).ToList();
        Assert.Contains("↑↓ preview   ↵ apply   Esc cancel", literals);
        Assert.Contains("Filter themes", literals);
        Assert.Contains("Themes", literals);
        Assert.Single(Palette().Case("OnViewModelPropertyChanged", "ModeLabel").Calls("UpdateModeChrome"));
    }

    /// <summary>
    /// The palette never moves focus off its search box, so the selection
    /// announcement is how a reader hears which theme is on the terminals;
    /// and the highlight carries the "Previewing" badge with it.
    /// </summary>
    [Fact]
    public void TheHighlightAnnouncesTheThemeRowAndMovesTheBadge()
    {
        var palette = Palette();
        var sync = palette.Method("SyncSelectedItem");
        var announce = Assert.Single(sync.Calls("UiaAnnouncer.Announce"));
        Assert.StartsWith("SelectionAnnouncement(", announce.Arg(1), System.StringComparison.Ordinal);
        Assert.Single(sync.Calls("RefreshThemeBadges"));
        Assert.Single(palette.Method("SelectionAnnouncement").Calls("ThemeRowPresentation.Announcement"));
        Assert.Single(palette.Method("RefreshBadge").Calls("ThemeRowPresentation.Badge"));
    }
}
