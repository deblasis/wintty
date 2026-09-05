using System;
using System.Collections.Generic;
using System.Text.Json;
using Ghostty.Controls;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// The transient Ctrl+Tab cycle popup. <see cref="Show"/> builds one slot
/// per row of the cycle -- tab tiles (icon + title over a small pane
/// preview) and chip cells for collapsed groups -- and marks the active one.
/// MainWindow flashes it on each Ctrl+Tab press and auto-dismisses it
/// shortly after.
///
/// The rows come from <see cref="TabStripProjection.HorizontalRows"/>, the
/// same reading the horizontal strip renders: a collapsed group is ONE chip
/// row (color dot + title + member count + chevron), its members suppressed
/// except the active one, which the Edge-135 walk projects as an ordinary
/// tile. The popup never decides visibility itself.
///
/// Two things the popup used to leave unsaid, and now says:
///
/// GROUPS. <see cref="TabSwitcherField"/> lowers those rows into cells and
/// says which of them carry a field -- the strips' group grammar: a tinted
/// FIELD with a header at its start and an end bar closing it. The wash is
/// composited to an OPAQUE value against the ground the window reports
/// rather than handed to XAML as a translucent brush, because the card
/// floats over the window's backdrop and Mica would dilute a translucent
/// tint by an amount that changes with the user's wallpaper.
///
/// SELECTION. A 2px accent ring is not an answer to "which one am I on"
/// when several tiles already carry preset colours of their own, so the
/// ring now comes with the two cues a switcher is actually read by: every
/// other card dims, and the active card lifts. All three cross on one
/// clock, and a repeat press that does not change the card's shape REUSES
/// the built slots so the highlight glides between them instead of the
/// whole card being torn down and rebuilt under the eye.
/// </summary>
internal sealed partial class TabSwitcherPopup : UserControl
{
    private readonly Dictionary<TabModel, Border> _cellByTab = new();

    // Idle border per tab rather than one shared brush: a tab carrying a
    // preset color rings itself in that color when it is not the active one.
    private readonly Dictionary<TabModel, Brush> _idleBorderByTab = new();

    // Every card the current build placed, tiles and chips alike, in row
    // order. The highlight walks this rather than the tab map: a chip is not
    // the active cell either, and it still has to be told so.
    private readonly List<CardParts> _cards = new();

    /// <summary>
    /// The two moving parts of one card, and they are NOT the same element.
    ///
    /// The lift is the card's. The dim is not, and the split is the whole
    /// point: an opacity on the card composites the tab's TITLE with
    /// everything else, and 70% of a caption over a light card measured
    /// 4.01:1 against WCAG AA's floor of 4.5 -- a text-contrast regression on
    /// a surface the contrast oracle had passing before this treatment
    /// existed. So the dim is spent on the pane preview, which is most of a
    /// tile's area and carries no text, and the title keeps the contrast it
    /// was built with.
    ///
    /// A chip has no preview and is nothing but text, so it has no dim
    /// target at all. It loses nothing by that: a chip can never be the
    /// selection -- an activation lands on manager truth, never on the chip
    /// -- so there is no "which of these two" for a dim to answer, and
    /// dimming it would be the same contrast regression with none of the
    /// cue.
    /// </summary>
    private readonly record struct CardParts(Border Card, FrameworkElement? Dim);

    // The seam's reading of the current build, one entry per slot, parallel
    // to _cards. Built once here rather than re-derived on demand: a driver
    // that asked the popup to re-walk its own tree would be measuring a
    // second reading, not the one on screen.
    private readonly List<SlotReport> _slots = new();

    // Fluent theme resources the build loop and Highlight() need, resolved
    // once per Show() instead of re-hitting the resource dictionary for every
    // element of every tile. Re-resolved on each open (not cached at ctor) so a
    // theme switch between openings is honored.
    private SwitcherTheme _theme;

    // The inputs the last build was made from. A repeat press whose inputs
    // all match rebuilds nothing: see ShouldRebuild.
    private List<CellKey> _keys = new();
    private string? _builtFontFamily;
    private uint _builtGroundRgb;

    // High Contrast as the WINDOW answered it at the last build. A build
    // input like the others -- the mode changes what the card is made of,
    // not just how fast it moves -- and read by the highlight, which in High
    // Contrast has no dim and no lift to give.
    private bool _builtHighContrast;

    // The card currently carrying the selection, so a highlight move knows
    // what it is moving FROM. Null between builds.
    private Border? _activeCard;

    private readonly Storyboard _highlightMove = new();
    private readonly Storyboard _enter = new();

    // What the in-flight highlight move is carrying, so it can be landed as
    // a local value before the next stop reverts it. See LandHighlight.
    private readonly List<(CardParts Parts, bool Selected)> _highlightLanding = new();


    private readonly record struct SwitcherTheme(
        double CaptionFontSize,
        Brush CardBackground,
        Brush BorderIdle,
        Brush BorderActive);

