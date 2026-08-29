using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Tabs;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// The motion-state contract, fuzzed headlessly. The pin flight and
/// the settle springs are decoration: no animation path touches the
/// manager, so whether motion is on or off, the sequence of manager
/// operations is the whole state story. That makes the state contract
/// testable without a WinUI host -- the two landing shapes the drag
/// layer actually commits, replayed as randomized scripts, must hold
/// their promises exactly, deterministically, whatever order they
/// arrive in.
///
/// The two shapes, exactly as the strip commits them. A mid-drag
/// hysteresis crossing classifies, pins, and then MOVES the row to the
/// slot the crossing named -- the row lands where the user was looking
/// when the hysteresis fired. A release on the preview classifies at
/// the prefix's last slot and pins WITHOUT a move -- the row lands at
/// the prefix end, exactly where the ghost sat. Neither shape may be
/// reshaped into the other: each matches what the user was shown at
/// that moment.
///
/// What this fuzz deliberately is not: a pixel-level proof that motion
/// off renders identically. That is the live-strip second pass; here
/// the claim is narrower and load-bearing -- state correctness never
/// waits on motion, and the shapes hold under any random order.
/// </summary>
public class TabPinMotionStateFuzzTests
{
    private static TabManager NewManager() => new(_ => new FakePaneHost());

    /// <summary>
    /// Default seed baked in for reproducibility; the env override lets
    /// a failing run be re-driven with the seed that broke it without a
    /// rebuild.
    /// </summary>
    private static int SeedFor()
    {
        var text = Environment.GetEnvironmentVariable("WINTTY_MOTION_FUZZ_SEED");
        return int.TryParse(text, out var seed) ? seed : 20260828;
    }

    private const int TabCount = 5;
    private const int StepsPerSeed = 120;
    private static readonly int[] Seeds = { 0, 1, 2 };

    /// <summary>
    /// One scripted step, manager-independent: the op kind, the dragged
    /// tab by creation order, and the crossing slot. The script is
    /// generated once and applied to each manager separately, so two
    /// replays differ by nothing but the manager under them.
    /// </summary>
    private readonly record struct Step(int Kind, int Tab, int To);

    private const int HysteresisPin = 0;
    private const int ReleasePin = 1;
    private const int Unpin = 2;
    private const int PlainMove = 3;

    private static List<Step> Script(int seed)
    {
        var random = new Random(seed);
        var steps = new List<Step>(StepsPerSeed);
        for (int i = 0; i < StepsPerSeed; i++)
        {
            int kind = random.Next(4);
            // The hysteresis shape only fires when its crossing lands
            // inside the prefix, which is the low end of the slot range;
            // targeting it there is what makes the shape fire often
            // enough to be worth asserting. Everything else stays uniform.
            int to = kind == HysteresisPin ? random.Next(2) : random.Next(TabCount);
            steps.Add(new Step(kind, random.Next(TabCount), to));
        }
        return steps;
    }

    private sealed class Replay
    {
        public TabManager Manager = null!;
        // Creation order -> tab, so a script's Step.Tab names the same
        // logical tab in every replay.
        public List<TabModel> Created = null!;
        public int HysteresisLands;
        public int ReleaseLands;
        public int UnpinLands;
        public List<string> Violations = new();
    }

