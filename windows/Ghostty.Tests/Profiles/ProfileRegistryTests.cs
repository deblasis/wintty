using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ghostty.Tests.Profiles;

public class ProfileRegistryTests
{
    // Test shim: the registry takes a dispatch delegate (Action<Action>)
    // so tests can run everything synchronously on the calling thread.
    private static readonly Action<Action> SynchronousDispatcher = a => a();

    // Discovery delegate returning an empty list synchronously. Later
    // tests use a TaskCompletionSource to control completion timing.
    private static Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> EmptyDiscovery()
        => (_, _) => Task.FromResult<IReadOnlyList<DiscoveredProfile>>(Array.Empty<DiscoveredProfile>());

    private static ProfileDef UserDef(string id, string name = "", string command = "cmd.exe")
        => new(
            Id: id,
            Name: name.Length > 0 ? name : id,
            Command: command,
            WorkingDirectory: null,
            Icon: null,
            TabTitle: null,
            Hidden: false,
            ProbeId: null,
            VisualsOrNull: null);

    [Fact]
    public void Ctor_FiresInitialEvent_UserOnlyCompose()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["a"] = UserDef("a", "A"),
                ["b"] = UserDef("b", "B"),
            },
            DefaultProfileId = "a",
        };

        using var registry = new ProfileRegistry(
            src,
            EmptyDiscovery(),
            SynchronousDispatcher,
            NullLogger<ProfileRegistry>.Instance);

        // Post-ctor, the registry has already done an initial synchronous
        // compose (discovery is still running). We verify via direct state
        // reads; the Ctor fires events synchronously before returning so
        // subscribers that only need "subsequent changes" add their handler
        // after construction.
        Assert.True(registry.Version >= 1);
        Assert.Equal(2, registry.Profiles.Count);
        Assert.Equal("a", registry.DefaultProfileId);
    }

    [Fact]
    public async Task DiscoveryCompletes_FiresSecondEvent_WithDiscovered()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["user-a"] = UserDef("user-a", "User A"),
            },
            DefaultProfileId = "user-a",
        };

        var tcs = new TaskCompletionSource<IReadOnlyList<DiscoveredProfile>>();
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> deferred =
            (_, _) => tcs.Task;

        var events = new List<int>();
        using var registry = new ProfileRegistry(
            src, deferred, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);
        registry.ProfilesChanged += r => events.Add(r.Profiles.Count);

        // Version is 1, Profiles has only the user entry.
        Assert.Equal(1L, registry.Version);
        Assert.Single(registry.Profiles);

        // Complete discovery with one discovered profile.
        tcs.SetResult(new List<DiscoveredProfile>
        {
            new(Id: "wsl-ubuntu", Name: "Ubuntu", Command: "wsl.exe",
                ProbeId: "wsl", WorkingDirectory: null, Icon: null, TabTitle: null),
        });

        // Give the continuation a chance to run.
        await Task.Yield();
        for (int i = 0; i < 20 && registry.Version < 2; i++) await Task.Delay(5);

        Assert.Equal(2L, registry.Version);
        Assert.Equal(2, registry.Profiles.Count);
        Assert.Single(events);
        Assert.Equal(2, events[0]);
    }

    [Fact]
    public async Task ProfileConfigChanged_RecomposesWithCachedDiscovered()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["a"] = UserDef("a"),
            },
        };

        var discoveredOnce = new List<DiscoveredProfile>
        {
            new(Id: "wsl", Name: "Ubuntu", Command: "wsl.exe",
                ProbeId: "wsl", WorkingDirectory: null, Icon: null, TabTitle: null),
        };
        var firstCallDone = new TaskCompletionSource();
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discovery =
            (_, _) => { firstCallDone.TrySetResult(); return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(discoveredOnce); };

        using var registry = new ProfileRegistry(
            src, discovery, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);
        await firstCallDone.Task;
        for (int i = 0; i < 20 && registry.Version < 2; i++) await Task.Delay(5);

        // Replace user profiles + raise the event; registry should
        // recompose with the same discovered list (Version bumps to 3).
        src.ParsedProfiles = new Dictionary<string, ProfileDef>
        {
            ["a"] = UserDef("a"),
            ["b"] = UserDef("b"),
        };
        src.Raise();

        Assert.Equal(3L, registry.Version);
        Assert.Equal(3, registry.Profiles.Count);  // user a, b + wsl
    }

    [Fact]
    public void Resolve_ReturnsProfile_WhenIdKnown()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["target"] = UserDef("target", "Target"),
            },
        };

        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        var result = registry.Resolve("target");
        Assert.NotNull(result);
        Assert.Equal("target", result!.Id);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenIdUnknown()
    {
        var src = new FakeProfileConfigSource();
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.Null(registry.Resolve("nope"));
    }

    [Fact]
    public void Version_IsMonotonic_AcrossRecompose()
    {
        var src = new FakeProfileConfigSource();
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        var v1 = registry.Version;
        src.Raise();
        var v2 = registry.Version;
        src.Raise();
        var v3 = registry.Version;

        Assert.True(v2 > v1);
        Assert.True(v3 > v2);
        Assert.Equal(v1 + 1, v2);
        Assert.Equal(v2 + 1, v3);
    }

    [Fact]
    public void DefaultProfileId_TracksIsDefaultEntry()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["a"] = UserDef("a"),
                ["b"] = UserDef("b"),
            },
            DefaultProfileId = "b",
        };

        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.Equal("b", registry.DefaultProfileId);
    }

    [Fact]
    public async Task RefreshDiscoveryAsync_BypassesCache_AndFiresEvent()
    {
        var src = new FakeProfileConfigSource();
        var callsWithBypass = 0;
        var callsWithoutBypass = 0;
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discovery =
            (bypass, _) =>
            {
                if (bypass) callsWithBypass++; else callsWithoutBypass++;
                return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(Array.Empty<DiscoveredProfile>());
            };

        using var registry = new ProfileRegistry(
            src, discovery, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);
        for (int i = 0; i < 20 && registry.Version < 2; i++) await Task.Delay(5);

        var eventsBefore = 0;
        registry.ProfilesChanged += _ => eventsBefore++;

        await registry.RefreshDiscoveryAsync(CancellationToken.None);

        Assert.Equal(1, callsWithoutBypass);  // initial bootstrap
        Assert.Equal(1, callsWithBypass);     // explicit refresh
        Assert.Equal(1, eventsBefore);        // one recompose event after refresh
    }

    [Fact]
    public async Task DiscoveryThrows_KeepsPriorState_DoesNotFireEvent()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["user"] = UserDef("user"),
            },
        };
        var throwOnBootstrap = new TaskCompletionSource<IReadOnlyList<DiscoveredProfile>>();
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discovery =
            (_, _) => throwOnBootstrap.Task;

        using var registry = new ProfileRegistry(
            src, discovery, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        var versionBefore = registry.Version;
        var eventsFiredAfterSubscribe = 0;
        registry.ProfilesChanged += _ => eventsFiredAfterSubscribe++;

        throwOnBootstrap.SetException(new InvalidOperationException("boom"));
        // Give the continuation time to run.
        for (int i = 0; i < 20; i++) await Task.Delay(5);

        Assert.Equal(versionBefore, registry.Version);        // unchanged
        Assert.Single(registry.Profiles);                      // user still there
        Assert.Equal(0, eventsFiredAfterSubscribe);            // no event after failure
    }

    [Fact]
    public async Task Dispose_CancelsPendingDiscovery_AndUnsubscribesSource()
    {
        var src = new FakeProfileConfigSource();
        var tcs = new TaskCompletionSource<IReadOnlyList<DiscoveredProfile>>();
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discovery =
            (_, ct) =>
            {
                ct.Register(() => tcs.TrySetCanceled(ct));
                return tcs.Task;
            };

        var registry = new ProfileRegistry(
            src, discovery, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        var eventsAfterDispose = 0;
        registry.ProfilesChanged += _ => eventsAfterDispose++;

        registry.Dispose();

        // Post-dispose: raising ProfileConfigChanged must not fire events.
        src.Raise();

        // Pending discovery is cancelled.
        await Assert.ThrowsAsync<TaskCanceledException>(() => tcs.Task);
        Assert.Equal(0, eventsAfterDispose);
    }
}
