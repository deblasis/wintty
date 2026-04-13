using System;
using Ghostty.Core.Hosting;
using Xunit;

namespace Ghostty.Tests.Hosting;

public sealed class HostLifetimeStateTests
{
    [Fact]
    public void Bootstrap_OwnsApp_AndIsNotDisposedInitially()
    {
        var s = HostLifetimeState.Bootstrap();
        Assert.True(s.IsBootstrap);
        Assert.True(s.OwnsApp);
        Assert.False(s.IsDisposed);
    }

    [Fact]
    public void PerWindow_DoesNotOwnApp()
    {
        var s = HostLifetimeState.PerWindow();
        Assert.False(s.IsBootstrap);
        Assert.False(s.OwnsApp);
        Assert.False(s.IsDisposed);
    }

    [Fact]
    public void MarkDisposed_IsIdempotent()
    {
        var s = HostLifetimeState.PerWindow();
        s.MarkDisposed();
        s.MarkDisposed();
        Assert.True(s.IsDisposed);
    }

    [Fact]
    public void Supervisor_RequiresBootstrapDrainLast()
    {
        var supervisor = new HostLifetimeSupervisor();
        var bootstrap = supervisor.RegisterBootstrap();
        var perWindow1 = supervisor.RegisterPerWindow();
        var perWindow2 = supervisor.RegisterPerWindow();

        perWindow1.MarkDisposed();
        supervisor.NotifyDisposed(perWindow1);
        perWindow2.MarkDisposed();
        supervisor.NotifyDisposed(perWindow2);

        // Bootstrap CAN dispose now; nothing should throw.
        bootstrap.MarkDisposed();
        supervisor.NotifyDisposed(bootstrap);

        Assert.True(bootstrap.IsDisposed);
        Assert.Equal(0, supervisor.LivePerWindowCount);
    }

    [Fact]
    public void Supervisor_BootstrapDispose_ThrowsIfPerWindowStillAlive()
    {
        var supervisor = new HostLifetimeSupervisor();
        var bootstrap = supervisor.RegisterBootstrap();
        _ = supervisor.RegisterPerWindow();

        // Attempt to dispose the bootstrap while a per-window host
        // is still live violates the drain-last invariant.
        Assert.Throws<InvalidOperationException>(
            () => supervisor.NotifyDisposed(bootstrap));
    }
}
