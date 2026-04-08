using System;
using Ghostty.Panes;

namespace Ghostty.Input;

/// <summary>
/// Dispatches a <see cref="PaneAction"/> against a target
/// <see cref="PaneHost"/>. Single switch lives here so that adding a
/// new pane action is one place to edit, not per-callsite.
/// </summary>
internal static class PaneActionRouter
{
    public static void Invoke(PaneAction action, PaneHost host)
    {
        switch (action)
        {
            case PaneAction.SplitVertical:
                host.Split(PaneOrientation.Vertical);
                break;
            case PaneAction.SplitHorizontal:
                host.Split(PaneOrientation.Horizontal);
                break;
            case PaneAction.ClosePane:
                host.CloseActive();
                break;
            case PaneAction.FocusLeft:
                host.FocusDirection(FocusDirection.Left);
                break;
            case PaneAction.FocusRight:
                host.FocusDirection(FocusDirection.Right);
                break;
            case PaneAction.FocusUp:
                host.FocusDirection(FocusDirection.Up);
                break;
            case PaneAction.FocusDown:
                host.FocusDirection(FocusDirection.Down);
                break;
            default:
                // Non-exhaustive switch: new PaneAction values must
                // grow a case here. Throwing rather than silently
                // dropping the action surfaces the mistake in a dev
                // build instead of at user report time.
                throw new ArgumentOutOfRangeException(nameof(action), action, null);
        }
    }
}
