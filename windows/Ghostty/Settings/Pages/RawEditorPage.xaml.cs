using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ghostty.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Ghostty.Settings.Pages;

internal sealed partial class RawEditorPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly ObservableCollection<string> _diagnostics = new();
    private int _lastDiagnosticCount = -1;

    // Last content we read from disk. Used to detect whether the
    // editor buffer is pristine (matches disk) or dirty (user has
    // edits). Pristine buffers get refreshed when the file changes
    // under us; dirty buffers are left alone so we never silently
    // clobber in-flight edits. Without this, saving from Raw Editor
    // would overwrite changes that the Appearance/Gradient/etc pages
    // wrote while this page was cached but off-screen.
    private string _lastLoadedText = string.Empty;

    public RawEditorPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();

        DiagList.ItemsSource = _diagnostics;
        // AOT-safe: populate TextBlock from code-behind instead of
        // {Binding} which relies on reflection that NativeAOT trims.
        DiagList.ContainerContentChanging += OnContainerContentChanging;
        // ConfigChanged is subscribed in Loaded (not the ctor) so the
        // subscription lifetime tracks visibility across arbitrary
        // navigate-away/navigate-back cycles. SettingsWindow caches
        // page instances, and Unloaded fires every time we swap pages;
        // if we subscribed in the ctor we'd only receive live updates
        // on the first visit.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadContent();
        RefreshDiagnostics(isInitialLoad: true);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged += OnConfigChanged;

        // Re-entering the page (SettingsWindow caches pages). Pull
        // fresh content from disk if the user's buffer is pristine,
        // so they see whatever other settings pages wrote while this
        // page was off-screen. If the buffer is dirty, leave it alone.
        RefreshFromDiskIfPristine();
        RefreshDiagnostics(isInitialLoad: false);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged -= OnConfigChanged;
    }

    private static void OnContainerContentChanging(
        ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer.ContentTemplateRoot is TextBlock tb
            && args.Item is string text)
        {
            tb.Text = text;
        }
    }

    private void OnConfigChanged(IConfigService _)
    {
        RefreshDiagnostics(isInitialLoad: false);
        RefreshFromDiskIfPristine();
    }

    private void RefreshFromDiskIfPristine()
    {
        // Only refresh when the user has no pending edits. Equality
        // against _lastLoadedText (what we last saw from disk) is a
        // reliable proxy: set to current text every time we read OR
        // write, so divergence means the user typed.
        if (Editor.Text == _lastLoadedText)
            LoadContent();
    }

    private void LoadContent()
    {
        Editor.Text = _editor.ReadAll();
        // Re-read after assignment: WinUI 3's TextBox canonicalizes line
        // endings internally (to bare \r), so storing what we read from
        // disk would drift from Editor.Text and make the pristine-check
        // in RefreshFromDiskIfPristine always miss.
        _lastLoadedText = Editor.Text;
        StatusText.Text = $"Loaded from {_configService.ConfigFilePath}";
    }

    // Auto-collapse when clean, auto-expand on transition from clean to
    // errors so a freshly introduced problem can't hide behind a
    // collapsed panel. User's choice is preserved during a steady
    // state (e.g. if they collapse with errors still present,
    // subsequent refreshes with the same non-zero count leave it alone).
    private void RefreshDiagnostics(bool isInitialLoad)
    {
        var count = _configService.DiagnosticsCount;
        var items = new List<string>(count);
        for (int i = 0; i < count; i++)
            items.Add(_configService.GetDiagnostic(i));

        _diagnostics.Clear();
        foreach (var item in items)
            _diagnostics.Add(item);

        // Status glyph + label. Badge only shows when there are errors.
        var hasErrors = count > 0;
        StatusOkIcon.Visibility = hasErrors ? Visibility.Collapsed : Visibility.Visible;
        StatusErrorIcon.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = hasErrors
            ? (count == 1 ? "1 issue" : $"{count} issues")
            : "No issues";
        CountBadge.Value = count;
        CountBadge.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;

        // Show the list when there are errors; show the empty-state
        // placeholder when the user expands the panel on a clean config.
        DiagList.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;
        DiagEmptyState.Visibility = hasErrors ? Visibility.Collapsed : Visibility.Visible;

        var wasClean = _lastDiagnosticCount == 0;
        if (count == 0)
            DiagnosticsExpander.IsExpanded = false;
        else if (isInitialLoad || wasClean)
            DiagnosticsExpander.IsExpanded = true;

        _lastDiagnosticCount = count;
        RefreshWindowsOnlyInfo();
    }

    // Windows-only section: hidden when empty, stays collapsed by
    // default when present. Unlike the Diagnostics expander these keys
    // aren't errors, so we don't auto-expand on transition.
    private void RefreshWindowsOnlyInfo()
    {
        var keys = _configService.WindowsOnlyKeysUsed;
        var count = keys.Count;

        if (count == 0)
        {
            WindowsOnlyExpander.Visibility = Visibility.Collapsed;
            WindowsOnlyExpander.IsExpanded = false;
            WindowsOnlyContentSlot.Content = null;
            return;
        }

        WindowsOnlyCountBadge.Value = count;
        WindowsOnlyContentSlot.Content = BuildWindowsOnlyContent(keys);
        WindowsOnlyExpander.Visibility = Visibility.Visible;
    }

    private RichTextBlock BuildWindowsOnlyContent(IReadOnlyList<string> keys)
    {
        var rtb = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
        };
        var para = new Paragraph();
        para.Inlines.Add(new Run { Text = "You're using " });
        for (int i = 0; i < keys.Count; i++)
        {
            if (i > 0) para.Inlines.Add(new Run { Text = ", " });
            para.Inlines.Add(BuildCodePill(keys[i]));
        }
        para.Inlines.Add(new Run
        {
            Text = ". These are Windows-only settings and may be ignored "
                 + "on other operating systems if you share this config.",
        });
        rtb.Blocks.Add(para);
        return rtb;
    }

    // Inline "code pill" that mirrors GitHub's backtick rendering: subtle
    // rounded background, 1px stroke, monospace text. The Styles live in
    // XAML (see Page.Resources) so the brushes re-resolve when the OS
    // theme flips. Hover tooltip carries the registry's description so
    // the user learns what the key does without leaving Settings.
    private InlineUIContainer BuildCodePill(string key)
    {
        var border = new Border
        {
            Style = (Style)Resources["CodePillBorderStyle"],
            Child = new TextBlock
            {
                Text = key,
                Style = (Style)Resources["CodePillTextStyle"],
            },
        };
        if (WindowsOnlyKeys.ByKey.TryGetValue(key, out var entry))
            ToolTipService.SetToolTip(border, entry.Description);
        return new InlineUIContainer { Child = border };
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _configService.SuppressWatcher(true);
        _editor.WriteRaw(Editor.Text);
        // What we just wrote is now the on-disk truth. Without this,
        // the ConfigChanged callback (fired async by Reload) would
        // see Editor.Text != _lastLoadedText and skip the refresh,
        // leaving _lastLoadedText stale for the next pristine check.
        _lastLoadedText = Editor.Text;
        _configService.SuppressWatcher(false);

        var success = _configService.Reload();
        var count = _configService.DiagnosticsCount;
        StatusText.Text = success
            ? $"Saved and reloaded ({count} diagnostic{(count == 1 ? "" : "s")})"
            : "Reload failed -- check diagnostics";
    }

    private void RevertButton_Click(object sender, RoutedEventArgs e)
    {
        LoadContent();
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e)
    {
        // Pick up external edits to the config file without touching
        // the editor text. Useful when a diagnostic points at a line
        // the user fixed in another editor.
        _configService.Reload();
        LoadContent();
    }
}
