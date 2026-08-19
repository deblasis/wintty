using System;
using Ghostty.Controls.Settings;
using Ghostty.Core.Settings;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Ghostty.Accessibility;

/// <summary>
/// UIA peer for a gradient point handle. Reports the handle as a Thumb -
/// the control type for something a user drags - names it by its ordinal,
/// and carries its position through the Value pattern.
///
/// Value rather than RangeValue: a gradient point is two coordinates, and
/// RangeValue is one number. Rather than pick an axis and leave the other
/// unreachable, the whole position is one readable string that a client can
/// also write back.
/// </summary>
internal sealed partial class GradientPointHandleAutomationPeer
    : FrameworkElementAutomationPeer, IValueProvider
{
    private readonly GradientPointHandle _owner;

    internal GradientPointHandleAutomationPeer(GradientPointHandle owner)
        : base(owner) => _owner = owner;

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Thumb;

    protected override string GetClassNameCore() => nameof(GradientPointHandle);

    /// <summary>
    /// NVDA reads the raw type as "thumb control", which says how it is
    /// operated and not what it is.
    /// </summary>
    protected override string GetLocalizedControlTypeCore() => "gradient point";

    protected override string GetNameCore()
    {
        var explicitName = base.GetNameCore();
        return string.IsNullOrEmpty(explicitName) ? _owner.AccessibleName : explicitName;
    }

    /// <summary>
    /// Neither key is discoverable from the canvas, and the visible hint
    /// above it mentions only the mouse.
    /// </summary>
    protected override string GetHelpTextCore() =>
        "Arrow keys move the point. Delete removes it.";

    protected override string GetAutomationIdCore() => $"GradientPoint{_owner.Index + 1}";

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Value
            ? this
            : base.GetPatternCore(patternInterface);

    /// <summary>
    /// IValueProvider ties writability to the element being enabled, and a
    /// client checks this before it bothers calling SetValue.
    /// </summary>
    public bool IsReadOnly => !IsEnabled();

    public string Value => _owner.PositionText;

    /// <summary>
    /// Accepts a whole written position: the spoken form ("35% across, 60%
    /// down") or a bare pair ("35, 60"). Anything else throws, because the
    /// parse is the only thing between a client and a config write.
    ///
    /// Out-of-range numbers clamp rather than throw - a client asking for
    /// 150% wants the edge - but a disabled element, an unreadable value, or
    /// a request the editor refuses all surface as exceptions. Returning
    /// quietly would leave a client believing a move it can never observe.
    /// </summary>
    public void SetValue(string value)
    {
        if (!IsEnabled()) throw new ElementNotEnabledException();

        if (!GradientPointsLogic.TryParsePosition(value, out var x, out var y))
            throw new ArgumentException(
                $"'{value}' is not a gradient point position.", nameof(value));

        // Writing the exact position back is a no-op, not a move. Compared on
        // coordinates and not on the spoken text: the text is whole percents,
        // so comparing it would also swallow a deliberate snap from 35.2% to
        // 35%, which is a real move a client is likely to ask for.
        if (x == _owner.X && y == _owner.Y) return;

        if (!_owner.RequestPosition(x, y))
            throw new InvalidOperationException(
                "The gradient point could not be moved: the handle is no longer "
                + "on the canvas, or a drag is in progress.");
    }

    /// <summary>
    /// Raise the change so a client polling nothing still hears the move.
    /// Dragging, arrow keys and SetValue all land here.
    /// </summary>
    internal void NotifyValueChanged(string oldPosition, string newPosition)
    {
        if (oldPosition == newPosition) return;

        // Gated like TerminalAutomationPeer's raises: a raise with nobody
        // advised still crosses the UIA boundary, and a drag reaches here
        // once per whole percent it crosses.
        if (!ListenerExists(AutomationEvents.PropertyChanged)) return;

        RaisePropertyChangedEvent(
            ValuePatternIdentifiers.ValueProperty, oldPosition, newPosition);
    }
}
