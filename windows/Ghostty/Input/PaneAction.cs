namespace Ghostty.Input;

/// <summary>
/// The vocabulary of pane-related actions a key binding can invoke.
/// Bindings map a chord to one of these; <see cref="PaneActionRouter"/>
/// dispatches each value to the corresponding <see cref="Panes.PaneHost"/>
/// method.
///
/// Adding a new bindable pane operation is two changes: add a value
/// here, add a case in <see cref="PaneActionRouter.Invoke"/>. Bindings
/// in <see cref="KeyBindings"/> can then reference it.
/// </summary>
internal enum PaneAction
{
    SplitVertical,
    SplitHorizontal,
    ClosePane,
    FocusLeft,
    FocusRight,
    FocusUp,
    FocusDown,
}
