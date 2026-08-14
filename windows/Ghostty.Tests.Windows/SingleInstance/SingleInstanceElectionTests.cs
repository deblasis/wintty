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
    /// A path no other test (or leftover process) can be holding. The names are
    /// a hash of the path, so a fresh path is a fresh election.
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
        var names = SingleInstanceNames.For(path);
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
                var blocker = new Semaphore(1, 1, SingleInstanceNames.For(path).Mutex);
                return (SingleInstanceElection.Run(enabled: true, path), blocker);

            default:
                throw new ArgumentOutOfRangeException(nameof(role));
        }
    }
}
