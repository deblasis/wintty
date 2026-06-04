using System.ComponentModel;
using System.Globalization;

namespace Ghostty.Core.ResizeOverlay;

/// <summary>
/// Observable state for the resize overlay: the transient pill that shows
/// the terminal grid dimensions (e.g. "80 x 24") while a pane is being
/// resized. Pure logic with no WinUI references; the ResizeOverlayControl
/// owns an instance and binds its TextBlock to <see cref="SizeText"/>,
/// while the per-pane controller calls <see cref="ShouldPulse"/> from the
/// surface size-changed handler.
///
/// The decision of whether a given size change should make the pill appear
/// lives here so it can be unit-tested. Time-based guards (the startup
/// settle grace and the focus-bounce window) stay in the view because they
/// depend on wall-clock instants the view already tracks.
/// </summary>
public sealed class ResizeOverlayState : INotifyPropertyChanged
{
    private int _columns;
    private int _rows;
    private bool _hasBaseline;

    /// <summary>
    /// Which <see cref="ResizeOverlayMode"/> governs visibility. Set by
    /// the view from the current config before each <see cref="ShouldPulse"/>
    /// call so hot-reloaded config is honored without a subscription.
    /// </summary>
    public ResizeOverlayMode Mode { get; set; } = ResizeOverlayMode.AfterFirst;

    /// <summary>Most recently observed grid column count.</summary>
    public int Columns
    {
        get => _columns;
        private set
        {
            if (_columns == value) return;
            _columns = value;
            Notify(nameof(Columns));
            Notify(nameof(SizeText));
        }
    }

    /// <summary>Most recently observed grid row count.</summary>
    public int Rows
    {
        get => _rows;
        private set
        {
            if (_rows == value) return;
            _rows = value;
            Notify(nameof(Rows));
            Notify(nameof(SizeText));
        }
    }

    // U+00D7 MULTIPLICATION SIGN, the separator macOS Ghostty uses between
    // columns and rows. Built from its code point so this source stays ASCII.
    private const char Separator = (char)0x00D7;

    /// <summary>
    /// Display string for the pill, e.g. "80 x 24" (using U+00D7
    /// MULTIPLICATION SIGN as the separator, matching macOS Ghostty).
    /// </summary>
    public string SizeText => string.Format(
        CultureInfo.InvariantCulture,
        "{0} {1} {2}",
        _columns,
        Separator,
        _rows);

    private bool _isVisible;

    /// <summary>
    /// Whether the pill should currently be shown. Driven by
    /// <see cref="NotifyResize"/> (set true when a resize qualifies) and
    /// <see cref="Hide"/> (the auto-hide timer elapsing). The view binds the
    /// pill's visibility to this property instead of toggling it imperatively,
    /// which keeps the show/hide decision here where it can be unit-tested and
    /// is immune to view lifecycle quirks (reparenting on split / rebuild).
    /// </summary>
    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            Notify(nameof(IsVisible));
        }
    }

    /// <summary>
    /// Record a new grid size and decide whether the pill should appear
    /// for this change. Always updates <see cref="Columns"/> /
    /// <see cref="Rows"/> (so the text tracks the latest value), then
    /// returns:
    /// <list type="bullet">
    /// <item><c>false</c> if the grid did not actually change.</item>
    /// <item><c>false</c> when <see cref="Mode"/> is
    /// <see cref="ResizeOverlayMode.Never"/>.</item>
    /// <item><c>false</c> for the first observed size when
    /// <see cref="Mode"/> is <see cref="ResizeOverlayMode.AfterFirst"/>.</item>
    /// <item><c>true</c> otherwise.</item>
    /// </list>
    /// </summary>
    public bool ShouldPulse(ushort cols, ushort rows)
    {
        var unchanged = _hasBaseline && cols == _columns && rows == _rows;
        var isFirst = !_hasBaseline;

        Columns = cols;
        Rows = rows;
        _hasBaseline = true;

        if (unchanged) return false;

        // No catch-all arm: each mode is handled explicitly so a future
        // ResizeOverlayMode addition surfaces here rather than silently
        // falling through to a default.
        if (Mode == ResizeOverlayMode.Never) return false;
        if (Mode == ResizeOverlayMode.Always) return true;
        return !isFirst; // AfterFirst
    }

    /// <summary>
    /// Record a resize. Always updates the tracked grid size (so
    /// <see cref="SizeText"/> stays current), then shows the pill when the
    /// change should pulse (per <see cref="Mode"/> and the first-layout / dedup
    /// rules in <see cref="ShouldPulse"/>) and the view allows it. Returns true
    /// when the pill was (re)shown so the caller can (re)start its auto-hide
    /// timer.
    ///
    /// A resize that does not qualify (suppressed by <paramref name="allowShow"/>,
    /// mode, or an unchanged grid) never hides an already-visible pill -- only
    /// <see cref="Hide"/> does that. This matters because a drag delivers many
    /// size events, some duplicate, and the pill must stay up until the resize
    /// settles.
    /// </summary>
    public bool NotifyResize(ushort cols, ushort rows, bool allowShow)
    {
        var pulse = ShouldPulse(cols, rows);
        if (!allowShow || !pulse) return false;
        IsVisible = true;
        return true;
    }

    /// <summary>Hide the pill. Called when the auto-hide interval elapses.</summary>
    public void Hide() => IsVisible = false;

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add => _pc += value;
        remove => _pc -= value;
    }

    private PropertyChangedEventHandler? _pc;

    private void Notify(string name) =>
        _pc?.Invoke(this, new PropertyChangedEventArgs(name));
}
