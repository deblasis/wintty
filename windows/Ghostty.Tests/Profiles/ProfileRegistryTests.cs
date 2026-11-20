using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Wait until the registry reaches <paramref name="version"/>.
    /// </summary>
    /// <remarks>
    /// Discovery completes on a thread-pool continuation, so what these tests
    /// are waiting for is a SCHEDULING event, not a duration. A fixed budget
    /// therefore measures how busy the machine is: at 20 x 5ms this failed in
    /// full-suite runs and passed in isolation, because the rest of the suite
    /// had the pool. A deadline that is generous when loaded and returns
    /// immediately when not removes the machine from the assertion.
    /// </remarks>
    private static async Task WaitForVersion(ProfileRegistry registry, long version)
    {
        // A hang guard, not a budget: the shape above (return the moment the
        // version lands) is what keeps the machine out of the assertion. The
        // deadline only decides whether a broken condition reports or hangs,
        // so it is wide enough to survive a fully loaded box.
        var deadline = Environment.TickCount64 + 60_000;
        while (registry.Version < version && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        Assert.True(
            registry.Version >= version,
            $"the registry never reached version {version} (stuck at {registry.Version})");
    }

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
        await WaitForVersion(registry, 2);

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
        await WaitForVersion(registry, 2);

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
        await WaitForVersion(registry, 2);

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
        // A fixed wait on purpose: this asserts nothing happened, so a
        // longer one is only ever stronger and a shorter one is what would
        // make it lie. It is not the deadline shape used above.
        for (int i = 0; i < 20; i++) await Task.Delay(5);

        Assert.Equal(versionBefore, registry.Version);        // unchanged
        Assert.Single(registry.Profiles);                      // user still there
        Assert.Equal(0, eventsFiredAfterSubscribe);            // no event after failure
    }

    [Fact]
    public void HiddenProfiles_ExposesEntriesFilteredFromVisibleList()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["a"] = UserDef("a", "A"),
                ["b"] = new ProfileDef(
                    Id: "b",
                    Name: "B",
                    Command: "b.exe",
                    WorkingDirectory: null,
                    Icon: null,
                    TabTitle: null,
                    Hidden: true,
                    ProbeId: null,
                    VisualsOrNull: null),
            },
        };

        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.Equal(new[] { "a" }, registry.Profiles.Select(p => p.Id));
        Assert.Equal(new[] { "b" }, registry.HiddenProfiles.Select(p => p.Id));
    }

    [Fact]
    public void HiddenProfiles_RecomposedAfterConfigChange()
    {
        var src = new FakeProfileConfigSource
        {
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["a"] = UserDef("a", "A"),
            },
        };

        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.Empty(registry.HiddenProfiles);

        src.HiddenProfileIds = new HashSet<string> { "a" }.ToFrozenSet();
        src.Raise();

        Assert.Empty(registry.Profiles);
        Assert.Equal(new[] { "a" }, registry.HiddenProfiles.Select(p => p.Id));
    }

    // The warning a partial profile block carries is the parser saying "this
    // bag is not a definition". The registry is the first thing that knows
    // whether discovery supplies the rest, so the warnings it publishes are
    // the parse-level list minus the blocks that turned out to be overrides
    // -- which is every block the settings page writes.
    private static FakeProfileConfigSource IconOverrideSource(string id)
        => new()
        {
            ProfileOverrides = new Dictionary<string, ProfileOverride>
            {
                [id] = new(Id: id, Icon: new IconSpec.BrandKey("pwsh", null)),
            },
            ProfileWarnings = new[] { $"profile '{id}': missing required key 'name', dropped" },
        };

    private static Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>>
        Discovering(params DiscoveredProfile[] found)
        => (_, _) => Task.FromResult<IReadOnlyList<DiscoveredProfile>>(found);

    [Fact]
    public async Task ProfileWarnings_DropsOneDiscoveryExplained()
    {
        var src = IconOverrideSource("pwsh");
        var pwsh = new DiscoveredProfile(
            Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe", ProbeId: "pwsh");

        using var registry = new ProfileRegistry(
            src, Discovering(pwsh), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);
        await WaitForVersion(registry, 2);

        // The override is applied...
        Assert.Equal(new IconSpec.BrandKey("pwsh", null), registry.Resolve("pwsh")!.Icon);
        // ...and the block is not also reported as a broken one.
        Assert.Empty(registry.ProfileWarnings);
    }

    [Fact]
    public void ProfileWarnings_KeepsOneNothingExplained()
    {
        var src = IconOverrideSource("ghost");

        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.Null(registry.Resolve("ghost"));
        Assert.Single(registry.ProfileWarnings);
        Assert.Contains("ghost", registry.ProfileWarnings[0]);
    }

    [Fact]
    public async Task ProfileWarnings_RepublishedOnDiscoveryArriving()
    {
        // The composition is what explains the block, so the answer changes
        // when discovery lands -- and the page's WarningsBar reads this
        // list off the same snapshot as the rows.
        var src = IconOverrideSource("pwsh");
        var pwsh = new DiscoveredProfile(
            Id: "pwsh", Name: "PowerShell", Command: "pwsh.exe", ProbeId: "pwsh");

        // The ctor's discovery pass finds nothing; the refresh the user
        // triggers finds the shell, which is the case the ctor pass cannot
        // have covered.
        var calls = 0;
        Func<bool, CancellationToken, Task<IReadOnlyList<DiscoveredProfile>>> discovering = (_, _) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<DiscoveredProfile>>(
                calls == 1 ? Array.Empty<DiscoveredProfile>() : new[] { pwsh });
        };

        using var registry = new ProfileRegistry(
            src, discovering, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);
        await WaitForVersion(registry, 2);

        Assert.Single(registry.ProfileWarnings);
        var events = new List<int>();
        registry.ProfilesChanged += _ => events.Add(registry.ProfileWarnings.Count);

        await registry.RefreshDiscoveryAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.Empty(registry.ProfileWarnings);
        Assert.Equal(new[] { 0 }, events);
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

    private const string KnownHosts = "devel.local ssh-ed25519 AAAA\n192.168.0.9 ssh-ed25519 BBBB\n";

    [Fact]
    public void SshHosts_OffByDefault_KnownHostsNotEvenRead()
    {
        var reads = 0;
        var src = new FakeProfileConfigSource();
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => { reads++; return KnownHosts; });

        Assert.DoesNotContain(registry.Profiles, p => p.Id.StartsWith("ssh-", StringComparison.Ordinal));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void SshHosts_ToggleAndUser_TakeEffectOnConfigReload()
    {
        var src = new FakeProfileConfigSource();
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => KnownHosts);

        src.SshHostsDiscovery = true;
        src.SshHostsUser = "jgill";
        src.Raise();
        var host = Assert.Single(registry.Profiles, p => p.Id == "ssh-devel-local");
        Assert.Equal("ssh jgill@devel.local", host.Command);

        src.SshHostsDiscovery = false;
        src.Raise();
        Assert.DoesNotContain(registry.Profiles, p => p.Id == "ssh-devel-local");
    }

    [Fact]
    public void SshHosts_HiddenId_And_UserOverride_ApplyLikeOtherDiscovered()
    {
        var src = new FakeProfileConfigSource
        {
            SshHostsDiscovery = true,
            ParsedProfiles = new Dictionary<string, ProfileDef>
            {
                ["ssh-devel-local"] = UserDef("ssh-devel-local", "Devel box", "ssh -p 2200 root@devel.local"),
            },
        };
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => KnownHosts + "other.example ssh-ed25519 CCCC\n");

        var overridden = Assert.Single(registry.Profiles, p => p.Id == "ssh-devel-local");
        Assert.Equal("ssh -p 2200 root@devel.local", overridden.Command);

        src.HiddenProfileIds = new HashSet<string> { "ssh-other-example" };
        src.Raise();
        Assert.DoesNotContain(registry.Profiles, p => p.Id == "ssh-other-example");
    }

    [Fact]
    public void SshConnections_AreListedWithoutTheDiscoveryToggle()
    {
        var reads = 0;
        var src = new FakeProfileConfigSource
        {
            SshConnections = [new Ghostty.Core.Ssh.SshConnection("devel", "devel.local", "Devel box", "jgill", 2222)],
        };
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => { reads++; return KnownHosts; },
            readSshConfig: () => { reads++; return "Host cfg\n"; });

        var saved = Assert.Single(registry.Profiles, p => p.Id == "ssh-devel");
        Assert.Equal("Devel box", saved.Name);
        Assert.Equal("ssh -p 2222 jgill@devel.local", saved.Command);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void SshHosts_SavedBeatsConfigBeatsKnownHosts()
    {
        var src = new FakeProfileConfigSource
        {
            SshHostsDiscovery = true,
            // Same id the known_hosts entry for devel.local would get.
            SshConnections = [new Ghostty.Core.Ssh.SshConnection("devel-local", "devel.local", User: "root")],
        };
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => KnownHosts + "build ssh-ed25519 DDDD\n",
            readSshConfig: () => "Host build\n  HostName build.example\nHost alias\n");

        Assert.Equal("ssh root@devel.local", Assert.Single(registry.Profiles, p => p.Id == "ssh-devel-local").Command);
        // ~/.ssh/config's alias wins over the bare known_hosts name.
        Assert.Equal("SSH: build", Assert.Single(registry.Profiles, p => p.Id == "ssh-build").Name);
        Assert.Single(registry.Profiles, p => p.Id == "ssh-alias");
    }

    [Fact]
    public void SshEntries_WaitForShellDiscovery_SoNoneBecomesTheDefault()
    {
        // The user's default is a discovered shell that has not been found
        // yet. If ssh entries were listed now, the fallback default (first
        // visible) would be an ssh host and a first pane would connect to it.
        var pending = new TaskCompletionSource<IReadOnlyList<DiscoveredProfile>>();
        var src = new FakeProfileConfigSource
        {
            SshHostsDiscovery = true,
            DefaultProfileId = "wsl-ubuntu",
            SshConnections = [new Ghostty.Core.Ssh.SshConnection("aap", "aap.local")],
        };
        using var registry = new ProfileRegistry(
            src, (_, _) => pending.Task, SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => KnownHosts);

        Assert.Empty(registry.Profiles);
        Assert.Null(registry.DefaultProfileId);

        // A config reload in that window must not let them in either.
        src.Raise();
        Assert.Empty(registry.Profiles);

        pending.SetResult([new DiscoveredProfile("wsl-ubuntu", "Ubuntu", "wsl.exe -d Ubuntu", "wsl")]);

        // The registry resumes off this thread.
        Assert.True(SpinWait.SpinUntil(() => registry.DefaultProfileId is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal("wsl-ubuntu", registry.DefaultProfileId);
        Assert.Contains(registry.Profiles, p => p.Id == "ssh-aap");
        Assert.Contains(registry.Profiles, p => p.Id == "ssh-devel-local");
    }

    [Fact]
    public void SshEntries_AreListedWhenShellDiscoveryFails()
    {
        var src = new FakeProfileConfigSource
        {
            SshConnections = [new Ghostty.Core.Ssh.SshConnection("aap", "aap.local")],
        };
        using var registry = new ProfileRegistry(
            src,
            (_, _) => Task.FromException<IReadOnlyList<DiscoveredProfile>>(new InvalidOperationException("probe")),
            SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance);

        Assert.True(SpinWait.SpinUntil(() => registry.Profiles.Count > 0, TimeSpan.FromSeconds(5)));
        Assert.Single(registry.Profiles, p => p.Id == "ssh-aap");
    }

    [Fact]
    public void SshHosts_UnreadableFile_KeepsTheRestOfTheList()
    {
        var src = new FakeProfileConfigSource
        {
            SshHostsDiscovery = true,
            ParsedProfiles = new Dictionary<string, ProfileDef> { ["a"] = UserDef("a") },
        };
        using var registry = new ProfileRegistry(
            src, EmptyDiscovery(), SynchronousDispatcher, NullLogger<ProfileRegistry>.Instance,
            readKnownHosts: () => throw new System.IO.IOException("locked"));

        Assert.Single(registry.Profiles, p => p.Id == "a");
        Assert.DoesNotContain(registry.Profiles, p => p.Id.StartsWith("ssh-", StringComparison.Ordinal));
    }
}
