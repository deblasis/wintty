using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Ghostty.Core;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// A tab is named by its active pane's title. Until the pane reports one,
/// it is named by what launched the pane: the profile's display name when
/// there is one, otherwise the shell or command actually running. The
/// application's own name is not a tab name.
///
/// The case these tests exist for is the cold start with no resolvable
/// default profile: nothing attaches a snapshot, the host spawns the
/// platform default shell, and until this the bottom of the chain was the
/// product name, so a window's first tab read "Wintty" while every tab
/// opened from the new-tab button -- which always carries a snapshot --
/// read its profile. The asymmetry was the fallback, not the wiring.
/// </summary>
public class TabLaunchNameTests
{
    private static ProfileSnapshot NamedProfile(string name) =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "p", Name: name, Command: "pwsh.exe",
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("pwsh"),
                TabTitle: name, Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    private static List<string?> Record(INotifyPropertyChanged source)
    {
        var seen = new List<string?>();
        source.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        return seen;
    }

    // --- the defect ---

    /// <summary>
    /// The cold start the owner saw: no profile, no title, no directory.
    /// Whatever the tab is called, it is not the application.
    /// </summary>
    [Fact]
    public void EffectiveTitle_ColdStartWithNoProfile_IsNeverTheApplicationsOwnName()
    {
        var tab = new TabModel(new FakePaneHost());

        Assert.NotEqual(AppIdentity.ProductName, tab.EffectiveTitle);
        Assert.False(string.IsNullOrWhiteSpace(tab.EffectiveTitle));
    }

    /// <summary>
    /// And once the pane says what it spawned, the tab says it too. This is
    /// the data path the fallback was missing: a tab with no profile had no
    /// name to fall back to, because nothing surfaced the launch shell.
    /// </summary>
    [Fact]
    public void EffectiveTitle_ColdStartWithNoProfile_NamesTheShellThePaneLaunched()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("pwsh.exe", @"""C:\Program Files\PowerShell\7\pwsh.exe""");

