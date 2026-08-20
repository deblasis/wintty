using System;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// What the command palette tells a screen reader: how the mode label is
/// named, and which result-count changes are worth speaking.
///
/// Pure and deterministic so the wording and the repeat suppression can be
/// exercised without a live UIA tree; the control only forwards whatever
/// comes back out of here to the announcer.
/// </summary>
public sealed class CommandPaletteAnnouncer
{
    private string? _lastStatus;

    /// <summary>
    /// Accessible name for the mode label, whose own text ("Search" /
    /// "Command") is the value the user needs.
    ///
    /// The value cannot simply be the name: a bare "Search" collides with
    /// the command titled the same on a find-by-name, which is how the
    /// mode label started absorbing clicks meant for the list row. Naming
    /// it after the field instead ("Palette mode") swaps one defect for a
    /// worse one, because an explicit name replaces the text a reader
    /// would otherwise speak, so the mode itself goes silent. Carrying
    /// both keeps the value audible and the string unambiguous.
    /// </summary>
    public static string ModeAccessibleName(string? modeLabel) =>
        string.IsNullOrWhiteSpace(modeLabel)
            ? "Palette mode"
            : modeLabel.Trim() + " mode";

    /// <summary>
    /// The result count to speak, or null when there is nothing new to
    /// say. Repeats are dropped because the palette recomputes its status
    /// on every keystroke and most keystrokes leave the count alone;
    /// hearing "12 commands" again after each letter buries the row title
    /// that is spoken alongside it.
    /// </summary>
    public string? StatusChanged(string? statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText)) return null;
        if (string.Equals(statusText, _lastStatus, StringComparison.Ordinal)) return null;
        _lastStatus = statusText;
        return statusText;
    }

    /// <summary>
    /// Forget the last spoken status, so reopening the palette on the same
    /// count says it again instead of opening in silence.
    /// </summary>
    public void Reset() => _lastStatus = null;
}
