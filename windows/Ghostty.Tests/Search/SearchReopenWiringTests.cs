using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Search;

/// <summary>
/// Closing the search bar ends the search inside libghostty but keeps the
/// needle, so a reopen has to start it again or the bar shows a query with
/// nothing behind it: no highlights, and next/previous inert. The decision
/// lives in WinUI code that this assembly cannot instantiate, so these read
/// the source the same way <c>PaletteOpenSearchTests</c> does.
///
/// This proves the call sites exist, not that they run. It is insurance
/// against a later edit quietly deleting them; the behaviour itself is
/// covered by <c>windows/scripts/search-fuzz.ps1</c>, whose reopen assertion
/// is what caught the bug in the first place.
/// </summary>
public class SearchReopenWiringTests
{
    [Fact]
    public void OpenSearch_reissues_the_surviving_needle()
    {
        var source = ReadSource("TerminalControl.xaml.cs");
        var body = Body(source, "internal void OpenSearch()");

        Assert.Contains("ReissueSearch()", body);
        // Only on the closed -> open transition: re-issuing onto a live
        // search would otherwise rely on libghostty ignoring an unchanged
        // needle, which is its implementation detail rather than a contract.
        Assert.Contains("wasOpen", body);
    }

    [Fact]
    public void Closing_the_bar_invalidates_the_counts()
    {
        var source = ReadSource("TerminalControl.xaml.cs");
        var body = Body(source, "private void OnSearchClosed(");

        Assert.Contains("MarkInactive()", body);
    }

    /// <summary>
    /// The counts must NOT be invalidated from the action callback: it
    /// arrives through the dispatcher, so it is the wrong place to reason
    /// about ordering from, and the teardown's null total already covers it.
    /// </summary>
    [Fact]
    public void OnSearchEnded_does_not_invalidate_the_counts()
    {
        var source = ReadSource("TerminalControl.xaml.cs");
        var body = Body(source, "internal void OnSearchEnded()");

        Assert.DoesNotContain("MarkInactive()", body);
    }

    /// <summary>
    /// libghostty's start_search reports an empty needle. Adopting it would
    /// clear the box and cancel the search a debounce later.
    /// </summary>
    [Fact]
    public void OnSearchStarted_ignores_an_empty_needle()
    {
        var source = ReadSource("TerminalControl.xaml.cs");
        var body = Body(source, "internal void OnSearchStarted(");

        Assert.Contains("needle.Length == 0", body);
    }

    /// <summary>
    /// WinUI raises Unloaded on every reparent, not just on teardown, and a
    /// pane is reparented on split, zoom, unzoom and sibling close. When the
    /// debounce Tick was dropped on Unloaded and never re-attached, typing a
    /// needle stopped starting searches after the first split.
    /// </summary>
    [Fact]
    public void The_debounce_tick_is_reattached_when_the_control_reloads()
    {
        var source = ReadSource("SearchBarControl.xaml.cs");

        Assert.Contains("Loaded += OnControlLoaded", source);
        var loaded = Body(source, "private void OnControlLoaded(");
        Assert.Contains("_debounceTimer.Tick += OnDebounceTick", loaded);

        // Dropping the Unloaded subscription would strand the control after
        // its second detach.
        var unloaded = Body(source, "private void OnControlUnloaded(");
        Assert.DoesNotContain("Unloaded -=", unloaded);
    }

    private static string ReadSource(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The text from a signature to the closing brace of its body, so an
    /// assertion cannot be satisfied by a match somewhere else in the file.
    /// Brace counting starts at the first brace after the signature.
    /// </summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"signature not found: {signature}");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body for: {signature}");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return source.Substring(open, i - open + 1);
            }
        }

        Assert.Fail($"unterminated body for: {signature}");
        return string.Empty;
    }
}
