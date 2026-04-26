using System;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

public sealed class TabManagerDetachTests
{
    private static TabManager NewManager(out FakePaneHost first)
    {
        FakePaneHost? captured = null;
        var mgr = new TabManager((_) =>
        {
            var h = new FakePaneHost();
            captured ??= h;
            return h;
        });
        first = captured!;
        return mgr;
    }

    [Fact]
    public void DetachTab_RemovesFromTabs_PreservesPaneHostIdentity()
    {
        var src = NewManager(out _);
        src.NewTab();
        src.NewTab();
        Assert.Equal(3, src.Tabs.Count);

        var target = src.Tabs[1];
        var paneHostBefore = target.PaneHost;

        var detached = src.DetachTab(target);

        Assert.Equal(2, src.Tabs.Count);
        Assert.DoesNotContain(target, src.Tabs);
        Assert.Same(target, detached);
        Assert.Same(paneHostBefore, detached.PaneHost);
    }

    [Fact]
    public void DetachTab_RaisesTabDetachingExactlyOnceBeforeRemoval()
    {
        var src = NewManager(out _);
        src.NewTab();
        var target = src.Tabs[1];

        int detachingCount = 0;
        int countAtDetachingTime = -1;
        src.TabDetaching += (_, t) =>
        {
            detachingCount++;
            Assert.Same(target, t);
            countAtDetachingTime = src.Tabs.Count;
        };

        src.DetachTab(target);

        Assert.Equal(1, detachingCount);
        Assert.Equal(2, countAtDetachingTime); // fires BEFORE removal
    }

    [Fact]
    public void DetachTab_DoesNotCallDisposeAllLeaves()
    {
        var src = NewManager(out var host);
        var target = src.Tabs[0];
        // Seed a second tab so the detach does not trip LastTabClosed.
        src.NewTab();

        src.DetachTab(target);

        Assert.Equal(0, host.DisposeAllCalls);
    }

    [Fact]
    public void DetachTab_LastTab_ThrowsInvalidOperation()
    {
        var src = NewManager(out _);
        Assert.Equal(1, src.Tabs.Count);

        var ex = Assert.Throws<InvalidOperationException>(
            () => src.DetachTab(src.Tabs[0]));

        Assert.Contains("Cannot detach the last tab", ex.Message);
    }

    [Fact]
    public void DetachTab_OfActiveTab_PicksNextActiveTab()
    {
        var src = NewManager(out _);
        src.NewTab(); // index 1, becomes active
        src.NewTab(); // index 2, becomes active
        var active = src.ActiveTab;

        src.DetachTab(active);

        Assert.Equal(2, src.Tabs.Count);
        Assert.NotSame(active, src.ActiveTab);
    }

    [Fact]
    public void AdoptTab_AddsToTargetManager_FiresTabAddedAndActivates()
    {
        var src = NewManager(out _);
        src.NewTab();
        var detached = src.DetachTab(src.Tabs[1]);

        var dst = NewManager(out _);
        Assert.Equal(1, dst.Tabs.Count);

        int tabAdded = 0;
        dst.TabAdded += (_, _) => tabAdded++;

        dst.AdoptTab(detached);

        Assert.Equal(2, dst.Tabs.Count);
        Assert.Contains(detached, dst.Tabs);
        Assert.Same(detached, dst.ActiveTab);
        Assert.Equal(1, tabAdded);
    }

    [Fact]
    public void SeededCtor_UsesSeed_DoesNotCallFactory()
    {
        int factoryCalls = 0;
        var seedHost = new FakePaneHost();
        var seed = new TabModel(seedHost);

        var dst = new TabManager(
            (_) => { factoryCalls++; return new FakePaneHost(); },
            seed);

        Assert.Equal(0, factoryCalls);
        Assert.Equal(1, dst.Tabs.Count);
        Assert.Same(seed, dst.Tabs[0]);
        Assert.Same(seed, dst.ActiveTab);
    }

    [Fact]
    public void DetachAdoptRoundTrip_ProgressRoutesToNewManager()
    {
        var src = NewManager(out var srcHost);
        src.NewTab();
        var target = src.Tabs[1];

        var detached = src.DetachTab(target);

        var dst = NewManager(out _);
        dst.AdoptTab(detached);

        // Simulate active leaf progress on the source host. Because
        // AdoptTab rebinds the progress handler, detached.Progress must
        // follow the host it now lives under.
        var hostOfDetached = (FakePaneHost)detached.PaneHost;
        hostOfDetached.RaiseProgressChanged(TabProgressState.Normal(42));

        Assert.Equal(TabProgressState.Kind.Normal, detached.Progress.State);
        Assert.Equal(42, detached.Progress.Percent);
    }
}
