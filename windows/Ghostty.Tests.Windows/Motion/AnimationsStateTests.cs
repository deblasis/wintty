using System;
using Ghostty.Core.Version;
using Ghostty.Motion;
using Ghostty.Services;
using Xunit;

namespace Ghostty.Tests.Windows;

/// <summary>
/// The support dump's state read is fresh per ask. The version dialog's
/// copy handler composes the payload per click through AnimationsState,
/// so the freshness the paste depends on lives here: the same ask
/// answered twice across a resolved-state change must carry the new
/// coded value, in the vocabulary the renderer prints.
///
/// AnimationsState is compiled from the same source the app builds (see
/// the csproj links), and the resolved state is driven through the
/// pane-motion coordinator -- the one seat whose answer does not depend
/// on the host's OS animation setting, which the legacy seats' Full
/// answers do. A test green only on a machine with system animations on
/// is a flake waiting for a reduced-motion host. The wiring half (the
/// dialog asking the compose per copy) is pinned in Ghostty.Tests'
/// AnimationsWiringTests; the dialog itself is WinUI and cannot be hosted
/// here.
/// </summary>
[Collection("PaneMotionSerial")]
public sealed partial class AnimationsStateTests : IDisposable
{
    private sealed partial class FixedPolicy : IPaneMotionCoordinator
    {
        private readonly MotionPolicyLevel _level;

        public FixedPolicy(MotionPolicyLevel level) => _level = level;

        public MotionPolicyLevel ResolvePolicy(MotionSurfaceClass surface) => _level;

        public void OnPaneTreeChanging(PaneTreeChange change) { }

        public void OnPaneTreeChanged(PaneTreeChange change) { }

        public void OnFocusTransfer(FocusTransfer transfer) { }

        public void OnOverlayOpening(OverlayKind kind) { }

        public void OnOverlayDismissed(OverlayKind kind) { }
    }

    public AnimationsStateTests()
    {
        PaneMotion.ResetForTests();
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
    }

    public void Dispose()
    {
        PaneMotion.ResetForTests();
        MotionGating.SetUserLeverSource(null);
        MotionGating.SetPowerSeatSource(null);
    }

    [Fact]
    public void The_dump_state_is_read_fresh_per_ask()
    {
        PaneMotion.Register(new FixedPolicy(MotionPolicyLevel.Full));
        var before = AnimationsState.Code();

        // The state changes under the open dialog; the next ask must see
        // it, not an answer remembered from the first one.
        PaneMotion.ResetForTests();
        PaneMotion.Register(new FixedPolicy(MotionPolicyLevel.Off));
        var after = AnimationsState.Code();

        Assert.Equal("full", before);
        Assert.Equal("off", after);
    }

    [Fact]
    public void The_fresh_code_is_what_the_payload_prints()
    {
        // The fit: the vocabulary Code() answers is exactly what the
        // renderer's animations field prints, so a re-resolve at copy time
        // is a paste that carries the new value -- not a line the renderer
        // silently drops. Build() itself is not callable here (it reads
        // libghostty through the FFI no test host loads), so the payload is
        // rendered over a synthetic info the way VersionRendererAnimationsTests
        // does; what this adds is the state read feeding it.
        PaneMotion.Register(new FixedPolicy(MotionPolicyLevel.Off));
        var payload = VersionRenderer.RenderPlain(Sample(AnimationsState.Code()));

        Assert.Contains("  animations:     off\n", payload, StringComparison.Ordinal);
    }

    private static VersionInfo Sample(string animations) => new(
        WinttyVersion:       "0.0.0",
        BuildLabel:          "",
        WinttyVersionString: "0.0.0-tip+abc1234",
        WinttyCommit:        "abc1234",
        Edition:             Edition.Oss,
        LibGhostty: new LibGhosttyBuildInfo(
            Version:       "0.0.0",
            VersionString: "0.0.0-tip+abc1234",
            Commit:        "abc1234",
            Channel:       "tip",
            ZigVersion:    "0.0.0",
            BuildMode:     "ReleaseFast"),
        DotnetRuntime:   "10.0.0",
        MsbuildConfig:   "Debug",
        AppRuntime:      "WinUI 3",
        Renderer:        "DX12",
        FontEngine:      "DirectWrite",
        WindowsVersion:  "11.0.0",
        Architecture:    "x64",
        Animations:      animations);
}

[CollectionDefinition("PaneMotionSerial", DisableParallelization = true)]
public sealed class PaneMotionSerialCollection { }
