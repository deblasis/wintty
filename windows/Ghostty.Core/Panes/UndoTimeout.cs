using System;

namespace Ghostty.Core.Panes;

/// <summary>
/// Resolves the effective undo/redo eviction window for a
/// <c>PaneHost</c> from the libghostty <c>undo-timeout</c> config value.
/// Pure (no config handle, no WinUI) so the fallback/guard decision is
/// unit-testable in Core; the actual native read lives in the WinUI
/// <c>ConfigService</c>.
/// </summary>
public static class UndoTimeout
{
    /// <summary>
    /// Compile-time default matching upstream Ghostty's <c>undo-timeout</c>
    /// default value (5s, see <c>src/config/Config.zig</c>). Used when the
    /// config value can't be read, or when it resolves to a non-positive
    /// (degenerate) duration — see <see cref="FromMilliseconds"/>.
    /// </summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Convert a raw <c>undo-timeout</c> reading (milliseconds) into the
    /// eviction window passed to <see cref="PaneHistory"/>.
    ///
    /// DIVERGENCE FROM UPSTREAM: Ghostty documents <c>undo-timeout = 0</c>
    /// as "effectively disable undo operations" (and the knob is macOS-only
    /// there). This Windows fork reuses the key for its own pane undo/redo
    /// and intentionally treats a non-positive value as "unset", falling
    /// back to <see cref="Default"/> instead of disabling. Rationale: a zero
    /// eviction window would not cleanly disable undo here anyway — the
    /// prune timer only ticks once a second, so synchronous undo would still
    /// work briefly — so until pane undo grows a dedicated disable switch,
    /// the predictable 5s default beats a half-disabled state. Read-failures
    /// already arrive here as the caller's default (also 5s), so both
    /// degenerate paths converge.
    /// </summary>
    public static TimeSpan FromMilliseconds(int milliseconds)
        => milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : Default;
}
