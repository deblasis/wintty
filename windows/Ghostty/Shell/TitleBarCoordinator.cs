using System;
using System.ComponentModel;
using Ghostty.Controls;
using Ghostty.Core.Panes;
using Ghostty.Core.Tabs;
using Ghostty.Panes;
using Ghostty.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Shell;

/// <summary>
/// Owns every cross-cutting title-bar concern that used to live
/// inside MainWindow:
///
///   1. Choosing which element <c>SetTitleBar</c> binds to
///      depending on whether the active layout is horizontal
///      (TabHost's footer) or vertical (the MainWindow-owned drag
///      region next to the sidebar).
///   2. Re-syncing the right-hand caption inset against
///      <c>AppWindow.TitleBar.RightInset</c> so the OS min/max/
///      close buttons get DPI- and theme-aware spacing instead of
///      the hard-coded 146 DIP from the original PR.
///   3. Mirroring the active leaf's TitleChanged stream into
///      <c>TabModel.ShellReportedTitle</c> and the
///      <c>Window.Title</c>.
///   4. Keeping the vertical-mode title TextBlock in sync with the
///      active tab's <c>EffectiveTitle</c>.
///
/// MainWindow stays the composition root and forwards
/// <c>AppWindow.Changed</c> + the active tab/leaf change events.
/// </summary>
internal sealed class TitleBarCoordinator
{
    private readonly Window _window;
    private readonly TabManager _tabs;
    private readonly TabHost _horizontalTabHost;
    private readonly VerticalTabHost _verticalTabHost;
    private readonly FrameworkElement _verticalDragRegion;
    private readonly TextBlock _verticalTitleText;
    private readonly ColumnDefinition _captionInset;
    private readonly Func<bool> _isVerticalMode;

    private TabModel? _boundTab;
    private LeafPane? _activeLeaf;

    public TitleBarCoordinator(
        Window window,
        TabManager tabs,
        TabHost horizontalTabHost,
        VerticalTabHost verticalTabHost,
        FrameworkElement verticalDragRegion,
        TextBlock verticalTitleText,
        ColumnDefinition captionInset,
        Func<bool> isVerticalMode)
    {
        _window = window;
        _tabs = tabs;
        _horizontalTabHost = horizontalTabHost;
        _verticalTabHost = verticalTabHost;
        _verticalDragRegion = verticalDragRegion;
        _verticalTitleText = verticalTitleText;
        _captionInset = captionInset;
        _isVerticalMode = isVerticalMode;

        // Tab/title plumbing.
        _tabs.ActiveTabChanged += (_, _) => RebindVerticalTitle();
        _tabs.ActiveTabChanged += (_, _) => HookActiveTabTitle();
        _tabs.WindowTitleChanged += (_, _) => _window.Title = _tabs.ActiveTab.EffectiveTitle;

        RebindVerticalTitle();
        HookActiveTabTitle();
    }

    /// <summary>
    /// Update <c>SetTitleBar</c> for the layout the user just
    /// switched to. Horizontal mode hands off to TabHost's footer;
    /// vertical mode hands off to the MainWindow-owned drag region.
    /// </summary>
    public void ApplyForCurrentMode()
    {
        if (_isVerticalMode())
            _window.SetTitleBar(_verticalDragRegion);
        else
            _window.SetTitleBar(_horizontalTabHost.DragRegion as FrameworkElement);
    }

    /// <summary>
    /// Re-read <c>AppWindow.TitleBar.RightInset</c> and apply it to
    /// the vertical title bar's caption-inset column. Called from
    /// MainWindow's <c>AppWindow.Changed</c> handler so the inset
    /// follows DPI / theme transitions instead of being frozen at
    /// the XAML default.
    /// </summary>
    public void SyncCaptionInset()
    {
        try
        {
            var inset = _window.AppWindow.TitleBar.RightInset;
            var scale = (_window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var dip = scale > 0 ? inset / scale : inset;
            if (dip > 0)
                _captionInset.Width = new GridLength(dip);
        }
        catch
        {
            // RightInset can throw early during construction; leave
            // the XAML default in place.
        }
    }

    private void RebindVerticalTitle()
    {
        _boundTab?.PropertyChanged -= OnBoundTabPropertyChanged;
        _boundTab = _tabs.ActiveTab;
        _boundTab?.PropertyChanged += OnBoundTabPropertyChanged;
        UpdateVerticalTitleText();
    }

    private void OnBoundTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabModel.EffectiveTitle) ||
            e.PropertyName == nameof(TabModel.ShellReportedTitle) ||
            e.PropertyName == nameof(TabModel.UserOverrideTitle))
        {
            UpdateVerticalTitleText();
        }
    }

    private void UpdateVerticalTitleText()
        => _verticalTitleText.Text = _boundTab?.EffectiveTitle ?? "Ghostty";

    /// <summary>
    /// Subscribe the active tab's active leaf to live title-change
    /// updates and write the title into
    /// <see cref="TabModel.ShellReportedTitle"/>. Re-runs every time
    /// the active tab changes or the active leaf within the active
    /// tab changes. Lives here (not inside TabManager) because it
    /// touches WinUI types that Ghostty.Core cannot reach.
    /// </summary>
    private void HookActiveTabTitle()
    {
        if (_activeLeaf is { } previous)
            previous.Terminal().TitleChanged -= OnLiveTitleChanged;

        var tab = _tabs.ActiveTab;
        var leaf = tab.PaneHost.ActiveLeaf;
        _activeLeaf = leaf;
        leaf.Terminal().TitleChanged += OnLiveTitleChanged;
        tab.ShellReportedTitle = leaf.Terminal().CurrentTitle;
        _window.Title = tab.EffectiveTitle;

        tab.PaneHost.LeafFocused -= OnActiveTabLeafFocused;
        tab.PaneHost.LeafFocused += OnActiveTabLeafFocused;
    }

    private void OnActiveTabLeafFocused(object? sender, LeafPane leaf)
    {
        if (_activeLeaf is { } previous)
            previous.Terminal().TitleChanged -= OnLiveTitleChanged;
        _activeLeaf = leaf;
        leaf.Terminal().TitleChanged += OnLiveTitleChanged;
        _tabs.ActiveTab.ShellReportedTitle = leaf.Terminal().CurrentTitle;
    }

    private void OnLiveTitleChanged(object? sender, string title)
    {
        // TabManager raises WindowTitleChanged in response, which
        // sets Window.Title via the constructor's subscription.
        _tabs.ActiveTab.ShellReportedTitle = title;
    }
}
