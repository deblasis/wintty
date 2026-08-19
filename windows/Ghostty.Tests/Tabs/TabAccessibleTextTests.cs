using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ghostty.Core;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// Every tab in the vertical strip announced itself as
/// "Ghostty.Tabs.VerticalTabNavRow": nothing named the
/// NavigationViewItem, so its peer fell back to the content's ToString,
/// and the content is a panel. The horizontal strip failed the other
/// way: a TabViewItem gets no name out of a StackPanel header, so its
/// tabs announced nothing at all. A listener could hear how many tabs
/// were open and nothing else about any of them.
///
/// The text itself is pure and tested directly. The wiring that puts it
/// on the item lives in WinUI types this project cannot reference, so it
/// is scanned out of the shipped source -- the same fallback
/// StripCloseAutomationTests uses.
/// </summary>
public class TabAccessibleTextTests
{
    [Fact]
    public void Name_IsTheTitleTheUserSees()
    {
        Assert.Equal("pwsh", TabAccessibleText.Name("pwsh"));
        Assert.Equal("vim README.md", TabAccessibleText.Name("vim README.md"));
    }

    /// <summary>
    /// An empty name is not a blank name: the peer treats it as "nobody
    /// named this" and goes back to the type name, which is the bug. A
    /// shell that reports an empty OSC 2 title, or one that reports a run
    /// of spaces, has to land on something a listener can hear.
    ///
    /// TabModel.EffectiveTitle now coalesces whitespace itself, so no
    /// live caller can reach this any more. It stays as the helper's own
    /// floor: the whole point of the class is that the name is never
    /// empty, and that should not depend on a caller getting it right.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyTitles_FallBackToAName(string? title)
    {
        Assert.Equal(AppIdentity.ProductName, TabAccessibleText.Name(title));
    }

    /// <summary>
    /// Titles are not trimmed or rewritten. What the strip renders and
    /// what it announces have to be the same string, or a user reading
    /// over someone's shoulder and a user listening are looking for
    /// different tabs.
    /// </summary>
    [Fact]
    public void Name_PassesTitlesThroughUntouched()
    {
        Assert.Equal(" pwsh ", TabAccessibleText.Name(" pwsh "));
        Assert.Equal("C:\\src -- build", TabAccessibleText.Name("C:\\src -- build"));
    }

    /// <summary>
    /// The tab overloads exist so a caller cannot quietly pass the wrong
    /// title. ShellReportedTitle compiles just as well as EffectiveTitle
    /// in the string overload and is wrong: it ignores a rename.
    /// </summary>
    [Fact]
    public void TabOverloads_ReadTheEffectiveTitle()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.ShellReportedTitle = "vim file.txt";
        tab.UserOverrideTitle = "renamed";

        Assert.Equal("renamed", TabAccessibleText.Name(tab));
        Assert.Equal(string.Empty, TabAccessibleText.Status(tab));

