using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Ghostty.Core.Config;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace Ghostty.Settings.Pages;

internal sealed partial class RawEditorPage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly ObservableCollection<string> _diagnostics = new();
    private int _lastDiagnosticCount = -1;

    private string _lastLoadedText = string.Empty;

    // Find state
    private readonly List<int> _findMatches = new();
    private int _findCurrentIndex = -1;
    private DispatcherTimer? _findDebounce;
    private string _findTextSnapshot = string.Empty;

    // Track highlighted ranges so we only clear those (never the full document).
    private readonly List<(int start, int end)> _prevRanges = new();
    private (int start, int end) _prevCurrentRange;

    // CharacterFormat.BackgroundColor on many ranges is expensive; cap to keep
    // per-keystroke latency acceptable (~50 ms for 50 ranges on a 94 KB file).
    private const int MaxHighlightedMatches = 50;

    // Reusable colors invalidated on theme change.
    private Color _matchColor;
    private Color _currentColor;
    // "Clear" color for wiping a highlight off a range. In WinUI 3's
    // RichEditBox, ITextCharacterFormat.BackgroundColor appears to ignore
    // the alpha channel, so Color.FromArgb(0,0,0,0) — which reads as
    // "transparent" to a human — actually renders as opaque black and
    // paints a black rectangle over the character. Pick a color that
    // matches the editor's theme background so cleared ranges visually
    // disappear regardless of whether alpha is respected.
    private Color _clearBgColor;
    private bool _colorsReady;

    public RawEditorPage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();

        // Theme flips while the page is live must force a recompute of
        // the highlight / clear-bg colors cached in EnsureColors, and
        // repaint the document bg so stale dark-mode clears don't remain
        // visible as dark boxes on a light background (issue #325).
        ActualThemeChanged += OnActualThemeChanged;

        DiagList.ItemsSource = _diagnostics;
        DiagList.ContainerContentChanging += OnContainerContentChanging;
        Editor.ActualThemeChanged += (_, _) => _colorsReady = false;
        Editor.TextChanged += Editor_TextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        LoadContent();
        RefreshDiagnostics(isInitialLoad: true);
    }

    private string GetEditorText()
    {
        // RichEditBox appends a trailing \r that wasn't in the original text;
        // trim it so comparisons and disk writes stay consistent.
        Editor.Document.GetText(TextGetOptions.None, out var text);
        return text.TrimEnd('\r');
    }

    private void SetEditorText(string text)
    {
        Editor.Document.SetText(TextSetOptions.None, text);
        ClearAllBackgrounds();
    }

    // Wipe any lingering CharacterFormat.BackgroundColor across the whole
    // document. SetText keeps the document's default character format, so
    // any highlight BG left over from a prior Ctrl+F cycle carries into
    // the freshly-loaded text and paints every character (issue #325:
    // save-and-reload turns the whole editor black). Also resets
    // Selection.CharacterFormat so subsequent typing doesn't inherit a
    // stale highlight format.
    private void ClearAllBackgrounds()
    {
        EnsureColors();
        // int.MaxValue is the documented "to end of document" sentinel for
        // RichEditTextDocument.GetRange; RichEditBox uses CRLF internally,
        // so the C# input string length would under-count.
        var range = Editor.Document.GetRange(0, int.MaxValue);
        range.CharacterFormat.BackgroundColor = _clearBgColor;
        Editor.Document.Selection.CharacterFormat.BackgroundColor = _clearBgColor;
        _prevRanges.Clear();
        _prevCurrentRange = default;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        _colorsReady = false;
        ClearAllBackgrounds();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _configService.ConfigChanged += OnConfigChanged;
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
        if (GetEditorText() == _lastLoadedText)
            LoadContent();
    }

    private void LoadContent()
    {
        SetEditorText(_editor.ReadAll());
        _lastLoadedText = GetEditorText();
        StatusText.Text = $"Loaded from {_configService.ConfigFilePath}";
    }

    private void RefreshDiagnostics(bool isInitialLoad)
    {
        var count = _configService.DiagnosticsCount;
        var items = new List<string>(count);
        for (int i = 0; i < count; i++)
            items.Add(_configService.GetDiagnostic(i));

        _diagnostics.Clear();
        foreach (var item in items)
            _diagnostics.Add(item);

        var hasErrors = count > 0;
        StatusOkIcon.Visibility = hasErrors ? Visibility.Collapsed : Visibility.Visible;
        StatusErrorIcon.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;
        StatusLabel.Text = hasErrors
            ? (count == 1 ? "1 issue" : $"{count} issues")
            : "No issues";
        CountBadge.Value = count;
        CountBadge.Visibility = hasErrors ? Visibility.Visible : Visibility.Collapsed;

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
        _editor.WriteRaw(GetEditorText());
        _lastLoadedText = GetEditorText();
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
        _configService.Reload();
        LoadContent();
    }

    // --- Find bar ---

    private void EnsureColors()
    {
        if (_colorsReady) return;
        // Read the Page's ActualTheme rather than the Editor's: during the
        // first Loaded tick the inner RichEditBox may still report
        // ElementTheme.Default before the theme cascades, which previously
        // made us cache dark-mode brush values inside a light-mode window
        // (issue #325).
        var isDark = ActualTheme == ElementTheme.Dark;
        _matchColor = isDark
            ? Color.FromArgb(70, 200, 170, 50)
            : Color.FromArgb(90, 255, 230, 100);
        _currentColor = isDark
            ? Color.FromArgb(180, 255, 200, 60)
            : Color.FromArgb(220, 255, 190, 50);
        // Opaque editor-background color so a "cleared" highlight renders
        // indistinguishable from un-highlighted text. Read it from the
        // active theme dictionary via TextControlBackground (the brush
        // RichEditBox fills its surface with) so the clear colour tracks
        // the editor regardless of future Fluent/brand tweaks. Fall back
        // to ApplicationPageBackgroundThemeBrush, then to the pre-fix
        // hand-picked values as a last resort.
        _clearBgColor = TryReadSolidThemeColor("TextControlBackground", isDark, out var c)
            || TryReadSolidThemeColor("ApplicationPageBackgroundThemeBrush", isDark, out c)
            ? c
            : (isDark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 255, 255, 255));
        _colorsReady = true;
    }

    // Look up a resource by key from Application.Current.Resources'
    // theme dictionaries, picking the Light/Default variant from this
    // Page's theme rather than Application.RequestedTheme. Handles
    // either a SolidColorBrush or a raw Color value under that key —
    // TextControlBackground is a Color on WinAppSDK, not a Brush, so
    // callers can't assume either shape.
    private static bool TryReadSolidThemeColor(string key, bool isDark, out Color color)
    {
        var themeKey = isDark ? "Default" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(themeKey, out var dictObj)
            && dictObj is Microsoft.UI.Xaml.ResourceDictionary themeDict
            && themeDict.TryGetValue(key, out var v))
        {
            switch (v)
            {
                case SolidColorBrush b:
                    color = b.Color;
                    return true;
                case Color raw:
                    color = raw;
                    return true;
            }
        }
        color = default;
        return false;
    }

    private void ClearPreviousHighlights()
    {
        EnsureColors();
        foreach (var (s, e) in _prevRanges)
        {
            var r = Editor.Document.GetRange(s, e);
            r.CharacterFormat.BackgroundColor = _clearBgColor;
        }
        if (_prevCurrentRange != default)
        {
            var cr = Editor.Document.GetRange(_prevCurrentRange.start, _prevCurrentRange.end);
            cr.CharacterFormat.BackgroundColor = _clearBgColor;
        }
        _prevRanges.Clear();
        _prevCurrentRange = default;
    }

    private void CtrlF_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FindBar.Visibility == Visibility.Visible)
        {
            FindInput.SelectAll();
            FindInput.Focus(FocusState.Keyboard);
        }
        else
        {
            ShowFindBar();
        }
        args.Handled = true;
    }

    private void F3_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FindBar.Visibility != Visibility.Visible)
            ShowFindBar();
        if (_findMatches.Count > 0)
            FindNextMatch();
        args.Handled = true;
    }

    private void ShiftF3_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FindBar.Visibility != Visibility.Visible)
            ShowFindBar();
        if (_findMatches.Count > 0)
            FindPrevMatch();
        args.Handled = true;
    }

    private void ShowFindBar()
    {
        FindBar.Visibility = Visibility.Visible;
        var sel = Editor.Document.Selection;
        if (!string.IsNullOrEmpty(sel.Text))
            FindInput.Text = sel.Text;
        else
            FindInput.Text = string.Empty;
        FindInput.SelectAll();
        FindInput.Focus(FocusState.Keyboard);
        UpdateFindMatches();
    }

    private void HideFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        ClearPreviousHighlights();
        _findMatches.Clear();
        _findCurrentIndex = -1;
        FindMatchCount.Text = string.Empty;
        Editor.Focus(FocusState.Programmatic);
    }

    private void FindInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateFindMatches();
    }

    private void FindInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideFindBar();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                VirtualKey.Shift);
            if (shift.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
                FindPrevMatch();
            else
                FindNextMatch();
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNextMatch();
    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindPrevMatch();
    private void FindClose_Click(object sender, RoutedEventArgs e) => HideFindBar();

    private void FindCaseSensitive_Click(object sender, RoutedEventArgs e)
    {
        UpdateFindMatches();
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape
            && FindBar.Visibility == Visibility.Visible)
        {
            HideFindBar();
            e.Handled = true;
        }
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        // CharacterFormat changes fire TextChanged too; skip if the actual
        // text content hasn't changed since the last find update.
        var current = GetEditorText();
        if (current == _findTextSnapshot) return;
        // Debounce: restart the timer on each change, fire after 300ms of silence.
        _findDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _findDebounce.Stop();
        _findDebounce.Tick -= OnFindDebounce;
        _findDebounce.Tick += OnFindDebounce;
        _findDebounce.Start();
    }

    private void OnFindDebounce(object? sender, object e)
    {
        _findDebounce!.Stop();
        _findDebounce.Tick -= OnFindDebounce;
        UpdateFindMatches();
    }

    // --- Find logic ---

    private void UpdateFindMatches()
    {
        var query = FindInput.Text;
        _findMatches.Clear();
        _findCurrentIndex = -1;
        _findTextSnapshot = GetEditorText();

        if (string.IsNullOrEmpty(query))
        {
            FindMatchCount.Text = string.Empty;
            ClearPreviousHighlights();
            return;
        }

        var text = GetEditorText();
        var comparison = FindCaseSensitive.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        int pos = 0;
        while (pos <= text.Length - query.Length)
        {
            var idx = text.IndexOf(query, pos, comparison);
            if (idx < 0) break;
            _findMatches.Add(idx);
            pos = idx + query.Length;
        }

        if (_findMatches.Count == 0)
        {
            FindMatchCount.Text = "No results";
            ClearPreviousHighlights();
            return;
        }

        _findCurrentIndex = 0;
        ApplyHighlights();
        // Only scroll to the match when the find input has focus (user is
        // searching). Skip when focus is in the editor (user is editing)
        // to avoid jarring auto-scroll.
        if (FindInput.FocusState != FocusState.Unfocused)
            NavigateToMatch(_findCurrentIndex);
        UpdateMatchCountDisplay();
    }

    private void FindNextMatch()
    {
        if (_findMatches.Count == 0) return;
        _findCurrentIndex = (_findCurrentIndex + 1) % _findMatches.Count;
        ApplyHighlights();
        NavigateToMatch(_findCurrentIndex);
        UpdateMatchCountDisplay();
    }

    private void FindPrevMatch()
    {
        if (_findMatches.Count == 0) return;
        _findCurrentIndex = (_findCurrentIndex - 1 + _findMatches.Count) % _findMatches.Count;
        ApplyHighlights();
        NavigateToMatch(_findCurrentIndex);
        UpdateMatchCountDisplay();
    }

    private void NavigateToMatch(int index)
    {
        var start = _findMatches[index];
        var end = start + FindInput.Text.Length;
        var range = Editor.Document.GetRange(start, end);
        range.ScrollIntoView(PointOptions.Start);
        Editor.Document.Selection.SetRange(start, end);
    }

    private void ApplyHighlights()
    {
        EnsureColors();
        ClearPreviousHighlights();

        var query = FindInput.Text;
        var queryLen = query.Length;
        var limit = Math.Min(_findMatches.Count, MaxHighlightedMatches);
        var currentStart = _findMatches[_findCurrentIndex];

        for (int i = 0; i < limit; i++)
        {
            var s = _findMatches[i];
            if (s == currentStart) continue;
            var range = Editor.Document.GetRange(s, s + queryLen);
            range.CharacterFormat.BackgroundColor = _matchColor;
            _prevRanges.Add((s, s + queryLen));
        }

        // Current match gets brighter highlight.
        var curRange = Editor.Document.GetRange(currentStart, currentStart + queryLen);
        curRange.CharacterFormat.BackgroundColor = _currentColor;
        _prevCurrentRange = (currentStart, currentStart + queryLen);
    }

    private void UpdateMatchCountDisplay()
    {
        FindMatchCount.Text = $"{_findCurrentIndex + 1} of {_findMatches.Count}";
    }
}
