using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;
using Ghostty.Motion;
using Ghostty.Services;
using Xunit;

namespace Ghostty.Tests.Motion;

/// <summary>
/// Everything that registers a pane-motion coordinator, and every
/// in-process reader of the gates the registered state now drives,
/// serializes here. PaneMotion is a process-wide set-once static, and the
/// strip gate consults it through the installed route, so two parallel
/// collections could otherwise answer each other's questions: one class
/// mid-test with a coordinator registered would flip another class's
/// gate rows, and two classes registering at once would trip the
/// set-once refusal.
/// </summary>
[CollectionDefinition("PaneMotionSerial", DisableParallelization = true)]
public sealed class PaneMotionSerialCollection { }

/// <summary>
/// MotionGating is the one place the shell's animation gates route
/// through. Two contracts:
///
/// - with no coordinator registered (the default process state), it must
///   answer EXACTLY what the legacy gates answered: on iff system
///   animations are enabled and high contrast is not applied, whatever
///   surface class asks; and
/// - with a coordinator registered, that coordinator's per-surface level
///   wins and the legacy inputs stop mattering.
///
/// Both contracts run in-process against the same source the app
/// compiles (the seam is linked into this assembly), and the strip's own
/// gate is asserted through its installed route, so the end-to-end path
/// is exercised, not just the static.
/// </summary>
[Collection("PaneMotionSerial")]
public sealed class MotionGatingTests
{
    private sealed class PerSurfaceCoordinator : IPaneMotionCoordinator
    {
        private readonly IReadOnlyDictionary<MotionSurfaceClass, MotionPolicyLevel> _levels;

        public PerSurfaceCoordinator(IReadOnlyDictionary<MotionSurfaceClass, MotionPolicyLevel> levels)
            => _levels = levels;

        public List<MotionSurfaceClass> PolicyRequests { get; } = new();

        public MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface)
        {
            PolicyRequests.Add(surface);
            return _levels[surface];
        }

        public void OnPaneTreeChanging(PaneTreeChange change) { }

        public void OnPaneTreeChanged(PaneTreeChange change) { }

        public void OnFocusTransfer(FocusTransfer transfer) { }

        public void OnOverlayOpening(OverlayKind kind) { }

        public void OnOverlayDismissed(OverlayKind kind) { }
    }

    public MotionGatingTests() => PaneMotion.ResetForTests();

    // -- The legacy truth table, all four cells, no coordinator ----------

    [Fact]
    public void With_no_coordinator_Effective_answers_the_legacy_truth_table()
    {
        Assert.False(PaneMotion.Active);

        // All four cells, verbatim: on iff animations are enabled and
        // high contrast is not applied.
        var cells = new (bool AnimationsEnabled, bool HighContrast, MotionPolicyLevel Expected)[]
        {
            (true,  false, MotionPolicyLevel.Full),
            (true,  true,  MotionPolicyLevel.Off),
            (false, false, MotionPolicyLevel.Off),
            (false, true,  MotionPolicyLevel.Off),
        };

        foreach (var cell in cells)
        {
            // The named cell, on the surface the strips ask through.
            Assert.Equal(cell.Expected, MotionGating.Effective(
                MotionSurfaceClass.Chrome, cell.AnimationsEnabled, cell.HighContrast));

            // Inactive, the answer is a function of the two inputs only:
            // every surface class gets the same cell, so a per-surface
            // split cannot sneak in under the legacy behaviour.
            foreach (MotionSurfaceClass surface in Enum.GetValues<MotionSurfaceClass>())
            {
                Assert.Equal(cell.Expected,
                    MotionGating.Effective(surface, cell.AnimationsEnabled, cell.HighContrast));
            }
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void With_no_coordinator_the_strip_gate_answers_the_legacy_cell(
        bool animationsEnabled, bool highContrast, bool expected)
    {
        Assert.False(PaneMotion.Active);

        // End to end: the gate the strips call, through the route the app
        // installs, still answers the legacy truth table.
        Assert.Equal(expected, TabStripMotion.Enabled(animationsEnabled, highContrast));
    }

    // -- The coordinator wins, per surface class -------------------------

    [Fact]
    public void A_registered_coordinator_decides_per_surface_class()
    {
        var levels = new Dictionary<MotionSurfaceClass, MotionPolicyLevel>
        {
            [MotionSurfaceClass.PaneGeometry] = MotionPolicyLevel.Reduced,
            [MotionSurfaceClass.Chrome] = MotionPolicyLevel.Off,
            [MotionSurfaceClass.Overlay] = MotionPolicyLevel.Full,
            [MotionSurfaceClass.Ambient] = MotionPolicyLevel.Reduced,
        };
        var coordinator = new PerSurfaceCoordinator(levels);
        PaneMotion.Register(coordinator);

        foreach (var (surface, level) in levels)
        {
            // The coordinator's level wins whatever the legacy inputs
            // say: inputs that would have answered Full and inputs that
            // would have answered Off both land on its answer.
            Assert.Equal(level,
                MotionGating.Effective(surface, animationsEnabled: true, highContrast: false));
            Assert.Equal(level,
                MotionGating.Effective(surface, animationsEnabled: false, highContrast: true));
        }

        // Every ask reached the coordinator with its surface class intact.
        Assert.Equal(8, coordinator.PolicyRequests.Count);
    }

    [Fact]
    public void The_strip_gate_defers_to_the_coordinator_through_the_installed_route()
    {
        var coordinator = new PerSurfaceCoordinator(
            new Dictionary<MotionSurfaceClass, MotionPolicyLevel>
            {
                [MotionSurfaceClass.Chrome] = MotionPolicyLevel.Off,
            });
        PaneMotion.Register(coordinator);

        // Chrome is Off: a gate the legacy inputs would have passed now
        // cuts, and the coordinator was asked about Chrome, by name.
        Assert.False(TabStripMotion.Enabled(animationsEnabled: true, highContrast: false));
        Assert.Equal(new[] { MotionSurfaceClass.Chrome }, coordinator.PolicyRequests);
    }

    [Fact]
    public void Reduced_still_counts_as_on_for_the_boolean_gates()
    {
        var coordinator = new PerSurfaceCoordinator(
            new Dictionary<MotionSurfaceClass, MotionPolicyLevel>
            {
                [MotionSurfaceClass.Chrome] = MotionPolicyLevel.Reduced,
            });
        PaneMotion.Register(coordinator);

        // Chrome is Reduced: reduced still counts as on for the boolean
        // gates. Off is the only cut, so a coordinator that wants the
        // strip silent says Off, and this row is the pin that says the
        // route's mapping is != Off rather than == Full.
        Assert.True(TabStripMotion.Enabled(animationsEnabled: true, highContrast: false));
        Assert.Equal(new[] { MotionSurfaceClass.Chrome }, coordinator.PolicyRequests);
    }
}
