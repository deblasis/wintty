using System;
using System.ComponentModel;
using Ghostty.Commands;
using Ghostty.Core.Config;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace Ghostty.Controls.CommandPalette;

/// <summary>
/// Floating command-palette control.
///
/// The control is intentionally dumb: it owns only the visual tree and
/// user-interaction handling. All state lives in <see cref="CommandPaletteViewModel"/>.
/// The two are connected by <see cref="Bind"/>, which subscribes to
/// PropertyChanged and pushes ViewModel state into the named XAML elements.
///
/// We deliberately avoid x:Bind / DataContext assignment because
/// <see cref="CommandPaletteViewModel"/> is an <c>internal</c> type and
/// AOT-safe x:Bind requires public types. Code-behind binding lets the type
/// stay internal.
/// </summary>
internal sealed partial class CommandPaletteControl : UserControl
{
    private CommandPaletteViewModel? _vm;
    // Cached background mode so ApplyTheme can re-resolve the right
    // theme variant on a theme flip — a code-behind lookup against
    // Application.Current.Resources bakes in Application.RequestedTheme
    // at call time, so the brush wouldn't auto-update when the window's
    // theme flipped otherwise.
    private string _backgroundSetting = string.Empty;

    // Config+OS-driven theme resolver. The palette uses System fallback
    // (same as SettingsWindow) so it feels OS-native regardless of the
    // terminal's palette: a dark palette on a light OS leaves the
    // palette light, matching every other system UI surface. Created
    // in Configure() and disposed on Unloaded. Null until Configure
    // runs, which happens once during MainWindow init.
    private WindowThemeManager? _themeManager;

    public CommandPaletteControl()
    {
        InitializeComponent();
        // Configure() runs during MainWindow init, before this control
        // is in the visual tree. Parent-walk to the Popup may not
        // resolve yet, so re-apply on Loaded to guarantee the Popup's
        // RequestedTheme ends up in sync.
        Loaded += OnPaletteLoaded;
        Unloaded += OnPaletteUnloaded;
    }

    private void OnPaletteLoaded(object sender, RoutedEventArgs e) => ApplyTheme();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Wires theme resolution to the live config + OS theme. Matches
    /// the SettingsWindow pattern: explicit <c>window-theme</c> wins,
    /// "system" follows the OS, and any other value (auto/ghostty)
    /// falls back to the OS too — the palette is a system-native
    /// surface, not a palette-coloured one.
    ///
    /// Idempotent re-wiring is supported: a subsequent call tears
    /// down the previous subscription before creating a new one. In
    /// practice MainWindow calls Configure exactly once during init.
    /// </summary>
    public void Configure(IConfigService configService)
    {
        ArgumentNullException.ThrowIfNull(configService);

        if (_themeManager is not null)
        {
            _themeManager.ThemeChanged -= OnThemeChanged;
            _themeManager.Dispose();
        }

        _themeManager = new WindowThemeManager(
            configService, DispatcherQueue, ThemeFallbackStyle.System);
        _themeManager.ThemeChanged += OnThemeChanged;
        ApplyTheme();
    }

    private void OnPaletteUnloaded(object sender, RoutedEventArgs e)
    {
        if (_themeManager is null) return;
        _themeManager.ThemeChanged -= OnThemeChanged;
        _themeManager.Dispose();
        _themeManager = null;
    }

    private void OnThemeChanged(bool _) => ApplyTheme();

    private void ApplyTheme()
    {
        if (_themeManager is null) return;
        var theme = _themeManager.ElementTheme;
        RequestedTheme = theme;
        // Popup is a theme-inheritance boundary: its own RequestedTheme
        // stays at Default (= Application.RequestedTheme, which we pin
        // at launch based on the initial system theme) regardless of
        // what we set on the child UserControl. That leaves XAML
        // {ThemeResource} bindings in templated ListView rows resolved
        // against the stale Application theme, which is why flipping
        // Windows light/dark left the palette text stuck on the wrong
        // brushes even after our own RequestedTheme flipped. Sync the
        // Popup's theme too.
        if (Parent is Popup popup) popup.RequestedTheme = theme;
        // Re-resolve the background brush — acrylic + solid-fill brushes
        // are pulled from Application.Resources.ThemeDictionaries in
        // code-behind, so they don't auto-update when RequestedTheme
        // flips the way XAML {ThemeResource} bindings do.
        if (!string.IsNullOrEmpty(_backgroundSetting))
            ApplySettings(_backgroundSetting);
    }

