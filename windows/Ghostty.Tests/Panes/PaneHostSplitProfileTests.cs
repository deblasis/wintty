using Ghostty.Core.Panes;
using Ghostty.Core.Profiles;
using Ghostty.Tests.Tabs;
using Xunit;

namespace Ghostty.Tests.Panes;

public class PaneHostSplitProfileTests
{
    [Fact]
    public void Split_WithSnapshot_RecordsSnapshot()
    {
        var host = new FakePaneHost();
        var profile = new ResolvedProfile(
            Id: "foo", Name: "Foo", Command: "cmd.exe",
            WorkingDirectory: null, Icon: new IconSpec.BundledKey("default"),
            TabTitle: "Foo", Visuals: EffectiveVisualOverrides.Empty,
            ProbeId: null, OrderIndex: 0, IsDefault: true);
        var snapshot = ProfileSnapshotStore.From(profile, version: 1);

        host.Split(PaneOrientation.Horizontal, snapshot);

        Assert.Equal(1, host.SplitCalls);
        Assert.Equal(PaneOrientation.Horizontal, host.LastSplitOrientation);
        Assert.Same(snapshot, host.LastSplitSnapshot);
    }

    [Fact]
    public void Split_NullSnapshot_PreservesLegacyBehavior()
    {
        var host = new FakePaneHost();
        host.Split(PaneOrientation.Vertical, snapshot: null);

        Assert.Equal(1, host.SplitCalls);
        Assert.Null(host.LastSplitSnapshot);
        // FakePaneHost starts at PaneCount=1; Split increments to 2.
        Assert.Equal(2, host.PaneCount);
    }

    [Fact]
    public void PaneHost_NoArgSplit_InheritsActiveLeafSnapshot()
    {
        var src = ReadEmbedded(@"Panes\PaneHost.cs");
        Assert.Contains("snapshot: _activeLeaf.Snapshot", src);
        Assert.DoesNotContain("Legacy keyboard-Split path; no profile", src);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var name = System.Linq.Enumerable.Single(
            asm.GetManifestResourceNames(),
            n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