    /// <summary>
    /// Everything one slot renders FROM. Two builds whose keys match
    /// element for element would paint the same card, so the second one
    /// does not have to happen. Spelled out rather than hashed: a key that
    /// missed a field would silently freeze a stale card on screen, and
    /// this way the omission is visible in review.
    /// </summary>
    private readonly record struct CellKey(
        TabModel? Tab,
        TabGroup? Group,
        bool IsHead,
        bool IsTail,
        string Title,
        string GroupTitle,
        TabColor TabTint,
        TabColor GroupTint,
        int Members);

    /// <summary>One slot as the test seam reports it.</summary>
    private readonly record struct SlotReport(
        string Kind,
        string Title,
        string? Group,
        bool IsHead,
        bool IsTail,
        FrameworkElement Card,
        FrameworkElement? Header,
        FrameworkElement? Preview,
        FrameworkElement? Field);

    // Cap each title so a long one can't stretch a tile; it ellipsizes past this.
    private const double TitleMaxWidth = 150;
    private const double PreviewWidth = 150;
    private const double PreviewHeight = 84;
    private const double PreviewFontSize = 9;

    private const double CellMargin = 4;
    private const double CellPadding = 6;
    private const double CellBorder = 2;

    /// <summary>
    /// The field's edge in High Contrast, where the wash is not available.
    /// Matches <see cref="TabStripMotion.JoinRingStrokePx"/>'s role for the
    /// join halo: the same trade of a fill for an outline, at a weight that
    /// reads as a deliberate edge rather than as a hairline artifact.
    /// </summary>
    private const double FieldOutlinePx = 2;

    /// <summary>
    /// How far a count is held back from the title beside it. One value,
    /// because the field header's count and the chip's count are the same
    /// rank of information and drifting apart would read as a mistake.
    /// </summary>
    private const double SecondaryInkOpacity = 0.7;

    /// <summary>
    /// The tile/chip card itself: the preview plus the card's own padding
    /// and border. Both row kinds take it, so a chip and a tile occupy the
    /// same footprint and the wrap grid's columns stay square.
    /// </summary>
    private const double CardWidth =
        PreviewWidth + (CellPadding * 2) + (CellBorder * 2);

    /// <summary>
    /// Gap between a card and its slot's edge. The slot's edge is where a
    /// field's wash stops, so this is BOTH the spacing between cards and
    /// the wash's bleed around one -- which is what lets two field cells
    /// sit flush and read as one continuous ground. Splitting it into a
    /// slot margin plus a field pad would put a hole between every pair of
    /// members: the wrap grid lays out uniform cells, so a slot that gave
    /// its outer margin back could not be made to touch its neighbour.
    /// </summary>
    private const double CellInset = CellMargin + TabSwitcherShape.FieldPadPx;

    /// <summary>
    /// Outer width of one slot, and therefore the unit the column count is
    /// computed in. It has to track the values the build loop applies.
    /// </summary>
    private const double CellOuterWidth = CardWidth + (CellInset * 2);

    /// <summary>
    /// Share of the window the tile grid may take before it starts
    /// scrolling, leaving the card's chrome and a margin visible around it.
    /// </summary>
    private const double MaxGridHeightRatio = 0.7;
    private const double MaxGridWidthRatio = 0.9;

    public TabSwitcherPopup() => InitializeComponent();

    /// <summary>
    /// Render the cycle's rows for <paramref name="manager"/> and mark
    /// <paramref name="active"/>.
    ///
    /// <paramref name="motionOn"/> is the strips' motion gate, asked by the
    /// window at the press rather than cached, and
    /// <paramref name="fresh"/> is false for a repeat press onto a popup
    /// that is already up -- the case whose whole point is that nothing
    /// re-enters.
    ///
    /// The ground the group wash is composited against is NOT passed in: it
    /// is this card's own fill (see <see cref="CardGroundRgb"/>). The window
    /// knows what is behind its chrome, and that is the wrong surface --
    /// the popup floats on its own acrylic card, whose polarity follows the
    /// app's theme while the window's backdrop estimate follows the OS. A
    /// dark-themed app on a light desktop washed the field against a light
    /// ground and painted a pale band across a dark card.
    /// </summary>
    public void Show(
        TabManager manager,
        TabModel active,
        string? fontFamily,
        bool motionOn,
        bool highContrast,
        bool fresh)
    {
        // Read at call time: a cycle step that expanded a group (a chip
        // activation) is rendered expanded, in step with the strip.
        var plan = TabSwitcherField.Plan(TabStripProjection.HorizontalRows(manager));
        var keys = KeysFor(manager, plan);
        var theme = ResolveTheme();
        var groundRgb = CardGroundRgb;

        // OUTSIDE the rebuild, all three of these, and for one reason: none
        // of them is a function of the card's CONTENTS, which is all
        // ShouldRebuild compares.
        //
        // The column count and the scroll cap are functions of the WINDOW,
        // and the window can be resized between two opens of an unchanged
        // tab set. Left inside, a card built while the window was maximised
        // kept its wide column count after a restore, and the wrap grid --
        // centred in a Grid sized to the window -- hung off both edges with
        // its outer tiles clipped away, which is the exact failure the
        // grid's own XAML comment says it exists to prevent. The predecessor
        // could not have this bug because it rebuilt on every press; making
        // the rebuild conditional is what moved these out.
        //
        // Before the highlight, not after: the highlight ends by bringing
        // the selection into view, and a scroll reset behind it would take
        // the tile straight back off screen.
        SizeGrid(plan.Count);
        // Only on a fresh open. A scroll offset left over from the previous
        // open would show the new grid already scrolled past its first rows
        // -- but doing this on every press would fight the
        // StartBringIntoView that follows the selection down a scrolled
        // card.
        if (fresh) CandidateScroll.ChangeView(null, 0, null, disableAnimation: true);

        if (ShouldRebuild(keys, theme, fontFamily, groundRgb, highContrast))
        {
            _theme = theme;
            _keys = keys;
            _builtFontFamily = fontFamily;
            _builtGroundRgb = groundRgb;
            _builtHighContrast = highContrast;
            Build(manager, plan, fontFamily, groundRgb, highContrast);
            // A card that was just rebuilt has no previous selection to
            // move from, so the highlight lands as a cut and the ENTRANCE
            // is what the eye follows.
            _activeCard = null;
            Highlight(active, motionOn: false);
        }
        else
        {
            // Same card, one press later: only the selection moved.
            Highlight(active, motionOn);
        }

        // The entrance is a function of whether the popup was DOWN a moment
        // ago, which is what `fresh` carries and no key can. Left inside the
        // rebuild, a second open of an unchanged tab set arrived with no
        // entrance at all.
        if (fresh) RunEnter(motionOn);
    }

