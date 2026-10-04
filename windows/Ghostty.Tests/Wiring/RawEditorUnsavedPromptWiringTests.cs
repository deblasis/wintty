using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The Raw Editor is the only settings page whose edits live in the editor
/// buffer rather than in the config file, so closing the settings window
/// discarded a hand-written config with no word -- and quitting the app
/// discarded it the same way, because that is how the last window's teardown
/// closes the settings window.
/// <para>
/// The answer rule is Core's and is tested there
/// (<c>UnsavedRawEditPromptTests</c>): what matters here is that the close is
/// intercepted at all, that the intercept asks the page rather than a stale
/// local, and that a failed save does not close the window. Wiring guards,
/// because the shell assembly cannot be loaded into a test host.
/// </para>
/// </summary>
public sealed class RawEditorUnsavedPromptWiringTests
{
    private static ShellSource Window() => ShellSource.Load("Settings.SettingsWindow.xaml.cs");

    private static ShellSource Page() => ShellSource.Load("Settings.Pages.RawEditorPage.xaml.cs");

    [Fact]
    public void The_close_is_intercepted_where_the_page_is_torn_down()
    {
        var window = Window();

        // AppWindow.Closing, not Window.Closed: Closed is after the page is
        // gone, so a prompt there could not cancel anything.
        Assert.NotEmpty(window.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Closing")
                        && a.Right.ToString().EndsWith("OnClosing")));

        var closing = window.Method("OnClosing");

        // A property, not a call: the page exposes the question, the window
        // asks it. Read as a member access so a rewrite to a method (or to a
        // hardcoded true) moves this.
        var asked = closing.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(m => m.Name.Identifier.ValueText == "HasUnsavedChanges");
        var cancel = closing.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Single(m => m.Name.Identifier.ValueText == "Cancel"
                        && m.Expression.ToString() == "args");

        Assert.True(
            asked.SpanStart < cancel.SpanStart,
            "the close is cancelled before the page is asked, so unsaved text "
            + "is discarded without a word");

        // Window.Closed is where the page cache is dropped; the question has
        // to be asked before that, which the Closing intercept is.
        var closed = window.Method("OnClosed");
        Assert.NotEmpty(closed.Body!.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression.ToString().EndsWith("Clear")));
    }

    [Fact]
    public void Only_a_raw_editor_buffer_can_hold_unsaved_text()
    {
        var closing = Window().Method("OnClosing");

        Assert.Contains("RawEditorPage", closing.ToString(), System.StringComparison.Ordinal);

        // The page comes out of the window's own cache, which is the only
        // instance whose buffer the user has been typing into: a fresh page
        // would report no unsaved changes and the close would proceed.
        Assert.Equal(
            "_pageCache.TryGetValue",
            Window().Method("UnsavedRawEditorPage").Call("_pageCache.TryGetValue").CalleeText());
    }

    /// <summary>
    /// The failed save is the case a mutation inverts quietly: closing then
    /// would be the same silent loss the prompt exists to prevent, one step
    /// later, with the text gone from both the editor and the file.
    /// </summary>
    [Fact]
    public void A_save_that_did_not_land_keeps_the_window_open()
    {
        var prompt = Window().Method("PromptUnsavedRawEditorAsync");

        var save = prompt.CallEndingWith("SaveNow");
        var arm = prompt.AssignsTo("_closeConfirmed").Single();

        Assert.True(
            save.SpanStart < arm.SpanStart,
            "the close is re-armed without asking whether the save landed");

        // The decision itself is Core's (UnsavedRawEditPromptTests), and this
        // is the call site that has to route through it: a local re-statement
        // of the rule is a second copy to drift.
        var decide = prompt.Call("UnsavedRawEditPrompt.ShouldClose");
        Assert.True(
            decide.SpanStart < arm.SpanStart,
            "the close is re-armed before the answer is consulted");

        // And the answer of a Save is the save's outcome, not a constant.
        Assert.Equal("saved", decide.Arg(1));
        Assert.Equal("answer", decide.Arg(0));
    }

    [Fact]
    public void Only_one_prompt_is_ever_up()
    {
        // WinUI allows one ContentDialog at a time; a second ShowAsync on top
        // of the first throws out of the async state machine.
        var prompt = Window().Method("PromptUnsavedRawEditorAsync");

        Assert.NotNull(prompt.CallEndingWith("AskAboutUnsavedRawEditorAsync"));
        Assert.NotEmpty(prompt.AssignsTo("_rawEditorPromptOpen"));
    }

    [Fact]
    public void The_prompt_does_not_default_to_the_answer_that_loses_text()
    {
        var ask = Window().Method("AskAboutUnsavedRawEditorAsync");

        var dialog = ask.Body!.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
            .Single(o => o.Type.ToString() == "ContentDialog");

        var assign = dialog.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Single(a => a.Left.ToString() == "DefaultButton");
        Assert.Equal(
            "ContentDialogButton.Close",
            assign.Right.ToString());
    }

    [Fact]
    public void The_page_reports_unsaved_text_against_what_is_on_disk()
    {
        // RefreshFromDiskIfPristine can move _lastLoadedText under the user
        // when the file changes externally, so this compares against the
        // current text rather than latching a flag at edit time.
        var property = Page().Property("HasUnsavedChanges");

        Assert.NotNull(property.ExpressionBody);
        Assert.Equal(
            "GetEditorText() != _lastLoadedText",
            property.ExpressionBody!.Expression.ToString());
    }
}

internal static class RawEditorPageQueries
{
    /// <summary>The one property with this name in the file.</summary>
    public static PropertyDeclarationSyntax Property(
        this ShellSource source, string name)
    {
        var found = source.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.ValueText == name)
            .ToList();
        Assert.True(found.Count == 1, $"expected one property named '{name}', found {found.Count}");
        return found[0];
    }
}
