using System;
using System.Linq;
using System.Threading;
using Ghostty.Core.SingleInstance;
using Xunit;

namespace Ghostty.Tests.Windows.SingleInstance;

/// <summary>
/// Exercises the real named mutex, which is the whole point: the guarantee
/// under test is one the OS provides and a fake would only restate.
/// </summary>
public sealed class SingleInstanceElectionTests
{
    /// <summary>
    /// A path no other test (or leftover process) can be holding. The names
    /// hash the path (plus the edition and, when armed, the config root --
    /// constant within one host run), so a fresh path is a fresh election.
    /// </summary>
    private static string UniqueExePath()
        => $@"C:\wintty-tests\{Guid.NewGuid():N}\Wintty.exe";

    [Fact]
    public void Disabled_CreatesNothingAndElectsNobody()
    {
        var path = UniqueExePath();

        using var election = SingleInstanceElection.Run(enabled: false, path);

        Assert.Equal(SingleInstanceRole.Disabled, election.Role);
        Assert.Null(election.Mutex);
        Assert.Null(election.Failure);

        // The mutex must not exist. A process running with the feature off that
        // left one behind would make the next process with it on read a primary
        // that is not there.
        Assert.False(Mutex.TryOpenExisting(election.Names.Mutex, out var leaked));
        leaked?.Dispose();
    }

