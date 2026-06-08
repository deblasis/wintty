using System;

namespace Ghostty.Core.Panes;

/// <summary>
/// How pane undo/redo behaves, resolved from the libghostty
/// <c>undo-timeout</c> config value. Pure (no config handle, no WinUI) so
/// the resolution is unit-testable in Core; the native read lives in the
/// WinUI <c>ConfigService</c> and the behavioral gating lives in
/// <c>PaneHost</c>.
///
/// Mirrors upstream Ghostty's <c>undo-timeout</c> knob, which is macOS-only
/// upstream (Linux has no undo; see <c>src/config/Config.zig</c>) and which
/// this fork reuses for its own Windows pane undo/redo. Upstream documents
/// <c>undo-timeout = 0</c> as "effectively disable undo operations", so a zero
/// config value resolves to <see cref="Disabled"/>: <c>PaneHost</c> then
/// captures nothing (bounded memory) and hard-closes panes with no soft-close
/// retention (closed shells don't linger running) — the two rationales
/// upstream gives for the timeout existing.
///
/// One intentional divergence from upstream's third rationale ("disabling
/// frees the keybinds for terminal apps"): the Windows Ctrl+Shift+Z/Y chords
/// are still consumed (the residual key matcher swallows them) rather than
/// forwarded to the terminal when undo is disabled — they just become no-ops.
/// Those chords are effectively never used by terminal apps, so the no-op is
/// preferred over threading per-tab undo state into the static key matcher.
/// </summary>
/// <param name="Enabled">Whether undo/redo is active at all.</param>
/// <param name="Window">Per-operation eviction window handed to
/// <see cref="PaneHistory"/>. Meaningless (and <see cref="TimeSpan.Zero"/>)
/// when <paramref name="Enabled"/> is false.</param>
public readonly record struct UndoPolicy(bool Enabled, TimeSpan Window)
{
    /// <summary>
    /// Upstream Ghostty's default: undo enabled with a 5s per-operation
    /// eviction window (<c>undo-timeout</c> default, see
    /// <c>src/config/Config.zig</c>). Used when the config value can't be
    /// read.
    /// </summary>
    public static readonly UndoPolicy Default = new(Enabled: true, Window: TimeSpan.FromSeconds(5));

    /// <summary>
    /// Undo fully disabled — upstream's <c>undo-timeout = 0</c>. No capture,
    /// no soft-close retention, no eviction timer.
    /// </summary>
    public static readonly UndoPolicy Disabled = new(Enabled: false, Window: TimeSpan.Zero);

    /// <summary>
    /// Resolve a raw <c>undo-timeout</c> reading (milliseconds) into a policy:
    /// <list type="bullet">
    /// <item><description>positive → enabled with that eviction window;</description></item>
    /// <item><description>zero → <see cref="Disabled"/> (faithful to upstream's
    /// "0 disables undo");</description></item>
    /// <item><description>negative → <see cref="Default"/>. A valid libghostty
    /// <c>Duration</c> is unsigned and the reader clamps to non-negative, so a
    /// negative only arises from a corrupt/defensive path — falling back to the
    /// safe 5s default beats silently disabling undo over a glitch.</description></item>
    /// </list>
    /// </summary>
    public static UndoPolicy FromConfigMilliseconds(int milliseconds) =>
        milliseconds > 0 ? new UndoPolicy(Enabled: true, Window: TimeSpan.FromMilliseconds(milliseconds))
        : milliseconds == 0 ? Disabled
        : Default;
}
