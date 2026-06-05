using System;
using System.Collections.Generic;
using Ghostty.Core.Input;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Settings;

/// <summary>Args for a click on a physical key cell of the keyboard map.</summary>
internal sealed class KeyboardKeyClickedEventArgs : EventArgs
{
    public required KeyCell Cell { get; init; }
    public required KeyboardKeyState? State { get; init; }
    public required FrameworkElement Anchor { get; init; }
}

internal sealed partial class KeyboardMapView : UserControl
{
    private const double UnitWidth = 34;
    private const double KeyHeight = 34;
    private const uint Shift = 1u << 0, Ctrl = 1u << 1, Alt = 1u << 2, Super = 1u << 3;

    private KeyboardMapModel? _model;
    private readonly Dictionary<string, Button> _keyButtons = new();

    public event EventHandler? ModifierChanged;
    public event EventHandler<KeyboardKeyClickedEventArgs>? KeyClicked;

    public KeyboardMapView()
    {
        InitializeComponent();
        BuildBlock(MainBlock, KeyboardLayout.Main);
        BuildBlock(NavBlock, KeyboardLayout.Nav);
        BuildBlock(NumpadBlock, KeyboardLayout.Numpad);
    }

    public uint ModifierMask
    {
        get
        {
            uint m = 0;
            if (CtrlChip.IsChecked == true) m |= Ctrl;
            if (ShiftChip.IsChecked == true) m |= Shift;
            if (AltChip.IsChecked == true) m |= Alt;
            if (WinChip.IsChecked == true) m |= Super;
            return m;
        }
    }

    private void BuildBlock(StackPanel block, IReadOnlyList<IReadOnlyList<KeyCell>> rows)
    {
        foreach (var row in rows)
        {
            var rowPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var cell in row)
            {
                if (string.IsNullOrEmpty(cell.KeyName) && !cell.Bindable)
                {
                    rowPanel.Children.Add(new Border { Width = cell.Units * UnitWidth, Height = KeyHeight });
                    continue;
                }

                var btn = new Button
                {
                    Width = cell.Units * UnitWidth,
                    Height = KeyHeight,
                    Padding = new Thickness(2),
                    FontSize = 11,
                    Content = cell.Label,
                    Tag = cell,
                    IsEnabled = cell.Bindable,
                };
                if (cell.Bindable)
                {
                    btn.Click += Key_Click;
                    _keyButtons[cell.KeyName] = btn;
                }
                rowPanel.Children.Add(btn);
            }
            block.Children.Add(rowPanel);
        }
    }

    /// <summary>Repaint every key for the given layer model.</summary>
    public void Apply(KeyboardMapModel model)
    {
        _model = model;
        foreach (var (name, btn) in _keyButtons)
        {
            var state = model.ForKey(name);
            if (state is null)
            {
                btn.ClearValue(Control.BackgroundProperty);
                btn.ClearValue(Control.ForegroundProperty);
                btn.ClearValue(Control.BorderBrushProperty);
                btn.ClearValue(Control.BorderThicknessProperty);
                var cell0 = (KeyCell)btn.Tag;
                btn.Content = cell0.Label;
                ToolTipService.SetToolTip(btn, null);
                continue;
            }

            btn.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xB3, 0x47));
            btn.Foreground = new SolidColorBrush(Colors.Black);
            if (state.Source == KeybindSource.User)
            {
                btn.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD6, 0x5A, 0x2E));
                btn.BorderThickness = new Thickness(2);
            }
            else
            {
                btn.ClearValue(Control.BorderBrushProperty);
                btn.ClearValue(Control.BorderThicknessProperty);
            }

            var mark = state.IsMultiStep ? " ⋯" : (state.Conflict.HasConflict ? " ⚠" : "");
            btn.Content = state.ActionLabel + mark;
            ToolTipService.SetToolTip(btn,
                state.ActionLabel + (state.Conflict.HasConflict ? "\n" + state.Conflict.Message : ""));
        }
    }

    private void Chip_Click(object sender, RoutedEventArgs e) => ModifierChanged?.Invoke(this, EventArgs.Empty);

    private void Key_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not KeyCell cell) return;
        KeyClicked?.Invoke(this, new KeyboardKeyClickedEventArgs
        {
            Cell = cell,
            State = _model?.ForKey(cell.KeyName),
            Anchor = btn,
        });
    }
}
