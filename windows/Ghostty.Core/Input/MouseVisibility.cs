namespace Ghostty.Core.Input;

/// <summary>
/// Mirror of <c>ghostty_action_mouse_visibility_e</c> from
/// <c>include/ghostty.h</c>. Ordinals are checked against the header
/// itself by <c>GhosttyActionTagHeaderParityTests</c>; do not renumber
/// without updating the upstream enum first.
/// </summary>
public enum MouseVisibility
{
    Visible = 0,
    Hidden = 1,
}
