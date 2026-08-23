using System;
using System.Collections.Generic;
using Ghostty.Core.Settings;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Ghostty.Settings;

/// <summary>
/// Gallery shader picker window. The combo and the left/right arrow keys
/// both walk the gallery; a live terminal underneath previews the selected
/// shader immediately (per-surface override, never the app config). Select
/// commits the picked path to <see cref="PickedPath"/> and closes; Cancel
/// (or closing the window) leaves it null.
/// </summary>
public sealed partial class ShaderPickerWindow : Window
{
    /// <summary>Path to preselect in the combo, if it is a gallery entry.</summary>
    public string? CurrentPath { get; set; }

    /// <summary>The committed selection, or null when cancelled.</summary>
    public string? PickedPath { get; private set; }

    private Controls.TerminalControl? _preview;
    private readonly List<string> _orderedPaths = new();

    public ShaderPickerWindow()
    {
        InitializeComponent();

        Title = "Shader gallery";
        // WinUI 3 Window has no MinWidth/MinHeight; size the OS window.
        if (AppWindow is { } appWindow)
        {
            appWindow.ResizeClient(new Windows.Graphics.SizeInt32(780, 640));
        }

        // NativeAOT-safe manifest binding (see ShaderGalleryJson). Same
        // wiring as the settings page: idempotent, first consumer wins.
        ShaderGallery.ManifestParser ??= ShaderGalleryJson.Parse;

        PopulateCombo();

        // The preview owns a native surface + shell process on the shared
        // bootstrap host; OnUnloaded detachment does NOT free them, so the
        // window closing must dispose explicitly (TerminalControl lesson).
        Closed += (_, _) => DisposePreview();

        // Arrows flip shaders unless the terminal has focus: the terminal
        // consumes arrow keys itself (typing), so its handled events never
        // bubble here. Everything else in the window has no arrow use.
        RootGrid.KeyDown += OnRootKeyDown;
    }

    private void PopulateCombo()
    {
        if (ShaderGallery.Entries.Count == 0)
        {
            // A broken gallery install must be diagnosable from the UI, not
            // only from logs (same contract as the settings page had).
            PickerCombo.Items.Add(new ComboBoxItem
            {
                IsEnabled = false,
                Content = $"Gallery unavailable: {ShaderGallery.LoadDetail}",
            });
            return;
        }

        var selected = 0;
        foreach (var entry in ShaderGallery.Entries)
        {
            var path = ShaderGallery.AbsolutePathFor(entry);
            _orderedPaths.Add(path);

            var panel = new StackPanel();
            // Explicit typography rather than theme-resource lookups:
            // programmatic fetches are unreliable in WinUI 3 desktop, and
            // the two-line shape matches the settings page's old items.
            panel.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            panel.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            PickerCombo.Items.Add(new ComboBoxItem { Tag = path, Content = panel });
            if (path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
            {
                selected = PickerCombo.Items.Count - 1;
            }
        }

        // Setting the selection fires SelectionChanged, which builds the
        // first preview. Guard for the degenerate no-selection case.
        if (PickerCombo.Items.Count > 0)
        {
            PickerCombo.SelectedIndex = selected;
        }
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_orderedPaths.Count == 0) return;
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;

        var delta = e.Key == Windows.System.VirtualKey.Right ? 1 : -1;
        var next = (PickerCombo.SelectedIndex + delta + _orderedPaths.Count) % _orderedPaths.Count;
        PickerCombo.SelectedIndex = next;
        e.Handled = true;
    }

    private void PickerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PickerCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string path)
        {
            ShowPreview(path);
        }
        else
        {
            DisposePreview();
        }
    }

    private void ShowPreview(string shaderPath)
    {
        DisposePreview();

        var host = App.BootstrapHost;
        if (host is null) return;

        var control = new Controls.TerminalControl
        {
            Host = host,
            PreviewCustomShader = shaderPath,
        };
        _preview = control;
        PickerPreviewHost.Child = control;
    }

    private void DisposePreview()
    {
        _preview?.DisposeSurface();
        _preview = null;
        if (PickerPreviewHost is not null) PickerPreviewHost.Child = null;
    }

    private void Select_Click(object sender, RoutedEventArgs e)
    {
        PickedPath = (PickerCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