    /// <summary>
    /// Whether the plan on screen still stands. Everything a slot is
    /// painted from is compared -- the cells, the text and tints they
    /// render, the preview font, and the ground the wash is composited
    /// against -- plus the resolved theme, because a theme switch between
    /// two presses changes every brush in the card and none of the keys.
    /// </summary>
    private bool ShouldRebuild(
        List<CellKey> keys, SwitcherTheme theme, string? fontFamily, uint groundRgb,
        bool highContrast)
    {
        if (_cards.Count == 0) return true;
        if (!theme.Equals(_theme)) return true;
        if (!string.Equals(fontFamily, _builtFontFamily, StringComparison.Ordinal)) return true;
        if (groundRgb != _builtGroundRgb) return true;
        // High Contrast can be turned on between two presses, and it changes
        // every brush the field is made of.
        if (highContrast != _builtHighContrast) return true;
        if (keys.Count != _keys.Count) return true;
        for (int i = 0; i < keys.Count; i++)
            if (!keys[i].Equals(_keys[i])) return true;
        return false;
    }

    private static List<CellKey> KeysFor(TabManager manager, IReadOnlyList<SwitcherCell> plan)
    {
        var keys = new List<CellKey>(plan.Count);
        foreach (var cell in plan)
        {
            keys.Add(new CellKey(
                cell.Tab,
                cell.Group,
                cell.IsHead,
                cell.IsTail,
                cell.Tab?.EffectiveTitle ?? string.Empty,
                cell.Group?.Title ?? string.Empty,
                cell.Tab?.Color ?? TabColor.None,
                cell.Group?.Color ?? TabColor.None,
                cell.Group is { } group ? manager.MembersOf(group).Count : 0));
        }
        return keys;
    }

    private void Build(
        TabManager manager,
        IReadOnlyList<SwitcherCell> plan,
        string? fontFamily,
        uint groundRgb,
        bool highContrast)
    {
        // The old card's clocks go with the old card. A Storyboard left
        // running on elements about to be dropped from the tree is the leak
        // VerticalTabStrip.StopAllFieldMotion exists to close.
        _highlightMove.Stop();
        _highlightMove.Children.Clear();
        _highlightLanding.Clear();

        CandidateRow.Children.Clear();
        _cellByTab.Clear();
        _idleBorderByTab.Clear();
        _cards.Clear();
        _slots.Clear();

        // The header band is reserved on EVERY slot of a card that PAINTS
        // one somewhere, not just on the slots that paint into it: the
        // members of a run must sit on one baseline, and so must the
        // ungrouped tiles beside them, or a card reads as broken rather than
        // as grouped. A card that paints none spends nothing on it.
        //
        // The test is the header's own condition, not "any cell has a
        // group". A collapsed run reaches the plan as a CHIP, which is a
        // field of one cell and carries the group's anatomy on its own card
        // -- so it paints no header. Asking the looser question reserved
        // 20px of band across a card where nothing would ever paint into it,
        // which is the "16px of empty spent on a question nobody asked" the
        // band's own doc says it is skipped to avoid.
        var headerBand = false;
        foreach (var cell in plan)
            if (PaintsHeader(cell)) { headerBand = true; break; }

        var renderer = new PanePreviewRenderer(PreviewFont.Resolve(fontFamily));
        foreach (var cell in plan)
            CandidateRow.Children.Add(
                BuildSlot(manager, cell, renderer, groundRgb, headerBand, highContrast));
    }

    /// <summary>
    /// Whether <paramref name="cell"/> paints a field header: the head of a
    /// field whose cell is a TAB. A chip is a field's head too, but it
    /// already carries the group's anatomy on its card and a header above it
    /// would name the group twice in one cell.
    /// </summary>
    private static bool PaintsHeader(SwitcherCell cell)
        => cell.IsHead && cell.Tab is not null && cell.Group is not null;

