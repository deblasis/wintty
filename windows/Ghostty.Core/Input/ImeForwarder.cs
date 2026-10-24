using System;
using System.Text;

namespace Ghostty.Core.Input;

/// <summary>
/// The IME commit state machine, independent of WinUI and of the native
/// surface. The caller (TerminalControl) owns the focused ImeSink TextBox and
/// supplies its text before each event; this type owns the composition
/// lifecycle, the forwarded watermark and the ASCII provenance credit, and
/// raises the text that is final.
///
/// Invariants:
///   * [0, Forwarded) of the sink has already been handed to the surface.
///   * Forwarded never exceeds the sink length; a shrinking sink rewinds it.
///   * Composing is true exactly while one or more compositions are live.
///   * A live preedit is never forwarded by a key; only composition end, an
///     explicit commit, or non-composition text commits.
///   * An ASCII character already delivered by CharacterReceived is credited
///     and skipped once, so it is typed exactly once.
/// </summary>
public sealed class ImeForwarder
{
    private int _compositions;
    private int _asciiCredited;

    /// <summary>The text the IME sink currently holds. The caller keeps it in sync.</summary>
    public string SinkText { get; private set; } = string.Empty;

    /// <summary>True while at least one composition is live.</summary>
    public bool Composing => _compositions > 0;

    /// <summary>How much of <see cref="SinkText"/> has been forwarded.</summary>
    public int Forwarded { get; private set; }

    /// <summary>The display-only tail of the sink, or null when there is none.</summary>
    public string? Preedit { get; private set; }

    /// <summary>Committed text, in order, ready for SurfaceText / the preview sink.</summary>
    public event Action<string>? ForwardedText;

    /// <summary>The preedit to display, or null to clear it.</summary>
    public event Action<string?>? PreeditChanged;

    /// <summary>Supplies the sink text the caller read before an event.</summary>
    public void SetSinkText(string text) => SinkText = text ?? string.Empty;

    /// <summary>A composition started at <paramref name="startIndex"/>.</summary>
    public void OnCompositionStarted(int startIndex)
    {
        _compositions++;
        // Text before a new composition is final; a new Started arriving after
        // an Ended in one keystroke also forwards the old prefix here.
        ForwardRange(startIndex);
        UpdatePreedit();
    }

    /// <summary>The live composition changed; the tail is the preedit.</summary>
    public void OnCompositionChanged() => UpdatePreedit();

    /// <summary>
    /// A composition ended. The count keeps a Started for the next composition
    /// from committing that next composition when both arrive in one keystroke;
    /// only the last End closes the session.
    /// </summary>
    public void OnCompositionEnded()
    {
        if (_compositions > 0) _compositions--;
        if (_compositions > 0) return;
        CommitCore();
    }

    /// <summary>
    /// The commit decision a key press takes. Returns false while a
    /// composition is live: the preedit is the IME's, not final input.
    /// </summary>
    public bool OnKeyDown()
    {
        if (Composing) return false;
        CommitCore();
        return true;
    }

    /// <summary>An ASCII character has been delivered through CharacterReceived.</summary>
    public void NoteAsciiDelivered() => _asciiCredited++;

    /// <summary>Forward the rest of the sink and empty it.</summary>
    public void Commit() => CommitCore();

    /// <summary>Drop a live preedit without forwarding it.</summary>
    public void Cancel()
    {
        SinkText = string.Empty;
        Forwarded = 0;
        _asciiCredited = 0;
        _compositions = 0;
        Preedit = null;
        PreeditChanged?.Invoke(null);
    }

    private void CommitCore()
    {
        if (SinkText.Length > 0) ForwardRange(SinkText.Length);
        SinkText = string.Empty;
        Forwarded = 0;
        _asciiCredited = 0;
        _compositions = 0;
        Preedit = null;
        PreeditChanged?.Invoke(null);
    }

    private void ForwardRange(int end)
    {
        var text = SinkText;
        // A composition may rewrite the buffer shorter (reconversion, a mode
        // switch). A watermark past the end would drop the new tail silently.
        if (text.Length < Forwarded) Forwarded = 0;
        var from = Math.Min(Forwarded, text.Length);
        end = Math.Min(end, text.Length);
        if (end <= from) return;

        var sb = new StringBuilder(end - from);
        for (var i = from; i < end; i++)
        {
            var ch = text[i];
            // Ordinary ASCII typing already went through CharacterReceived.
            // IME/TSF-inserted ASCII has no credit and is forwarded here.
            if (ch < (char)0x80 && _asciiCredited > 0)
            {
                _asciiCredited--;
                continue;
            }
            sb.Append(ch);
        }

        Forwarded = end;
        if (sb.Length > 0) ForwardedText?.Invoke(sb.ToString());
    }

    private void UpdatePreedit()
    {
        var text = SinkText;
        if (text.Length < Forwarded) Forwarded = 0;
        var from = Math.Min(Forwarded, text.Length);
        var tail = from < text.Length ? text.Substring(from) : null;
        Preedit = tail;
        PreeditChanged?.Invoke(tail);
    }
}