    [Fact]
    public void FirstElection_IsPrimary()
    {
        var path = UniqueExePath();

        using var election = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, election.Role);
        Assert.NotNull(election.Mutex);
        Assert.Null(election.Failure);
    }

    [Fact]
    public void SecondElectionWhilePrimaryLives_IsSecondary()
    {
        var path = UniqueExePath();

        using var primary = SingleInstanceElection.Run(enabled: true, path);
        using var secondary = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, primary.Role);
        Assert.Equal(SingleInstanceRole.Secondary, secondary.Role);
    }

    [Fact]
    public void AfterPrimaryReleases_NextElectionIsPrimaryAgain()
    {
        var path = UniqueExePath();

        var first = SingleInstanceElection.Run(enabled: true, path);
        Assert.Equal(SingleInstanceRole.Primary, first.Role);
        first.Dispose();

        using var second = SingleInstanceElection.Run(enabled: true, path);
        Assert.Equal(SingleInstanceRole.Primary, second.Role);
    }

    /// <summary>
    /// A named mutex outlives its owner for as long as any handle is open, so a
    /// secondary that kept its losing handle would keep the name alive after
    /// the primary exited - and the next launch would elect itself secondary
    /// with nobody left to forward to.
    /// </summary>
    [Fact]
    public void SecondarysHandleDoesNotOutlivePrimary()
    {
        var path = UniqueExePath();

        var primary = SingleInstanceElection.Run(enabled: true, path);
        using var secondary = SingleInstanceElection.Run(enabled: true, path);
        Assert.Equal(SingleInstanceRole.Secondary, secondary.Role);
        Assert.Null(secondary.Mutex);

        primary.Dispose();

        using var third = SingleInstanceElection.Run(enabled: true, path);
        Assert.Equal(SingleInstanceRole.Primary, third.Role);
    }

    /// <summary>
    /// The property the whole change rests on. A probe ("does the mutex
    /// exist?") followed by an election is two decisions with a gap between
    /// them, and racing launches can both read "no primary" in that gap.
    /// Creating the mutex IS the decision, so no spacing produces two primaries.
    /// </summary>
    [Fact]
    public void ConcurrentElections_ProduceExactlyOnePrimary()
    {
        var path = UniqueExePath();
        const int racers = 16;

        // Dedicated threads, not Parallel.For. The barrier requires all 16 to
        // be running at once, and Parallel.For grows its replica count only as
        // replicas start work - so blocked racers never free a pool thread for
        // the next one, and the run stalls (or hangs outright on a pool capped
        // below 16) waiting on the starvation heuristic.
        using var ready = new Barrier(racers);
        var elections = new SingleInstanceElection[racers];
        var threads = new Thread[racers];

        for (var i = 0; i < racers; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                // Release them together, so they contend rather than queue.
                ready.SignalAndWait();
                elections[index] = SingleInstanceElection.Run(enabled: true, path);
            })
            { IsBackground = true };
        }

        foreach (var thread in threads) thread.Start();

        try
        {
            foreach (var thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a racer never finished");

            Assert.Equal(1, elections.Count(e => e.Role == SingleInstanceRole.Primary));
            Assert.Equal(racers - 1, elections.Count(e => e.Role == SingleInstanceRole.Secondary));
        }
        finally
        {
            foreach (var e in elections) e?.Dispose();
        }
    }

    /// <summary>
    /// The election asks whether single-instance is on for THIS process, not
    /// whether a mutex happens to exist. A primary started with the setting on
    /// holds its mutex for its whole lifetime regardless of later config edits,
    /// so a launch with the setting off must be unaffected by it.
    /// </summary>
    [Fact]
    public void FeatureOff_IsUnaffectedByALivePrimary()
    {
        var path = UniqueExePath();

        using var primary = SingleInstanceElection.Run(enabled: true, path);
        using var later = SingleInstanceElection.Run(enabled: false, path);

        Assert.Equal(SingleInstanceRole.Primary, primary.Role);
        Assert.Equal(SingleInstanceRole.Disabled, later.Role);
    }

    [Fact]
    public void DifferentExePaths_DoNotContend()
    {
        using var a = SingleInstanceElection.Run(enabled: true, UniqueExePath());
        using var b = SingleInstanceElection.Run(enabled: true, UniqueExePath());

        Assert.Equal(SingleInstanceRole.Primary, a.Role);
        Assert.Equal(SingleInstanceRole.Primary, b.Role);
    }

    /// <summary>
    /// An unusable name is the one input that makes the mutex throw. The
    /// election must carry the failure rather than raise it: it runs before
    /// there is a logger, and a launch must not be lost over a coordination
    /// primitive.
    /// </summary>
    [Fact]
    public void ElectionFailure_IsCarriedNotThrown()
    {
        // SingleInstanceNames hashes the path, so no path can produce a bad
        // name. Contend against a name already taken by an object of another
        // type instead: Windows keeps mutexes, semaphores and events in one
        // namespace, and CreateMutexEx reports ERROR_INVALID_HANDLE for a kind
        // mismatch, which .NET surfaces as WaitHandleCannotBeOpenedException.
        var path = UniqueExePath();
        var names = SingleInstanceNames.ForProcess(path);
        using var blocker = new Semaphore(1, 1, names.Mutex);

        using var election = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Failed, election.Role);
        Assert.IsType<WaitHandleCannotBeOpenedException>(election.Failure);
        Assert.Null(election.Mutex);
    }

    /// <summary>
    /// Pins the splash gate's semantics. The defect this replaced lived at the
    /// call site rather than in the election, and the call site is in the WinUI
    /// assembly where no test can reach it - so the decision is a property here
    /// and this is what would fail if it drifted.
    /// </summary>
    [Theory]
    [InlineData(SingleInstanceRole.Disabled, true)]
    [InlineData(SingleInstanceRole.Primary, true)]
    [InlineData(SingleInstanceRole.Failed, true)]
    [InlineData(SingleInstanceRole.Secondary, false)]
    public void OnlyASecondarySuppressesTheSplash(SingleInstanceRole role, bool expected)
    {
        var (election, incumbent) = ElectionWithRole(role);
        try
        {
            Assert.Equal(role, election.Role);
            Assert.Equal(expected, election.ShouldShowLaunchSplash);
        }
        finally
        {
            election.Dispose();
            incumbent?.Dispose();
        }
    }

    /// <summary>
    /// A role added without updating the theory above would get no coverage
    /// and no failure, and would also fall through the gate in App silently.
    /// </summary>
    [Fact]
    public void EveryRoleIsCoveredByTheSplashTheory()
    {
        Assert.Equal(4, Enum.GetValues<SingleInstanceRole>().Length);
    }

    /// <summary>
    /// The warm seam is read through this property, not alongside it, so a
    /// decision type that drifted away from the election would still compile
    /// and every launch would splash again. Pinned here rather than over the
    /// decision type alone because this is the member the shell reads.
    /// </summary>
    [Fact]
    public void TheSplashGate_ReadsTheInstalledWarmProbe()
    {
        var (election, incumbent) = ElectionWithRole(SingleInstanceRole.Primary);
        try
        {
            LaunchSplashDecision.WarmSessionProbe = () => true;
            try
            {
                Assert.False(election.ShouldShowLaunchSplash);
            }
            finally
            {
                LaunchSplashDecision.WarmSessionProbe = null;
            }

            // Reset rather than assumed: the false half only means something
            // if the probe really is gone, and a leak here would suppress the
            // splash for the rest of the run.
            Assert.Null(LaunchSplashDecision.WarmSessionProbe);
            Assert.True(election.ShouldShowLaunchSplash);
        }
        finally
        {
            election.Dispose();
            incumbent?.Dispose();
        }
    }

    // ---- test-marker scoping (issue #1094) ---------------------------
    //
    // Single-instance is on by default, so the identity has to keep a launch
    // under WINTTY_TEST_CONFIG away from every instance that is NOT under it
    // (the founder's real app above all), and away from other armed launches
    // with their own throwaway config roots. These drive the real election
    // against the real mutex with only the guard's environment swapped.

    /// <summary>
    /// The guard's env seam pointed at a dictionary, unset names still reading
    /// the real environment. The same pattern Config.TestConfigGuardTests
    /// uses: mutating the real process environment races every concurrently
    /// running collection, and these tests live in this class precisely so
    /// the swap is serialized against the elections above.
    /// </summary>
    private sealed class FakeEnvironment : IDisposable
    {
        private readonly Func<string, string?> _previous =
            Ghostty.Core.Config.TestConfigGuard.ReadEnvironment;
        private readonly Dictionary<string, string?> _values = new();

        public FakeEnvironment() =>
            Ghostty.Core.Config.TestConfigGuard.ReadEnvironment = Read;

        private string? Read(string name) =>
            _values.TryGetValue(name, out var value) ? value : _previous(name);

        public void Set(string name, string? value) => _values[name] = value;

        public void Dispose() =>
            Ghostty.Core.Config.TestConfigGuard.ReadEnvironment = _previous;
    }

    [Fact]
    public void ArmedLaunchesWithDifferentTestRoots_AreSeparateProcesses()
    {
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "1");
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\a");

        using var incumbent = SingleInstanceElection.Run(enabled: true, path);

        // A second harness arms the same marker over its own random root.
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\b");
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Primary, challenger.Role);
    }

    /// <summary>
    /// #1094 review M1: the same user logged on in two terminal-services
    /// sessions (console plus RDP of one account). Each session's Local\
    /// namespace elects its own primary, and the pipe name is machine-global,
    /// so without the session id in the material session 2's launches would
    /// connect into session 1's pipe and exit "served" with no window ever
    /// appearing in session 2. Two session ids must be two elections.
    /// </summary>
    [Fact]
    public void DifferentTerminalSessions_ElectSeparatePrimaries()
    {
        var path = UniqueExePath();

        var previousSession = SingleInstanceNames.ReadSessionId;
        try
        {
            using var env = new FakeEnvironment();
            env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "0");

            SingleInstanceNames.ReadSessionId = () => 1;
            using var incumbent = SingleInstanceElection.Run(enabled: true, path);

            SingleInstanceNames.ReadSessionId = () => 2;
            using var challenger = SingleInstanceElection.Run(enabled: true, path);

            Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
            Assert.Equal(SingleInstanceRole.Primary, challenger.Role);
        }
        finally
        {
            SingleInstanceNames.ReadSessionId = previousSession;
        }
    }

    /// <summary>
    /// #1094 review L1: the misconfigured-harness direction. A launch with a
    /// non-default XDG_CONFIG_HOME but NO test marker must be its own
    /// process rather than forward into the real instance that holds the
    /// default-root identity.
    /// </summary>
    [Fact]
    public void UnarmedLaunch_WithANonDefaultRoot_IsItsOwnProcess()
    {
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "0");
        // No XDG override: the incumbent resolves the real default root,
        // which is what the founder's running app holds.
        using var incumbent = SingleInstanceElection.Run(enabled: true, path);

        // The harness that redirected its config but forgot the marker.
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\forgotten");
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Primary, challenger.Role);
    }

    /// <summary>
    /// The absolute form of the isolation rule: the marker alone keeps an
    /// armed launch off an unarmed election even when both resolve the very
    /// same config root.
    /// </summary>
    [Fact]
    public void ArmedAndUnarmed_SharingARoot_NeverForwardToEachOther()
    {
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\same");

        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "0");
        using var incumbent = SingleInstanceElection.Run(enabled: true, path);

        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "1");
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Primary, challenger.Role);
    }

    [Fact]
    public void ArmedLaunch_NeverForwardsToAnUnarmedIncumbent()
    {
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        // The incumbent is the founder's real app: no test marker, no test
        // root, the product default identity for this exe and edition.
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "0");
        using var incumbent = SingleInstanceElection.Run(enabled: true, path);

        // The armed launch may share the exe, the edition and even the config
        // root spelling; the marker alone must keep it off the incumbent's
        // election, or a test harness hands its window to the real app.
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "1");
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\a");
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Primary, challenger.Role);
    }

    [Fact]
    public void ArmedLaunchesSharingATestRoot_ForwardToEachOther()
    {
        // One harness, one root, two launches: the second must still forward
        // (splash-single-instance-race.ps1 builds exactly this shape), so the
        // test scope is the config root, not the marker alone.
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "1");
        env.Set("XDG_CONFIG_HOME", @"C:\wintty-test-roots\a");

        using var incumbent = SingleInstanceElection.Run(enabled: true, path);
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Secondary, challenger.Role);
    }

    [Fact]
    public void UnarmedLaunchesOfOneEdition_ForwardToEachOther()
    {
        // The product default with no test marker: one edition, one exe,
        // one process. This is the behaviour a user installs.
        var path = UniqueExePath();

        using var env = new FakeEnvironment();
        env.Set(Ghostty.Core.Config.TestConfigGuard.EnvVar, "0");

        using var incumbent = SingleInstanceElection.Run(enabled: true, path);
        using var challenger = SingleInstanceElection.Run(enabled: true, path);

        Assert.Equal(SingleInstanceRole.Primary, incumbent.Role);
        Assert.Equal(SingleInstanceRole.Secondary, challenger.Role);
    }

    /// <summary>
    /// Drive a real election into <paramref name="role"/>, so the theory above
    /// tests reachable states rather than a hand-built object.
    /// </summary>
    /// <returns>
    /// The election, plus whatever has to stay reachable to hold it in that
    /// role. Returned rather than discarded: an unrooted incumbent can be
    /// collected between the two calls, its handle finalized, the name
    /// released - and the election under test comes back Primary instead.
    /// </returns>
    private static (SingleInstanceElection Election, IDisposable? Incumbent) ElectionWithRole(
        SingleInstanceRole role)
    {
        var path = UniqueExePath();
        switch (role)
        {
            case SingleInstanceRole.Disabled:
                return (SingleInstanceElection.Run(enabled: false, path), null);

            case SingleInstanceRole.Primary:
                return (SingleInstanceElection.Run(enabled: true, path), null);

            case SingleInstanceRole.Secondary:
                var incumbent = SingleInstanceElection.Run(enabled: true, path);
                return (SingleInstanceElection.Run(enabled: true, path), incumbent);

            case SingleInstanceRole.Failed:
                // A kind mismatch on the name is what makes the mutex throw.
                var blocker = new Semaphore(1, 1, SingleInstanceNames.ForProcess(path).Mutex);
                return (SingleInstanceElection.Run(enabled: true, path), blocker);

            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }
    }
}
