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
internal sealed partial class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2
{
    // Screen reads take the renderer mutex, so we serve a cached document for
    // this long between fetches. Matches the macOS surface's 500ms CachedValue.
    private const long ScreenTextCacheMs = 500;

    private readonly TerminalControl _owner;
    private readonly CachedValue<TerminalDocument> _document;
    // Viewport cells for color attributes. Fetched lazily (only when a color
    // attribute is queried) and cached for the same 500ms window as the
    // document, since read_cells is expensive and takes the renderer mutex.
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
    {
        if (AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
            RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
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

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface is PatternInterface.Text or PatternInterface.Text2
            ? this
            : base.GetPatternCore(patternInterface);

    // ---- ITextProvider ----------------------------------------------------

    public ITextRangeProvider DocumentRange =>
        new TerminalTextRangeProvider(this, new TextSpan(0, Document.Length));

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    public ITextRangeProvider[] GetSelection()
    {
        var offsets = _owner.AccessibilitySelectionOffsets();
        if (offsets is not { } o) return Array.Empty<ITextRangeProvider>();
        var span = SelectionRange.FromOffsets(o.OffsetStart, o.OffsetLen, Document.Length);
        return new ITextRangeProvider[] { new TerminalTextRangeProvider(this, span) };
    }

    // The whole screen is treated as visible (macOS parity).
    public ITextRangeProvider[] GetVisibleRanges() =>
        new ITextRangeProvider[] { DocumentRange };

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => DocumentRange;

    public ITextRangeProvider RangeFromPoint(global::Windows.Foundation.Point screenLocation) =>
        new TerminalTextRangeProvider(this, new TextSpan(0, 0));

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

        // Falls back to end-of-document when there are no cells to anchor
        // against, which is also where an off-screen cursor maps to.
        var cells = ViewportCells;
        var offset = cells is { } grid
            ? ViewportCaret.Offset(Document, grid)
            : Document.Length;

        return new TerminalTextRangeProvider(this, new TextSpan(offset, offset));
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        DocumentRange;
}
