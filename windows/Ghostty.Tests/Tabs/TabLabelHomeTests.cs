using System;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The user's own directory reads as "~" on every tab surface, the way the
/// shells it hosts already write it. Display only: the directory a tab
/// respawns in is still the one the shell reported.
/// </summary>
public class TabLabelHomeTests
{
    private const string Home = @"C:\Users\alex";

    [Theory]
    [InlineData(@"C:\Users\alex", "~")]
    [InlineData(@"C:\Users\alex\", "~")]
    [InlineData(@"C:\Users\alex\src\repo", @"~\src\repo")]
    [InlineData(@"c:\users\ALEX\src", @"~\src")]
    [InlineData("C:/Users/alex/src", "~/src")]
    [InlineData(@"\\?\C:\Users\alex\src", @"~\src")]
    [InlineData(@"C:\Users\alex\src\", @"~\src")]
    public void Collapse_ReplacesTheHomePrefixWithATilde(string cwd, string expected)
        => Assert.Equal(expected, TabLabel.Collapse(cwd, Home));

    [Theory]
    [InlineData(@"C:\Users\alex\")]
    [InlineData("C:/Users/alex")]
    [InlineData(@"\\?\C:\Users\alex")]
    public void Collapse_ReadsTheHomeInAnySpelling(string home)
        => Assert.Equal(@"~\src", TabLabel.Collapse(@"C:\Users\alex\src", home));

    [Fact]
    public void Collapse_TreatsAShareAsAHomeLikeAnyOther()
        => Assert.Equal(@"~\src", TabLabel.Collapse(@"\\server\profiles\alex\src", @"\\server\profiles\alex"));

    [Theory]
    [InlineData(@"C:\Users\alexandra\src")]
    [InlineData(@"C:\Users\alex2")]
    [InlineData(@"D:\Users\alex\src")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\alex")]
    [InlineData(@"C:\temp\wintty-fx-cwd")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\ale")]
    [InlineData(@"\\?\UNC\server\share\alex")]
    public void Collapse_LeavesEveryOtherDirectoryAlone(string cwd)
        => Assert.Equal(cwd, TabLabel.Collapse(cwd, Home));

    [Theory]
    [InlineData("C:\\Users\\alex\ndel *")]
    [InlineData("C:\\Users\\alex\\\u202Esdaolnwod")]
    [InlineData("C:\\x\u0085y")]
    [InlineData("C:\\x\u2028y")]
    [InlineData("C:\\x\u007Fy")]
    public void APathAProgramSmuggledCharactersInto_IsNotPlain_AndNotShown(string cwd)
    {
        Assert.False(TabLabel.IsPlain(cwd));
        Assert.Null(TabLabel.Collapse(cwd, Home));
    }

    [Theory]
    [InlineData(@"C:\Users\alex")]
    [InlineData(@"C:\Users\álex\Ünïcode")]
    [InlineData("C:\\Users\\alex\\\U0001F4C1 docs")]
    [InlineData("C:\\Users\\alex\\\U0001F468\u200D\U0001F469\u200D\U0001F467")]
    public void AnOrdinaryFolderName_IsPlain(string cwd)
        => Assert.True(TabLabel.IsPlain(cwd));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    public void Collapse_WithoutAUsableHome_IsTheDirectoryItself(string? home)
        => Assert.Equal(@"C:\Users\alex\src", TabLabel.Collapse(@"C:\Users\alex\src", home));

    [Fact]
    public void Collapse_OfNothing_IsNull()
    {
        Assert.Null(TabLabel.Collapse(null, Home));
        Assert.Null(TabLabel.Collapse("   ", Home));
    }

    [Fact]
    public void ATabSittingInHome_ReadsAsTilde()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = Home;
        Assert.Equal("~", tab.EffectiveTitle);

        // And a folder under it is still named by its leaf.
        tab.ShellReportedCwd = @"C:\Users\alex\src\repo";
        Assert.Equal("repo", tab.EffectiveTitle);
    }

    [Fact]
    public void WithoutAHome_TheLabelIsTheFolderName()
    {
        var tab = new TabModel(new FakePaneHost()) { ShellReportedCwd = Home };
        Assert.Equal("alex", tab.EffectiveTitle);
    }

    [Fact]
    public void TheActionableDirectory_IsTheReportedOne_NeverTheCollapsedForm()
    {
        // Copy and Open act on this; cmd does not expand a tilde.
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\src";
        Assert.Equal(@"~\src", tab.TooltipText);
        Assert.Equal(@"C:\Users\alex\src", tab.ActionableCwd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("C:\\Users\\alex\ndel *")]
    [InlineData(@"\\wintty-unc-refused.invalid\share")]
    public void NothingActsOnADirectory_TheTabWouldNotSpawnInto(string? cwd)
    {
        var tab = new TabModel(new FakePaneHost()) { ShellReportedCwd = cwd };
        Assert.Null(tab.ActionableCwd);
    }

    [Fact]
    public void TheManager_HandsEveryTabItsHome()
    {
        var host = new FakePaneHost();
        var mgr = new TabManager(_ => host, homeDirectory: Home);
        Assert.Equal(Home, mgr.Tabs[0].HomeDirectory);
        Assert.Equal(Home, mgr.NewTab().HomeDirectory);

        var adopted = new TabModel(new FakePaneHost());
        mgr.AdoptTab(adopted);
        Assert.Equal(Home, adopted.HomeDirectory);
    }

    [Fact]
    public void TheManagersDefaultHome_IsTheUsersProfile()
    {
        var mgr = new TabManager(_ => new FakePaneHost());
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            mgr.Tabs[0].HomeDirectory);
    }

    [Fact]
    public void AssistiveClients_HearHome_NotTilde()
        => Assert.Equal("Home", TabAccessibleText.Name("~"));
}
