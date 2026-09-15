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
}
