using System;
using System.Collections.Generic;
using Ghostty.Core.Config;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace Ghostty.Settings;

internal sealed partial class SettingsWindow : Window
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly IKeyBindingsProvider _keybindings;
    private readonly IThemeProvider _theme;
    private readonly Dictionary<string, Page> _pageCache = new();

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

        // Mica backdrop so the window isn't a black flash while XAML
        // measures its first layout pass.
        SystemBackdrop = new MicaBackdrop();

        // WinUI 3 Window doesn't expose Width/Height in XAML.
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(900, 650));

        Closed += OnClosed;
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Unsubscribe providers from ConfigChanged to avoid leaking
        // event subscriptions back to the long-lived ConfigService.
        (_keybindings as IDisposable)?.Dispose();
        (_theme as IDisposable)?.Dispose();
        _pageCache.Clear();
    }

    private void NavView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        var tag = item.Tag?.ToString();
        if (tag == null) return;

        if (!_pageCache.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "general" => new Pages.GeneralPage(_configService, _editor),
                "appearance" => new Pages.AppearancePage(_configService, _editor),
                "colors" => new Pages.ColorsPage(_configService, _editor, _theme),
                "terminal" => new Pages.TerminalPage(_configService, _editor),
                "keybindings" => new Pages.KeybindingsPage(_keybindings),
                "advanced" => new Pages.AdvancedPage(_configService, _editor),
                "raw" => new Pages.RawEditorPage(_configService, _editor),
                "diagnostics" => new Pages.DiagnosticsPage(_configService),
                _ => null,
            };
            if (page != null) _pageCache[tag] = page;
        }

        ContentFrame.Content = page;
    }
}
