using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Ghostty.Branding;

/// <summary>
/// Notepad-style app icon that sits at the left edge of the tab strip.
/// Click opens the Windows system menu over the badge. Instantiated
/// once in TabHost (horizontal mode) and once in VerticalTabHost
/// (vertical mode); both live in subtrees that are only ever visible
/// one at a time.
/// </summary>
public sealed partial class AppIconBadge : UserControl
{
    // Instance property returning a BitmapImage so x:Bind's type check
    // matches ImageIcon.Source (ImageSource). Mode=OneTime means the
    // XAML compiler emits a direct call at InitializeComponent; AOT-safe.
    public BitmapImage IconSource { get; } = new BitmapImage(AppIconSource.Current);

    // The title-icon contract: click opens the menu, press-and-move drags
    // the window. The badge sits at the title bar's left edge where a user
    // expects chrome to move the window, and the SetTitleBar passthrough
    // cannot reach it - an interactive element is exactly what that
    // passthrough excludes. So the Button carries its own drag: captured,
    // and once the pointer travels past a threshold the OS move loop takes
    // over and the would-be click is swallowed.
    private bool _pressed;
    private bool _suppressClick;
    private Point _pressPoint;
    // The pointer the press started with; a move from any other pointer
    // (a second finger, a mouse crossing while a dead touch press lingers)
    // must not arm the drag.
    private uint _pressPointerId;

    private const nuint HTCAPTION = 2;

    public AppIconBadge()
    {
        InitializeComponent();
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = Shell.TabChromeMetrics.AppIconMargin;

        // handledEventsToo: the Button takes PointerPressed itself, and the
        // drag has to see the press anyway. The handlers are wrapped in the
        // WinRT delegate shape: the projection rejects a raw method group
        // here with InvalidCastException across the ABI.
        ClickTarget.AddHandler(PointerPressedEvent,
            new PointerEventHandler(OnBadgePointerPressed), true);
        ClickTarget.AddHandler(PointerMovedEvent,
            new PointerEventHandler(OnBadgePointerMoved), true);
        ClickTarget.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(OnBadgePointerReleased), true);
        ClickTarget.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(OnBadgePointerCanceled), true);
        // Capture can transfer away without a Cancel (another element
        // capturing the same pointer, some deactivation paths) - a stale
        // _pressed would then arm on the next unrelated in-contact move,
        // the same hazard class the drag engines clear on.
        ClickTarget.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler(OnBadgeCaptureLost), true);
        // The suppression only exists to eat the release after a drag;
        // a keyboard or UIA activation is never that release.
        ClickTarget.GotFocus += (_, _) => _suppressClick = false;
    }

    private void OnBadgePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // The caption contract is primary-button-only. Touch and pen have
        // no other buttons to get wrong; a mouse must use the left one
        // (middle-press-and-move is autoscroll muscle memory, not a drag).
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse &&
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressed = true;
        _suppressClick = false;
        _pressPointerId = e.Pointer.PointerId;
        _pressPoint = e.GetCurrentPoint(this).Position;
        ClickTarget.CapturePointer(e.Pointer);
    }

    private void OnBadgePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || e.Pointer.PointerId != _pressPointerId) return;
        if (!e.Pointer.IsInContact) return;
        var p = e.GetCurrentPoint(this).Position;
        var dx = p.X - _pressPoint.X;
        var dy = p.Y - _pressPoint.Y;
        // ~5 DIP before the move counts as a drag rather than a sloppy
        // click - the same order as the system's own drag threshold.
        if (dx * dx + dy * dy < 25) return;
        _pressed = false;
        _suppressClick = true;
        ReleaseCapture(e.Pointer);
        BeginWindowDrag();
    }

    /// <summary>
    /// Enter the OS move loop from the icon's press: WM_NCLBUTTONDOWN on
    /// HTCAPTION with the live cursor position is the same drag the
    /// caption runs, and it is the canonical way a WinUI 3 app moves its
    /// window from inside custom title-bar chrome (this SDK has no
    /// AppWindow.DragMove). Dispatched synchronously like the system-menu
    /// path: the move loop wants to start under live input.
    /// </summary>
    private void BeginWindowDrag()
    {
        var window = WindowHelper.GetWindow(this);
        if (window is null) return;
        if (!PInvoke.GetCursorPos(out var pos)) return;
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        PInvoke.ReleaseCapture();
        PInvoke.SendMessage(
            hwnd,
            PInvoke.WM_NCLBUTTONDOWN,
            (WPARAM)HTCAPTION,
            new LPARAM((nint)((uint)pos.X | ((uint)pos.Y << 16))));
    }

    private void OnBadgePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        ReleaseCapture(e.Pointer);
        // Below the threshold this release IS the click: let OnClick run.
    }

    private void OnBadgePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pressed = false;
        ReleaseCapture(e.Pointer);
    }

    private void OnBadgeCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // The capture we armed is gone without a release: back to idle.
        _pressed = false;
    }

    private void ReleaseCapture(Pointer pointer)
    {
        try { ClickTarget.ReleasePointerCapture(pointer); }
        catch { /* already released */ }
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }
        var window = WindowHelper.GetWindow(this);
        if (window is null) return;
        SystemMenuPopup.ShowAt(window, ClickTarget);
    }
}
