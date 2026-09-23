using System;
using System.ComponentModel;
using Ghostty.Core;
using Ghostty.Core.Tabs;
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
///   3. Keeping <c>Window.Title</c> on the active tab's label. The
///      label itself is not fed from here: every tab follows its own
///      pane host's <c>TitleChanged</c>, wired in <c>TabManager</c>, so
///      a background tab keeps following its shell and no host can
///      name a tab it does not belong to.
///   4. Keeping the vertical-mode title TextBlock in sync with the
///      active tab's <c>EffectiveTitle</c>.
///
/// MainWindow stays the composition root and forwards
/// <c>AppWindow.Changed</c>; the tab events come from TabManager.
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
    // Masks the pane top stroke under the OS caption buttons. Must track
    // the same RightInset the caption column does; a fixed width leaves a
    // stroke fragment showing or overhangs into the drag region whenever
    // the OS caption metrics are not the XAML default.
    private readonly Func<bool> _isVerticalMode;

    private TabModel? _boundTab;

    /// <summary>
    /// Fired when a caption-inset change has reflowed the strip's content.
    /// The inset writes the drag region's MinWidth, which moves the tab
    /// slots without any SizeChanged (the TabView's own size did not
    /// change) - so whoever places strip-relative chrome from slot offsets
    /// (the seam cover) needs this nudge or places from stale offsets.
    /// </summary>
    public event Action? CaptionInsetChanged;

    // Quake window: borderless, no OS caption buttons, so the reserved
    // caption inset (vertical column + horizontal drag-region MinWidth) is
    // dead space and the vertical-mode window title is noise. When set,
    // SyncCaptionInset keeps the inset collapsed instead of re-expanding.
    private bool _captionless;

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
        // WordTitle, not EffectiveTitle: the window title is words only, so
        // the home glyph the strips draw reads "Home" here. TabManager
        // raises this on a tab switch and whenever the active tab's label
        // moves, so a background tab's title changes never reach it.
        _tabs.WindowTitleChanged += (_, _) => _window.Title = _tabs.ActiveTab.WordTitle;

        RebindVerticalTitle();
        _window.Title = _tabs.ActiveTab.WordTitle;
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
        if (_captionless)
        {
            _captionInset.Width = new GridLength(0);
            return;
        }

        try
        {
            var inset = _window.AppWindow.TitleBar.RightInset;
            var scale = (_window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
            var dip = scale > 0 ? inset / scale : inset;
            if (dip > 0)
            {
                // The seam cover lives in this column and stretches to
                // it, so setting the column is setting both. It used to
                // carry its own copy of this width, which is one more
                // thing that could disagree with the caption lane it is
                // supposed to be part of. Fired only when the inset MOVED:
                // AppWindow.Changed arrives per position change, so an
                // unconditional fire ran the seam refresh on every
                // mouse-move of a window drag.
                if (_captionInset.Width.GridUnitType == GridUnitType.Pixel
                    && Math.Abs(_captionInset.Width.Value - dip) < 0.01) return;
                _captionInset.Width = new GridLength(dip);
                if (_horizontalTabHost.DragRegion is FrameworkElement drag)
                    drag.MinWidth = dip;
                // The horizontal sponsor overlay host's own cell sits at
                // the footer's right edge, which is the window's, so it
                // needs this number as a margin or the pill draws under
                // the caption buttons. The vertical row's presenter sits
                // in its own cell ahead of the inset column, which clears
                // it for free.
                _horizontalTabHost.SyncSponsorOverlayInset(dip);
                CaptionInsetChanged?.Invoke();
            }
        }
        catch
        {
            // RightInset can throw early during construction; leave
            // the XAML default in place.
        }
    }

    /// <summary>
    /// Quake-only: collapse the caption-button inset (the OS buttons are
    /// gone in borderless mode) and hide the vertical-mode title text for
    /// a minimal look. Idempotent.
    /// </summary>
    public void SetCaptionless(bool value)
    {
        _captionless = value;
        if (!value) return;
        _captionInset.Width = new GridLength(0);
        if (_horizontalTabHost.DragRegion is FrameworkElement dragRegion)
            dragRegion.MinWidth = 0;
        // No buttons to clear on a borderless window, so the overlay host
        // keeps only its gap. Left to the XAML default it would hold 146 DIP
        // of empty lane open on the one layout that has nothing there.
        _horizontalTabHost.SyncSponsorOverlayInset(0);
        _verticalTitleText.Visibility = Visibility.Collapsed;
    }

    private void RebindVerticalTitle()
    {
        _boundTab?.PropertyChanged -= OnBoundTabPropertyChanged;
        _boundTab = _tabs.ActiveTab;
        _boundTab?.PropertyChanged += OnBoundTabPropertyChanged;
        UpdateVerticalTitleText();
    }

    // The label itself, not the inputs that feed it. The two extra names
    // were redundant (each raises EffectiveTitle with it) and were the
    // beginnings of the hand-maintained input list that left the window
    // caption a tier behind the strip; see TabManager.OnTabPropertyChanged,
    // which routes the same way so the two layouts cannot disagree.
    private void OnBoundTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabModel.EffectiveTitle))
        {
            UpdateVerticalTitleText();
        }
    }

    // The coalesce covers NO TAB BOUND, not a tab with nothing to say:
    // WordTitle is never null or empty, and its own floor is the generic
    // word rather than the product, because a tab is named after what runs
    // in it. With no tab at all this strip is naming the window, and the
    // product name is still the right answer to that.
    private void UpdateVerticalTitleText()
        => _verticalTitleText.Text = _boundTab?.WordTitle ?? AppIdentity.ProductName;
}