    /// <summary>
    /// The popup went down. Everything the last open built is dropped here
    /// rather than at the top of the next <see cref="Build"/>: held to the
    /// next press, a dismissed card keeps every <c>TabModel</c> it drew --
    /// including tabs the user has closed since -- and every pane preview's
    /// visual tree alive for as long as the window lives.
    ///
    /// It also makes the seam's "is the popup up" refusal a belt rather than
    /// the only thing standing: with the slots cleared, a driver that got
    /// past that refusal reads an empty card instead of a convincing stale
    /// one.
    /// </summary>
    internal void Dismissed()
    {
        _highlightMove.Stop();
        _highlightMove.Children.Clear();
        _highlightLanding.Clear();
        _enter.Stop();
        _enter.Children.Clear();

        CandidateRow.Children.Clear();
        _cellByTab.Clear();
        _idleBorderByTab.Clear();
        _cards.Clear();
        _slots.Clear();
        _keys = new List<CellKey>();
        _activeCard = null;
    }

    private void SizeGrid(int cells)
    {
        // MainWindow assigns Width/Height immediately before calling Show, so
        // ActualWidth still holds the previous open's value (zero the first
        // time). Prefer the explicit size.
        var hostWidth = double.IsNaN(Width) ? ActualWidth : Width;
        var hostHeight = double.IsNaN(Height) ? ActualHeight : Height;
        CandidateRow.MaximumRowsOrColumns = ColumnsFor(cells, hostWidth);
        if (hostHeight > 0)
            CandidateScroll.MaxHeight = hostHeight * MaxGridHeightRatio;
    }

    /// <summary>
    /// One slot: the field's ground (when the cell is in one), the header
    /// band, the card, and the end bar. The slot's outer width is the same
    /// for every cell, so two field cells side by side sit flush and their
    /// washes read as one continuous ground; the rounding is applied to the
    /// field's OUTER corners alone, which is what makes a run of cells look
    /// like a single band rather than a row of tinted boxes.
    /// </summary>
    private Grid BuildSlot(
        TabManager manager,
        SwitcherCell cell,
        PanePreviewRenderer renderer,
        uint groundRgb,
        bool headerBand,
        bool highContrast)
    {
        var slot = new Grid { Width = CellOuterWidth };
        slot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        slot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // The ground everything inside this slot is scored against: the
        // field's wash where there is one, the card itself where the field
        // is an outline or the cell is ungrouped. Computed once, because the
        // header's ink, the header's dot and the end bar all have to agree
        // with the surface they are actually painted on.
        var fieldGroundRgb = groundRgb;

        Border? field = null;
        if (cell.Group is { } group)
        {
            // A wash normally, an outline in High Contrast: the same fork
            // TabJoinRing.SetHaloForm makes one gesture along, for the same
            // reason. The wash is a tint composited under whole tiles, and
            // in High Contrast those tiles carry the tab's own title -- a
            // colour the mode did not choose, laid under text.
            //
            // The outline is scored against the CARD, and lifted off it
            // rather than painted as the raw preset: a Graphite group on a
            // dark High Contrast ground is otherwise very nearly the ground.
            if (!highContrast)
                fieldGroundRgb = TabColorPalette.FieldBackgroundRgb(group.Color, groundRgb);
            field = new Border
            {
                Background = highContrast
                    ? null
                    : TabColorBrush.FromPackedRgb(fieldGroundRgb),
                BorderBrush = highContrast
                    ? TabColorBrush.FromPackedRgb(
                        TabGroupField.TerminalRgbOn(groundRgb, group.Color))
                    : null,
                BorderThickness = new Thickness(highContrast ? FieldOutlinePx : 0),
                CornerRadius = FieldCorners(cell),
            };
            Grid.SetRow(field, 0);
            Grid.SetRowSpan(field, 2);
            slot.Children.Add(field);
        }

        FrameworkElement? header = null;
        if (headerBand)
        {
            header = PaintsHeader(cell)
                ? BuildFieldHeader(manager, cell.Group!, fieldGroundRgb, highContrast)
                : new Grid();
            header.Height = TabSwitcherShape.HeaderHeightPx;
            header.Margin = new Thickness(CellInset, CellInset, CellInset, 0);
            Grid.SetRow(header, 0);
            slot.Children.Add(header);
        }

        FrameworkElement? preview = null;
        Border card;
        string kind, title;
        if (cell.Tab is { } tab)
        {
            card = BuildTabTile(tab, renderer, out preview);
            kind = "tile";
            title = tab.EffectiveTitle;
        }
        else
        {
            // A chip cell has no tab: the projection suppressed the run's
            // members, so the chip stands for all of them and the field is
            // one cell wide.
            var chipGroup = cell.Group!;
            card = BuildGroupChip(manager, chipGroup, groundRgb, highContrast);
            kind = "chip";
            title = chipGroup.Title;
        }
        card.Margin = new Thickness(
            CellInset, headerBand ? TabSwitcherShape.FieldPadPx : CellInset, CellInset, CellInset);
        Grid.SetRow(card, 1);
        slot.Children.Add(card);

        if (cell.IsTail && cell.Group is { } tailGroup)
        {
            // The end bar closes the field: a colour rather than the wash,
            // because its whole job is to be the one edge of the field the
            // eye can find.
            //
            // Through TerminalRgbOn, not the raw preset, and the ground it
            // is scored against is the FIELD it sits inside -- the bar lives
            // in the slot's inset, which is under the field Border. The raw
            // preset on the field's own wash of that preset is the failure
            // the strips already named: a Yellow bar on a Yellow field's
            // light-theme wash scored 1.28:1, and the one mark that has to
            // be findable was the one that could not be found.
            var endBar = new Rectangle
            {
                Width = TabSwitcherShape.EndBarWidthPx,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, CellMargin, 0, CellMargin),
                RadiusX = 1,
                RadiusY = 1,
                Fill = TabColorBrush.FromPackedRgb(
                    TabGroupField.TerminalRgbOn(fieldGroundRgb, tailGroup.Color)),
            };
            Grid.SetRow(endBar, 0);
            Grid.SetRowSpan(endBar, 2);
            slot.Children.Add(endBar);
        }

