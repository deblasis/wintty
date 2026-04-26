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

        Assert.Equal("Ghostty", tab.EffectiveTitle);
    }
}
