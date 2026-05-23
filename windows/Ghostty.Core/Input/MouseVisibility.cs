namespace Ghostty.Core.Input;

/// <summary>
/// Mirror of <c>ghostty_action_mouse_visibility_e</c> from
/// <c>include/ghostty.h</c>. Ordinals are pinned by
/// <c>MouseVisibilityEnumTests</c>; do not renumber without updating
/// the upstream enum first.
///
/// To re-verify against upstream after a rebase:
///   grep -nE "GHOSTTY_MOUSE_(VISIBLE|HIDDEN)" include/ghostty.h
/// </summary>
public enum MouseVisibility
{
    Visible = 0,
    Hidden = 1,
}
