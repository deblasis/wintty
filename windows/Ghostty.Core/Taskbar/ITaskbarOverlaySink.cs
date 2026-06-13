namespace Ghostty.Core.Taskbar;

/// <summary>
/// Narrow surface the <see cref="TaskbarAttentionCoordinator"/> writes
/// to. Implemented in the WinUI project by a facade forwarding to
/// <c>ITaskbarList3::SetOverlayIcon</c>. Tests use a recording fake.
///
/// Pure Ghostty.Core — no WinUI types so the coordinator is unit-
/// testable without dragging WinAppSDK in.
/// </summary>
internal interface ITaskbarOverlaySink
{
    /// <summary>Show (<paramref name="active"/> == true) or clear the
    /// attention overlay badge. Expected to be idempotent.</summary>
    void SetAttention(bool active);
}
