using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Ghostty.Tabs;

/// <summary>
/// 1px-wide draggable handle that updates a callback with the new
/// width on each pointer move. Used by <see cref="VerticalTabHost"/>
/// to let the user resize the pinned-expanded strip width live.
///
/// Why not reuse <see cref="Ghostty.Panes.Splitter"/> from #163: that
/// one is wired to <c>SplitPane.Ratio</c> and assumes a sibling-pair
/// model. The vertical tab strip has no such pair — it just owns
/// one column width.
/// </summary>
internal sealed partial class ColumnDragHandle : Grid
{
    private readonly Action<double> _onWidthChanged;
    private readonly Func<double> _readCurrentWidth;
    private bool _dragging;
    private double _dragStartX;
    private double _dragStartWidth;

    public ColumnDragHandle(Action<double> onWidthChanged, Func<double> readCurrentWidth)
    {
        _onWidthChanged = onWidthChanged;
        _readCurrentWidth = readCurrentWidth;
        // 4px wide, subtle visible fill so the user can actually find
        // it. A 1px transparent strip is technically grabbable but
        // invisible, which was the original complaint.
        Width = 4;
        Background = (SolidColorBrush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = true;

        PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerExited += (_, _) => { if (!_dragging) ProtectedCursor = null; };
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += OnReleased;
        PointerCaptureLost += OnCaptureLost;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _dragStartX = e.GetCurrentPoint(null).Position.X;
        _dragStartWidth = _readCurrentWidth();
        CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var dx = e.GetCurrentPoint(null).Position.X - _dragStartX;
        var newWidth = _dragStartWidth + dx;
        if (newWidth < 80) newWidth = 80;
        if (newWidth > 600) newWidth = 600;
        _onWidthChanged(newWidth);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        ProtectedCursor = null;
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ProtectedCursor = null;
    }
}