    /// <summary>
    /// Connects the control to a <see cref="CommandPaletteViewModel"/>.
    /// May be called multiple times; each call replaces the previous subscription.
    /// </summary>
    public void Bind(CommandPaletteViewModel viewModel)
    {
        _vm?.PropertyChanged -= OnViewModelPropertyChanged;

        // The subscribe below skips ?. on purpose: _vm was just
        // assigned the non-nullable viewModel parameter on the
        // previous line.
        _vm = viewModel;
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        // Defer initial sync until the control is loaded into the visual tree.
        // Setting ItemsSource on a ListView that hasn't been measured yet throws
        // ArgumentException from WinUI's ItemsControl.
        if (IsLoaded)
            SyncAll();
        else
            Loaded += OnFirstLoaded;
    }

    private void OnFirstLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded;
        SyncAll();
    }

    /// <summary>
    /// Moves keyboard focus into the search TextBox.
    /// Called by MainWindow after opening the Popup, because WinUI Popups
    /// don't automatically move focus into their content.
    /// </summary>
    public void FocusSearchBox()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Applies the configured background material to the outer Border.
    ///
    /// Supported values:
    ///   "acrylic" (default) — in-app acrylic via <c>AcrylicInAppFillColorDefaultBrush</c>.
    ///     This is the frosted-glass translucent look that uses the WinUI acrylic brush.
    ///     In high contrast mode, WinUI's ThemeResource automatically substitutes a
    ///     solid system color so no special handling is needed.
    ///   "mica"    — Mica is a window-level SystemBackdrop, not a per-element brush.
    ///     For individual control surfaces we approximate it with the solid base fill
    ///     (<c>SolidBackgroundFillColorBaseBrush</c>).
    ///   "opaque"  — fully opaque solid fill using <c>SolidBackgroundFillColorBaseBrush</c>.
    /// </summary>
    public void ApplySettings(string backgroundSetting)
    {
        _backgroundSetting = backgroundSetting ?? string.Empty;
        var key = backgroundSetting switch
        {
            "mica" => "SolidBackgroundFillColorBaseBrush",
            "opaque" => "SolidBackgroundFillColorBaseBrush",
            _ => "AcrylicInAppFillColorDefaultBrush",
        };
        OuterBorder.Background = ResolveAppBrushForElementTheme(key);
    }

    // Resolve a system brush by walking
    // Application.Resources.ThemeDictionaries with THIS control's
    // ActualTheme rather than the Application's RequestedTheme.
    // Application.Current.Resources[key] returns whichever theme entry
    // the Application is pinned to, which is wrong whenever the control
    // lives under a window whose ElementTheme differs from the app's —
    // e.g. a dark-palette main window while the OS (and Application) are
    // in Light mode. That mismatch was visible as invisible text on a
    // wrong-tone acrylic in the command palette.
    private Brush ResolveAppBrushForElementTheme(string key)
    {
        if (ThemedResources.TryFindBrush(Application.Current.Resources, key, ActualTheme, out var brush))
            return brush;
        // Fallback preserves pre-fix behavior if the theme dictionary
        // can't be walked (custom app resources, HighContrast, etc.).
        return (Brush)Application.Current.Resources[key];
    }

    // ── ViewModel → UI ────────────────────────────────────────────────────────

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // All UI updates must happen on the UI thread.
        if (DispatcherQueue is null) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_vm is null) return;

            switch (e.PropertyName)
            {
                case nameof(CommandPaletteViewModel.ModeLabel):
                    ModeLabel.Text = _vm.ModeLabel;
                    UpdateFooterHints();
                    break;

                case nameof(CommandPaletteViewModel.IsPinned):
                    PinButton.IsChecked = _vm.IsPinned;
                    break;

                case nameof(CommandPaletteViewModel.SearchText):
                    // Only sync when ViewModel drives the change (not when
                    // the TextBox itself fired TextChanged — avoids cursor jump).
                    if (SearchBox.Text != _vm.SearchText)
                        SearchBox.Text = _vm.SearchText;
                    break;

                case nameof(CommandPaletteViewModel.FilteredCommands):
                    SyncFilteredCommands();
                    break;

                case nameof(CommandPaletteViewModel.SelectedCommand):
                    SyncSelectedItem();
                    break;

                case nameof(CommandPaletteViewModel.StatusText):
                    StatusLabel.Text = _vm.StatusText;
                    LiveAnnouncer.Text = _vm.StatusText;
                    break;
            }
        });
    }

    private void SyncAll()
    {
        if (_vm is null) return;

        ModeLabel.Text = _vm.ModeLabel;
        PinButton.IsChecked = _vm.IsPinned;
        SearchBox.Text = _vm.SearchText;
        StatusLabel.Text = _vm.StatusText;
        SyncFilteredCommands();
        UpdateFooterHints();
    }

    private void SyncFilteredCommands()
    {
        if (_vm is null) return;

        // WinUI's ItemsSource setter throws ArgumentException for internal
        // types because the XAML runtime can't access them via reflection.
        // Use Items.Clear() + Add() instead — the ContainerContentChanging
        // handler populates the template elements from the CommandItem.
        ResultsList.Items.Clear();
        foreach (var cmd in _vm.FilteredCommands)
            ResultsList.Items.Add(cmd);

        SyncSelectedItem();
    }

    private void SyncSelectedItem()
    {
        if (_vm is null) return;

        // Map ViewModel.SelectedCommand back to the ListView's SelectedItem.
        // The ListView's ItemsSource is a CommandItem[], so we match by index.
        if (_vm.SelectedCommand is null)
        {
            ResultsList.SelectedItem = null;
            return;
        }

        var idx = _vm.FilteredCommands.IndexOf(_vm.SelectedCommand);
        if (idx >= 0 && ResultsList.Items.Count > idx)
        {
            ResultsList.SelectedIndex = idx;
            ResultsList.ScrollIntoView(ResultsList.Items[idx]);
        }
    }

    private void UpdateFooterHints()
    {
        if (_vm is null) return;

        ShortcutHints.Text = _vm.Mode == PaletteMode.CommandLine
            ? "Tab autocomplete   ↑↓ navigate   ↵ run   Esc close"
            : "↑↓ navigate   ↵ run   Esc close";
    }

    // ── ContainerContentChanging: populate DataTemplate elements ─────────────

    /// <summary>
    /// Populates the named elements inside each <see cref="CommandItemTemplate"/>
    /// with data from the corresponding <see cref="CommandItem"/>.
    ///
    /// This replaces XAML data-binding: because <see cref="CommandItem"/> is
    /// <c>internal</c>, x:Bind would require public types, and regular
    /// {Binding} on a record is fragile with AOT. Code-behind element lookup
    /// is straightforward and AOT-safe.
    /// </summary>
    private void OnContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.Item is not CommandItem item) return;

        // Phase 0 fires synchronously during measure; grab the template root.
        // Children are indexed by column order in the DataTemplate:
        //   [0] Ellipse  [1] FontIcon  [2] StackPanel  [3] Border>TextBlock
        if (args.ItemContainer.ContentTemplateRoot is not Grid root || root.Children.Count < 4)
            return;

        // Color dot (column 0)
        if (root.Children[0] is Ellipse dot)
        {
            if (item.LeadingColor is { } color)
            {
                dot.Fill = new SolidColorBrush(
                    Color.FromArgb(color.A, color.R, color.G, color.B));
                dot.Visibility = Visibility.Visible;
            }
            else
            {
                dot.Visibility = Visibility.Collapsed;
            }
        }

        // Leading icon glyph (column 1)
        if (root.Children[1] is FontIcon icon)
        {
            if (!string.IsNullOrEmpty(item.LeadingIcon))
            {
                icon.Glyph = item.LeadingIcon;
                icon.Visibility = Visibility.Visible;
            }
            else
            {
                icon.Visibility = Visibility.Collapsed;
            }
        }

        // Title + Description (column 2 = StackPanel with 2 TextBlocks)
        if (root.Children[2] is StackPanel stack && stack.Children.Count >= 2)
        {
            if (stack.Children[0] is TextBlock title)
                title.Text = item.Title;

            if (stack.Children[1] is TextBlock desc)
            {
                if (!string.IsNullOrEmpty(item.Description))
                {
                    desc.Text = item.Description;
                    desc.Visibility = Visibility.Visible;
                }
                else
                {
                    desc.Visibility = Visibility.Collapsed;
                }
            }
        }

        // Shortcut (column 3 = Border containing TextBlock)
        if (root.Children[3] is Border shortcutBorder)
        {
            if (item.Shortcut is { } kb)
            {
                if (shortcutBorder.Child is TextBlock shortcutText)
                    shortcutText.Text = FormatKeyBinding(kb);
                shortcutBorder.Visibility = Visibility.Visible;
            }
            else
            {
                shortcutBorder.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ── UI → ViewModel ────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null) return;

        // Push the raw text into the ViewModel; OnSearchTextChanged partial
        // method there will update Mode, FilteredCommands, etc.
        _vm.SearchText = SearchBox.Text;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_vm is null) return;

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var isCtrl = ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        switch (e.Key)
        {
            case VirtualKey.Escape:
                _vm.Close();
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                _vm.ExecuteSelectedCommand();
                e.Handled = true;
                break;

            case VirtualKey.Tab:
                if (_vm.Mode == PaletteMode.CommandLine)
                {
                    _vm.AcceptAutocomplete();
                    // Sync the TextBox immediately so the cursor lands at end.
                    SearchBox.Text = _vm.SearchText;
                    SearchBox.SelectionStart = SearchBox.Text.Length;
                    e.Handled = true;
                }
                break;

            case VirtualKey.Up:
                _vm.MoveSelectionUp();
                e.Handled = true;
                break;

            case VirtualKey.Down:
                _vm.MoveSelectionDown();
                e.Handled = true;
                break;

            case VirtualKey.P when isCtrl:
                _vm.MoveSelectionUp();
                e.Handled = true;
                break;

            case VirtualKey.N when isCtrl:
                _vm.MoveSelectionDown();
                e.Handled = true;
                break;
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (_vm is null) return;
        if (e.ClickedItem is CommandItem item)
        {
            _vm.SelectedCommand = item;
            _vm.ExecuteSelectedCommand();
        }
    }

    private void OnResultsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // When the user clicks a different item without executing, keep the
        // ViewModel's SelectedCommand in sync so keyboard Enter works on the
        // visually-selected item.
        if (_vm is null) return;
        if (ResultsList.SelectedItem is CommandItem item)
            _vm.SelectedCommand = item;
    }

    private void OnPinChecked(object sender, RoutedEventArgs e)
    {
        _vm?.IsPinned = true;
    }

    private void OnPinUnchecked(object sender, RoutedEventArgs e)
    {
        _vm?.IsPinned = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a <see cref="Input.KeyBinding"/> into a compact display string
    /// suitable for the shortcut key-cap, e.g. "Ctrl+Shift+P".
    /// </summary>
    private static string FormatKeyBinding(Input.KeyBinding kb)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (kb.Modifiers.HasFlag(VirtualKeyModifiers.Control)) parts.Add("Ctrl");
        if (kb.Modifiers.HasFlag(VirtualKeyModifiers.Menu))    parts.Add("Alt");
        if (kb.Modifiers.HasFlag(VirtualKeyModifiers.Shift))   parts.Add("Shift");
        if (kb.Modifiers.HasFlag(VirtualKeyModifiers.Windows)) parts.Add("Win");

        // Convert VirtualKey to a display name.
        var key = kb.Key.ToString();

        // Trim "Number" prefix from digit keys (VirtualKey.Number0 → "0")
        if (key.StartsWith("Number", StringComparison.Ordinal))
            key = key["Number".Length..];

        parts.Add(key);
        return string.Join("+", parts);
    }
}
