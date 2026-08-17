using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.JumpList;

/// <summary>
/// JumpListBuilder already knows how to pin profiles. App used to pass
/// Array.Empty so the taskbar never showed them, and never parsed the
/// --jumplist-* argv the builder emits. These pins keep both halves live.
/// </summary>
public class JumpListAppWiringTests
{
    [Fact]
    public void App_MapsRegistryProfilesIntoTheJumpList()
    {
        var source = ReadEmbedded("App.xaml.cs");
        Assert.DoesNotContain(
            "profilesProvider: () => System.Array.Empty<Ghostty.Core.JumpList.ProfileEntry>()",
            source);
        Assert.Contains("JumpListProfiles.From", source);
        Assert.Contains("ProfilesChanged", source);
    }

    [Fact]
    public void App_NewTabDoesNotFallThroughToNewWindow()
    {
        var app = ReadEmbedded("App.xaml.cs");
        var main = ReadEmbedded("MainWindow.xaml.cs");
        // Live fuzz: --jumplist-action=new-tab opened another window
        // because missing DefaultProfileId made TryOpenJumpListTab
        // return false and OpenWindowFromLaunch fell through to
        // OpenJumpListWindow. NewTab must add a tab on an existing
        // window even when no profile id resolves.
        Assert.Contains("OpenJumpListTab", app);
        Assert.Contains("internal void OpenJumpListTab", main);
        Assert.Contains("_tabManager.NewTab()", main);
    }

    [Fact]
    public void App_ParsesJumpListLaunchArgs()
    {
        var source = ReadEmbedded("App.xaml.cs");
        Assert.Contains("JumpListLaunch.Parse", source);
        Assert.Contains("JumpListAction.NewTab", source);
        Assert.Contains("JumpListAction.NewWindow", source);
    }

    [Fact]
    public void App_HonorsJumpListArgvOnColdStart()
    {
        var source = ReadEmbedded("App.xaml.cs");
        Assert.Contains("Environment.GetCommandLineArgs()", source);
        Assert.Contains("HandleColdStartJumpList", source);
        Assert.Contains("honorJumpList", source);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
