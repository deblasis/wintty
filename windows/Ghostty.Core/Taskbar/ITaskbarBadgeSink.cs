namespace Ghostty.Core.Taskbar;

/// <summary>The one writer of ITaskbarList3::SetOverlayIcon for a window.
/// Tests use a recording fake; the WinUI facade maps kinds to icons.
/// Public: <see cref="TaskbarBadgeArbiter"/>'s public constructor takes one,
/// and the producer assembly that constructs the arbiter is not guaranteed
/// to be a friend assembly of Ghostty.Core (see AssemblyAttributes.cs)
/// everywhere this type ships, notably a sponsor-tier producer built in
/// wintty-release rather than in this repo.</summary>
public interface ITaskbarBadgeSink
{
    void Show(TaskbarBadgeKind kind, string description);
    void Clear();
}