    private static Replay Run(int seed)
    {
        var replay = new Replay { Manager = NewManager(), Created = new() };
        for (int i = 0; i < TabCount; i++)
            replay.Created.Add(replay.Manager.NewTab());

        foreach (var step in Script(seed))
        {
            var manager = replay.Manager;
            if (step.Tab >= replay.Created.Count) continue;
            var tab = replay.Created[step.Tab];
            if (!manager.Tabs.Contains(tab)) continue;

            // World maintenance, not a shape: neither landing shape can
            // fire at zero pins -- the preview's own gate is "pins
            // exist" -- so a script that unpinned everything would go
            // silent for the rest of its run. The live strip always has
            // the boundary back the moment a user pins again; here the
            // first tab takes the pin.
            if (manager.PinCount == 0)
                manager.SetPinned(replay.Created[0], true);

            switch (step.Kind)
            {
                case HysteresisPin when !tab.IsPinned && manager.PinCount >= 1:
                {
                    var crossing = TabPinBoundary.Classify(
                        draggedIsPinned: false, manager.PinCount,
                        manager.Tabs.Count, step.To);
                    if (crossing.Op != TabPinZoneOp.Pin) break;
                    manager.SetPinned(tab, true);
                    manager.Move(manager.IndexOf(tab), crossing.To);
                    // The shape's promise: the row sits in the slot the
                    // crossing named -- what the hysteresis showed.
                    if (manager.IndexOf(tab) != crossing.To)
                        replay.Violations.Add(
                            $"seed {seed} hysteresis landed {manager.IndexOf(tab)} "
                            + "!= crossing " + crossing.To);
                    replay.HysteresisLands++;
                    break;
                }
                case ReleasePin when !tab.IsPinned && manager.PinCount >= 1:
                {
                    var crossing = TabPinBoundary.Classify(
                        draggedIsPinned: false, manager.PinCount,
                        manager.Tabs.Count, manager.PinCount - 1);
                    if (crossing.Op != TabPinZoneOp.Pin) break;
                    manager.SetPinned(tab, true);
                    // The other promise: the prefix end, exactly where
                    // the preview sat -- no move, no re-derivation.
                    if (manager.IndexOf(tab) != manager.PinCount - 1)
                        replay.Violations.Add(
                            $"seed {seed} release landed {manager.IndexOf(tab)} "
                            + "!= prefix end " + (manager.PinCount - 1));
                    replay.ReleaseLands++;
                    break;
                }
                case Unpin when tab.IsPinned:
                {
                    var crossing = TabPinBoundary.Classify(
                        draggedIsPinned: true, manager.PinCount,
                        manager.Tabs.Count, step.To);
                    if (crossing.Op != TabPinZoneOp.Unpin) break;
                    manager.SetPinned(tab, false);
                    manager.Move(manager.IndexOf(tab), crossing.To);
                    replay.UnpinLands++;
                    break;
                }
                case PlainMove:
                    manager.Move(manager.IndexOf(tab), step.To);
                    break;
            }

            // The invariant every one of these ops must preserve: the
            // pinned prefix is exactly the pinned set -- the manager's
            // clamps and relocations mean nothing if the zones can blur.
            var pinned = manager.Tabs.Count(t => t.IsPinned);
            if (manager.PinCount != pinned)
                replay.Violations.Add(
                    $"seed {seed} PinCount {manager.PinCount} != pinned {pinned}");
            for (int i = 0; i < manager.Tabs.Count; i++)
            {
                if (manager.Tabs[i].IsPinned != (i < manager.PinCount))
                    replay.Violations.Add(
                        $"seed {seed} prefix broken at {i}: "
                        + Snapshot(replay));
            }
        }
        return replay;
    }

    private static string Snapshot(Replay replay) =>
        string.Join("|", replay.Manager.Tabs.Select(
            t => (t.IsPinned ? "P" : "U") + replay.Created.IndexOf(t)));

    [Fact]
    public void TheMotionTokens_AreTheSanctionedValues()
    {
        // The flight and its landing, the fade, and the horizontal
        // lift's shadow. Values live in Core next to the machine so
        // both strips and the tests read one copy.
        Assert.Equal(250, TabStripMotion.PinFlightMs);
        Assert.Equal(0.6f, TabStripMotion.PinSettleDampingRatio);
        Assert.Equal(60, TabStripMotion.PinSettlePeriodMs);
        Assert.Equal(83, TabStripMotion.FadeMs);
        Assert.Equal(83, TabStripMotion.UnliftFadeMs);
        Assert.Equal(16, TabStripMotion.LiftShadowBlurRadiusPx);
        Assert.Equal(4, TabStripMotion.LiftShadowOffsetYPx);
        Assert.Equal(0.25f, TabStripMotion.LiftShadowOpacity);
    }

    [Fact]
    public void TheTwoLandingShapes_KeepTheirPromises_UnderAnyOrder()
    {
        // The shapes are only exercised if they actually fired: a script
        // that skipped every zone crossing would prove nothing, so the
        // coverage is part of the fact.
        var replay = Run(SeedFor());
        Assert.Empty(replay.Violations);
        Assert.True(replay.HysteresisLands > 0, "no hysteresis pin fired");
        Assert.True(replay.ReleaseLands > 0, "no release pin fired");
        Assert.True(replay.UnpinLands > 0, "no unpin fired");
    }

    [Fact]
    public void OneScript_TwoManagers_IdenticalFinalState()
    {
        // Motion is not a state input anywhere in either landing shape,
        // so the same script through two fresh managers is the
        // motion-on/motion-off pair in miniature: if the final states
        // ever diverged, some path would be reading the animation
        // clock into the manager.
        var seed = SeedFor();
        var first = Run(seed);
        var second = Run(seed);
        Assert.Empty(first.Violations);
        Assert.Empty(second.Violations);
        Assert.Equal(Snapshot(first), Snapshot(second));
    }

    [Fact]
    public void ThePrefixInvariant_SurvivesEveryOperation_AcrossSeeds()
    {
        foreach (var offset in Seeds)
        {
            var replay = Run(SeedFor() + offset);
            Assert.Empty(replay.Violations);
        }
    }
}
