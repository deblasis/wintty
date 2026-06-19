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
internal sealed partial class TerminalAutomationPeer : FrameworkElementAutomationPeer, ITextProvider
{
    // Screen reads take the renderer mutex, so we serve a cached document for
    // this long between fetches. Matches the macOS surface's 500ms CachedValue.
    private const long ScreenTextCacheMs = 500;

    private readonly TerminalControl _owner;
    private readonly CachedValue<TerminalDocument> _document;
    private readonly TerminalOutputAnnouncer _announcer = new();
    private DispatcherTimer? _announceTimer;

    internal TerminalAutomationPeer(TerminalControl owner) : base(owner)
    {
        _owner = owner;
        _document = new CachedValue<TerminalDocument>(
            durationMs: ScreenTextCacheMs,
            fetch: () => new TerminalDocument(_owner.AccessibilityReadScreenText()),
            nowMs: () => Environment.TickCount64);

        // Poll on the UI thread; the body is inert unless a screen reader is
        // present and this surface is active, so non-AT users pay nothing beyond
        // a cached read. The 500ms document cache throttles announcements.
        _announceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _announceTimer.Tick += OnAnnounceTick;
        _announceTimer.Start();
        owner.Unloaded += OnOwnerUnloaded;
    }

    private void OnOwnerUnloaded(object sender, RoutedEventArgs e)
    {
        if (_announceTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnAnnounceTick;
            _announceTimer = null;
        }
        _owner.Unloaded -= OnOwnerUnloaded;
    }

    private void OnAnnounceTick(object? sender, object e)
    {
        // Inert unless a screen reader is attached and this surface is active.
        if (!ScreenReaderDetector.IsRunning() || !_owner.IsActive)
        {
            _announcer.Reseed(Document.Text);
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
    }

    /// <summary>Current cached screen document. Refreshed at most every 500ms.</summary>
    internal TerminalDocument Document => _document.Get();

    /// <summary>Bridge this peer to its UIA provider, for range GetEnclosingElement.</summary>
    internal IRawElementProviderSimple Provider => ProviderFromPeer(this);

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Text;

    protected override string GetClassNameCore() => nameof(TerminalControl);

    protected override string GetNameCore() => "Terminal";

    protected override object GetPatternCore(PatternInterface patternInterface)
        => patternInterface == PatternInterface.Text ? this : base.GetPatternCore(patternInterface);

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
}
