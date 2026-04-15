using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Ghostty.Controls.Settings;

/// <summary>
/// Visual 2D editor for background-gradient-point values. Position
/// editing via drag on the canvas; radius via numeric input below.
/// The editor is stateless from the config perspective: consumers
/// call SetPoints to load, then listen for PointsChanged to persist.
/// </summary>
public sealed partial class GradientPointsEditor : UserControl
{
    // Maximum gradient points supported by libghostty's config.
    public const int MaxPoints = 5;

    // Handle dot is 14 px diameter (normalized coords computed per-frame
    // based on actual canvas pixel size).
    private const double HandleDiameter = 14.0;

    private readonly List<GradientPointModel> _points = new();

    // Guard to suppress echo when we programmatically change row inputs
    // (e.g. during drag) so NumberBox.ValueChanged doesn't loop back.
    private bool _suppressRowEcho;

    // -1 means no drag in progress; set to the handle index being dragged.
    private int _dragIndex = -1;

    // Element instances currently in PointsCanvas.Children, parallel to
    // _points. RenderCanvas clears and repopulates these. MovePoint
    // mutates existing instances in place so pointer capture survives.
    private readonly List<Microsoft.UI.Xaml.Shapes.Ellipse> _falloffs = new();
    private readonly List<Microsoft.UI.Xaml.UIElement> _handles = new();

    public event EventHandler<IReadOnlyList<GradientPointModel>>? PointsChanged;

    public IReadOnlyList<GradientPointModel> Points => _points;

    public GradientPointsEditor()
    {
        InitializeComponent();
        PointsCanvas.SizeChanged += (_, _) =>
        {
            if (_dragIndex != -1)
            {
                // Mid-drag: reposition in place so pointer capture survives.
                MovePoint(_dragIndex);
            }
            else
            {
                RenderCanvas();
            }
        };
        AddPointButton.Click += (_, _) => TryAdd(0.5f, 0.5f);
        PointsCanvas.DoubleTapped += (_, e) =>
        {
            var pos = e.GetPosition(PointsCanvas);
            var w = PointsCanvas.ActualWidth;
            var h = PointsCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var (x, y) = GradientPointsLogic.Clamp(
                (float)(pos.X / w), (float)(pos.Y / h));

            // Don't stack a new point directly on top of an existing handle.
            // Hit-test in normalized space using the handle's on-canvas radius.
            var handleNorm = (float)(HandleDiameter / 2.0 / Math.Min(w, h));
            var existing = _points
                .Select(p => (p.X, p.Y))
                .ToList();
            if (GradientPointsLogic.HitTest(existing, x, y, handleNorm) is not null)
                return;

            TryAdd(x, y);
        };
    }

    /// <summary>
    /// Replaces the editor state. Does NOT raise PointsChanged --
    /// this is the load path, not a user edit.
    /// </summary>
    public void SetPoints(IReadOnlyList<GradientPointModel> points)
    {
        // Guard against external re-seeds (e.g. ConfigChanged handler)
        // happening in the middle of an active drag. A rebuild would
        // destroy the captured handle and stall the drag.
        if (_dragIndex != -1) return;
        _points.Clear();
        _points.AddRange(points.Take(MaxPoints));
        RenderCanvas();
        RebuildRows();
    }

    private void TryAdd(float x, float y)
    {
        if (_points.Count >= MaxPoints) return;
        // Default new point: mid-radius, neutral amber so it's visible
        // even against dark backgrounds. Consumers can re-color via the row.
        var color = Color.FromArgb(0xFF, 0xF7, 0xC9, 0x48);
        _points.Add(new GradientPointModel(x, y, color, 0.4f));
        RenderCanvas();
        RebuildRows();
        RaisePointsChanged();
    }

    private void RebuildRows()
    {
        RowsPanel.Children.Clear();
        for (int i = 0; i < _points.Count; i++)
        {
            RowsPanel.Children.Add(BuildRow(i));
        }
        AddPointButton.IsEnabled = _points.Count < MaxPoints;
    }