        Assert.Equal("PowerShell", tab.EffectiveTitle);
    }

    // --- what the pane surfaces, and how it degrades ---

    /// <summary>
    /// The pane hands over the two raw facts -- the executable's basename
    /// and its command line -- and the model names them, exactly as it
    /// already does for the foreground process. A known interpreter gets
    /// the name a person uses for it.
    /// </summary>
    [Theory]
    [InlineData("pwsh.exe", null, "PowerShell")]
    [InlineData("powershell.exe", null, "Windows PowerShell")]
    [InlineData("cmd.exe", null, "Command Prompt")]
    [InlineData("bash.exe", null, "Bash")]
    public void ThePaneLaunchName_IsWhatAPersonCallsTheShell(
        string exe, string? commandLine, string expected)
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched(exe, commandLine);

        Assert.Equal(expected, tab.EffectiveTitle);
    }

    /// <summary>
    /// A distro-carrying launch reads as the distro, the same way the
    /// profile probe writes it, because the distro is what distinguishes
    /// one WSL tab from another.
    /// </summary>
    [Fact]
    public void ThePaneLaunchName_NamesTheWslDistro()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("wsl.exe", "wsl.exe -d Ubuntu-24.04");

        Assert.Equal("WSL: Ubuntu-24.04", tab.EffectiveTitle);
    }

    /// <summary>
    /// A pane running an arbitrary command rather than a shell is the case
    /// that has to degrade sanely: no table entry, so the executable names
    /// itself the way a person types it.
    /// </summary>
    [Fact]
    public void ThePaneLaunchName_DegradesToTheExecutablesOwnName_ForAnArbitraryCommand()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("btop.exe", @"btop.exe --preset 0");

        Assert.Equal("btop", tab.EffectiveTitle);
    }

    /// <summary>
    /// Nothing to report is not a name. A pane whose process could not be
    /// read -- exited, access denied -- leaves the tab where it was rather
    /// than blanking it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ThePaneLaunchName_IsNotSet_WhenThereIsNothingToReport(string? exe)
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched(exe, commandLine: null);

        Assert.Null(tab.PaneLaunchName);
        Assert.NotEqual(AppIdentity.ProductName, tab.EffectiveTitle);
        Assert.False(string.IsNullOrWhiteSpace(tab.EffectiveTitle));
    }

    /// <summary>
    /// The basename crosses from another process's image path, so it gets
    /// the same treatment as every other string the tab renders from
    /// outside: a name carrying a line break or a bidi override would write
    /// a second line into the tooltip and reorder the strip.
    /// </summary>
    [Theory]
    [InlineData("ev\u202Eil.exe")]
    [InlineData("two\nlines.exe")]
    public void ThePaneLaunchName_RefusesANameThatIsNotPlainText(string exe)
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched(exe, commandLine: null);

        Assert.Null(tab.PaneLaunchName);
    }

    /// <summary>
    /// The refusal is a line about the INPUT and a line about the RESULT,
    /// and the rows above reach both at once: the name for an unknown exe
    /// is its basename with the extension dropped, so an offending rune in
    /// the middle survives into the name and either line alone would catch
    /// it. The next two reach one line each.
    ///
    /// This one reaches the input line. The extension here IS the bidi
    /// override, so dropping it leaves a name that is perfectly plain --
    /// "wsl" -- from a basename that was not, and the result line lets it
    /// through. Without the input line the tab would be named out of a
    /// string nothing vouched for.
    /// </summary>
    [Fact]
    public void ThePaneLaunchName_RefusesAnInputThatIsNotPlain_EvenWhenTheNameWouldBe()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("wsl.exe\u202E", commandLine: null);

        Assert.Null(tab.PaneLaunchName);
    }

    /// <summary>
    /// And this one reaches the result line -- specifically its BLANK
    /// half. A bare drive is plain text, and Path.GetFileNameWithoutExtension
    /// of "C:" is "" (the volume separator sits at index 1, so the slice
    /// starts past the whole string), so a plain input produces no name at
    /// all. Without that line the tab latches an empty launch name, stops
    /// asking, and sits on the generic word for good.
    ///
    /// The other half of that line, !IsPlain(name), is DEAD given the
    /// input line above it: every value ProcessDisplayName.For can return
    /// is a literal, a table constant, "WSL: {distro}" with the distro
    /// already checked inside For, or a prefix of an already-plain
    /// basename. It is kept as documented defence in depth -- For is not
    /// this file's to constrain -- and no test can reach it, which is
    /// stated here rather than left for the next reader to rediscover.
    /// </summary>
    [Fact]
    public void ThePaneLaunchName_RefusesAnEmptyName_FromAPlainInput()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.True(TabLabel.IsPlain("C:"));

        tab.OnPaneLaunched("C:", commandLine: null);

        Assert.Null(tab.PaneLaunchName);
        Assert.False(tab.PaneLaunchNameIsComplete);
    }

    /// <summary>
    /// What launched the tab happened once. A later report -- a respawned
    /// shell, a second resolution racing the first -- does not rename a tab
    /// the user has been looking at.
    /// </summary>
    [Fact]
    public void ThePaneLaunchName_IsSettledByTheFirstCompleteReport()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("pwsh.exe", "pwsh.exe");
        Assert.True(tab.PaneLaunchNameIsComplete);

        tab.OnPaneLaunched("cmd.exe", "cmd.exe");

        Assert.Equal("PowerShell", tab.PaneLaunchName);
    }

    /// <summary>
    /// The two facts behind the name are read from a live process and fail
    /// independently: the image path still answers while the command line
    /// is refused mid-exit. A name built without the command line can be
    /// strictly poorer than the one that process had to give, so it names
    /// the tab -- a poorer name beats the generic one -- without closing
    /// the question.
    /// </summary>
    [Fact]
    public void AHalfReport_NamesTheTab_ButDoesNotLatchIt()
    {
        var tab = new TabModel(new FakePaneHost());

        // The image path answered; the command line did not.
        tab.OnPaneLaunched("wsl.exe", commandLine: null);

        Assert.Equal("WSL", tab.EffectiveTitle);
        Assert.False(tab.PaneLaunchNameIsComplete);

        // So a complete read a moment later still gets to improve it.
        tab.OnPaneLaunched("wsl.exe", "wsl.exe -d Ubuntu-24.04");

        Assert.Equal("WSL: Ubuntu-24.04", tab.EffectiveTitle);
        Assert.True(tab.PaneLaunchNameIsComplete);
    }

    /// <summary>
    /// A repeat that says the same thing is not a change. Raising for it
    /// would retitle the window and re-announce the tab to a listener for
    /// nothing.
    /// </summary>
    [Fact]
    public void ASecondReport_WithTheSameAnswer_RaisesNothing()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.OnPaneLaunched("pwsh.exe", commandLine: null);
        var seen = Record(tab);

        tab.OnPaneLaunched("pwsh.exe", "pwsh.exe");

        Assert.Empty(seen);
        Assert.Equal("PowerShell", tab.PaneLaunchName);
        // The question is closed all the same, so nothing asks a third time.
        Assert.True(tab.PaneLaunchNameIsComplete);
    }

    /// <summary>
    /// A command line that is present but says nothing is not an answer
    /// either. The resolver returns null for an unreadable one today, but
    /// this entry point is public, and a caller handing over "" would
    /// otherwise latch the poorer name for good.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCommandLine_DoesNotCloseTheQuestion(string commandLine)
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched("wsl.exe", commandLine);

        Assert.Equal("WSL", tab.PaneLaunchName);
        Assert.False(tab.PaneLaunchNameIsComplete);

        tab.OnPaneLaunched("wsl.exe", "wsl.exe -d Ubuntu-24.04");

        Assert.Equal("WSL: Ubuntu-24.04", tab.PaneLaunchName);
        Assert.True(tab.PaneLaunchNameIsComplete);
    }

    [Fact]
    public void NothingToReport_LeavesTheQuestionOpen()
    {
        var tab = new TabModel(new FakePaneHost());

        tab.OnPaneLaunched(exeBasename: null, commandLine: null);

        Assert.Null(tab.PaneLaunchName);
        Assert.False(tab.PaneLaunchNameIsComplete);
    }

    // --- where it sits in the chain ---

    /// <summary>
    /// The profile's display name is what launched the pane when there is a
    /// profile, so it outranks the shell underneath it: a tab opened from a
    /// profile called "Build" says "Build", not "PowerShell".
    /// </summary>
    [Fact]
    public void TheProfilesDisplayName_OutranksTheLaunchName()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(NamedProfile("Build"));

        tab.OnPaneLaunched("pwsh.exe", null);

        Assert.Equal("Build", tab.EffectiveTitle);
    }

    /// <summary>
    /// And everything the pane itself says outranks both: the directory the
    /// shell reported, the title it sent, and the name the user gave the
    /// tab. The launch name is only what fills the gap before any of them.
    /// </summary>
    [Fact]
    public void EverythingThePaneSays_OutranksTheLaunchName()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.OnPaneLaunched("pwsh.exe", null);
        Assert.Equal("PowerShell", tab.EffectiveTitle);

        tab.ShellReportedCwd = @"C:\src\repo";
        Assert.Equal("repo", tab.EffectiveTitle);

        tab.ShellReportedTitle = "vim build.zig";
        Assert.Equal("vim build.zig", tab.EffectiveTitle);

        tab.UserOverrideTitle = "notes";
        Assert.Equal("notes", tab.EffectiveTitle);
    }

    // --- the notifications and the starting state ---

    [Fact]
    public void OnPaneLaunched_RaisesTheLabelAndItsDerivedReadings()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.OnPaneLaunched("pwsh.exe", null);

        Assert.Equal(
            [
                nameof(TabModel.PaneLaunchName),
                nameof(TabModel.EffectiveTitle),
                nameof(TabModel.IsHome),
                nameof(TabModel.WordTitle),
                nameof(TabModel.TooltipText),
                nameof(TabModel.HoverText),
            ],
            seen);
    }

    [Fact]
    public void OnPaneLaunched_RaisesNothing_WhenThereIsNothingToReport()
    {
        var tab = new TabModel(new FakePaneHost());
        var seen = Record(tab);

        tab.OnPaneLaunched(exeBasename: null, commandLine: null);

        Assert.Empty(seen);
    }

    /// <summary>
    /// Learning what was spawned is not the pane speaking. A tab keeps
    /// reading as starting until its surface paints or its shell says
    /// something, which is what the strips dim the label for.
    /// </summary>
    [Fact]
    public void OnPaneLaunched_LeavesTheTabStarting()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.BeginSettling();

        tab.OnPaneLaunched("pwsh.exe", null);

        Assert.True(tab.IsSettling);
    }
}
