using System;
using Ghostty.Controls;
using Ghostty.Core.Accessibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Ghostty.Accessibility;

/// <summary>
/// UIA automation peer for the terminal surface. Exposes the screen contents and
/// current selection to screen readers through the Text pattern, mirroring the
/// macOS VoiceOver model (textArea role, cached screen contents, read_selection
/// offsets as the selected range). Read-only in this stage.
/// </summary>
internal sealed partial class TerminalAutomationPeer
    : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2, IValueProvider
{
    // Screen reads take the renderer mutex, so we serve a cached document for
    // this long between fetches. Matches the macOS surface's 500ms CachedValue.
    private const long ScreenTextCacheMs = 500;

    private readonly TerminalControl _owner;
    private readonly CachedValue<TerminalDocument> _document;
    // Viewport cells, for color attributes and for locating the cursor. Fetched
    // lazily and cached for the same 500ms window as the document, since
    // read_cells is expensive and takes the renderer mutex. The caret path is
    // the exception and refreshes both - see CaretOffset.
    private readonly CachedValue<Ghostty.Core.Tabs.CellGrid?> _cells;
    private readonly TerminalOutputAnnouncer _announcer = new();
    private readonly DispatcherTimer _announceTimer;
    // Last cursor cell seen by the tick; null means "not observed yet".
    private (int Row, int Col, bool InViewport)? _lastCaretCell;

    internal TerminalAutomationPeer(TerminalControl owner) : base(owner)
    {
        _owner = owner;
        _document = new CachedValue<TerminalDocument>(
            durationMs: ScreenTextCacheMs,
            fetch: () => new TerminalDocument(_owner.AccessibilityReadScreenText()),
            nowMs: () => Environment.TickCount64);
        _cells = new CachedValue<Ghostty.Core.Tabs.CellGrid?>(
            durationMs: ScreenTextCacheMs,
            fetch: () => _owner.AccessibilityReadViewportCells(),
            nowMs: () => Environment.TickCount64);

        // Poll on the UI thread; the body is inert unless a screen reader is
        // present and this surface is active, so non-AT users pay nothing beyond
        // a cached read. The 500ms document cache throttles announcements. The
        // timer runs only while the owner is loaded (started/stopped on its
        // Loaded/Unloaded, including across tab/split reloads); a cached peer
        // means one timer per control, not one per UIA query.
        _announceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _announceTimer.Tick += OnAnnounceTick;
        owner.Loaded += OnOwnerLoaded;
        owner.Unloaded += OnOwnerUnloaded;
        if (owner.IsLoaded) _announceTimer.Start();
    }

    private void OnOwnerLoaded(object sender, RoutedEventArgs e) => _announceTimer.Start();

    private void OnOwnerUnloaded(object sender, RoutedEventArgs e) => _announceTimer.Stop();

    private void OnAnnounceTick(object? sender, object e)
    {
        // Inert unless a screen reader is attached and this surface is active.
        if (!ScreenReaderDetector.IsRunning() || !_owner.IsActive)
        {
            _announcer.Reseed(Document.Text);
            // Drop the remembered caret too, so returning to this surface
            // re-announces where the caret is rather than staying silent
            // because it happens to match where it was on the way out.
            _lastCaretCell = null;
            return;
        }

        var text = _announcer.Observe(Document.Text);
        if (!string.IsNullOrEmpty(text))
        {
            RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                AutomationNotificationProcessing.All,
                text,
                "terminal-output");

            // Notification carries the words; TextChanged tells a client that
            // tracks the document to re-read rather than rely on the
            // announcement alone.
            //
            // LiveRegionChanged is deliberately NOT raised, even though
            // GetLiveSettingCore reports Polite. NVDA answers it by
            // re-announcing the element's name, so every burst of output ended
            // with a spoken "Terminal" on top of the text we just sent
            // (measured: it disappears the moment the event is dropped, and the
            // caret behaviour is unchanged either way). The property is still
            // honest about what this control is; the event only added noise.
            RaiseIfListening(AutomationEvents.TextPatternOnTextChanged);
        }

        RaiseCaretMovedIfChanged();
    }

    // Caret moves have no dedicated UIA event; the Text pattern's
    // TextSelectionChanged is what tells a screen reader to re-read the caret,
    // so a move and a selection change raise the same event.
    private void RaiseCaretMovedIfChanged()
    {
        if (ViewportCells is not { } grid) return;

        // Compare the cursor CELL before mapping it. Resolving an offset walks
        // the grid and the document, and the document is the whole screen
        // including scrollback; doing that every 300ms to discover the caret
        // has not moved is the expensive way to learn nothing.
        var cell = (grid.CursorRow, grid.CursorCol, grid.CursorInViewport);
        if (_lastCaretCell == cell) return;

        var first = _lastCaretCell is null;
        _lastCaretCell = cell;

        // The first observation establishes a baseline; there is no move yet.
        if (!first) RaiseSelectionChangedEvent();
    }

    /// <summary>
    /// Raise the UIA TextSelectionChanged event so assistive tech re-queries
    /// <see cref="GetSelection"/>. Mirrors the macOS surface posting
    /// NSAccessibility .selectedTextChanged. Guarded by ListenerExists so it
    /// is inert when no AT client is attached.
    /// </summary>
    internal void RaiseSelectionChangedEvent()
        => RaiseIfListening(AutomationEvents.TextPatternOnTextSelectionChanged);

    // Raising an event with no client attached still crosses the UIA boundary,
    // so every raise is gated. On a non-AT machine this reduces to one check.
    private void RaiseIfListening(AutomationEvents which)
    {
        if (AutomationPeer.ListenerExists(which)) RaiseAutomationEvent(which);
    }

    /// <summary>Current cached screen document. Refreshed at most every 500ms.</summary>
    internal TerminalDocument Document => _document.Get();

    /// <summary>Current cached viewport cells, or null. Refreshed at most every
    /// 500ms; fetched only when a color attribute is queried.</summary>
    internal Ghostty.Core.Tabs.CellGrid? ViewportCells => _cells.Get();

    /// <summary>Bridge this peer to its UIA provider, for range GetEnclosingElement.</summary>
    internal IRawElementProviderSimple Provider => ProviderFromPeer(this);

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Text;

    protected override string GetClassNameCore() => nameof(TerminalControl);

    protected override string GetNameCore() => "Terminal";

    /// <summary>
    /// Stable identity for this pane. Without one, every terminal in a split
    /// or a tab set is indistinguishable to an automation client, which can
    /// then only address them positionally.
    /// </summary>
    protected override string GetAutomationIdCore() => _owner.AccessibilityAutomationId;

    /// <summary>
    /// The terminal is a live region: content arrives on its own rather than
    /// in response to the user. Polite so a screen reader finishes what it is
    /// saying first - Assertive would interrupt the user mid-sentence on every
    /// line of build output.
    /// </summary>
    protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Text or PatternInterface.Text2 or PatternInterface.Value
            ? this
            : base.GetPatternCore(patternInterface);

    // ---- ITextProvider ----------------------------------------------------

    public ITextRangeProvider DocumentRange =>
        new TerminalTextRangeProvider(this, new TextSpan(0, Document.Length));

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    /// <summary>
    /// The selection, or the caret as a degenerate range when nothing is
    /// selected. Never empty: <see cref="SupportedTextSelection"/> is
    /// <c>Single</c>, which promises a range here, and screen readers resolve
    /// the caret through this method rather than through
    /// <see cref="GetCaretRange"/>. Returning an empty array makes NVDA fail to
    /// construct a text position at all ("UIAutomationTextRangeArray is
    /// empty"), which silences every caret-move announcement.
    /// </summary>
    public ITextRangeProvider[] GetSelection()
    {
        var span = _owner.AccessibilitySelectionOffsets() is { } o
            ? SelectionRange.FromOffsets(o.OffsetStart, o.OffsetLen, Document.Length)
            : Degenerate(CaretOffset());
        return new ITextRangeProvider[] { new TerminalTextRangeProvider(this, span) };
    }

    // The whole screen is treated as visible (macOS parity).
    public ITextRangeProvider[] GetVisibleRanges() =>
        new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    /// <summary>
    /// The character under a screen point, as a degenerate range. This is what
    /// backs screen-reader mouse and touch exploration, so an unmappable point
    /// collapses to the start of the document rather than inventing a
    /// plausible-looking offset somewhere in the middle.
    /// </summary>
    public ITextRangeProvider RangeFromPoint(global::Windows.Foundation.Point screenLocation)
    {
        var offset = 0;
        if (ViewportCells is { } grid && _owner.AccessibilityViewportGeometry() is { } geom)
        {
            var hit = ViewportHitTest.OffsetFromPoint(
                Document, grid, geom, screenLocation.X, screenLocation.Y);
            if (hit >= 0) offset = hit;
        }
        return new TerminalTextRangeProvider(this, Degenerate(offset));
    }

    // ---- ITextProvider2 ---------------------------------------------------

    /// <summary>
    /// The caret as a degenerate range. <paramref name="isActive"/> means "the
    /// element holding this caret has keyboard focus": reporting a constant
    /// true would tell a screen reader that a background pane's caret is the
    /// live one.
    /// </summary>
    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        isActive = _owner.IsActive;
        return new TerminalTextRangeProvider(this, Degenerate(CaretOffset()));
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        DocumentRange;

    // ---- IValueProvider ---------------------------------------------------

    /// <summary>
    /// The screen contents as one string. Redundant with the Text pattern, but
    /// some clients read Value first and never reach TextPattern; it is the
    /// same document, so the two cannot disagree.
    /// </summary>
    public string Value => Document.Text;

    public bool IsReadOnly => true;

    public void SetValue(string value)
        => throw new InvalidOperationException("The terminal grid is not editable through UIA.");

    // ---- geometry ---------------------------------------------------------

    /// <summary>
    /// Screen rectangles for a range, one per visual line, or empty when the
    /// text is not on screen. Used for braille routing and for the visual
    /// highlight a screen reader draws around what it is reading.
    ///
    /// Reads the CACHED grid, unlike the caret. A caret query follows the
    /// keystroke that moved it, so a stale answer is wrong by a whole
    /// character; a rectangle query follows the pointer or a braille cursor
    /// over text that is already on screen, where 500ms of staleness is not
    /// observable and the fresh reads would land on every mouse move.
    /// </summary>
    internal double[] BoundingRectangles(TextSpan span)
    {
        if (ViewportCells is not { } grid) return Array.Empty<double>();
        if (_owner.AccessibilityViewportGeometry() is not { } geom) return Array.Empty<double>();
        return ViewportHitTest.Rects(Document, grid, geom, span);
    }

    // ---- helpers ----------------------------------------------------------

    /// <summary>
    /// Document offset of the terminal cursor. Falls back to end-of-document
    /// when there are no cells to anchor against, which is also where an
    /// off-screen cursor maps to.
    ///
    /// Deliberately uncached. A screen reader asks for the caret immediately
    /// after the keystroke that moved it, so any time-boxed window can hand
    /// back a value captured before that keystroke and make the caret trail
    /// input by one key; shortening the window narrows the race without
    /// closing it. Both caches are dropped rather than bypassed so the range
    /// this offset is handed to reads the same snapshot the offset came from.
    /// Only assistive tech reaches this path, and it costs a few reads per
    /// second there (measured against NVDA: ~4/s while navigating, peaking at
    /// 28 in the burst that follows a keypress).
    /// </summary>
    private int CaretOffset()
    {
        _document.Invalidate();
        _cells.Invalidate();
        return ViewportCells is { } grid ? ViewportCaret.Offset(Document, grid) : Document.Length;
    }

    private static TextSpan Degenerate(int offset) => new(offset, offset);
}
