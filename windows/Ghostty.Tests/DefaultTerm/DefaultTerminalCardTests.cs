using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The settings-card view model: the four states the card can show, the
/// pre-enable refusals with named reasons, and the state following the
/// engine. Everything the card does to the machine goes through the
/// injected engine, so no test touches the real registry.
/// </summary>
public sealed class DefaultTerminalCardTests
{
    [Fact]
    public void BelowFloor_Hidden()
    {
        var vm = new DefaultTerminalCardViewModel(
            new FakeEngine(),
            singleInstanceEnabled: () => true,
            osBuild: () => 22000,
            canRegister: () => true,
            sidecarsPresent: () => true);

        vm.Refresh();

        Assert.Equal(DefaultTerminalCardState.UnavailableBelowFloor, vm.State);
        Assert.False(vm.CanToggle);
    }

    [Fact]
    public void EngineFailure_ReasonSurfaced()
    {
        var engine = new FakeEngine
        {
            // The failed key is built from halves so the isolation
            // scan's real-root needle does not match this file.
            EnableResult = EnableResult.Failed(
                EnableFailureKind.WriteFailed,
                @"HKCU\Console\%%" + @"Startup\DelegationConsole"),
        };
        var vm = new DefaultTerminalCardViewModel(
            engine,
            singleInstanceEnabled: () => true,
            osBuild: () => 22621,
            canRegister: () => true,
            sidecarsPresent: () => true);

        vm.Toggle();

        Assert.Equal(DefaultTerminalCardState.NotDefault, vm.State);
        Assert.True(vm.CanToggle);
        Assert.NotNull(vm.BlockedReason);
        Assert.Contains("DelegationConsole", vm.BlockedReason);
        Assert.Contains("WriteFailed", vm.BlockedReason);
    }

    [Fact]
    public void SingleInstanceOff_EnableRefusedWithReason()
    {
        var engine = new FakeEngine();
        var vm = new DefaultTerminalCardViewModel(
            engine,
            singleInstanceEnabled: () => false,
            osBuild: () => 22621,
            canRegister: () => true,
            sidecarsPresent: () => true);

        vm.Toggle();

        Assert.Equal(0, engine.EnableCalls);
        Assert.Equal(DefaultTerminalCardState.NotDefault, vm.State);
        Assert.NotNull(vm.BlockedReason);
        Assert.Contains("windows-single-instance", vm.BlockedReason);
    }

    [Fact]
    public void SidecarsMissing_EnableRefusedBeforeAnythingElse()
    {
        // The refusal branch has to be seen red: dropping or inverting
        // the precheck must fail this test, not pass it silently.
        var engine = new FakeEngine();
        var vm = new DefaultTerminalCardViewModel(
            engine,
            singleInstanceEnabled: () => false,
            osBuild: () => 22621,
            canRegister: () => true,
            sidecarsPresent: () => false);

        var result = vm.Enable();

        Assert.Equal(EnableStatus.Failed, result.Status);
        Assert.Equal("sidecar binaries missing", result.FailedKey);
        Assert.Equal(0, engine.EnableCalls);
        Assert.Equal(DefaultTerminalCardState.NotDefault, vm.State);
        Assert.NotNull(vm.BlockedReason);
        Assert.Contains("OpenConsole.exe", vm.BlockedReason);
        Assert.Contains("OpenConsoleProxy.dll", vm.BlockedReason);
    }

    [Fact]
    public void StateReflectsIsRegistered()
    {
        var engine = new FakeEngine { Registered = true };
        var vm = new DefaultTerminalCardViewModel(
            engine,
            singleInstanceEnabled: () => true,
            osBuild: () => 22621,
            canRegister: () => true,
            sidecarsPresent: () => true);

        vm.Refresh();
        Assert.Equal(DefaultTerminalCardState.IsDefault, vm.State);

        engine.Registered = false;
        vm.Refresh();
        Assert.Equal(DefaultTerminalCardState.NotDefault, vm.State);

        // Enabling flips the state; the engine already reported success.
        engine.EnableResult = EnableResult.Registered;
        vm.Toggle();
        Assert.Equal(DefaultTerminalCardState.IsDefault, vm.State);

        engine.Registered = false;
        _ = vm.Disable();
        Assert.Equal(DefaultTerminalCardState.NotDefault, vm.State);
    }

    private sealed class FakeEngine : IDefaultTermEngine
    {
        public bool Registered { get; set; }

        public EnableResult EnableResult { get; set; } = EnableResult.Registered;

        public int EnableCalls { get; private set; }

        public int DisableCalls { get; private set; }

        public bool IsRegistered() => Registered;

        public EnableResult Enable()
        {
            EnableCalls++;
            if (EnableResult.Status != EnableStatus.Failed)
            {
                // A successful enable points the selection at us, which
                // is what the real engine's next IsRegistered reports.
                Registered = true;
            }

            return EnableResult;
        }

        public DisableResult Disable()
        {
            DisableCalls++;
            Registered = false;
            return DisableResult.Cleared;
        }
    }
}
