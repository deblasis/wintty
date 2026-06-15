using System;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

using Ghostty.Core;
using Ghostty.Interop;

using WinRT.Interop;

namespace Ghostty;

// Separate window hosting the terminal inspector (ImGui overlay), mirroring
// the macOS inspector window. libghostty owns the DX12 swap chain bound to the
// SwapChainPanel; we drive a frame on a timer and forward pointer/character
// input into the ImGui context. The inspector is bound to the surface that was
// active when it opened (one inspector window per main window, per v1 scope).
internal sealed partial class InspectorWindow : Window
{
    // ~60fps. ImGui is immediate-mode, so we redraw continuously while the
    // window is open; this can later be made on-demand (present on input +
    // when ImGui wants another frame).
    private static readonly TimeSpan PresentInterval = TimeSpan.FromMilliseconds(16);

    private readonly GhosttyInspector _inspector;
    private readonly DispatcherTimer _timer;
    private bool _initialized;
    private bool _closed;

    public InspectorWindow(GhosttyInspector inspector)
    {
        InitializeComponent();

        _inspector = inspector;

        Title = $"{AppIdentity.ProductName} Inspector";

        // Stamp the bug .ico into the OS slots (taskbar group, alt-tab,
        // thumbnail) so the inspector is distinguishable from a terminal
        // window, mirroring how SettingsWindow uses the gear .ico.
        Ghostty.Branding.WindowHelper.TryApplyInspectorIcon(this);

        // Center at a reasonable default size.
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        const int width = 900;
        const int height = 700;
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var x = work.X + (work.Width - width) / 2;
        var y = work.Y + (work.Height - height) / 2;
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        _timer = new DispatcherTimer { Interval = PresentInterval };
        _timer.Tick += OnPresentTick;

        Panel.Loaded += OnPanelLoaded;
        Panel.SizeChanged += OnPanelSizeChanged;
        Panel.PointerPressed += OnPointerPressed;
        Panel.PointerReleased += OnPointerReleased;
        Panel.PointerMoved += OnPointerMoved;
        Panel.PointerWheelChanged += OnPointerWheelChanged;
        Panel.CharacterReceived += OnCharacterReceived;
        Panel.KeyDown += OnKeyDown;
        Activated += OnActivated;
        Closed += OnClosed;
    }

    private double RasterScale => Panel.XamlRoot?.RasterizationScale ?? 1.0;