        tab.BellRinging = true;
        Assert.Equal("Bell", TabAccessibleText.Status(tab));
    }

    /// <summary>
    /// The bell is state, not identity, so it rides ItemStatus and the
    /// name stays put across a ring and an acknowledge.
    /// </summary>
    [Fact]
    public void Bell_IsStatus_AndLeavesTheNameAlone()
    {
        Assert.Equal("Bell", TabAccessibleText.Status(bellRinging: true));
        Assert.Equal(string.Empty, TabAccessibleText.Status(bellRinging: false));
        Assert.Equal("pwsh", TabAccessibleText.Name("pwsh"));
    }

    /// <summary>
    /// Acknowledging a bell has to clear the status. Returning null or
    /// leaving the previous value in place would leave a tab reading
    /// "Bell" long after the bell was answered.
    /// </summary>
    [Fact]
    public void AcknowledgedBell_ClearsTheStatus()
    {
        var status = TabAccessibleText.Status(bellRinging: false);
        Assert.NotNull(status);
        Assert.Equal(string.Empty, status);
    }

    /// <summary>
    /// The announcement has to say which tab rang; the user is not
    /// looking at the strip, which is why it is being spoken at all.
    /// </summary>
    [Fact]
    public void BellAnnouncement_NamesTheTab()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.ShellReportedTitle = "make -j8";

        Assert.Equal("Bell in make -j8", TabAccessibleText.BellAnnouncement(tab));
    }

    /// <summary>
    /// The vertical strip is where this was reported. The name has to be
    /// on the NavigationViewItem, not on the row inside it: the item is
    /// what surfaces as the ListItem.
    /// </summary>
    [Fact]
    public void VerticalStrip_NamesTheNavItem()
    {
        var strip = Source("VerticalTabStrip.xaml.cs");
        var apply = MethodBody(strip, "private static void ApplyItemTitleChrome");

        Assert.Contains("AutomationProperties.SetName(item, TabAccessibleText.Name(", apply);
        Assert.Contains("AutomationProperties.SetItemStatus(item, TabAccessibleText.Status(", apply);
    }

    /// <summary>
    /// Naming the item once at build time is half a fix: a renamed tab,
    /// or one whose shell reported a new title, keeps the name it was
    /// born with. The title binding already re-applies the row text and
    /// the tooltip, so it is the one place that has to carry the name too.
    /// </summary>
    [Fact]
    public void VerticalStrip_RenamesTheNavItemWhenTheTitleChanges()
    {
        var strip = Source("VerticalTabStrip.xaml.cs");

        var build = Between(strip, "var item = new NavigationViewItem", "var textBinding");
        Assert.Contains("ApplyItemTitleChrome(item, tab)", build);

        var binding = Between(strip, "var textBinding = AotBinding.Create", "var colorBinding");
        Assert.Contains("ApplyItemTitleChrome(navItem, tab)", binding);
        Assert.Contains("nameof(TabModel.EffectiveTitle)", binding);
        Assert.Contains("nameof(TabModel.BellRinging)", binding);
    }

    /// <summary>
    /// The horizontal strip has the same hole with a different symptom:
    /// its tabs came out unnamed rather than identically named. Both
    /// layouts run against one TabManager, so a fix in only one of them
    /// is a fix the user loses by switching layout.
    /// </summary>
    [Fact]
    public void HorizontalStrip_NamesTheTabViewItem()
    {
        var host = Source("TabHost.xaml.cs");
        var apply = MethodBody(host, "private static void ApplyItemAccessibleText");

        Assert.Contains("AutomationProperties.SetName(item, TabAccessibleText.Name(", apply);
        Assert.Contains("AutomationProperties.SetItemStatus(item, TabAccessibleText.Status(", apply);
    }

    /// <summary>
    /// Build, rename and bell all have to reach the write, and the
    /// property handler is a chain of else-if arms, so each arm is its
    /// own call site. Asserted arm by arm rather than by counting the
    /// calls in the method: a count ties no call to any arm, so moving
    /// the bell arm's call up into the title arm keeps the total at three
    /// while a tab that rings once reads "Bell" for the rest of its life.
    /// </summary>
    [Fact]
    public void HorizontalStrip_RenamesTheItemOnBuildRenameAndBell()
    {
        var host = Source("TabHost.xaml.cs");
        const string call = "ApplyItemAccessibleText(item, tab)";

        var build = Between(host, "var item = new TabViewItem", "tab.PropertyChanged +=");
        Assert.Contains(call, build);

        var titleArm = Between(host, "headerText.Text = tab.EffectiveTitle", "else if");
        Assert.Contains(call, titleArm);

        var bellArm = Between(host, "bellGlyph.Visibility = tab.BellRinging", "_itemByModel[tab] = item");
        Assert.Contains(call, bellArm);
    }

    /// <summary>
    /// The announcement is raised once per window, not once per strip.
    /// Both strips are alive at once and both watch the same TabModel, so
    /// a strip-level raise would speak every bell twice.
    ///
    /// This only proves the wiring exists; when it fires is covered
    /// directly by TabBellAnnouncerTests.
    /// </summary>
    [Fact]
    public void Window_RaisesTheBellAnnouncement()
    {
        var window = Source("MainWindow.xaml.cs");

        Assert.Contains("new TabBellAnnouncer(", window);
        Assert.Contains("UiaAnnouncer.Announce(", window);
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{start}' not found");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{end}' not found after '{start}'");
        return source[from..to];
    }

    /// <summary>
    /// One method, ending at its own closing brace rather than at
    /// whatever declaration happens to follow it. A window that runs to
    /// the next declaration also covers the gap between the two, so
    /// gutting the method and dropping its contents into a new one below
    /// would still satisfy every assertion made against it.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var from = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(from >= 0, $"'{signature}' not found");
        // Members sit at one indent level, so the first line that closes
        // at that level closes this method.
        var to = source.IndexOf("\n    }", from, StringComparison.Ordinal);
        Assert.True(to > from, $"'{signature}' has no closing brace");
        return source[from..to];
    }

    /// <summary>
    /// Read the shipped source with every comment removed, so no assertion
    /// here can be satisfied by prose naming the same thing.
    /// </summary>
    private static string Source(string suffix) => StripComments(ReadEmbedded(suffix));

    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var lines = withoutBlocks.Split('\n').Select(line =>
        {
            var slash = line.IndexOf("//", StringComparison.Ordinal);
            return slash >= 0 ? line[..slash] : line;
        });
        return string.Join('\n', lines);
    }

    /// <summary>
    /// These ride the Ghostty\**\*.cs glob that MarshalComplianceTests
    /// declares; they need no entry of their own.
    ///
    /// A plain EndsWith is not enough here: "TabHost.xaml.cs" is also the
    /// tail of "VerticalTabHost.xaml.cs", and the two are the horizontal
    /// and vertical halves of this very fix. Requiring a non-identifier
    /// character in front of the suffix separates them without assuming
    /// which separator the host substituted for the source folder.
    /// </summary>
    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.Length > suffix.Length
                && n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && !char.IsLetterOrDigit(n[n.Length - suffix.Length - 1]));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
