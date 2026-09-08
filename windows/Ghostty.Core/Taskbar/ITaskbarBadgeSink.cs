namespace Ghostty.Core.Taskbar;

/// <summary>The one writer of ITaskbarList3::SetOverlayIcon for a window.
/// Tests use a recording fake; the WinUI facade maps kinds to icons.</summary>
internal interface ITaskbarBadgeSink
{
    void Show(TaskbarBadgeKind kind, string description);
    void Clear();
}
