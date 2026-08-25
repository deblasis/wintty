using System;
using System.Collections.Generic;

namespace Ghostty.Core.Clipboard;

/// <summary>
/// Why a clipboard read ended the way it did. Mirrors the three answers
/// ghostty_clipboard_read_result_e distinguishes, and the distinction is
/// load-bearing: libghostty gates the mode 5522 report on it, so
/// answering Empty where Unsupported is true advertises a capability we
/// do not have.
/// </summary>
public enum ClipboardReadStatus
{
    /// <summary>Served, with whatever contents were found.</summary>
    Ok,

    /// <summary>Nothing on the clipboard, or it could not be read now.</summary>
    Empty,

    /// <summary>This runtime cannot serve that clipboard at all.</summary>
    Unsupported,
}

/// <summary>
/// The result of a Kitty clipboard read: what we can hand back, and what
/// the clipboard is offering.
/// </summary>
public sealed record ClipboardReadOutcome(
    ClipboardReadStatus Status,
    IReadOnlyList<ClipboardPayload> Contents,
    IReadOnlyList<string> Available)
{
    public static readonly ClipboardReadOutcome Empty = new(
        ClipboardReadStatus.Empty, Array.Empty<ClipboardPayload>(), Array.Empty<string>());

    public static readonly ClipboardReadOutcome Unsupported = new(
        ClipboardReadStatus.Unsupported, Array.Empty<ClipboardPayload>(), Array.Empty<string>());
}
