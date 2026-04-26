using System;
using System.Collections.Generic;
using System.Linq;
using Ghostty.Core;
using Ghostty.Core.Config;
using Ghostty.Core.Settings;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Ghostty.Settings;

internal sealed partial class SettingsWindow : Window
{
    // 150ms matches the spec; longer than keystroke bursts, shorter
    // than perceptible lag on a sub-30-item index.
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(150);

    // One row per nav item. Drives three lookups (tag -> NavigationViewItem,
    // index-page-name -> tag, iteration for sidebar counts) so that page
    // renames don't require editing three parallel switch statements.
    // IndexName is null for nav items that don't host SettingsIndex entries.
    private readonly record struct PageMapping(string Tag, string? IndexName, NavigationViewItem Item);

    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly IKeyBindingsProvider _keybindings;
    private readonly IThemeProvider _theme;
    private readonly WindowThemeManager _themeManager;
    private readonly Dictionary<string, Page> _pageCache = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly IReadOnlyList<PageMapping> _pageMappings;

    private Pages.SearchResultsPage? _resultsPage;
    private string _pendingQuery = string.Empty;
    private string? _preSearchSelectedTag;  // restored when Esc clears search

    // Set while programmatically changing NavView.SelectedItem from search
    // flows so NavView_SelectionChanged doesn't redundantly call ShowPage
    // (the caller drives navigation explicitly).
    private bool _suppressNavSelection;

    public SettingsWindow(
        IConfigService configService,
        IConfigFileEditor editor,
        IKeyBindingsProvider keybindings,
        IThemeProvider theme)
    {
        _configService = configService;
        _editor = editor;
        _keybindings = keybindings;
        _theme = theme;
        InitializeComponent();

        // Branded window title and custom title bar. Title is used by the
        // taskbar / alt-tab; AppTitleBar.Title renders the same text inside
        // the window next to the gear FontIcon. Both read from
        // AppIdentity.ProductName so a rebrand touches one constant.
        var titleText = $"{AppIdentity.ProductName} Settings";
        Title = titleText;
        AppTitleBar.Title = titleText;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Mica backdrop so the window isn't a black flash while XAML
        // measures its first layout pass.
        SystemBackdrop = new MicaBackdrop();

        // WinUI 3 Window doesn't expose Width/Height in XAML.
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        // Settings window is centered on the display the cursor is on,
        // sized to give room for the new sub-sectioned pages. The
        // DisplayArea API is the WinUI 3 equivalent of macOS's
        // NSScreen.mainScreen and handles multi-monitor correctly.
        const int width = 1100;
        const int height = 750;
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var x = work.X + (work.Width - width) / 2;
        var y = work.Y + (work.Height - height) / 2;
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        // Settings UI follows the OS theme unless window-theme is
        // explicitly "light" or "dark". Unlike the terminal chrome,
        // the config pane should feel OS-native by default; a user on
        // window-theme=ghostty with a dark palette might still prefer
        // a bright settings window if their OS is in light mode.
        _themeManager = new WindowThemeManager(
            _configService, DispatcherQueue, ThemeFallbackStyle.System);
        ApplyTheme();
        _themeManager.ThemeChanged += OnThemeChanged;

        _searchTimer = new DispatcherTimer { Interval = SearchDebounce };
        _searchTimer.Tick += OnSearchTimerTick;

        _pageMappings = new[]
        {
            new PageMapping("general", "General", NavGeneral),
            new PageMapping("appearance", "Appearance", NavAppearance),
            // Profiles page renders the registry directly and doesn't host
            // SettingsIndex entries yet; null IndexName keeps it out of search.
            new PageMapping("profiles", null, NavProfiles),
            new PageMapping("colors", "Colors", NavColors),
            new PageMapping("terminal", "Terminal", NavTerminal),
            new PageMapping("keybindings", "Keybindings", NavKeybindings),
            new PageMapping("advanced", "Advanced", NavAdvanced),
            // Raw Editor doesn't host SettingsIndex entries; diagnostics live inline.
            new PageMapping("raw", null, NavRaw),
        };

        // Ctrl+F from anywhere in the window focuses the search box.
        // Keyboard accelerator on the root element so it still fires
        // when focus is inside a Page loaded into ContentFrame.
        var ctrlF = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        ctrlF.Invoked += (_, args) => { args.Handled = true; SearchBox.Focus(FocusState.Keyboard); };
        NavView.KeyboardAccelerators.Add(ctrlF);

        // NavView hosts this accelerator, so WinUI auto-shows its shortcut
        // tooltip wherever hover lands inside NavView's template -- which
        // is every nav item. The shortcut is already advertised by the
        // SearchBox placeholder, so hide the auto-tooltip. Matches the
        // policy on MainWindow's RootGrid accelerators.
        NavView.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        Closed += OnClosed;
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _searchTimer.Stop();

        // Unsubscribe providers from ConfigChanged to avoid leaking
        // event subscriptions back to the long-lived ConfigService.
        _themeManager.ThemeChanged -= OnThemeChanged;
        _themeManager.Dispose();
        (_keybindings as IDisposable)?.Dispose();
        (_theme as IDisposable)?.Dispose();
        _pageCache.Clear();
    }

