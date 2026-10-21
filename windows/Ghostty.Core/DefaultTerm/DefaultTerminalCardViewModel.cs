using System.Windows.Input;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// What the settings card shows. <see cref="ManagedByPolicy"/> is
/// reserved for the enterprise policy applier, which is compile-time
/// gated and lives outside this module; no other build reports it.
/// </summary>
public enum DefaultTerminalCardState
{
    IsDefault,
    NotDefault,
    ManagedByPolicy,
    UnavailableBelowFloor,
}

/// <summary>
/// The engine surface the card consumes, so the card is testable
/// without a registry. <see cref="DefaultTermRegistration"/> implements
/// it over the real per-user state.
/// </summary>
public interface IDefaultTermEngine
{
    bool IsRegistered();

    EnableResult Enable();

    DisableResult Disable();
}

/// <summary>
/// The view model of the default-terminal settings card: the state, the
/// toggle, and the refusal reasons the design's enable-time checks
/// produce. All machine effects go through the injected engine; the
/// floor and single-instance checks run before any engine call, each
/// refusing with a named reason.
/// </summary>
public sealed class DefaultTerminalCardViewModel
{
    /// <summary>The first build that honors the delegation keys.</summary>
    public const int MinimumOsBuild = 22621;

    private readonly IDefaultTermEngine _engine;
    private readonly Func<bool> _singleInstanceEnabled;
    private readonly Func<int> _osBuild;

    public DefaultTerminalCardViewModel(
        IDefaultTermEngine engine,
        Func<bool> singleInstanceEnabled,
        Func<int> osBuild)
    {
        _engine = engine;
        _singleInstanceEnabled = singleInstanceEnabled;
        _osBuild = osBuild;
        ToggleCommand = new CardToggleCommand(this);
        Refresh();
    }

    public DefaultTerminalCardState State { get; private set; }

    public bool CanToggle { get; private set; }

    /// <summary>
    /// Why the last action refused or failed; null after a clean state.
    /// </summary>
    public string? BlockedReason { get; private set; }

    public ICommand ToggleCommand { get; }

    /// <summary>
    /// Recompute the state from the engine. Below the OS floor the card
    /// is hidden, which the page expresses by collapsing its row.
    /// </summary>
    public void Refresh()
    {
        BlockedReason = null;
        if (_osBuild() < MinimumOsBuild)
        {
            State = DefaultTerminalCardState.UnavailableBelowFloor;
            CanToggle = false;
            return;
        }

        State = _engine.IsRegistered()
            ? DefaultTerminalCardState.IsDefault
            : DefaultTerminalCardState.NotDefault;
        CanToggle = true;
    }

    /// <summary>
    /// Enable when not default, disable when default; the outcome is the
    /// refreshed <see cref="State"/> and <see cref="BlockedReason"/>. The
    /// single-instance requirement is checked before any engine call: the
    /// receiver is the elected primary, so enabling without it would
    /// leave a registration whose routing model is unsatisfiable.
    /// </summary>
    public void Toggle()
    {
        if (!CanToggle)
        {
            return;
        }

        if (State == DefaultTerminalCardState.IsDefault)
        {
            _ = Disable();
            return;
        }

        _ = Enable();
    }

    public EnableResult Enable()
    {
        if (!_singleInstanceEnabled())
        {
            BlockedReason =
                "windows-single-instance mode is required: the default " +
                "terminal receiver is the elected primary instance";
            return EnableResult.Failed(
                EnableFailureKind.None, "windows-single-instance");
        }

        var result = _engine.Enable();
        if (result.Status == EnableStatus.Failed)
        {
            BlockedReason =
                $"enable failed ({result.FailureKind}) at {result.FailedKey}";
            return result;
        }

        Refresh();
        return result;
    }

    public DisableResult Disable()
    {
        var result = _engine.Disable();
        Refresh();
        return result;
    }

    private sealed class CardToggleCommand : ICommand
    {
        private readonly DefaultTerminalCardViewModel _owner;

        public CardToggleCommand(DefaultTerminalCardViewModel owner) => _owner = owner;

#pragma warning disable 67
        public event EventHandler? CanExecuteChanged;
#pragma warning restore 67
        public bool CanExecute(object? parameter) => _owner.CanToggle;

        public void Execute(object? parameter) => _owner.Toggle();
    }
}
