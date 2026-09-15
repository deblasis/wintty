using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using Ghostty.Accessibility;
using Ghostty.Commands;
using Ghostty.Core.Accessibility;
using Ghostty.Core.Config;
using Ghostty.Core.Themes;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    // Config+OS-driven theme resolver. The palette uses Palette fallback
    // (same as MainWindow) so it matches the window chrome: window-theme=
    // ghostty + a dark terminal renders a dark window AND a dark palette,
    // instead of the palette tracking the OS theme and mismatching the
    // chrome (#236). Created in Configure() and disposed on Unloaded.
    // Null until Configure runs, which happens once during MainWindow init.
    private WindowThemeManager? _themeManager;

    // Decides the mode label's accessible name and which result counts are
    // worth speaking. Stateful across keystrokes (it suppresses repeats),
    // so it lives with the control rather than being created per update.
    private readonly CommandPaletteAnnouncer _announcer = new();

    // A theme browse holds the palette on the light or dark variant it opened
    // in. Each preview re-resolves the window's variant from the previewed
    // background, so without the hold a held arrow key across light and dark
    // themes flips the whole palette surface on every boundary. The window
    // chrome still previews; the palette catches up when the browse ends.
    private bool _themeSyncDeferred;

    // Kept from Configure, so a Loaded after an Unloaded can rebuild the
    // theme manager the Unloaded disposed.
    private IConfigService? _configService;

    // Swatch fills, one brush per colour. Rows are realized and recycled as
    // the list scrolls or refilters, and the same few dozen colours recur.
    private readonly Dictionary<uint, SolidColorBrush> _swatchBrushes = new();

    // The row template's fixed height, which Page Up/Down counts screens in.
    private const double RowHeight = 44;

    public CommandPaletteControl()
    {
        InitializeComponent();
        LinkSearchBoxToResults();
        // Configure() runs during MainWindow init, before this control
        // is in the visual tree. Parent-walk to the Popup may not
        // resolve yet, so re-apply on Loaded to guarantee the Popup's
        // RequestedTheme ends up in sync.
        Loaded += OnPaletteLoaded;
        Unloaded += OnPaletteUnloaded;
    }

    private void OnPaletteLoaded(object sender, RoutedEventArgs e)
    {
        // The palette lives in a Popup, and closing the Popup unloads it, which
        // disposes the theme manager (OnPaletteUnloaded). Without a new one
        // here, every open after the first would keep whatever variant the
        // first close left behind, and a browse that ends in Enter could not
        // catch up with the theme it just applied.
        if (_themeManager is null && _configService is not null)
        {
            _themeSyncDeferred = false;
            SubscribeThemeManager(_configService);
        }
        ApplyTheme();
    }

    // Up/Down are handled in OnSearchKeyDown and marked handled, so focus
    // never leaves the search box while the user walks the list. That is
    // the case ControlledPeers exists for: it tells a reader which element
    // the box it is sitting in drives, so the list is reachable at all.
    private void LinkSearchBoxToResults() =>
        AutomationProperties.GetControlledPeers(SearchBox).Add(ResultsList);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Wires theme resolution to the live config + OS theme. Matches
    /// the MainWindow chrome: explicit <c>window-theme</c> wins,
    /// "system" follows the OS, and any other value (auto/ghostty)
    /// falls back to the terminal background luminance — so the palette
    /// renders in the same light/dark variant as the window it floats
    /// over (#236).
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

        _configService = configService;
        SubscribeThemeManager(configService);
        ApplyTheme();
    }

    private void SubscribeThemeManager(IConfigService configService)
    {
        _themeManager = new WindowThemeManager(
            configService, DispatcherQueue, ThemeFallbackStyle.Palette);
        _themeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnPaletteUnloaded(object sender, RoutedEventArgs e)
    {
        if (_themeManager is null) return;
        _themeManager.ThemeChanged -= OnThemeChanged;
        _themeManager.Dispose();
        _themeManager = null;
    }

    private void OnThemeChanged(bool _)
    {
        if (_vm is { IsOpen: true, Mode: PaletteMode.Theme })
        {
            _themeSyncDeferred = true;
            return;
        }
        ApplyTheme();
    }

    // The browse is over (Escape, Enter, any close): take whatever variant the
    // window settled on while the palette was holding still.
    private void ResumeThemeSync()
    {
        if (!_themeSyncDeferred) return;
        if (_vm is { IsOpen: true, Mode: PaletteMode.Theme }) return;
        _themeSyncDeferred = false;
        ApplyTheme();
    }

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

        // The result count is published only now, and in a follow-up turn
        // of the queue rather than alongside the focus call. Everything
        // the view model raises while opening is already queued ahead of
        // this, and a reader flushes what it is holding when focus moves,
        // so a count published any earlier is a count published into the
        // gap. Focused() is what holds it back until here.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (_vm is null) return;
            PublishStatus(_announcer.Focused(_vm.StatusText));
        });
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
                case nameof(CommandPaletteViewModel.IsOpen):
                    // Arm the announcer before any of the counts this open
                    // is about to raise reach it. Reopening on the count it
                    // closed on raises no StatusText at all, because the
                    // view model suppresses same-value changes, so the open
                    // itself has to be what speaks.
                    if (_vm.IsOpen) _announcer.Opening();
                    else ResumeThemeSync();
                    break;

                case nameof(CommandPaletteViewModel.ModeLabel):
                    SetModeLabel(_vm.ModeLabel);
                    UpdateModeChrome();
                    ResumeThemeSync();
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
                    PublishStatus(_announcer.StatusChanged(_vm.StatusText));
                    break;

                case nameof(CommandPaletteViewModel.ThemeNoMatchText):
                    SyncNoMatch();
                    break;
            }
        });
    }

    // Put the current count on screen, and speak it when the announcer
    // says this one is worth speaking.
    //
    // The count rides the footer label as a live region rather than a
    // notification from the search box. Notifications are queued per
    // source element and MostRecent discards whatever is still pending
    // from that element, so a count raised from the search box is
    // discarded by the row title raised from the search box immediately
    // after it - which is every keystroke. A live region is a different
    // element and a different event class, so the two stop colliding.
    private void PublishStatus(string? toSpeak)
    {
        if (_vm is null) return;
        StatusLabel.Text = _vm.StatusText;
        if (toSpeak is null) return;
        UiaAnnouncer.RaiseLiveRegionChanged(StatusLabel);
    }

    // Keep the spoken name in step with the visible text. An explicit
    // AutomationProperties.Name replaces the text a reader would derive
    // from the TextBlock, so leaving a fixed name here reads the field and
    // never the mode.
    private void SetModeLabel(string modeLabel)
    {
        ModeLabel.Text = modeLabel;
        AutomationProperties.SetName(
            ModeLabel, CommandPaletteAnnouncer.ModeAccessibleName(modeLabel));
    }

    private void SyncAll()
    {
        if (_vm is null) return;

        SetModeLabel(_vm.ModeLabel);
        PinButton.IsChecked = _vm.IsPinned;
        SearchBox.Text = _vm.SearchText;
        StatusLabel.Text = _vm.StatusText;
        SyncFilteredCommands();
        UpdateModeChrome();
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

            // Arrowing the palette never moves focus: OnSearchKeyDown handles
            // Up/Down and marks them handled, so the list is never focused and
            // its selection-changed event goes unannounced. Without this the
            // row names below are invisible on the one path a screen-reader
            // user actually takes through the palette. Announce is gated on a
            // listener existing, so nobody else pays for it.
            UiaAnnouncer.Announce(SearchBox, SelectionAnnouncement(_vm.SelectedCommand), "palette-selection");
        }

        // The highlight is the preview: move the "Previewing" badge with it.
        RefreshThemeBadges();
    }

    // What is spoken when the highlight lands on a row. A theme row says
    // whether it is the configured theme or the one now on the terminals.
    private string SelectionAnnouncement(CommandItem item)
    {
        if (_vm is null || item.ThemeName is not { } theme) return item.Title;
        return ThemeRowPresentation.Announcement(
            theme,
            item.IsCurrentTheme,
            _vm.IsThemePreviewed(theme),
            _vm.LoadThemeSwatch(theme)?.IsDark);
    }

    // The footer hint, and what the search box and the list are called, per
    // mode. The accessible names follow the mode for the same reason the mode
    // label's does: a reader in the theme list that hears "Search commands"
    // is told the wrong thing about what typing does.
    private void UpdateModeChrome()
    {
        if (_vm is null) return;

        var theme = _vm.Mode == PaletteMode.Theme;
        ShortcutHints.Text = _vm.Mode switch
        {
            PaletteMode.CommandLine => "Tab autocomplete   ↑↓ navigate   ↵ run   Esc close",
            PaletteMode.Theme => "↑↓ preview   ↵ apply   Esc cancel",
            _ => "↑↓ navigate   ↵ run   Esc close",
        };
        SearchBox.PlaceholderText = theme
            ? "Filter themes..."
            : "Search commands or type > for actions...";
        AutomationProperties.SetName(SearchBox, theme ? "Filter themes" : "Search commands");
        AutomationProperties.SetName(ResultsList, theme ? "Themes" : "Command results");

        // The theme list keeps its full height while a filter narrows it, so
        // the card does not shrink and grow under the pointer as the user
        // types; the command list sizes to its results as it always has.
        ResultsList.MinHeight = theme ? ResultsList.MaxHeight : 0;
        SyncNoMatch();
    }

    // The theme list's "no themes match" line, in place of the rows.
    private void SyncNoMatch()
    {
        var text = _vm?.Mode == PaletteMode.Theme ? _vm.ThemeNoMatchText : null;
        NoMatchText.Text = text ?? "";
        NoMatchRow.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
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

        // A theme row's swatch, when it has been read already. Painted now,
        // in this phase, so a row that refilters or scrolls back never shows
        // the blank tile for a frame; an unread one is read in phase 1.
        ThemeSwatch? swatch = null;
        var swatchKnown = item.ThemeName is { } themeName
            && _vm is not null
            && _vm.TryGetCachedThemeSwatch(themeName, out swatch);

        // Name the container before anything below can bail out; the
        // decision of what each property should hold (and which of them
        // are absent for this item) is CommandRowAutomation's, or for a
        // theme row ThemeRowPresentation's, which says "current theme".
        var row = item.ThemeName is { } theme
            ? ThemeRowPresentation.Automation(theme, item.IsCurrentTheme, swatch?.IsDark)
            : CommandRowAutomation.For(
                item.Title,
                item.Description,
                item.Shortcut is { } binding ? FormatKeyBinding(binding) : null);

        AutomationProperties.SetName(args.ItemContainer, row.Name);
        SetOrClear(args.ItemContainer, AutomationProperties.HelpTextProperty, row.HelpText);
        SetOrClear(args.ItemContainer, AutomationProperties.AcceleratorKeyProperty, row.AcceleratorKey);

        // Phase 0 fires synchronously during measure; grab the template root.
        // Children are indexed by column order in the DataTemplate:
        //   [0] Ellipse  [1] FontIcon  [2] StackPanel  [3] Border>TextBlock
        // and the theme row part after them (found by name, see ThemeRowParts).
        if (args.ItemContainer.ContentTemplateRoot is not Grid root || root.Children.Count < 4)
            return;

        var parts = ThemeRowParts.Of(root);
        if (item.ThemeName is { } rowTheme && parts is not null)
        {
            ShowThemeRow(root, parts, true);
            parts.Theme = rowTheme;
            parts.Name.Text = rowTheme;
            if (swatchKnown) PaintSwatch(parts, swatch);
            else
            {
                ClearSwatch(parts);
                args.RegisterUpdateCallback(OnThemeRowSwatchPhase);
            }
            RefreshBadge(parts, item);
            return;
        }
        ShowThemeRow(root, parts, false);

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

        // Leading icon (column 1): a custom path icon (e.g. the Quake mark) wins
        // over the glyph; otherwise fall back to the LeadingIcon glyph.
        var usePathIcon = !string.IsNullOrEmpty(item.LeadingIconPathKey);

        if (root.Children[1] is FontIcon icon)
        {
            if (!usePathIcon && !string.IsNullOrEmpty(item.LeadingIcon))
            {
                icon.Glyph = item.LeadingIcon;
                icon.Visibility = Visibility.Visible;
            }
            else
            {
                icon.Visibility = Visibility.Collapsed;
            }
        }

        // Locate the custom path-icon host by type rather than by child index,
        // so reordering the template's children can't silently break it (the
        // template has exactly one Viewbox).
        if (root.Children.OfType<Viewbox>().FirstOrDefault() is { } pathHost)
            pathHost.Visibility = usePathIcon ? Visibility.Visible : Visibility.Collapsed;

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

    // ── Theme rows ───────────────────────────────────────────────────────────

    /// <summary>
    /// The named parts of one realized row's theme half, looked up once per
    /// template instance and kept on its root. Null when the template has no
    /// theme half (it always has; the null keeps a bad edit from crashing).
    /// </summary>
    private sealed class ThemeRowParts
    {
        public required Grid Row { get; init; }
        public required Border Tile { get; init; }
        public required TextBlock Sample { get; init; }
        public required Rectangle Cursor { get; init; }
        public required Rectangle[] Strip { get; init; }
        public required TextBlock Name { get; init; }
        public required FontIcon HintGlyph { get; init; }
        public required TextBlock HintText { get; init; }
        public required FrameworkElement CurrentBadge { get; init; }
        public required FrameworkElement PreviewBadge { get; init; }
        public string? Theme { get; set; }
        public bool Painted { get; set; }
        public ThemeSwatch? Swatch { get; set; }

        public static ThemeRowParts? Of(Grid root)
        {
            if (root.Tag is ThemeRowParts known) return known;
            if (root.FindName("ThemeRow") is not Grid row
                || root.FindName("SwatchTile") is not Border tile
                || root.FindName("SwatchSample") is not TextBlock sample
                || root.FindName("SwatchCursor") is not Rectangle cursor
                || root.FindName("SwatchStrip") is not StackPanel strip
                || root.FindName("ThemeName") is not TextBlock name
                || root.FindName("ThemeHintGlyph") is not FontIcon glyph
                || root.FindName("ThemeHintText") is not TextBlock hint
                || root.FindName("ThemeCurrentBadge") is not FrameworkElement current
                || root.FindName("ThemePreviewBadge") is not FrameworkElement preview)
                return null;
            var parts = new ThemeRowParts
            {
                Row = row,
                Tile = tile,
                Sample = sample,
                Cursor = cursor,
                Strip = strip.Children.OfType<Rectangle>().ToArray(),
                Name = name,
                HintGlyph = glyph,
                HintText = hint,
                CurrentBadge = current,
                PreviewBadge = preview,
            };
            root.Tag = parts;
            return parts;
        }
    }

    // One template carries both kinds of row; show the half this item needs.
    // The command half's own elements are set per item by the code below it,
    // except the title stack, which a theme row collapsed.
    private static void ShowThemeRow(Grid root, ThemeRowParts? parts, bool theme)
    {
        if (parts is not null)
            parts.Row.Visibility = theme ? Visibility.Visible : Visibility.Collapsed;
        foreach (var child in root.Children)
        {
            if (parts is not null && ReferenceEquals(child, parts.Row)) continue;
            if (theme) child.Visibility = Visibility.Collapsed;
        }
        if (!theme) root.Children[2].Visibility = Visibility.Visible;
    }

    // Phase 1: read the swatch this row did not find cached. Deferred so a
    // long list realizes and shows its rows before any theme file is read.
    private void OnThemeRowSwatchPhase(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || _vm is null) return;
        if (args.Item is not CommandItem { ThemeName: { } theme }) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid root) return;
        if (ThemeRowParts.Of(root) is not { } parts || parts.Theme != theme) return;

        var swatch = _vm.LoadThemeSwatch(theme);
        PaintSwatch(parts, swatch);
        SetOrClear(args.ItemContainer, AutomationProperties.HelpTextProperty,
            ThemeRowPresentation.HelpText(swatch?.IsDark));
    }

    // Fill the swatch and the hint. A theme with no readable file keeps the
    // neutral tile and no hint: nothing about it is known.
    private void PaintSwatch(ThemeRowParts parts, ThemeSwatch? swatch)
    {
        parts.Painted = true;
        parts.Swatch = swatch;
        if (swatch is null)
        {
            ClearSwatch(parts);
            parts.Painted = true;
            return;
        }

        parts.Tile.Background = SwatchBrush(swatch.Background);
        parts.Sample.Foreground = SwatchBrush(swatch.Foreground);
        parts.Cursor.Fill = SwatchBrush(swatch.Cursor);
        for (var i = 0; i < parts.Strip.Length && i < swatch.Palette.Count; i++)
            parts.Strip[i].Fill = SwatchBrush(swatch.Palette[i]);
        parts.Tile.Opacity = 1;

        // Segoe Fluent Icons: U+E708 = QuietHours (moon), U+E706 = Brightness (sun).
        parts.HintGlyph.Glyph = swatch.IsDark ? "\uE708" : "\uE706";
        parts.HintText.Text = ThemeRowPresentation.Hint(swatch.IsDark) ?? "";
    }

    // Back to the neutral tile, for a recycled container whose next theme has
    // not been read yet. Opacity only: sizes never change, so nothing moves.
    private static void ClearSwatch(ThemeRowParts parts)
    {
        parts.Painted = false;
        parts.Swatch = null;
        parts.Tile.Opacity = 0;
        parts.HintGlyph.Glyph = "";
        parts.HintText.Text = "";
    }

    private SolidColorBrush SwatchBrush(uint rgb)
    {
        if (_swatchBrushes.TryGetValue(rgb, out var brush)) return brush;
        brush = new SolidColorBrush(Color.FromArgb(
            0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        _swatchBrushes[rgb] = brush;
        return brush;
    }

    private void RefreshBadge(ThemeRowParts parts, CommandItem item)
    {
        if (_vm is null || item.ThemeName is not { } theme) return;
        var badge = ThemeRowPresentation.Badge(item.IsCurrentTheme, _vm.IsThemePreviewed(theme));
        parts.CurrentBadge.Visibility = badge == ThemeRowBadge.Current ? Visibility.Visible : Visibility.Collapsed;
        parts.PreviewBadge.Visibility = badge == ThemeRowBadge.Previewing ? Visibility.Visible : Visibility.Collapsed;
    }

    // Every realized theme row, re-badged for where the highlight is now.
    private void RefreshThemeBadges()
    {
        if (_vm?.Mode != PaletteMode.Theme) return;
        foreach (var (item, parts, _) in RealizedThemeRows())
            RefreshBadge(parts, item);
    }

    private IEnumerable<(CommandItem Item, ThemeRowParts Parts, ListViewItem Container)> RealizedThemeRows()
    {
        if (ResultsList.ItemsPanelRoot is not Panel panel) yield break;
        foreach (var child in panel.Children)
        {
            if (child is ListViewItem { Content: CommandItem { ThemeName: not null } item } container
                && container.ContentTemplateRoot is Grid root
                && root.Tag is ThemeRowParts parts
                && parts.Theme == item.ThemeName
                && parts.Row.Visibility == Visibility.Visible)
                yield return (item, parts, container);
        }
    }

    // Containers are recycled - SyncFilteredCommands clears and refills the
    // list on every keystroke - and a local value set for one item outlives
    // it, so an absent value has to be cleared rather than blanked: UIA
    // reports an empty string as present-but-blank, which leaves the next
    // row reading the previous row's text.
    private static void SetOrClear(DependencyObject target, DependencyProperty property, string? value)
    {
        if (value is null)
            target.ClearValue(property);
        else
            target.SetValue(property, value);
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

        if (HandleSearchKey(e.Key, isCtrl)) e.Handled = true;
    }

    /// <summary>
    /// What a key pressed in the search box does, and whether it was
    /// consumed. The one decision both the real KeyDown and the test seam's
    /// palette-key op go through, so the seam cannot drift from the keyboard.
    /// </summary>
    private bool HandleSearchKey(VirtualKey key, bool isCtrl)
    {
        if (_vm is null) return false;

        switch (key)
        {
            case VirtualKey.Escape:
                _vm.Close();
                return true;

            case VirtualKey.Enter:
                _vm.ExecuteSelectedCommand();
                return true;

            case VirtualKey.Tab:
                if (_vm.Mode == PaletteMode.CommandLine)
                {
                    _vm.AcceptAutocomplete();
                    // Sync the TextBox immediately so the cursor lands at end.
                    SearchBox.Text = _vm.SearchText;
                    SearchBox.SelectionStart = SearchBox.Text.Length;
                    return true;
                }
                return false;

            case VirtualKey.Up:
                _vm.MoveSelectionUp();
                return true;

            case VirtualKey.Down:
                _vm.MoveSelectionDown();
                return true;

            case VirtualKey.P when isCtrl:
                _vm.MoveSelectionUp();
                return true;

            case VirtualKey.N when isCtrl:
                _vm.MoveSelectionDown();
                return true;

            // A screenful at a time, for a long theme list. A single-line
            // search box has no use of its own for either key.
            case VirtualKey.PageUp:
                _vm.MoveSelectionBy(-PageStep());
                return true;

            case VirtualKey.PageDown:
                _vm.MoveSelectionBy(PageStep());
                return true;

            default:
                return false;
        }
    }

    // One row short of what the list shows, so the row the highlight left is
    // still on screen after the page turns.
    private int PageStep() => Math.Max(1, (int)(ResultsList.ActualHeight / RowHeight) - 1);

    // ---- test seam accessors (compiled into every build, reachable only
    // through the seam's pipe, which exists only in a TESTSEAM build) ------

    /// <summary>A key pressed in the search box, through the real handler.</summary>
    internal bool TestSeamKey(VirtualKey key) => HandleSearchKey(key, isCtrl: false);

    /// <summary>
    /// Text typed into the search box. The box's TextChanged reaches the view
    /// model a turn later, so the view model is told now as well; the second
    /// assignment of the same text is a no-op there.
    /// </summary>
    internal void TestSeamType(string text)
    {
        SearchBox.Text = text;
        if (_vm is not null) _vm.SearchText = text;
    }

    /// <summary>
    /// Backspace in the search box: what the TextBox does with the key itself
    /// (it is not one HandleSearchKey takes), the last character removed.
    /// </summary>
    internal void TestSeamBackspace()
    {
        var text = SearchBox.Text;
        if (text.Length > 0) TestSeamType(text[..^1]);
    }

    /// <summary>The search box's text.</summary>
    internal string TestSeamSearchText => SearchBox.Text;

    /// <summary>The "no themes match" line, when it is on screen, else null.</summary>
    internal string? TestSeamNoMatch =>
        NoMatchRow.Visibility == Visibility.Visible ? NoMatchText.Text : null;

    /// <summary>The light/dark variant the palette is drawn in.</summary>
    internal string TestSeamElementTheme => ActualTheme.ToString();

    /// <summary>Whether the palette is following the window's light/dark variant.</summary>
    internal bool TestSeamTracksWindowTheme => _themeManager is not null;

    /// <summary>The footer's key hint.</summary>
    internal string TestSeamFooterHint => ShortcutHints.Text;

    /// <summary>The search box's accessible name and placeholder.</summary>
    internal (string Name, string Placeholder) TestSeamSearchBox =>
        (AutomationProperties.GetName(SearchBox), SearchBox.PlaceholderText);

    /// <summary>The results list's accessible name.</summary>
    internal string TestSeamListName => AutomationProperties.GetName(ResultsList);

    /// <summary>The palette's card, for a rect a pixel oracle can sample.</summary>
    internal FrameworkElement TestSeamCard => OuterBorder;

    /// <summary>One realized theme row, as drawn.</summary>
    internal sealed record TestSeamThemeRow(
        string Theme,
        bool Current,
        bool Selected,
        string Badge,
        string AutomationName,
        string? HelpText,
        string Hint,
        bool Painted,
        ThemeSwatch? Swatch,
        FrameworkElement Container,
        FrameworkElement Tile,
        FrameworkElement Sample,
        FrameworkElement Cursor,
        IReadOnlyList<FrameworkElement> Strip);

    /// <summary>The theme rows the list has realized, top to bottom.</summary>
    internal IReadOnlyList<TestSeamThemeRow> TestSeamThemeRows()
    {
        var rows = new List<TestSeamThemeRow>();
        foreach (var (item, parts, container) in RealizedThemeRows())
        {
            var badge = parts.CurrentBadge.Visibility == Visibility.Visible ? "Current"
                : parts.PreviewBadge.Visibility == Visibility.Visible ? "Previewing"
                : "";
            rows.Add(new TestSeamThemeRow(
                item.ThemeName!,
                item.IsCurrentTheme,
                ReferenceEquals(ResultsList.SelectedItem, item) || container.IsSelected,
                badge,
                AutomationProperties.GetName(container),
                container.ReadLocalValue(AutomationProperties.HelpTextProperty) as string,
                parts.HintText.Text,
                parts.Painted,
                parts.Swatch,
                container,
                parts.Tile,
                parts.Sample,
                parts.Cursor,
                parts.Strip));
        }
        return rows
            .OrderBy(r => ResultsList.IndexFromContainer(r.Container))
            .ToList();
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

        // Render the key via the shared OEM-aware table so punctuation keys
        // (VK 188 → ",", 191 → "/") and digits (Number1 → "1") show their
        // symbol instead of the raw VirtualKey name or numeric code.
        parts.Add(Input.KeyBindings.KeyDisplayName(kb.Key));
        return string.Join("+", parts);
    }
}