    private uint PixelWidth => (uint)Math.Max(1, Math.Round(Panel.ActualWidth * RasterScale));
    private uint PixelHeight => (uint)Math.Max(1, Math.Round(Panel.ActualHeight * RasterScale));

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed) return;

        var panelPtr = SwapChainPanelInterop.QueryInterface(Panel);
        try
        {
            // libghostty creates the swap chain and binds it to the panel
            // synchronously here, so the panel pointer can be released right
            // after (same contract as the terminal surface).
            _initialized = NativeMethods.InspectorDirectX12SurfaceInit(
                _inspector, panelPtr, PixelWidth, PixelHeight);
        }
        finally
        {
            SwapChainPanelInterop.Release(panelPtr);
        }

        if (!_initialized) return;

        NativeMethods.InspectorSetContentScale(_inspector, RasterScale, RasterScale);
        NativeMethods.InspectorSetSize(_inspector, PixelWidth, PixelHeight);
        // Seed focus: the window's first Activated fires before this Loaded, so
        // OnActivated skips it (not yet initialized). ImGui needs focus to take
        // keyboard/text input.
        NativeMethods.InspectorSetFocus(_inspector, true);
        Panel.Focus(FocusState.Programmatic);
        _timer.Start();
    }

    private void OnPresentTick(object? sender, object e)
    {
        if (!_initialized || _closed) return;
        NativeMethods.InspectorDirectX12SurfacePresent(_inspector);
    }

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized || _closed) return;
        NativeMethods.InspectorDirectX12SurfaceResize(_inspector, PixelWidth, PixelHeight);
        NativeMethods.InspectorSetSize(_inspector, PixelWidth, PixelHeight);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (!_initialized || _closed) return;
        NativeMethods.InspectorSetFocus(
            _inspector, e.WindowActivationState != WindowActivationState.Deactivated);
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        _timer.Tick -= OnPresentTick;
        if (_initialized)
        {
            NativeMethods.InspectorDirectX12SurfaceShutdown(_inspector);
            _initialized = false;
        }
    }

    // ---- input forwarding ----------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Panel.Focus(FocusState.Pointer);
        SendMouseButton(e);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        SendMouseButton(e);
        e.Handled = true;
    }

    private void SendMouseButton(PointerRoutedEventArgs e)
    {
        if (!_initialized) return;
        var kind = e.GetCurrentPoint(Panel).Properties.PointerUpdateKind;
        var (button, state) = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => (GhosttyMouseButton.Left, GhosttyMouseState.Press),
            PointerUpdateKind.LeftButtonReleased => (GhosttyMouseButton.Left, GhosttyMouseState.Release),
            PointerUpdateKind.RightButtonPressed => (GhosttyMouseButton.Right, GhosttyMouseState.Press),
            PointerUpdateKind.RightButtonReleased => (GhosttyMouseButton.Right, GhosttyMouseState.Release),
            PointerUpdateKind.MiddleButtonPressed => (GhosttyMouseButton.Middle, GhosttyMouseState.Press),
            PointerUpdateKind.MiddleButtonReleased => (GhosttyMouseButton.Middle, GhosttyMouseState.Release),
            _ => (GhosttyMouseButton.Unknown, GhosttyMouseState.Release),
        };
        if (button == GhosttyMouseButton.Unknown) return;
        // The core inspector ignores mouse-button mods, so pass None.
        NativeMethods.InspectorMouseButton(_inspector, state, button, GhosttyMods.None);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_initialized) return;
        // Logical coordinates: the core scales by content_scale internally.
        var pos = e.GetCurrentPoint(Panel).Position;
        NativeMethods.InspectorMousePos(_inspector, pos.X, pos.Y);
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_initialized) return;
        var point = e.GetCurrentPoint(Panel);
        // Wheel delta is +/-120 per notch; ImGui expects ~1.0 per notch.
        var delta = point.Properties.MouseWheelDelta / 120.0;
        var horizontal = point.Properties.IsHorizontalMouseWheel;
        NativeMethods.InspectorMouseScroll(
            _inspector,
            horizontal ? delta : 0.0,
            horizontal ? 0.0 : delta,
            0);
        e.Handled = true;
    }

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (!_initialized) return;
        // ghostty_inspector_text takes a NUL-terminated UTF-8 string.
        var bytes = Encoding.UTF8.GetBytes(e.Character.ToString());
        var buf = new byte[bytes.Length + 1];
        Array.Copy(bytes, buf, bytes.Length);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            NativeMethods.InspectorText(_inspector, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    // Ctrl++ / Ctrl+- zoom the inspector UI, Ctrl+0 resets. Handled here (not
    // forwarded to ImGui) since we don't route key events into the context.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_initialized) return;

        var ctrl = (InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control) &
            Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (!ctrl) return;

        // OemPlus/OemMinus are the main-row '='/'-' keys; Add/Subtract are the
        // numpad equivalents. '+' needs no Shift here (Ctrl+= zooms in).
        const Windows.System.VirtualKey OemPlus = (Windows.System.VirtualKey)0xBB;
        const Windows.System.VirtualKey OemMinus = (Windows.System.VirtualKey)0xBD;
        switch (e.Key)
        {
            case OemPlus:
            case Windows.System.VirtualKey.Add:
                NativeMethods.InspectorZoomBy(_inspector, 1.1);
                e.Handled = true;
                break;
            case OemMinus:
            case Windows.System.VirtualKey.Subtract:
                NativeMethods.InspectorZoomBy(_inspector, 1.0 / 1.1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Number0:
            case Windows.System.VirtualKey.NumberPad0:
                NativeMethods.InspectorZoomReset(_inspector);
                e.Handled = true;
                break;
        }
    }
}
