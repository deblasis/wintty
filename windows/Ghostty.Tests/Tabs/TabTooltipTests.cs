using Ghostty.Core;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// What a pointer resting on a tab, or on its icon, is told. The label is
/// the folder's leaf; the tooltip is where the whole directory lives, and
/// the icon's tooltip is where the shell is named.
/// </summary>
public class TabTooltipTests
{
    private const string Home = @"C:\Users\alex";

    private static ProfileSnapshot Profile(string name, string command) =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "p", Name: name, Command: command,
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("pwsh"),
                TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    [Fact]
    public void TheTooltip_IsTheWholeDirectory_WhenTheLabelIsItsLeaf()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\src\repo";

        Assert.Equal("repo", tab.EffectiveTitle);
        Assert.Equal(@"~\src\repo", tab.TooltipText);
    }

    [Fact]
    public void TheTooltip_AtHome_IsTheRealDirectory()
    {
        // The label already shows the one glyph; the hover is where the
        // directory it stands for belongs.
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = Home;
        Assert.Equal("~", tab.EffectiveTitle);
        Assert.Equal(Home, tab.TooltipText);
    }

    [Fact]
    public void TheTooltip_IsTheDirectoryAlone_UnderTheConsolesDefaultTitle()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\src\repo";
        tab.ShellReportedTitle = @"C:\Program Files\PowerShell\7\pwsh.exe";
        Assert.Equal(@"~\src\repo", tab.TooltipText);
    }

    [Fact]
    public void TheTooltip_PutsTheTitleAboveTheDirectory_WhenBothAreKnown()
    {
        var tab = new TabModel(new FakePaneHost()) { HomeDirectory = Home };
        tab.ShellReportedCwd = @"C:\Users\alex\src\repo";

        tab.ShellReportedTitle = "vim build.zig";
        Assert.Equal("vim build.zig\n~\\src\\repo", tab.TooltipText);

        tab.UserOverrideTitle = "deploy";
        Assert.Equal("deploy\n~\\src\\repo", tab.TooltipText);
    }

    [Fact]
    public void TheTooltip_IsTheLabel_WhenNoDirectoryIsKnown()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.Equal(AppIdentity.ProductName, tab.TooltipText);

        tab.UserOverrideTitle = "deploy";
        Assert.Equal("deploy", tab.TooltipText);

        // The console's exe-path default is not a title on the tooltip
        // either; the tooltip tells the truth the label tells.
        tab.UserOverrideTitle = null;
        tab.ShellReportedTitle = @"C:\Program Files\PowerShell\7\pwsh.exe";
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));
        Assert.Equal("Primary", tab.TooltipText);
    }

    [Fact]
    public void TheTooltip_IsRaisedOnce_ByEachOfItsInputs_AndNotByARepeat()
    {
        var tab = new TabModel(new FakePaneHost());
        var raised = 0;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabModel.TooltipText)) raised++;
        };

        tab.ShellReportedCwd = @"C:\src\repo";
        tab.ShellReportedTitle = "vim x.zig";
        tab.UserOverrideTitle = "deploy";
        tab.HomeDirectory = Home;
        Assert.Equal(4, raised);

        // The equality guards hold for the new property as for the old ones.
        tab.HomeDirectory = Home;
        tab.ShellReportedCwd = @"C:\src\repo";
        Assert.Equal(4, raised);
    }

    [Fact]
    public void TheIconTooltip_NamesTheShell_NotTheProfile()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", @"""C:\Program Files\PowerShell\7\pwsh.exe"""));
        Assert.Equal("PowerShell\nPrimary", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void TheIconTooltip_SaysTheShellOnce_WhenTheProfileIsNamedAfterIt()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Command Prompt", "cmd.exe"));
        Assert.Equal("Command Prompt", tab.TabIcon.TooltipText);

        var pwsh = new TabModel(new FakePaneHost());
        pwsh.AttachProfileSnapshot(Profile("PowerShell 7", "pwsh.exe"));
        Assert.Equal("PowerShell 7", pwsh.TabIcon.TooltipText);

        // And the other way round: a profile named by the exe adds nothing
        // to the shell's name.
        var terse = new TabModel(new FakePaneHost());
        terse.AttachProfileSnapshot(Profile("pwsh", "pwsh.exe"));
        Assert.Equal("PowerShell", terse.TabIcon.TooltipText);
    }

    [Fact]
    public void TheIconTooltip_NamesTheDistro_ForAWslProfile()
    {
        // The distro name says everything the profile name "Ubuntu" says.
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Ubuntu", "wsl.exe -d Ubuntu-24.04"));
        Assert.Equal("WSL: Ubuntu-24.04", tab.TabIcon.TooltipText);

        var work = new TabModel(new FakePaneHost());
        work.AttachProfileSnapshot(Profile("Work", "wsl.exe -d Ubuntu-24.04"));
        Assert.Equal("WSL: Ubuntu-24.04\nWork", work.TabIcon.TooltipText);
    }

    [Fact]
    public void TheIconTooltip_KeepsTheProfileName_WhenTheCommandIsNotAShell()
    {
        // MSYS2 launches through winpty; the first token names a pty
        // bridge, not the interpreter, and the profile name is the better
        // answer.
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("MSYS2", @"C:\msys64\usr\bin\winpty.exe C:\msys64\usr\bin\bash.exe --login -i"));
        Assert.Equal("MSYS2", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void TheIconTooltip_SaysWhatRunsInsideWhichShell_WhileAProcessIsInFront()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(Profile("Primary", "pwsh.exe"));

        tab.OnActiveProcessChanged("vim.exe", "vim x.zig");
        Assert.Equal("Vim in PowerShell", tab.TabIcon.TooltipText);

        tab.OnActiveProcessChanged("pwsh.exe", "pwsh");
        Assert.Equal("PowerShell\nPrimary", tab.TabIcon.TooltipText);
    }

    [Fact]
    public void TheIconTooltip_WithoutAProfile_IsTheProcessAlone()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.Equal("Terminal", tab.TabIcon.TooltipText);

        tab.OnActiveProcessChanged("node.exe", "node server.js");
        Assert.Equal("Node.js", tab.TabIcon.TooltipText);
    }
}
