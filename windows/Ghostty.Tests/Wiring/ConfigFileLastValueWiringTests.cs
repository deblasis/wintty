using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The settings chrome read its Windows-only keys first-wins while every other
/// reader in the app was last-wins: libghostty applies the last line, and the
/// settings writer rewrites the last occurrence (<c>ConfigFileParser.SetValue</c>
/// -> <c>FindLastUncommented</c>). A config with two <c>theme</c> lines
/// therefore painted the window from the first palette and the panes from the
/// second, and the reader's own documentation on the other side of the Core
/// boundary already claimed the value it did not return.
/// <para>
/// The rule itself is <c>ConfigIniFile</c>'s and is tested there; what is
/// pinned here is that the service's readers ask for it. Wiring guards,
/// because the shell assembly cannot be loaded into a test host.
/// </para>
/// </summary>
public sealed class ConfigFileLastValueWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    [Fact]
    public void The_file_value_read_asks_for_the_last_value()
    {
        var read = ConfigService().Method("GetFileValue");

        // (file, key, default) -- the cache, the key asked about, and the
        // caller's fallback. All three are named so a reader cannot quietly
        // drop the default or read the wrong cache.
        var last = read.Call("ConfigIniFile.Last");
        Assert.Equal("_configFileCache", last.Arg(0));
        Assert.Equal("key", last.Arg(1));
        Assert.Equal("defaultValue", last.Arg(2));
    }

    [Fact]
    public void The_presence_read_asks_for_the_last_value_too()
    {
        // frame-style is the one reader whose meaning rides on presence: an
        // unset key inherits the backdrop. A reader that answered from the
        // first line could not tell a reset from a configured value.
        var read = ConfigService().Method("TryGetFileValue");

        var tryLast = read.Call("ConfigIniFile.TryLast");
        Assert.Equal("_configFileCache", tryLast.Arg(0));
        Assert.Equal("key", tryLast.Arg(1));
    }

    [Fact]
    public void The_active_theme_read_asks_for_the_last_value()
    {
        var read = ConfigService().Method("GetActiveThemeValue");

        var tryLast = read.Call("ConfigIniFile.TryLast");
        Assert.Equal("_activeThemeFileCache", tryLast.Arg(0));
        Assert.Equal("key", tryLast.Arg(1));
    }

    /// <summary>
    /// The readers above are the ones a caller reaches, but the defect was
    /// also spread over inline <c>list[0]</c> reads of the same caches, and a
    /// new one would read just like the code this replaced. Pinned across the
    /// whole file so the next reader has to be written through
    /// <c>ConfigIniFile</c>.
    /// </summary>
    [Fact]
    public void No_reader_indexes_a_first_value_of_its_own()
    {
        var firstValueReads = ConfigService().Root.DescendantNodes()
            .OfType<ElementAccessExpressionSyntax>()
            .Where(e => e.Expression.ToString().EndsWith("list", System.StringComparison.Ordinal)
                        || e.Expression.ToString().EndsWith("values", System.StringComparison.Ordinal))
            .Where(e => e.ArgumentList.Arguments.Count == 1
                        && e.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax
                        && e.ArgumentList.Arguments[0].ToString() == "0")
            .Select(e => e.ToString())
            .ToList();

        Assert.True(
            firstValueReads.Count == 0,
            "first-value reads left in ConfigService: " + string.Join(", ", firstValueReads)
                + " -- config keys are last-wins (see ConfigIniFile.Last)");
    }
}
