using System;
using Ghostty.Branding;
using Ghostty.Core;
using Ghostty.Core.Config;
using Ghostty.Core.Version;
using Ghostty.Core.Windows;
using Ghostty.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;

namespace Ghostty.Dialogs;

internal sealed partial class AboutWindow : Window
{
    private readonly IConfigService _configService;
    private readonly WindowThemeManager _themeManager;

    public AboutWindow(IConfigService configService)
    {
        _configService = configService;
        InitializeComponent();

        WindowHelper.TryApplyAppIcon(this);

        var titleText = $"About {AppIdentity.ProductName}";
        Title = titleText;
        AppTitleBar.Title = titleText;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SystemBackdrop = new MicaBackdrop();

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);

        // Small panel centered on the cursor's display, like SettingsWindow
        // but sized to the content rather than the 1100x750 settings shell.
        const int width = 420;
        const int height = 560;
        var display = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var x = work.X + (work.Width - width) / 2;
        var y = work.Y + (work.Height - height) / 2;
        appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));

        // Follow OS theme unless window-theme forces light/dark, matching
        // the Settings window's System fallback.
        _themeManager = new WindowThemeManager(
            _configService, DispatcherQueue, ThemeFallbackStyle.System);
        ApplyTheme();
        _themeManager.ThemeChanged += OnThemeChanged;

        PopulateContent();

        Closed += OnClosed;
    }

    private void PopulateContent()
    {
        AppIcon.Source = new BitmapImage(AppIconSource.Current);
        ProductNameText.Text = AppIdentity.ProductName;
        TaglineText.Text = AboutContent.Tagline;
        LicenseText.Text = AboutContent.LicenseNote;
        CopyrightText.Text = AboutContent.Copyright;

        var info = VersionRenderer.Build();
        VersionValue.Text = info.WinttyVersion;

        // The Version row already carries the semantic version, so the Build
        // row surfaces the distribution identity instead: edition, plus the
        // libghostty channel (tip/stable) when reported. Mirrors the split in
        // `wintty +version`.
        var edition = EditionLabel.Format(info.Edition);
        BuildValue.Text = string.IsNullOrEmpty(info.LibGhostty.Channel)
            ? edition
            : $"{edition} ({info.LibGhostty.Channel})";

        // Free-form build identifier. Empty by default, in which case the row
        // collapses (both cells hidden) just like the Commit row below.
        if (string.IsNullOrEmpty(info.BuildLabel))
        {
            LabelLabel.Visibility = Visibility.Collapsed;
            LabelValue.Visibility = Visibility.Collapsed;
        }
        else
        {
            LabelValue.Text = info.BuildLabel;
        }

        // CommitUrl is null when the commit is unknown. Guard the parse too:
        // the commit string is build-derived, so a malformed value collapses
        // the row rather than throwing and taking down the whole window.
        var commitUrl = VersionRenderer.CommitUrl(info);
        if (commitUrl is not null && Uri.TryCreate(commitUrl, UriKind.Absolute, out var commitUri))
        {
            CommitText.Text = info.WinttyCommit;
            CommitLink.NavigateUri = commitUri;
        }
        else
        {
            CommitLabel.Visibility = Visibility.Collapsed;
            CommitValue.Visibility = Visibility.Collapsed;
        }

        GitHubButton.NavigateUri = new Uri(AboutContent.GitHubUrl);
        DocsButton.NavigateUri = new Uri(AboutContent.DocsUrl);
        HomepageButton.NavigateUri = new Uri(AboutContent.HomepageUrl);
        SponsorButton.NavigateUri = new Uri(AboutContent.SponsorUrl);
    }

    private void OnThemeChanged(bool _) => ApplyTheme();

    private void ApplyTheme()
    {
        RootGrid.RequestedTheme = _themeManager.ElementTheme;
        _themeManager.ApplyToWindow(this);
        ApplyCaptionButtonColors();
    }

    // Mirror SettingsWindow: system caption buttons default to white glyphs,
    // invisible on a light Mica backdrop; pick colors that follow the window
    // theme.
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

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _themeManager.ThemeChanged -= OnThemeChanged;
        _themeManager.Dispose();
    }
}
