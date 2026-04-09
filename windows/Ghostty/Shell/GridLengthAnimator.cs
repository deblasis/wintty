using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Shell;

/// <summary>
/// Bridges a <see cref="ColumnDefinition.Width"/> (a
/// <see cref="GridLength"/>, which the WinUI 3 animation system
/// cannot animate directly) to a <see cref="DoubleAnimation"/>.
///
/// Mechanism: a small <see cref="DependencyObject"/> with a single
/// <c>double</c> dependency property. A <see cref="Storyboard"/>
/// targets that property; the <c>PropertyChangedCallback</c>
/// converts each tick value to a <see cref="GridLength"/> and
/// writes it to the target column. An optional
/// <see cref="OnTick"/> hook fires the same value out to a
/// caller-supplied callback so collaborators (e.g.
/// VerticalTabHost's internal strip column) can stay in lockstep.
///
/// Why this exists: WinUI 3 has no <c>GridLengthAnimation</c>.
/// Either the app uses Star/Auto and lets layout drive the
/// transition (a layout topology change we are not making in
/// this PR), or it owns the conversion. The previous
/// implementation drove the conversion off a 16 ms
/// <see cref="Microsoft.UI.Dispatching.DispatcherQueueTimer"/>
/// using <c>DateTime.UtcNow</c> for elapsed time. The Storyboard
/// path runs on the framework animation timer, gets easing for
/// free, and integrates with the cross-fade Storyboard so the
/// two share a single Completed event.
/// </summary>
internal sealed class GridLengthAnimator : DependencyObject
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(GridLengthAnimator),
            new PropertyMetadata(0.0, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ColumnDefinition Target { get; }
    public Action<double>? OnTick { get; set; }

    public GridLengthAnimator(ColumnDefinition target, double initial)
    {
        Target = target;
        Value = initial;
        // Snap the column to the initial value so the storyboard's
        // first frame does not jump from whatever the layout had
        // before.
        target.Width = new GridLength(initial);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var animator = (GridLengthAnimator)d;
        var v = (double)e.NewValue;
        animator.Target.Width = new GridLength(v);
        animator.OnTick?.Invoke(v);
    }
}