        // Idle from birth: Highlight is the only writer that brightens one,
        // so a card can never be left lit by a build that never ran it. In
        // High Contrast there is no dim to be idle WITH -- see SetSelected.
        var parts = new CardParts(card, highContrast ? null : preview);
        if (parts.Dim is { } dim) dim.Opacity = TabSwitcherShape.IdleTileOpacity;
        card.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        card.RenderTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        _cards.Add(parts);
        _slots.Add(new SlotReport(
            kind, title, cell.Group?.Title, cell.IsHead, cell.IsTail,
            card, header, preview, field));
        return slot;
    }

    /// <summary>
    /// The field's header: the strips' group anatomy minus the chevron, on
    /// the field's own wash. No chevron because the switcher expands
    /// nothing -- a chip cell is the expand gesture, and it carries its own.
    ///
    /// <paramref name="groundRgb"/> is the ground the header actually sits
    /// on: the field's wash normally, the card itself when High Contrast has
    /// turned the field into an outline.
    /// </summary>
    private Grid BuildFieldHeader(
        TabManager manager, TabGroup group, uint groundRgb, bool highContrast)
    {
        // In High Contrast the ink is the system's own text colour, not a
        // pole computed from the ground: the mode's whole contract is that
        // the user picked these colours and chrome uses them. Outside it,
        // the computed pole is what keeps the title readable on a wash whose
        // luminance changes with the group's hue.
        var ink = highContrast && Application.Current.Resources[
                "TextFillColorPrimaryBrush"] is Brush systemInk
            ? systemInk
            : TabColorBrush.FromPackedRgb(
                TabColorPalette.FieldForegroundRgb(group.Color, groundRgb));
        var dot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            // Lifted off the ground it sits on, which is the field's own
            // wash of this very preset: a Yellow dot painted raw on a Yellow
            // field's light-theme wash is 1.28:1, and the dot is the header's
            // only statement of WHICH group this is.
            Background = TabColorBrush.FromPackedRgb(
                TabGroupField.TerminalRgbOn(groundRgb, group.Color)),
        };
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TitleMaxWidth,
            Margin = new Thickness(0, 0, 4, 0),
            FontSize = _theme.CaptionFontSize,
            Foreground = ink,
            Text = group.Title,
        };
        var count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            // Text held back from the title by opacity -- except in High
            // Contrast, where a translucent glyph is a colour the user did
            // not choose and the count is the least readable text on the
            // card to begin with.
            Opacity = highContrast ? 1 : SecondaryInkOpacity,
            FontSize = _theme.CaptionFontSize,
            Foreground = ink,
            // The count answers the manager: the field spans the run's
            // VISIBLE cells, and a collapsed run's field spans one, so the
            // rows cannot say how many tabs the group holds.
            Text = manager.MembersOf(group).Count.ToString(),
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(dot, 0);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(count, 2);
        row.Children.Add(dot);
        row.Children.Add(title);
        row.Children.Add(count);
        return row;
    }

    /// <summary>
    /// A field's rounding, applied to its OUTER corners alone: a head
    /// rounds its leading edge, a tail its trailing one, and a cell in the
    /// middle of a run rounds nothing so the band runs straight through it.
    /// </summary>
    private static CornerRadius FieldCorners(SwitcherCell cell)
    {
        const double R = 10;
        return new CornerRadius(
            cell.IsHead ? R : 0,
            cell.IsTail ? R : 0,
            cell.IsTail ? R : 0,
            cell.IsHead ? R : 0);
    }

    /// <summary>
    /// One tab tile: icon + title over the pane preview, ringed by the
    /// active accent when it is the cycle's target.
    /// </summary>
    private Border BuildTabTile(
        TabModel tab, PanePreviewRenderer renderer, out FrameworkElement previewBody)
    {
        var icon = new TabIconPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        icon.Attach(tab.TabIcon);

        var title = new TextBlock
        {
            // The tile prints; it draws no glyph, so a home tab is spelled.
            Text = tab.WordTitle,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TitleMaxWidth,
            FontSize = _theme.CaptionFontSize,
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(2, 0, 2, 6),
        };
        header.Children.Add(icon);
        header.Children.Add(title);

        // Shared slate fill so the 1px per-pane inset reads as dividers
        // between splits (matches the overview); panes paint the terminal
        // background over it.
        var body = new Canvas
        {
            Width = PreviewWidth,
            Height = PreviewHeight,
            Background = PanePreviewRenderer.DividerFill,
        };
        renderer.BuildMiniLayout(tab.PaneHost.RootNode, body, PreviewFontSize);
        previewBody = body;

        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(header);
        content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = body });

        // A tab's preset color reaches the preview the same way it
        // reaches the strip: the tint composites over the card fill
        // rather than replacing it, so a colored tile still reads as the
        // same surface as an uncolored one.
        var colored = tab.Color != TabColor.None;
        var idleBorder = colored
            ? TabColorBrush.From(TabColorPalette.Border(tab.Color))
            : _theme.BorderIdle;
        _idleBorderByTab[tab] = idleBorder;

        var cell = new Border
        {
            Width = CardWidth,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(CellBorder),
            BorderBrush = idleBorder,
            Background = colored
                ? TabColorBrush.From(TabColorPalette.Background(tab.Color, selected: false))
                : _theme.CardBackground,
            Padding = new Thickness(CellPadding),
            Child = content,
        };
        _cellByTab[tab] = cell;
        return cell;
    }

    /// <summary>
    /// One collapsed group as ONE chip cell: the strip chip's four-part
    /// anatomy (color dot, title, member count, chevron) on a card of the
    /// tile's width, so both row kinds share the wrap grid's column math.
    /// The members render nowhere -- the projection suppressed them -- and
    /// the card is never the highlighted cell: a chip activation lands on
    /// manager truth, never on the chip.
    /// </summary>
    private Border BuildGroupChip(TabManager manager, TabGroup group, uint groundRgb, bool highContrast)
    {
        // The card under the dot is this same preset washed over the card
        // ground, so the dot is lifted OFF that composite rather than painted
        // as the raw preset: a Yellow dot on a Yellow chip is otherwise
        // 1.3:1 on the light theme, and the dot is the chip's only
        // statement of which colour the group is.
        var chipGroundRgb = TabColorPalette.EffectiveBackgroundRgb(
            group.Color, selected: false, groundRgb);
        var dot = new Border
        {
            Width = 10,
            Height = 10,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Background = TabColorBrush.FromPackedRgb(
                TabGroupField.TerminalRgbOn(chipGroundRgb, group.Color)),
        };
        var title = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TitleMaxWidth,
            Margin = new Thickness(0, 0, 4, 0),
            Text = group.Title,
        };
        var count = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            // Full strength in High Contrast: a translucent glyph is a
            // colour the user did not choose.
            Opacity = highContrast ? 1 : SecondaryInkOpacity,
            Text = manager.MembersOf(group).Count.ToString(),
        };
        var chevron = new FontIcon
        {
            // FontIcon's default FontFamily is not guaranteed to be the
            // symbol font, so pin it explicitly or the glyph can render
            // as nothing.
            FontFamily = Application.Current.Resources.TryGetValue(
                "SymbolThemeFontFamily", out var ff) && ff is FontFamily fam
                ? fam
                : null,
            Glyph = "\uE76C", // Segoe Fluent / MDL2 "ChevronRight"
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var anatomy = new StackPanel { Orientation = Orientation.Horizontal };
        anatomy.Children.Add(dot);
        anatomy.Children.Add(title);
        anatomy.Children.Add(count);
        anatomy.Children.Add(chevron);

        // Centered in the cell so a short row does not hug the top of a
        // slot the tiles size.
        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(anatomy);

        // The group always has a color, so the card always takes the tint:
        // the same translucent wash a tinted tile gets, ringed by the
        // preset's border color as its idle border.
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(CellBorder),
            BorderBrush = TabColorBrush.From(TabColorPalette.Border(group.Color)),
            Background = TabColorBrush.From(
                TabColorPalette.Background(group.Color, selected: false)),
            Padding = new Thickness(CellPadding),
            Width = CardWidth,
            Child = content,
        };
    }

    /// <summary>
    /// The surface a group field's wash is composited against: this card's
    /// own fill, read off the card rather than assumed.
    ///
    /// The card is acrylic, and an acrylic brush carries the opaque colour
    /// it tints toward; that colour is the honest stand-in for "what is
    /// behind a tile". A High Contrast or theme-overridden card resolves to
    /// a plain solid brush instead, so both shapes are read, and only when
    /// the card is painted with neither does this fall back to the Fluent
    /// base fill for the theme the card is ACTUALLY rendering in.
    ///
    /// ActualTheme, not the window's backdrop estimate and not the OS: those
    /// two answer for the window's chrome, which is a different surface. A
    /// dark-themed app on a light desktop is exactly where they diverge, and
    /// composing the wash against the wrong one paints a pale band across a
    /// dark card.
    /// </summary>
    private uint CardGroundRgb
    {
        get
        {
            switch (Card.Background)
            {
                case AcrylicBrush acrylic:
                    return Pack(acrylic.TintColor);
                case SolidColorBrush solid:
                    return Pack(solid.Color);
                default:
                    return ActualTheme == ElementTheme.Dark
                        ? DarkCardGroundRgb
                        : LightCardGroundRgb;
            }
        }
    }

    // Fluent's SolidBackgroundFillColorBase per theme: the last-resort
    // stand-in for a card whose brush says nothing about its colour.
    private const uint DarkCardGroundRgb = 0x202020;
    private const uint LightCardGroundRgb = 0xF3F3F3;

    private static uint Pack(Color color)
        => ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    // Fallbacks mirror the Fluent dark-theme token values; they only apply if a
    // key is missing or mistyped (e.g. an early-init/teardown race), which the
    // AOT-safe ThemeResources.Get swallows instead of throwing mid-open.
    private static SwitcherTheme ResolveTheme() => new(
        CaptionFontSize: ThemeResources.Get("CaptionTextBlockFontSize", 12.0),
        CardBackground: ThemeResources.Get<Brush>("CardBackgroundFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF))),
        // SubtleFillColorTransparent is fully transparent white in both themes.
        BorderIdle: ThemeResources.Get<Brush>("SubtleFillColorTransparentBrush",
            new SolidColorBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF))),
        // The selection ring; defaults to the Fluent dark accent (SystemAccentColorLight2).
        BorderActive: ThemeResources.Get<Brush>("AccentFillColorDefaultBrush",
            new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF))));

    /// <summary>
    /// Column count for <paramref name="count"/> cells: square-ish, so a
    /// handful of tabs still make a compact card, but never wider than the
    /// window can show.
    /// </summary>
    private static int ColumnsFor(int count, double hostWidth)
    {
        if (count <= 1) return 1;
        var square = (int)Math.Ceiling(Math.Sqrt(count));
        var byWidth = hostWidth > 0
            ? (int)((hostWidth * MaxGridWidthRatio) / CellOuterWidth)
            : square;
        return Math.Max(1, Math.Min(square, byWidth));
    }

    /// <summary>
    /// Put the selection on <paramref name="tab"/>: the accent ring, the
    /// lift, and every other card's dim. All three are one move, so they
    /// ride one storyboard and land together -- a ring that arrives before
    /// the dim reads as two separate changes.
    ///
    /// Motion off is a cut, and the end state is written in this same pass
    /// rather than left to a zero-length storyboard's dispatcher tick: the
    /// strips' rule, and the reason a High Contrast session never sees a
    /// half-applied highlight.
    /// </summary>
    private void Highlight(TabModel tab, bool motionOn)
    {
        _cellByTab.TryGetValue(tab, out var target);

        foreach (var (model, cell) in _cellByTab)
        {
            cell.BorderBrush = ReferenceEquals(model, tab)
                ? _theme.BorderActive
                : _idleBorderByTab.TryGetValue(model, out var idle)
                    ? idle
                    : _theme.BorderIdle;
        }

        // LAND the last move before starting the next one. A Storyboard that
        // is stopped puts every property it animated back to that property's
        // BASE value, and Animate writes no base -- its tracks carry only a
        // To. So a stop un-did the move before it: the third press of a
        // cycle re-lit the tile the FIRST press had dimmed, and the card
        // drew two tiles bright and lifted with only the ring saying which
        // one the release would take. The strips learned the same rule twice
        // (ActiveFieldFill's "stop before the write", VerticalTabStrip's
        // StopFieldMotion).
        //
        // What is load-bearing is that the landing happens before the next
        // move BEGINS, not where it sits relative to the stop: landing after
        // the stop simply writes back what the revert undid, and a run of
        // the group harness against that ordering is clean. Before, so the
        // card is never momentarily wrong.
        //
        // Not from Completed: a Stop on this Storyboard RAISES Completed, so
        // a handler would have to know which move it belonged to -- and the
        // natural end of a move needs no landing at all, because the
        // animation holds its end value until the next press lands it.
        LandHighlight();
        _highlightMove.Stop();
        _highlightMove.Children.Clear();
        var duration = TabSwitcherShape.HighlightDuration(motionOn);
        if (duration == TimeSpan.Zero)
        {
            foreach (var parts in _cards)
                SetSelected(parts, ReferenceEquals(parts.Card, target));
        }
        else
        {
            // Only the two cards that actually change are animated: the one
            // losing the selection and the one taking it. Re-animating the
            // rest to the values they already hold is work the compositor
            // does not need.
            if (PartsFor(_activeCard) is { } outgoing && !ReferenceEquals(_activeCard, target))
                Animate(outgoing, selected: false, duration);
            if (PartsFor(target) is { } incoming && !ReferenceEquals(_activeCard, target))
                Animate(incoming, selected: true, duration);
            if (_highlightMove.Children.Count > 0) _highlightMove.Begin();
        }
        _activeCard = target;

        // Once the grid scrolls, the ring alone cannot tell the user which
        // tile is active if it sits below the fold.
        target?.StartBringIntoView();
    }

    /// <summary>
    /// Write the in-flight move's destinations as local values, so the stop
    /// that follows has nothing to revert. Idempotent: the list is emptied,
    /// and a second call before the next move does nothing.
    /// </summary>
    private void LandHighlight()
    {
        foreach (var (parts, selected) in _highlightLanding) SetSelected(parts, selected);
        _highlightLanding.Clear();
    }

    /// <summary>The built parts behind a card, or null when it is not one.</summary>
    private CardParts? PartsFor(Border? card)
    {
        if (card is null) return null;
        foreach (var parts in _cards)
            if (ReferenceEquals(parts.Card, card)) return parts;
        return null;
    }

    private void SetSelected(CardParts parts, bool selected)
    {
        // The dim is on the preview, never on the card: the card's subtree
        // has the tab's title in it. A chip has no preview and takes no dim
        // at all -- see CardParts.
        if (parts.Dim is { } dim)
            dim.Opacity = selected ? 1 : TabSwitcherShape.IdleTileOpacity;
        var scale = selected && !_builtHighContrast ? TabSwitcherShape.ActiveTileScale : 1;
        if (parts.Card.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }

    private void Animate(CardParts parts, bool selected, TimeSpan duration)
    {
        // Recorded, not written: writing the destination now would make it
        // the value the To-only track animates FROM, and the move would be a
        // cut. LandHighlight writes it at the top of the next Highlight.
        _highlightLanding.Add((parts, selected));
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (parts.Dim is { } dim)
        {
            AddTrack(_highlightMove, dim, "Opacity",
                selected ? 1 : TabSwitcherShape.IdleTileOpacity, duration, easing);
        }
        // In High Contrast the lift is a cut like every other spring, and
        // SetSelected gives it 1 either way -- so there is nothing here to
        // animate and the landing entry above is the whole move.
        if (_builtHighContrast) return;
        var scale = selected ? TabSwitcherShape.ActiveTileScale : 1;
        AddTrack(_highlightMove, parts.Card, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)",
            scale, duration, easing);
        AddTrack(_highlightMove, parts.Card, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)",
            scale, duration, easing);
    }

    /// <summary>
    /// The card's entrance: a short fade and rise, so a popup that was not
    /// on screen a moment ago arrives instead of appearing. Skipped for a
    /// repeat press -- the popup is already up and the only thing that
    /// should move is the selection.
    /// </summary>
    private void RunEnter(bool motionOn)
    {
        _enter.Stop();
        _enter.Children.Clear();
        var duration = TabSwitcherShape.EnterDuration(motionOn);
        if (duration == TimeSpan.Zero)
        {
            Card.Opacity = 1;
            if (Card.RenderTransform is TranslateTransform cut) cut.Y = 0;
            return;
        }
        if (Card.RenderTransform is not TranslateTransform)
            Card.RenderTransform = new TranslateTransform();
        Card.Opacity = 0;
        ((TranslateTransform)Card.RenderTransform).Y = TabSwitcherShape.EnterRisePx;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        AddTrack(_enter, Card, "Opacity", 1, duration, easing);
        AddTrack(_enter, Card, "(UIElement.RenderTransform).(TranslateTransform.Y)",
            0, duration, easing);
        _enter.Begin();
    }

    private static void AddTrack(
        Storyboard board, DependencyObject target, string path,
        double to, TimeSpan duration, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        board.Children.Add(animation);
    }

    /// <summary>
    /// The card as the test seam reads it: one entry per slot, with the
    /// rects a pixel oracle has to point at. None of these surfaces is
    /// reachable over UIA -- the field, the header band and the preview
    /// body are bare panels and get no automation peer -- and the popup
    /// dismisses itself on a timer, so a driver must be handed the geometry
    /// rather than left to hunt for it.
    ///
    /// The active flag is read back off the SAME map the highlight writes,
    /// so a harness asserting "exactly one cell is active" is asserting
    /// about the selection the popup actually applied.
    /// </summary>
    internal void TestSeamWriteCells(Utf8JsonWriter json, Func<FrameworkElement, object?> rect)
    {
        json.WriteStartArray("cells");
        foreach (var slot in _slots)
        {
            json.WriteStartObject();
            json.WriteString("kind", slot.Kind);
            json.WriteString("title", slot.Title);
            if (slot.Group is null) json.WriteNull("group");
            else json.WriteString("group", slot.Group);
            json.WriteBoolean("head", slot.IsHead);
            json.WriteBoolean("tail", slot.IsTail);
            json.WriteBoolean("field", slot.Field is not null);
            json.WriteBoolean("active", ReferenceEquals(slot.Card, _activeCard));
            WriteRect(json, "card", rect(slot.Card));
            WriteRect(json, "header", slot.Header is null ? null : rect(slot.Header));
            WriteRect(json, "preview", slot.Preview is null ? null : rect(slot.Preview));
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteRect(Utf8JsonWriter json, string name, object? value)
    {
        if (value is not ValueTuple<int, int, int, int> r) { json.WriteNull(name); return; }
        json.WriteStartObject(name);
        json.WriteNumber("x", r.Item1);
        json.WriteNumber("y", r.Item2);
        json.WriteNumber("w", r.Item3);
        json.WriteNumber("h", r.Item4);
        json.WriteEndObject();
    }
}