    private StackPanel BuildRow(int index)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Tag = index,
        };

        // ColorPickerControl provides its own swatch and flyout -- no outer
        // wrapper needed. Wrapping it in a second Flyout caused the inner
        // WinUI ColorPicker flyout to light-dismiss whenever the user dragged
        // a slider past the outer flyout boundary.
        var picker = new ColorPickerControl
        {
            Color = ColorToHex(_points[index].Color),
        };
        // Subscribe to the picker's Color DependencyProperty so slider
        // drags inside the flyout update the point live. (The
        // ColorChanged event itself only fires on flyout close.)
        picker.RegisterPropertyChangedCallback(
            ColorPickerControl.ColorProperty,
            (sender, _) =>
            {
                if (_suppressRowEcho) return;
                if (sender is not ColorPickerControl cp) return;
                var c = HexToColor(cp.Color) ?? _points[index].Color;
                var existing = _points[index].Color;
                if (existing.R == c.R && existing.G == c.G && existing.B == c.B)
                    return;
                var p = _points[index];
                _points[index] = p with { Color = c };
                RenderCanvas();
                RaisePointsChanged();
            });
        row.Children.Add(picker);

        row.Children.Add(BuildNumberBox("X", _points[index].X, v =>
        {
            var p = _points[index];
            _points[index] = p with { X = (float)v };
        }));
        row.Children.Add(BuildNumberBox("Y", _points[index].Y, v =>
        {
            var p = _points[index];
            _points[index] = p with { Y = (float)v };
        }));
        row.Children.Add(BuildNumberBox("R", _points[index].Radius, v =>
        {
            var p = _points[index];
            _points[index] = p with { Radius = (float)v };
        }));

        var remove = new Button
        {
            Content = "\u2715",
            Padding = new Thickness(6, 2, 6, 2),
        };
        remove.Click += (_, _) =>
        {
            _points.RemoveAt(index);
            RenderCanvas();
            RebuildRows();
            RaisePointsChanged();
        };
        row.Children.Add(remove);

        return row;
    }

    private NumberBox BuildNumberBox(string header, double value, Action<double> onChanged)
    {
        var nb = new NumberBox
        {
            Header = header,
            Minimum = 0,
            Maximum = 1,
            SmallChange = 0.05,
            LargeChange = 0.1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            Width = 80,
            Value = value,
        };
        nb.ValueChanged += (_, args) =>
        {
            if (_suppressRowEcho) return;
            if (double.IsNaN(args.NewValue)) return;
            var clamped = Math.Clamp(args.NewValue, 0.0, 1.0);
            onChanged(clamped);
            RenderCanvas();
            RaisePointsChanged();
        };
        return nb;
    }

    private static string ColorToHex(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color? HexToColor(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        var s = hex.TrimStart('#');
        if (s.Length != 6) return null;
        if (!byte.TryParse(s.AsSpan(0, 2),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var r)) return null;
        if (!byte.TryParse(s.AsSpan(2, 2),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var g)) return null;
        if (!byte.TryParse(s.AsSpan(4, 2),
            System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var b)) return null;
        return Color.FromArgb(0xFF, r, g, b);
    }

    private void RaisePointsChanged() =>
        PointsChanged?.Invoke(this, _points.AsReadOnly());

    private void RenderCanvas()
    {
        PointsCanvas.Children.Clear();
        _falloffs.Clear();
        _handles.Clear();
        var w = PointsCanvas.ActualWidth;
        var h = PointsCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        CanvasBorder.Visibility = _points.Count == 0
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;

        for (int i = 0; i < _points.Count; i++)
        {
            var p = _points[i];
            // Falloff: an Ellipse sized to 2*Radius in normalized space,
            // filled with a RadialGradientBrush from opaque center to
            // transparent edge.
            var falloffSize = 2.0 * p.Radius * Math.Min(w, h);
            var falloff = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = falloffSize,
                Height = falloffSize,
                IsHitTestVisible = false,
                Fill = BuildFalloffBrush(p.Color),
            };
            Canvas.SetLeft(falloff, p.X * w - falloffSize / 2);
            Canvas.SetTop(falloff, p.Y * h - falloffSize / 2);
            PointsCanvas.Children.Add(falloff);
            _falloffs.Add(falloff);

            // Handle: a small circular drag target above the falloff.
            // ContentControl (not Button) so Button's internal pointer template
            // does not steal PointerPressed before our drag handler can run.
            // XY focus nav is disabled so Up/Down reach our KeyDown instead of
            // moving focus to the next control outside the canvas.
            var handleSize = HandleDiameter + 4;
            var handle = new ContentControl
            {
                Width = handleSize,
                Height = handleSize,
                Padding = new Microsoft.UI.Xaml.Thickness(0),
                IsTabStop = true,
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled,
                Tag = i,
                Content = new Ellipse
                {
                    Width = HandleDiameter,
                    Height = HandleDiameter,
                    Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(p.Color),
                    Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Microsoft.UI.Colors.White),
                    StrokeThickness = 2,
                },
                HorizontalContentAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
                VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            };
            int capturedIndex = i;
            handle.KeyDown += (s, e) =>
            {
                if (capturedIndex >= _points.Count) return;
                const float step = 0.01f;
                var cur = _points[capturedIndex];
                switch (e.Key)
                {
                    case Windows.System.VirtualKey.Left:
                        _points[capturedIndex] = cur with { X = Math.Clamp(cur.X - step, 0f, 1f) };
                        break;
                    case Windows.System.VirtualKey.Right:
                        _points[capturedIndex] = cur with { X = Math.Clamp(cur.X + step, 0f, 1f) };
                        break;
                    case Windows.System.VirtualKey.Up:
                        _points[capturedIndex] = cur with { Y = Math.Clamp(cur.Y - step, 0f, 1f) };
                        break;
                    case Windows.System.VirtualKey.Down:
                        _points[capturedIndex] = cur with { Y = Math.Clamp(cur.Y + step, 0f, 1f) };
                        break;
                    case Windows.System.VirtualKey.Delete:
                        _points.RemoveAt(capturedIndex);
                        RenderCanvas();
                        RebuildRows();
                        RaisePointsChanged();
                        e.Handled = true;
                        return;
                    default:
                        return;
                }
                RenderCanvas();
                SyncRowNumbers(capturedIndex);
                RaisePointsChanged();
                e.Handled = true;
            };
            handle.PointerPressed += (_, e) =>
            {
                handle.CapturePointer(e.Pointer);
                _dragIndex = capturedIndex;
                e.Handled = true;
            };
            handle.PointerMoved += (s, e) =>
            {
                if (_dragIndex != capturedIndex) return;
                var pos = e.GetCurrentPoint(PointsCanvas).Position;
                var w = PointsCanvas.ActualWidth;
                var h = PointsCanvas.ActualHeight;
                if (w <= 0 || h <= 0) return;
                var (nx, ny) = GradientPointsLogic.Clamp(
                    (float)(pos.X / w), (float)(pos.Y / h));
                var cur = _points[capturedIndex];
                _points[capturedIndex] = cur with { X = nx, Y = ny };
                MovePoint(capturedIndex);
                SyncRowNumbers(capturedIndex);
                RaisePointsChanged();
            };
            handle.PointerReleased += (_, e) =>
            {
                if (_dragIndex == capturedIndex)
                    handle.ReleasePointerCapture(e.Pointer);
                _dragIndex = -1;
            };
            handle.PointerCaptureLost += (_, _) => _dragIndex = -1;
            Canvas.SetLeft(handle, p.X * w - handleSize / 2);
            Canvas.SetTop(handle, p.Y * h - handleSize / 2);
            PointsCanvas.Children.Add(handle);
            _handles.Add(handle);
        }
    }

    /// <summary>
    /// Update the visual position (and falloff size) of a single point
    /// in place, without rebuilding the canvas. Used during drag so
    /// pointer capture on the handle Ellipse survives the mutation.
    /// </summary>
    private void MovePoint(int index)
    {
        if (index < 0 || index >= _points.Count) return;
        if (index >= _handles.Count || index >= _falloffs.Count) return;
        var w = PointsCanvas.ActualWidth;
        var h = PointsCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var p = _points[index];
        var falloffSize = 2.0 * p.Radius * Math.Min(w, h);

        var falloff = _falloffs[index];
        falloff.Width = falloffSize;
        falloff.Height = falloffSize;
        Canvas.SetLeft(falloff, p.X * w - falloffSize / 2);
        Canvas.SetTop(falloff, p.Y * h - falloffSize / 2);

        var handle = _handles[index];
        var handleSize = HandleDiameter + 4;
        Canvas.SetLeft(handle, p.X * w - handleSize / 2);
        Canvas.SetTop(handle, p.Y * h - handleSize / 2);
    }

    // Updates the X/Y/R NumberBoxes in a row to match _points[index]
    // after a canvas drag, without triggering the ValueChanged -> RenderCanvas loop.
    private void SyncRowNumbers(int index)
    {
        if (index < 0 || index >= RowsPanel.Children.Count) return;
        if (RowsPanel.Children[index] is not StackPanel row) return;
        _suppressRowEcho = true;
        try
        {
            // Row layout is: picker, X nb, Y nb, R nb, remove. Indexes 1..3.
            if (row.Children[1] is NumberBox xn) xn.Value = _points[index].X;
            if (row.Children[2] is NumberBox yn) yn.Value = _points[index].Y;
            if (row.Children[3] is NumberBox rn) rn.Value = _points[index].Radius;
        }
        finally
        {
            _suppressRowEcho = false;
        }
    }

    private static Microsoft.UI.Xaml.Media.Brush BuildFalloffBrush(Color c)
    {
        // RadialGradientBrush: opaque at center, transparent at edge.
        // This is a visual editor aid ONLY -- it does not reflect blend
        // mode, opacity, or animation of the real gradient.
        var brush = new Microsoft.UI.Xaml.Media.RadialGradientBrush
        {
            Center = new Windows.Foundation.Point(0.5, 0.5),
            GradientOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Color.FromArgb(0xCC, c.R, c.G, c.B),
            Offset = 0.0,
        });
        brush.GradientStops.Add(new Microsoft.UI.Xaml.Media.GradientStop
        {
            Color = Color.FromArgb(0x00, c.R, c.G, c.B),
            Offset = 1.0,
        });
        return brush;
    }
}

/// <summary>
/// Editor-local DTO for a gradient point. Maps 1:1 to
/// <c>Ghostty.Services.GradientPoint</c>; the control does not
/// reference the service layer.
/// </summary>
public readonly record struct GradientPointModel(
    float X, float Y, Color Color, float Radius);
