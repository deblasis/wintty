using Ghostty.Core.Tabs;

namespace Ghostty.Core.Taskbar;

/// <summary>
/// Narrow surface the <see cref="TaskbarProgressCoordinator"/>
/// writes to. Implemented in the WinUI project by a facade that
/// forwards to <c>ITaskbarList3::SetProgressValue</c> and
/// <c>SetProgressState</c>. Tests implement it with a recording fake.
///
/// Pure Ghostty.Core — no WinUI types in the surface so the
/// coordinator can be unit-tested without dragging WinAppSDK in.
/// </summary>
internal interface ITaskbarProgressSink
{
    /// <summary>Reflect the given state+percent onto the taskbar.
    /// State == <see cref="TabProgressState.Kind.None"/> clears the
    /// progress indicator. The sink is expected to be idempotent —
    /// the coordinator may call this with the same value multiple
    /// times.</summary>
    void Write(TabProgressState state);
}
