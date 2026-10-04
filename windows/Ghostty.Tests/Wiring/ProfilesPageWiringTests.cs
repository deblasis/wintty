using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// "UNGUARDED CONFIG WRITES - ProfilesPage.xaml.cs ~143-151
/// (WriteBoolToggle), ~622-627, ~629-637 use the raw
/// SuppressWatcher/SetValue/Reload idiom with try/finally and NO catch, so
/// an IOException fails the process out of a routed event - the exact class
/// SettingsConfigWriter exists to prevent. Route all three through the
/// page's _writer.Write."
///
/// <para>
/// The three handlers this page has -- the hidden toggle, the icon pick and
/// the tracks-foreground toggle -- already call <c>_writer.Write</c>, so
/// these guards arrive green. They are here so it stays that way: the
/// failure they describe is a process kill, and the idiom that causes it is
/// shorter and reads fine, so the next writer on this page writes it.
/// </para>
///
/// <para>
/// These are call-site guards, not behaviour tests. That a caught
/// <see cref="System.IO.IOException"/> leaves the process running and the
/// file alone is <c>Config.SettingsConfigWriterTests</c>'s claim; what is
/// pinned here is that this page reaches the editor only through the writer
/// that makes it.
/// </para>
/// </summary>
public class ProfilesPageWiringTests
{
    // Every IConfigFileEditor mutation this page could make.
    private static readonly string[] s_editorMutations =
    {
        "_editor.SetValue",
        "_editor.RemoveValue",
        "_editor.WriteRaw",
        "_editor.SetRepeatableValues",
    };

    // The three handlers the brief names are the Theory cases below: two
    // routed and one left raw is the state this page was in for one handler
    // at a time, and each of those states passes a whole-file scan.

    private static ShellSource Page() => ShellSource.Load("Settings.Pages.ProfilesPage.xaml.cs");

    private static InvocationExpressionSyntax[] EditorWrites(SyntaxNode node)
        => node.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => s_editorMutations.Contains(i.CalleeText()))
            .ToArray();

    private static void AssertRouted(InvocationExpressionSyntax[] writes)
    {
        Assert.NotEmpty(writes);
        foreach (var write in writes)
        {
            Assert.True(
                write.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(i => i.CalleeText() == "_writer.Write"),
                $"{write.CalleeText()} is not inside a _writer.Write: the writer is what "
                + "catches the IOException and UnauthorizedAccessException this idiom leaves "
                + "to propagate out of the routed-event handler");
        }
    }

    [Theory]
    [InlineData("OnHiddenToggled")]
    [InlineData("OnProfileIconChanged")]
    [InlineData("OnTracksForegroundToggled")]
    public void Every_row_writer_on_the_page_goes_through_the_shared_writer(string handler)
    {
        AssertRouted(EditorWrites(Page().Method(handler)));
    }

    [Fact]
    public void No_config_write_anywhere_on_the_page_bypasses_the_shared_writer()
    {
        // The scan the three cases above are a readable sample of. Not
        // "assert none": the page is supposed to write config, and a page
        // that stopped writing would pass an emptiness check while breaking
        // every control on it.
        AssertRouted(EditorWrites(Page().Root));
    }

    [Fact]
    public void Every_editor_call_the_page_makes_is_one_these_guards_know()
    {
        // The list above is a census, and a census is only worth anything if
        // it cannot go stale silently: a new IConfigFileEditor method used
        // here and not listed there would pass every other test in this
        // file, which is exactly the edit this guard exists to catch.
        var unknown = Page().Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.CalleeText())
            .Where(c => c.StartsWith("_editor.", StringComparison.Ordinal))
            .Where(c => !s_editorMutations.Contains(c))
            .Distinct()
            .ToList();

        Assert.Empty(unknown);
    }

    [Fact]
    public void The_page_never_balances_the_watcher_or_reloads_itself()
    {
        // Both belong to the writer: it balances the flag in a finally and
        // reloads after the write, whether or not the write took. A page
        // that reloads on its own reloads twice on a watched write, and one
        // that balances the flag itself can leave the watcher suppressed for
        // the rest of the session when the write throws.
        var page = Page();

        Assert.Empty(page.Root.Calls("_configService.SuppressWatcher"));
        Assert.Empty(page.Root.Calls("_configService.Reload"));
    }
}
