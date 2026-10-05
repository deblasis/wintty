using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Branding;
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
using Windows.Win32;
using Windows.Win32.Foundation;
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

        Ghostty.Branding.WindowHelper.TryApplySettingsIcon(this);

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
        // The Raw Editor's unsaved-text prompt intercepts this close; see
        // OnClosing.
        appWindow.Closing += OnClosing;
        // Settings window is centered on the display the request came from,
        // sized to give room for the new sub-sectioned pages. Three numbers
        // make that more than a comment: it opens on the CALLER's monitor
        // (WorkAreaForCaller), AppWindow sizes are in PHYSICAL pixels so the
        // design size below is scaled by this window's own DPI (the shader
        // gallery picker's rule), and the result is clamped to the work area.
        // Without the monitor the window opens on the primary whatever the
        // user was working on; without the scale the 1100x750 opens at
        // 733x500 on a 150% monitor; without the clamp a window taller than
        // the work area centers to a negative y and starts off-screen.
        const int designWidth = 1100;
        const int designHeight = 750;
        var dpi = PInvoke.GetDpiForWindow(new HWND(hwnd));
        var placement = DpiScaledWindowPlacement.Compute(
            designWidth, designHeight, dpi,
            WindowHelper.WorkAreaForCaller(windowId));
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            placement.X, placement.Y, placement.Width, placement.Height));

        // Settings UI follows the OS theme unless window-theme is
        // explicitly "light" or "dark". Unlike the terminal chrome,
        // the config pane should feel OS-native by default; a user on
        // window-theme=wintty with a dark palette might still prefer
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

    /// <summary>
    /// The Raw Editor is the one settings page whose edits live in the editor
    /// buffer rather than in the config file, so closing this window used to
    /// discard a hand-written config with no word -- and quitting the app
    /// discards it the same way, because this window is what the last window's
    /// teardown closes.
    /// </summary>
    /// <remarks>
    /// <c>AppWindow.Closing</c> rather than <c>Window.Closed</c>, because
    /// Closed is the point after which the page is gone: the prompt has to
    /// cancel the close and get an answer first. This is the same intercept the
    /// quake terminal uses to hide instead of closing.
    /// <para>
    /// Re-entrancy is the shape to be careful about: the dialog is modal and
    /// asynchronous, so a second close landing while it is up has to be turned
    /// away rather than answered twice. The answer re-arms the close with
    /// <see cref="_closeConfirmed"/> set, which is what lets the second pass
    /// through instead of prompting again.
    /// </para>
    /// </remarks>
    private void OnClosing(object? sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed) return;

        var raw = UnsavedRawEditorPage();
        if (raw is null || !raw.HasUnsavedChanges) return;

        args.Cancel = true;
        _ = PromptUnsavedRawEditorAsync(raw);
    }

    private Pages.RawEditorPage? UnsavedRawEditorPage()
        => _pageCache.TryGetValue("raw", out var page) ? page as Pages.RawEditorPage : null;

    /// <summary>
    /// Ask about the Raw Editor's unsaved text, act on the answer, and let the
    /// close proceed only if the answer says so.
    /// </summary>
    /// <remarks>
    /// The decision is <see cref="UnsavedRawEditPrompt"/>'s, in Core, so the
    /// rule that matters -- a Save that did not land must not close the window
    /// -- is testable without a XamlRoot and cannot drift between here and
    /// there. This method only supplies the three pieces it needs: the answer,
    /// whether the write landed, and the writes themselves.
    /// </remarks>
    private async Task PromptUnsavedRawEditorAsync(Pages.RawEditorPage raw)
    {
        // One dialog at a time: WinUI allows only one ContentDialog, and a
        // second ShowAsync on top of this one throws out of the async state
        // machine. A close landing while the prompt is up is turned away
        // instead, so nothing is lost by declining it.
        if (_rawEditorPromptOpen) return;
        // A ContentDialog needs a live XamlRoot; the Window has none of its
        // own, so it comes off the content root (null until the tree loads,
        // which is also too early for a close to reach here).
        if (RootGrid.XamlRoot is not { } xamlRoot) return;
        _rawEditorPromptOpen = true;
        UnsavedRawEditAnswer answer;
        bool saved = false;
        try
        {
            answer = await AskAboutUnsavedRawEditorAsync(xamlRoot);
            if (answer == UnsavedRawEditAnswer.Save)
                saved = raw.SaveNow();
            else if (answer == UnsavedRawEditAnswer.Discard)
                raw.DiscardUnsavedChanges();
        }
        finally
        {
            _rawEditorPromptOpen = false;
        }

        // A failed save lands here with saved=false: the editor still holds the
        // text and the file still does not, which is the only state the user
        // can retry from. Closing would be the same loss the prompt exists to
        // prevent, one step later.
        if (!UnsavedRawEditPrompt.ShouldClose(answer, saved)) return;

        _closeConfirmed = true;
        Close();
    }

    private async Task<UnsavedRawEditAnswer> AskAboutUnsavedRawEditorAsync(
        Microsoft.UI.Xaml.XamlRoot xamlRoot)
    {
        var dialog = new ContentDialog
        {
            Title = "Save your changes?",
            Content = "The Raw Editor has changes that are not in your config file. "
                + "Closing Settings will discard them.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            // Cancel, not Save: Enter should not be the answer that loses text.
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };

        // A dialog closed by Enter or Escape hands focus back to whatever was
        // under it, and that key's trailing character would reach it. Same
        // reason every other ContentDialog in the app goes through Watch.
        Ghostty.Input.ConsumedCloseKey.Watch(dialog);

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedRawEditAnswer.Save,
            ContentDialogResult.Secondary => UnsavedRawEditAnswer.Discard,
            _ => UnsavedRawEditAnswer.Cancel,
        };
    }

    // Set once the Raw Editor question has been answered, so the second close
    // that follows the answer passes the intercept above instead of asking
    // again about text that is now saved or gone.
    private bool _closeConfirmed;

    // Whether the Raw Editor prompt is up. See PromptUnsavedRawEditorAsync.
    private bool _rawEditorPromptOpen;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Stop is not enough on its own: the timer runs against the
        // DispatcherQueue, which outlives this window, and a tick queued in
        // the same turn as the stop still arrives afterwards and re-enters
        // ApplyQuery against a torn-down tree. Detaching is what turns that
        // one away, since the raise reads the invocation list.
        _searchTimer.Stop();
        _searchTimer.Tick -= OnSearchTimerTick;

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
                // The keybindings page reads the libghostty config handle via
                // the enumerate ABI, which only the concrete ConfigService
                // exposes. The injected instance is always a ConfigService.
                "keybindings" => new Pages.KeybindingsPage((ConfigService)_configService, _editor),
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

        // AttentionValueInfoBadgeStyle is a WinUI 3 framework theme resource;
        // a raw cast throws if the key isn't present in the merged dictionaries
        // for the current theme. Mirror TabColorPalettePicker.GetBrushResource
        // and fall back to the InfoBadge default style by leaving Style unset.
        Style? badgeStyle = null;
        if (Application.Current.Resources.TryGetValue("AttentionValueInfoBadgeStyle", out var styleObj)
            && styleObj is Style s)
        {
            badgeStyle = s;
        }

        foreach (var m in _pageMappings)
        {
            int n = m.IndexName != null && counts.TryGetValue(m.IndexName, out var c) ? c : 0;
            // Dim pages with zero matches rather than collapsing, so the
            // sidebar layout stays stable while the user edits the query.
            m.Item.Opacity = n > 0 ? 1.0 : 0.4;
            if (n > 0)
            {
                var badge = new InfoBadge { Value = n };
                if (badgeStyle != null)
                {
                    badge.Style = badgeStyle;
                }
                m.Item.InfoBadge = badge;
            }
            else
            {
                m.Item.InfoBadge = null;
            }
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
