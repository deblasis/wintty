using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Ghostty.Core.Profiles.Tracking;

namespace Ghostty.Tests.Windows.Tracking;

/// <summary>
/// Shared setup for the tracker smoke tests, which spawn a real process tree
/// and assert on what <see cref="WindowsActiveProcessTracker"/> reports about
/// it.
///
/// <para>
/// Both tests used to give themselves a flat 8000 ms for "spawn pwsh, let it
/// start a child, and let the tracker notice" and then failed, intermittently
/// and in a different combination each run, on a loaded machine. Neither the
/// spawn nor the noticing has a bound the test controls, and the budget
/// covered both at once, so a slow host was indistinguishable from a broken
/// walker. What this class does about that is split the two and take the
/// load-dependent cadence out of the second one.
/// </para>
/// </summary>
internal static class TrackerSmoke
{
    /// <summary>
    /// Names the xunit collection that serialises the tracker smoke tests
    /// against each other. Each one spawns a process tree and then walks the
    /// WHOLE machine's process list repeatedly; running two at once means
    /// each is measuring a machine the other is loading, which is the one
    /// condition their assertions are least able to survive.
    /// </summary>
    internal const string SerialCollection = "ProcessTracker (serial)";

    /// <summary>Cadence the smoke tests pin the tracker to.</summary>
    internal const int TickIntervalMs = 500;

    /// <summary>
    /// How long the scenario itself gets to come up: pwsh cold start, plus
    /// pwsh launching its child, plus the child exec'ing the leaf. Generous
    /// on purpose -- none of it is the tracker's doing, and blowing this
    /// budget is reported as the scenario failing to start rather than as the
    /// tracker failing to see it. The pwsh spawn gate already allows 3500 ms
    /// for pwsh alone before it skips the test.
    /// </summary>
    internal const int ScenarioBudgetMs = 20_000;

    /// <summary>
    /// Ticks the tracker is allowed once the scenario is demonstrably live.
    /// Two are the floor: the debouncer emits on the SECOND observation of a
    /// value, never the first. A third covers a tick already mid-walk when
    /// the scenario went live. The remaining factor of three is for the timer
    /// callback waiting its turn on a busy thread pool -- it buys slack in
    /// the one dimension that is genuinely not the tracker's fault, and
    /// nine ticks is still bounded, unlike the shipped cadence.
    /// </summary>
    private const int TicksAllowed = 9;

    /// <summary>
    /// A tracker whose cadence does not stretch with machine load.
    /// </summary>
    /// <remarks>
    /// The shipped tracker re-arms at <c>max(interval, 3x the last walk)</c>,
    /// capped at 30s, so on a loaded box two consecutive observations can be
    /// many seconds apart -- and the debouncer needs two of them. That
    /// cadence is deliberate product behaviour (it is what holds the
    /// tracker's idle cost down on a process-heavy machine) but it is not
    /// what these tests are about: they are about the walker, the broker
    /// filter, the debouncer and the event wiring. Pinning the cadence leaves
    /// exactly those under test.
    /// </remarks>
    internal static WindowsActiveProcessTracker NewTracker() =>
        new(TickIntervalMs, TickIntervalMs);

    /// <summary>
    /// The tracker's budget, once <see cref="AwaitScenario"/> has established
    /// that there is something to report. With the cadence pinned, one
    /// observation costs the walk plus the interval, and
    /// <paramref name="walkMs"/> is the worst walk actually measured on this
    /// host seconds ago -- so the deadline tracks the machine rather than
    /// assuming one. Clamped so a pathological host still fails rather than
    /// hangs.
    /// </summary>
    internal static int TrackerDeadlineMs(long walkMs) =>
        (int)Math.Clamp(TicksAllowed * (walkMs + TickIntervalMs), 5_000, 45_000);

    /// <summary>
    /// What <see cref="AwaitScenario"/> found: whether the root ever had a
    /// descendant the caller accepts, which one, the worst single walk seen
    /// while looking, and how long the wait took.
    /// </summary>
    internal readonly record struct Scenario(
        bool Live, string? Exe, long WalkMs, long ElapsedMs);

    /// <summary>
    /// Polls the process tree until <paramref name="rootPid"/> has an
    /// innermost descendant that <paramref name="accept"/> approves.
    /// </summary>
    /// <remarks>
    /// This asks the walker directly rather than going through the tracker,
    /// which keeps two different failures apart: "this host never got as far
    /// as running the scenario" (pwsh slow or its child never started) and
    /// "the scenario ran and the tracker did not report it". Only the second
    /// is a defect in the code under test, and only the second is worth
    /// giving a tight budget to. The walk cost measured here is the same
    /// quantity that drives the tracker's own cadence, so it is what the
    /// tracker's deadline is then sized from.
    /// </remarks>
    internal static async Task<Scenario> AwaitScenario(
        int rootPid, Func<string, bool> accept)
    {
        var overall = Stopwatch.StartNew();
        long worstWalkMs = 0;

        while (overall.ElapsedMilliseconds < ScenarioBudgetMs)
        {
            var walk = Stopwatch.StartNew();
            var infos = ProcessTreeWalker.FindInnermostDescendants(new[] { (uint)rootPid });
            walk.Stop();
            if (walk.ElapsedMilliseconds > worstWalkMs)
                worstWalkMs = walk.ElapsedMilliseconds;

            var exe = infos.TryGetValue((uint)rootPid, out var info)
                ? info?.ExeBasename
                : null;
            if (exe is not null && accept(exe))
                return new Scenario(true, exe, worstWalkMs, overall.ElapsedMilliseconds);

            await Task.Delay(100).ConfigureAwait(false);
        }

        return new Scenario(false, null, worstWalkMs, overall.ElapsedMilliseconds);
    }
}
