using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Ghostty.Panes;

/// <summary>
/// A 1-pixel draggable Grid that updates a <see cref="SplitPane.Ratio"/>
/// from pointer drag deltas relative to its parent's rendered size.
///
/// Why hand-rolled and not the CommunityToolkit GridSplitter:
///   - Avoids pulling a NuGet dependency for ~80 lines of behavior.
///   - We can size it as a true 1px line; GridSplitter has a minimum
///     hit-test thickness that looks heavy on a terminal.
///   - We can route the ratio change directly to our model without an
///     intermediate VisualState/Behavior abstraction.
///
/// The splitter is told which <see cref="SplitPane"/> it adjusts and
/// is given a callback to invoke after each drag update so the parent
/// renderer can re-apply the Grid row/column definitions. It does NOT
/// reach into the visual tree itself.
/// </summary>
internal sealed class Splitter : Grid
{
    // Clamp range: prevents either side from being dragged to zero,
    // which would lose the pane visually and make it impossible to
    // recover with the mouse.
    private const double MinRatio = 0.1;
    private const double MaxRatio = 0.9;

    private readonly SplitPane _split;
    private readonly Action _onRatioChanged;
    private bool _capturing;
    private Point _lastPosition;

    public Splitter(SplitPane split, Action onRatioChanged)
    {
        _split = split;
        _onRatioChanged = onRatioChanged;

        // 1px line in a neutral terminal-chrome color. The dark grey
        // matches typical terminal panel chrome and stays visible
        // against both #0C0C0C backgrounds and Mica. TODO: source from
        // a theme resource once the config layer exists.
        Background = new SolidColorBrush(Microsoft.UI.Colors.DimGray);

        if (split.Orientation == PaneOrientation.Vertical)
        {
            // Vertical splitter LINE between left/right panes.
            Width = 1;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            ChangeCursor(InputSystemCursorShape.SizeWestEast);
        }
        else
        {
            Height = 1;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            ChangeCursor(InputSystemCursorShape.SizeNorthSouth);
        }

        PointerEntered += OnPointerEntered;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerCaptureLost;
    }

    // We change the ProtectedCursor on the element rather than fiddling
    // with global cursor state. ProtectedCursor was made public in
    // WinUI 3 specifically for cases like this.
    private void ChangeCursor(InputSystemCursorShape shape)
    {
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ChangeCursor(_split.Orientation == PaneOrientation.Vertical
            ? InputSystemCursorShape.SizeWestEast
            : InputSystemCursorShape.SizeNorthSouth);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_capturing) return; // keep the resize cursor while dragging
        ChangeCursor(InputSystemCursorShape.Arrow);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(this);
        if (!p.Properties.IsLeftButtonPressed) return;
        if (!CapturePointer(e.Pointer)) return;
        _capturing = true;
        _lastPosition = p.Position;
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing) return;

        // Compute the delta in PARENT coordinates. The parent is the
        // 2-cell Grid that hosts both children plus this splitter as
        // their visual divider. We use FrameworkElement.Parent which is
        // guaranteed to be that Grid because PaneHost adds the splitter
        // directly as a child of the split's Grid.
        if (Parent is not FrameworkElement parent) return;
        var pInParent = e.GetCurrentPoint(parent).Position;

        double newRatio;
        if (_split.Orientation == PaneOrientation.Vertical)
        {
            if (parent.ActualWidth <= 0) return;
            newRatio = pInParent.X / parent.ActualWidth;
        }
        else
        {
            if (parent.ActualHeight <= 0) return;
            newRatio = pInParent.Y / parent.ActualHeight;
        }

        newRatio = Math.Clamp(newRatio, MinRatio, MaxRatio);
        if (Math.Abs(newRatio - _split.Ratio) < 0.0005) return;

        _split.Ratio = newRatio;
        _onRatioChanged();
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_capturing) return;
        _capturing = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _capturing = false;
    }
}
