using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Taskbar;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Taskbar;

/// <summary>
/// Real implementation of <see cref="ITaskbarBadgeSink"/>. CoCreates
/// an <see cref="ITaskbarList3"/>, calls HrInit once, and forwards the
/// arbiter's writes to <c>SetOverlayIcon</c> against the window's HWND.
///
/// The bell dot is drawn by this assembly and cached for the lifetime of
/// the facade. Every other <see cref="TaskbarBadgeKind"/> is a producer
/// this assembly does not know about (the sponsor update overlay, wired
/// from another assembly in wintty-release); <see cref="IconProvider"/>
/// is the hook that lets such a producer register its own icon without
/// this class needing to know its shape. A kind with no registered icon
/// keeps its notice and shows no overlay dot.
///
/// One facade per window. <see cref="Ghostty.Shell.TaskbarHost"/>
/// constructs it and wires it into a <see cref="TaskbarBadgeArbiter"/>.
/// Mirrors <see cref="TaskbarList3Facade"/>.
/// </summary>
internal sealed partial class TaskbarOverlayFacade : ITaskbarBadgeSink, IDisposable
{
    private readonly HWND _hwnd;
    private readonly ITaskbarList3 _taskbar;
    private HICON _bell;

    /// <summary>Icons for the kinds this assembly does not draw itself
    /// (the sponsor update overlay registers its .ico set). A kind with no
    /// icon keeps its notice and shows no overlay.</summary>
    internal Func<TaskbarBadgeKind, HICON>? IconProvider { get; set; }

    public TaskbarOverlayFacade(IntPtr hwnd)
    {
        _hwnd = new HWND(hwnd);
        _taskbar = TaskbarList.CreateInstance<ITaskbarList3>();
        _taskbar.HrInit();
    }

    public void Show(TaskbarBadgeKind kind, string description)
    {
        // The overlay badge is a nice-to-have, like the progress
        // indicator. Show/Clear run on the UI thread, off the arbiter's
        // Raise/Clear path, so a COM failure here must not bubble up and
        // tear the window down: swallow it and leave the badge in its
        // previous state, the notice already carries the reason.
        try
        {
            HICON icon;
            if (kind == TaskbarBadgeKind.Bell)
            {
                // Create() returns a null HICON on the rare GDI failure;
                // SetOverlayIcon then just clears, and the next bell retries.
                if (_bell.IsNull) _bell = AttentionOverlayIcon.Create();
                icon = _bell;
            }
            else
            {
                icon = IconProvider?.Invoke(kind) ?? default;
            }
            if (icon.IsNull) { Clear(); return; }
            _taskbar.SetOverlayIcon(_hwnd, icon, description);
        }
        catch (COMException)
        {
            // Deliberately not logged: this fires on every focus
            // transition, the indicator is cosmetic, and the sibling
            // TaskbarList3Facade is likewise logger-free.
        }
    }

    public void Clear()
    {
        try
        {
            // Null HICON clears the overlay; empty description clears
            // the accessibility text alongside it.
            _taskbar.SetOverlayIcon(_hwnd, default, string.Empty);
        }
        catch (COMException)
        {
            // See Show: cosmetic, unlogged, matches TaskbarList3Facade.
        }
    }

    public void Dispose()
    {
        if (!_bell.IsNull)
        {
            PInvoke.DestroyIcon(_bell);
            _bell = default;
        }
    }
}