    private void OnThemeChanged(bool _) => ApplyTheme();

    private void ApplyTheme()
    {
        // RequestedTheme on the root Grid cascades to the custom title
        // bar AND the NavView subtree, so the gear FontIcon + title text
        // track the window theme. Without this the Grid falls back to
        // Application.RequestedTheme (Dark), leaving white title-bar
        // text on a light Mica backdrop.
        RootGrid.RequestedTheme = _themeManager.ElementTheme;
        _themeManager.ApplyToWindow(this);
        ApplyCaptionButtonColors();
    }

    // With ExtendsContentIntoTitleBar=true, the system-rendered caption
    // buttons (min/max/close) default to white glyphs — invisible on a
    // light Mica backdrop when the window is focused. AppWindow.TitleBar
    // exposes per-state color slots; pick ones that follow the window
    // theme rather than the Application's (pinned-Dark) theme.
    //
    // Inactive foreground is theme-neutral mid-grey (#999) — reads on
    // both Mica tints. Hover/pressed use the foreground tone layered
    // at CaptionButtonHoverAlpha / CaptionButtonPressedAlpha so the
    // feedback tint comes from the current theme rather than a hard
    // colour.
    private const byte CaptionButtonHoverAlpha = 0x33;
    private const byte CaptionButtonPressedAlpha = 0x66;
    private static readonly Windows.UI.Color CaptionButtonInactiveFg =
        Windows.UI.Color.FromArgb(0xFF, 0x99, 0x99, 0x99);

