using System;
using System.Collections.Generic;
using Ghostty.Core.Settings;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Ghostty.Settings;

/// <summary>
/// Gallery shader picker window. The combo, the chevron buttons, and the
/// left/right arrow keys all walk the gallery; a live terminal underneath
/// previews the selected shader immediately (per-surface override, never
/// the app config). Arrows leave the gallery alone while the terminal
/// holds focus, so its keystrokes (cursor movement included) are never
/// stolen. Select commits the picked path to <see cref="PickedPath"/> and
/// closes; Cancel (or closing the window) leaves it null.
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

        // Mica so the window is not a black flash during first layout,
        // same as the settings window.
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        // WinUI 3 Window has no MinWidth/MinHeight; size the OS window.
        // AppWindow sizing is in PHYSICAL pixels: scale the design size by
        // the monitor DPI, or above 100% scaling the window is smaller
        // than the content needs and clips its own button row.
        if (AppWindow is { } appWindow)
        {
            var hwnd = new HWND(WindowNative.GetWindowHandle(this));
            var dpi = PInvoke.GetDpiForWindow(hwnd);
            var scale = dpi == 0 ? 1.0 : dpi / 96.0;
            var pxWidth = (int)Math.Round(780 * scale);
            var pxHeight = (int)Math.Round(640 * scale);
            appWindow.ResizeClient(new Windows.Graphics.SizeInt32(pxWidth, pxHeight));

            // Center on the window's display (work area, physical pixels),
            // the same recipe as the settings window.
            var display = DisplayArea.GetFromWindowId(
                appWindow.Id, DisplayAreaFallback.Primary);
            var work = display.WorkArea;
            appWindow.Move(new Windows.Graphics.PointInt32(
                work.X + (work.Width - pxWidth) / 2,
                work.Y + (work.Height - pxHeight) / 2));
        }

        // NativeAOT-safe manifest binding (see ShaderGalleryJson). Same
        // wiring as the settings page: idempotent, first consumer wins.
        ShaderGallery.ManifestParser ??= ShaderGalleryJson.Parse;

        PopulateCombo();

        // Keyboard focus starts on the combo, NOT the preview terminal:
        // the terminal would otherwise own the arrows (PreviewHasFocus)
        // and the gallery stepper would never fire. The preview sets
        // AutoFocus = false for the same reason; this is the other half.
        // Content has not loaded yet in the ctor, so focus once the tree
        // exists.
        RootGrid.Loaded += (_, _) => PickerCombo.Focus(FocusState.Programmatic);

        // The preview owns a native surface + shell process on the shared
        // bootstrap host; OnUnloaded detachment does NOT free them, so the
        // window closing must dispose explicitly (TerminalControl lesson).
        Closed += (_, _) => DisposePreview();

        // Arrows flip shaders unless the terminal preview holds focus: the
        // terminal forwards keys to the shell without marking them handled,
        // so its arrow KeyDown still bubbles up here, and keys that move a
        // shell cursor must not also flip the gallery. The chevron buttons
        // beside the combo switch regardless of focus.
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
            UpdateSelectEnabled();
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

            var item = new ComboBoxItem { Tag = path, Content = panel };
            // The item content is a panel, so UIA would otherwise fall back
            // to text scraping; give every item a real name.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, entry.Name);
            PickerCombo.Items.Add(item);
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
        UpdateSelectEnabled();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_orderedPaths.Count == 0) return;
        if (e.Key is not (Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right)) return;
        // Chords (Ctrl+Right, Shift+Left, Alt+...) belong to whatever has
        // focus; the bare arrows are the gallery stepper.
        if (IsChordDown()) return;
        // A focused terminal bubbles its (unhandled) arrow KeyDown up to
        // RootGrid; those keys belong to the shell cursor, not the gallery.
        if (PreviewHasFocus) return;

        StepSelection(e.Key == Windows.System.VirtualKey.Right ? 1 : -1);
        e.Handled = true;
    }

    private static bool IsChordDown()
    {
        foreach (var key in new[]
        {
            Windows.System.VirtualKey.Control,
            Windows.System.VirtualKey.Shift,
            Windows.System.VirtualKey.Menu,
        })
        {
            if ((Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(key)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the live terminal preview (or anything inside it, like its
    /// search bar) holds keyboard focus. Containment, not identity: focus
    /// lives on the TerminalControl but can sit on inner elements too, and
    /// any of them means every keystroke belongs to the terminal.
    /// </summary>
    private bool PreviewHasFocus
    {
        get
        {
            // XamlRoot is null until the window content loads; no focus to own.
            if (RootGrid.XamlRoot is null) return false;
            var node = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
            while (node is not null)
            {
                if (ReferenceEquals(node, PickerPreviewHost)) return true;
                node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
            }
            return false;
        }
    }

    // Shared by the arrow keys and the chevron buttons: wrap around the
    // gallery and let SelectionChanged rebuild the preview.
    private void StepSelection(int delta)
    {
        if (_orderedPaths.Count == 0) return;
        var next = (PickerCombo.SelectedIndex + delta + _orderedPaths.Count) % _orderedPaths.Count;
        PickerCombo.SelectedIndex = next;
    }

    private void PrevShader_Click(object sender, RoutedEventArgs e) => StepSelection(-1);

    private void NextShader_Click(object sender, RoutedEventArgs e) => StepSelection(1);

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
        UpdateSelectEnabled();
    }

    // Select only means something with a committed gallery entry; the
    // degenerate one-item diagnostic case would commit null, which is just
    // Cancel wearing an accent button.
    private void UpdateSelectEnabled() =>
        SelectButton.IsEnabled =
            (PickerCombo.SelectedItem as ComboBoxItem)?.Tag is string;

    private void ShowPreview(string shaderPath)
    {
        DisposePreview();

        var host = App.BootstrapHost;
        if (host is null) return;

        var control = new Controls.TerminalControl
        {
            Host = host,
            PreviewCustomShader = shaderPath,
            // The preview must not steal focus when its surface loads:
            // TerminalControl focuses itself on load by default (real
            // panes want that), which would hand the arrows to the shell
            // after every selection change.
            AutoFocus = false,
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
