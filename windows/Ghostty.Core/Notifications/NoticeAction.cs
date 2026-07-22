using System;

namespace Ghostty.Core.Notifications;

/// <summary>
/// A button on a <see cref="Notice"/>. Informational notices carry none (just
/// the close affordance); user-choice notices carry one or more — e.g.
/// "Allow"/"Cancel" or a single "Don't show again".
/// </summary>
/// <param name="Label">Button text.</param>
/// <param name="Invoke">
/// Runs when the button is clicked. A notice shown in multiple windows renders
/// one button per window; a <see cref="DismissesNotice"/>=true action removes
/// the notice from every window before another copy can be clicked, but a
/// non-dismissing action can be invoked once per window, so such actions must be
/// idempotent.
/// </param>
/// <param name="IsPrimary">Render as the accent/default button.</param>
/// <param name="DismissesNotice">
/// Remove the notice after <paramref name="Invoke"/> runs. True for typical
/// actions (a choice resolves the notice); set false for actions that leave
/// the notice on screen (which must then be idempotent — see
/// <paramref name="Invoke"/>).
/// </param>
public sealed record NoticeAction(
    string Label,
    Action Invoke,
    bool IsPrimary = false,
    bool DismissesNotice = true);
