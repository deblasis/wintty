using System;
using System.Runtime.InteropServices;
using Ghostty.Core.Taskbar;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Taskbar;

/// <summary>
/// Real implementation of <see cref="ITaskbarOverlaySink"/>. CoCreates
/// an <see cref="ITaskbarList3"/>, calls HrInit once, and forwards
/// attention writes to <c>SetOverlayIcon</c> against the window's HWND.
/// The dot HICON is built lazily on first show and cached for the
/// lifetime of the facade.
///
/// One facade per window. <see cref="Ghostty.Shell.TaskbarHost"/>
/// constructs it. Mirrors <see cref="TaskbarList3Facade"/>.
/// </summary>
internal sealed class TaskbarOverlayFacade : ITaskbarOverlaySink, IDisposable
{
    // Accessibility text surfaced by screen readers on the overlay.
    private const string OverlayDescription = "Bell";

    private readonly HWND _hwnd;
    private readonly ITaskbarList3 _taskbar;
    private HICON _icon;

    public TaskbarOverlayFacade(IntPtr hwnd)
    {
        _hwnd = new HWND(hwnd);
        _taskbar = TaskbarList.CreateInstance<ITaskbarList3>();
        _taskbar.HrInit();
    }

    public void SetAttention(bool active)
    {
        // The overlay badge is a nice-to-have, like the progress
        // indicator. SetAttention runs on the UI thread off Window.
        // Activated and the bell path, so a COM failure here must not
        // bubble into those callbacks and tear the window down — swallow
        // it and leave the badge in its previous state.
        try
        {
            if (active)
            {
                // Create() returns a null HICON on the rare GDI failure;
                // SetOverlayIcon then just clears, and the next bell retries.
                if (_icon.IsNull) _icon = AttentionOverlayIcon.Create();
                _taskbar.SetOverlayIcon(_hwnd, _icon, OverlayDescription);
            }
            else
            {
                // Null HICON clears the overlay; empty description clears
                // the accessibility text alongside it.
                _taskbar.SetOverlayIcon(_hwnd, default, string.Empty);
            }
        }
        catch (COMException)
        {
        }
    }

    public void Dispose()
    {
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }
    }
}
