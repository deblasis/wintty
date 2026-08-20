using System;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// What the command palette tells a screen reader: how the mode label is
/// named, and which result counts are worth speaking, and when.
///
/// Pure and deterministic so the wording, the repeat suppression and the
/// hold-until-focus rule can be exercised without a live UIA tree; the
/// control only publishes whatever comes back out of here.
/// </summary>
public sealed class CommandPaletteAnnouncer
{
    private string? _lastSpoken;
    private bool _awaitingFocus;

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
    /// The palette has opened but focus has not reached it yet.
    ///
    /// Everything the view model raises while opening lands before the
    /// host moves focus into the search box, and a reader flushes what it
    /// is holding when focus moves. Speaking the count there means
    /// speaking it into the gap, and worse, banking it: the identical
    /// count arriving after focus would then be suppressed as a repeat.
    /// So the count is held, unrecorded, until <see cref="Focused"/>.
    /// </summary>
    public void Opening()
    {
        _lastSpoken = null;
        _awaitingFocus = true;
    }

    /// <summary>
    /// Focus has landed in the palette. Returns the result count to speak,
    /// or null when it has already been spoken.
    /// </summary>
    public string? Focused(string? statusText)
    {
        _awaitingFocus = false;
        return Speak(statusText);
    }

    /// <summary>
    /// A new result count. Returns what to speak, or null when there is
    /// nothing new to say.
    ///
    /// Repeats are dropped because the palette recomputes its status on
    /// every keystroke and most keystrokes leave the count alone; hearing
    /// "12 commands" again after each letter buries the row title that is
    /// spoken alongside it.
    /// </summary>
    public string? StatusChanged(string? statusText) =>
        _awaitingFocus ? null : Speak(statusText);

    private string? Speak(string? statusText)
    {
        if (string.IsNullOrWhiteSpace(statusText)) return null;
        if (string.Equals(statusText, _lastSpoken, StringComparison.Ordinal)) return null;
        _lastSpoken = statusText;
        return statusText;
    }
}
