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

    /// <summary>
    /// Icons for the kinds this assembly does not draw itself (the sponsor
    /// update overlay registers its .ico set). A kind with no icon keeps
    /// its notice and shows no overlay.
    ///
    /// <para>
    /// Ownership contract: the provider owns and caches one <see cref="HICON"/>
    /// per <see cref="TaskbarBadgeKind"/> for the life of the process (the
    /// same pattern this class itself uses for <c>_bell</c>) and must return
    /// that same handle on every call for a given kind. This class never
    /// calls <c>DestroyIcon</c> on a handle obtained through this hook, only
    /// on <c>_bell</c>, the one icon it creates itself; a provider that
    /// hands back a freshly created handle on every invocation leaks it.
    /// </para>
    ///
    /// <para>
    /// The delegate is invoked defensively (see <see cref="Show"/>): it is
    /// arbitrary code from another assembly, so an exception it throws is
    /// treated as "no icon for this kind" rather than allowed to propagate.
    /// </para>
    /// </summary>
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
                icon = InvokeIconProviderSafely(IconProvider, kind);
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

    /// <summary>
    /// Invokes <see cref="IconProvider"/> defensively. It is arbitrary code
    /// supplied by a producer in another assembly, and <see cref="Show"/>'s
    /// own contract promises a COM failure here must not tear the window
    /// down; that promise must hold for a misbehaving producer too, not
    /// just for <c>ITaskbarList3</c> itself. Any exception the provider
    /// throws is treated the same as "no icon registered for this kind":
    /// <see cref="Show"/> falls through to <see cref="Clear"/> and the
    /// badge lands in the same sane, dot-less state a null return produces.
    /// Not logged: no logger is threaded into this class (see the
    /// COMException handling above for the same tradeoff).
    /// </summary>
    internal static HICON InvokeIconProviderSafely(Func<TaskbarBadgeKind, HICON>? provider, TaskbarBadgeKind kind)
    {
        if (provider is null) return default;
        try
        {
            return provider(kind);
        }
        catch (Exception)
        {
            return default;
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
