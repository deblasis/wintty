using System;
using Ghostty.Core.JumpList;
using Xunit;

namespace Ghostty.Tests.JumpList;

public class JumpListLaunchTests
{
    [Fact]
    public void Parse_EmptyArgs_IsNone()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe"]);
        Assert.Equal(JumpListAction.None, launch.Action);
        Assert.Null(launch.ProfileId);
    }

    [Fact]
    public void Parse_NewWindowTask()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe", "--jumplist-action=new-window"]);
        Assert.Equal(JumpListAction.NewWindow, launch.Action);
        Assert.Null(launch.ProfileId);
    }

    [Fact]
    public void Parse_NewTabTask()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe", "--jumplist-action=new-tab"]);
        Assert.Equal(JumpListAction.NewTab, launch.Action);
        Assert.Null(launch.ProfileId);
    }

    [Fact]
    public void Parse_ProfileId_ImpliesNewWindow()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe", "--jumplist-profile=pwsh"]);
        Assert.Equal(JumpListAction.NewWindow, launch.Action);
        Assert.Equal("pwsh", launch.ProfileId);
    }

    [Fact]
    public void Parse_ProfilePlusNewTab_KeepsNewTab()
    {
        var launch = JumpListLaunch.Parse(
            ["Wintty.exe", "--jumplist-action=new-tab", "--jumplist-profile=cmd"]);
        Assert.Equal(JumpListAction.NewTab, launch.Action);
        Assert.Equal("cmd", launch.ProfileId);
    }

    [Fact]
    public void Parse_UnknownAction_IsNone()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe", "--jumplist-action=explode"]);
        Assert.Equal(JumpListAction.None, launch.Action);
    }

    [Fact]
    public void Parse_EmptyProfileValue_IsIgnored()
    {
        var launch = JumpListLaunch.Parse(["Wintty.exe", "--jumplist-profile="]);
        Assert.Null(launch.ProfileId);
        Assert.Equal(JumpListAction.None, launch.Action);
    }

    [Fact]
    public void Parse_NullArgs_IsNone()
    {
        var launch = JumpListLaunch.Parse(null);
        Assert.Equal(JumpListAction.None, launch.Action);
        Assert.Null(launch.ProfileId);
    }
}
