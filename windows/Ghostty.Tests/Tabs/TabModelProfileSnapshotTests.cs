using System;
using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public class TabModelProfileSnapshotTests
{
    private static ProfileSnapshot SampleSnapshot() =>
        ProfileSnapshotStore.From(
            new ResolvedProfile(
                Id: "foo", Name: "Foo", Command: "cmd.exe",
                WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
                TabTitle: "Foo", Visuals: EffectiveVisualOverrides.Empty,
                ProbeId: null, OrderIndex: 0, IsDefault: true),
            version: 1);

    [Fact]
    public void ProfileSnapshot_DefaultsToNull()
    {
        var tab = new TabModel(new FakePaneHost());
        Assert.Null(tab.ProfileSnapshot);
    }

    [Fact]
    public void AttachProfileSnapshot_ExposesSnapshot()
    {
        var tab = new TabModel(new FakePaneHost());
        var snapshot = SampleSnapshot();

        tab.AttachProfileSnapshot(snapshot);

        Assert.Same(snapshot, tab.ProfileSnapshot);
    }

    [Fact]
    public void AttachProfileSnapshot_CalledTwice_Throws()
    {
        var tab = new TabModel(new FakePaneHost());
        var snapshot = SampleSnapshot();
        tab.AttachProfileSnapshot(snapshot);

        Assert.Throws<InvalidOperationException>(
            () => tab.AttachProfileSnapshot(snapshot));
    }

    [Fact]
    public void EffectiveTitle_FallsBackToProfileDisplayName_WhenNoShellOrUserTitle()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(SampleSnapshot());

        Assert.Equal("Foo", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_ShellReportedTitle_BeatsProfileDisplayName()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(SampleSnapshot());
        tab.ShellReportedTitle = "vim file.txt";

        Assert.Equal("vim file.txt", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_UserOverride_BeatsEverything()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(SampleSnapshot());
        tab.ShellReportedTitle = "vim file.txt";
        tab.UserOverrideTitle = "renamed";

        Assert.Equal("renamed", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_NoSnapshotAndNoTitles_FallsBackToHardcoded()
    {
        var tab = new TabModel(new FakePaneHost());

        Assert.Equal("Wintty", tab.EffectiveTitle);
    }

    /// <summary>
    /// A shell can report a title that is empty or all spaces:
    /// an OSC 2 with an empty payload lands here as "" and never as null, and
    /// the setter only guards against null. Coalescing on null alone let
    /// that win the precedence chain, so the strip drew a blank label and
    /// a reader read a blank name.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EffectiveTitle_BlankShellTitle_FallsThroughToProfile(string blank)
    {
        var tab = new TabModel(new FakePaneHost());
        tab.AttachProfileSnapshot(SampleSnapshot());
        tab.ShellReportedTitle = blank;

        Assert.Equal("Foo", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_BlankShellTitleAndNoProfile_FallsBackToProductName()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.ShellReportedTitle = "   ";

        Assert.Equal("Wintty", tab.EffectiveTitle);
    }

    [Fact]
    public void EffectiveTitle_BlankUserOverride_FallsThroughToShellTitle()
    {
        var tab = new TabModel(new FakePaneHost());
        tab.ShellReportedTitle = "vim file.txt";
        tab.UserOverrideTitle = "   ";

        Assert.Equal("vim file.txt", tab.EffectiveTitle);
    }
}