    private void ApplyCaptionButtonColors()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var titleBar = AppWindow.GetFromWindowId(windowId).TitleBar;
        var dark = _themeManager.ElementTheme == ElementTheme.Dark;
        var fg = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;

        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = fg;
        titleBar.ButtonInactiveForegroundColor = CaptionButtonInactiveFg;
        titleBar.ButtonHoverBackgroundColor =
            Windows.UI.Color.FromArgb(CaptionButtonHoverAlpha, fg.R, fg.G, fg.B);
        titleBar.ButtonHoverForegroundColor = fg;
        titleBar.ButtonPressedBackgroundColor =
            Windows.UI.Color.FromArgb(CaptionButtonPressedAlpha, fg.R, fg.G, fg.B);
        titleBar.ButtonPressedForegroundColor = fg;
    }

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        // Suppress during search: the sidebar stays visible so users
        // can still read match counts, but a click on a menu item
        // while searching should leave the results pane alone.
        if (!string.IsNullOrEmpty(_pendingQuery)) return;

        // Skip when a search-exit flow is driving the selection itself;
        // that caller calls ShowPage explicitly so we'd otherwise navigate twice.
        if (_suppressNavSelection) return;

        if (args.SelectedItem is not NavigationViewItem item) return;
        ShowPage(item.Tag?.ToString());
    }

    private void ShowPage(string? tag)
    {
        if (tag == null) return;

        if (!_pageCache.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "general" => new Pages.GeneralPage(_configService, _editor),
                "appearance" => new Pages.AppearancePage(_configService, _editor),
                "profiles" => new Pages.ProfilesPage(
                    App.ProfileRegistry
                        ?? throw new InvalidOperationException("ProfileRegistry not initialized"),
                    _configService,
                    _editor),
                "colors" => new Pages.ColorsPage(_configService, _editor, _theme),
                "terminal" => new Pages.TerminalPage(_configService, _editor),
                "keybindings" => new Pages.KeybindingsPage(_keybindings),
                "advanced" => new Pages.AdvancedPage(_configService, _editor),
                "raw" => new Pages.RawEditorPage(_configService, _editor),
                _ => null,
            };
            if (page != null) _pageCache[tag] = page;
        }

        ContentFrame.Content = page;
    }

    // ---- Search ----

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _pendingQuery = sender.Text ?? string.Empty;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        ClearSearch();
    }

    private void OnSearchTimerTick(object? sender, object e)
    {
        _searchTimer.Stop();
        ApplyQuery(_pendingQuery);
    }

    private void ApplyQuery(string query)
    {
        var trimmed = query.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            ExitSearchMode();
            return;
        }

        var hits = SettingsSearch.Search(trimmed, SettingsIndex.All);
        UpdateSidebarCounts(hits);
        UpdateResultsPane(trimmed, hits);

        ResultCountText.Text = $"{hits.Count} result{(hits.Count == 1 ? "" : "s")}";
        ResultCountText.Visibility = Visibility.Visible;
    }

    private void ExitSearchMode()
    {
        _pendingQuery = string.Empty;
        ClearSidebarCounts();
        ResultCountText.Visibility = Visibility.Collapsed;

        // Restore the page the user was on before searching.
        if (_preSearchSelectedTag != null && FindNavItem(_preSearchSelectedTag) is { } prev)
        {
            var tag = _preSearchSelectedTag;
            _preSearchSelectedTag = null;
            _suppressNavSelection = true;
            try { NavView.SelectedItem = prev; }
            finally { _suppressNavSelection = false; }
            ShowPage(tag);
        }
        else if (NavView.SelectedItem is NavigationViewItem current)
        {
            ShowPage(current.Tag?.ToString());
        }
    }

    private void ClearSearch()
    {
        // Programmatic text change won't re-raise TextChanged with
        // reason=UserInput, so ExitSearchMode must be called directly.
        SearchBox.Text = string.Empty;
        _searchTimer.Stop();
        ExitSearchMode();
    }

    private void UpdateResultsPane(string query, IReadOnlyList<SearchHit> hits)
    {
        _resultsPage ??= new Pages.SearchResultsPage();

        // Remember where the user was so Esc can restore it.
        if (_preSearchSelectedTag == null && NavView.SelectedItem is NavigationViewItem current)
            _preSearchSelectedTag = current.Tag?.ToString();

        _resultsPage.Show(query, hits, OnResultChosen, ClearSearch);
        ContentFrame.Content = _resultsPage;
    }

    private void OnResultChosen(string configKey)
    {
        // Resolve the entry to its owning page.
        var entry = SettingsIndex.All.FirstOrDefault(x => x.Key == configKey);
        if (entry == null) return;
        var tag = PageTagFor(entry.Page);
        if (tag == null) return;

        // Leave search mode; select the target nav item; load the page.
        SearchBox.Text = string.Empty;
        _pendingQuery = string.Empty;
        _searchTimer.Stop();
        ClearSidebarCounts();
        ResultCountText.Visibility = Visibility.Collapsed;
        _preSearchSelectedTag = null;

        if (FindNavItem(tag) is { } item)
        {
            _suppressNavSelection = true;
            try { NavView.SelectedItem = item; }
            finally { _suppressNavSelection = false; }
        }
        ShowPage(tag);

        // Defer card discovery until the page has loaded and measured;
        // on first navigation the visual tree won't exist yet.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ContentFrame.Content is not FrameworkElement root) return;
            ScrollAndPulseAfterLoad(root, configKey);
        });
    }

    private static void ScrollAndPulseAfterLoad(FrameworkElement root, string configKey)
    {
        // The page is already measured if it was cached, but not if
        // this is the first time it's been navigated to. Hook Loaded
        // once and defer to DispatcherQueue to run after first layout.
        if (root.IsLoaded)
        {
            DoScrollAndPulse(root, configKey);
            return;
        }

        void Handler(object? s, RoutedEventArgs e)
        {
            root.Loaded -= Handler;
            root.DispatcherQueue.TryEnqueue(() => DoScrollAndPulse(root, configKey));
        }
        root.Loaded += Handler;
    }

    private static void DoScrollAndPulse(FrameworkElement root, string configKey)
    {
        var card = SettingsCardLocator.FindByConfigKey(root, configKey);
        if (card == null) return;
        SettingsCardLocator.ScrollIntoView(card);
        SettingsCardLocator.Pulse(card);
    }

    // ---- Sidebar match counts ----

    private void UpdateSidebarCounts(IReadOnlyList<SearchHit> hits)
    {
        var counts = hits.GroupBy(h => h.Entry.Page)
                         .ToDictionary(g => g.Key, g => g.Count());

        foreach (var m in _pageMappings)
        {
            int n = m.IndexName != null && counts.TryGetValue(m.IndexName, out var c) ? c : 0;
            // Dim pages with zero matches rather than collapsing, so the
            // sidebar layout stays stable while the user edits the query.
            m.Item.Opacity = n > 0 ? 1.0 : 0.4;
            m.Item.InfoBadge = n > 0
                ? new InfoBadge
                {
                    Value = n,
                    Style = (Style)Application.Current.Resources["AttentionValueInfoBadgeStyle"],
                }
                : null;
        }
    }

    private void ClearSidebarCounts()
    {
        foreach (var m in _pageMappings)
        {
            m.Item.Opacity = 1.0;
            m.Item.InfoBadge = null;
        }
    }

    private NavigationViewItem? FindNavItem(string tag)
    {
        foreach (var m in _pageMappings)
            if (m.Tag == tag) return m.Item;
        return null;
    }

    private string? PageTagFor(string indexPageName)
    {
        foreach (var m in _pageMappings)
            if (m.IndexName == indexPageName) return m.Tag;
        return null;
    }
}
