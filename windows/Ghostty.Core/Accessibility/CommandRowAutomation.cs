using System;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// The automation properties one command-palette row carries.
///
/// Absent values are null rather than empty, because the two are not the
/// same to UIA: an empty string reads as present-but-blank, and the row
/// containers are recycled, so a value that is merely blanked leaves the
/// previous row's still in place on the next item to reuse the container.
/// Null is the signal to clear the property outright.
/// </summary>
public readonly record struct CommandRowAutomation(
    string Name,
    string? HelpText,
    string? AcceleratorKey)
{
    /// <summary>
    /// Decide the three properties for a row from its item's fields.
    /// <paramref name="shortcut"/> is the already-formatted key-cap text,
    /// or null when the command has no binding.
    /// </summary>
    public static CommandRowAutomation For(string? title, string? description, string? shortcut) =>
        new(
            // Without a name the row falls back to the item's ToString(),
            // which is the whole record: id, description, category and the
            // Execute delegate, read out for every command in the list.
            title ?? string.Empty,
            Present(description),
            // The key-cap is rendered in its own column so a sighted user
            // can see the command is bound; AcceleratorKey is where a
            // reader looks for that. Folding it into the name would only
            // make every row longer.
            Present(shortcut));

    private static string? Present(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
