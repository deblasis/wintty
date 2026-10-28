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
///   * Forwarded never exceeds the sink length. A rewrite lowers it to the
///     prefix that survived unchanged. A suffix that survives an interior
///     replacement cannot be expressed on a terminal, so it is sent again: the
///     alternative is dropping input the user still sees in the box.
///   * Composing is true while a composition is live. A composition that goes
///     quiet is discarded (Composing false, text dropped) and re-tracked by its
///     next Changed, carrying the prefix already delivered.
///   * Untracked implies Composing: it is a mode of a live composition and is
///     cleared when the last one ends, and when a new one starts.
///   * A *tracked* live preedit is never forwarded by a key. Untracked text is
///     pending: a key, focus loss or opening the search bar delivers it, and
///     Escape or closing the pane discards it.
///   * An ASCII character already delivered by CharacterReceived is credited
///     and skipped once, so it is typed exactly once.
/// </summary>
public sealed class ImeForwarder
{
    private int _compositions;
    private int _asciiCredited;
    private string _asciiCreditChars = string.Empty;
    private bool _deletionPending;
    private bool _deletionWasUnsent;
    private int _eventsSinceKey;
    private int _quietKeys;
    private bool _discardNextText;
    private string _discardText = string.Empty;
    private bool _discarded;
    private int _discardForwarded;

    /// <summary>The text the IME sink currently holds. The caller keeps it in sync.</summary>
    public string SinkText { get; private set; } = string.Empty;

    /// <summary>True while at least one composition is live.</summary>
    public bool Composing => _compositions > 0;

    /// <summary>
    /// True when the live composition started inside the previous one and
    /// that one's End arrived after it. An IME that ends one composition and
    /// starts the next in the same keystroke gets no Changed or Ended for the
    /// new one (Korean IME trace), so a key the IME lets through is the only
    /// sign that it is over.
    ///
    /// This is an inference from that trace, not a documented TSF rule: a key
    /// the text service declines reports <c>pfEaten = false</c> for that key
    /// and leaves its composition where it was. The consumers therefore treat
    /// the text as *pending* — it is drawn as a preedit, Escape and closing
    /// the pane discard it, and a key, focus loss or opening the search bar
    /// delivers it — rather than as text known to be final.
    /// </summary>
    public bool Untracked { get; private set; }

    /// <summary>
    /// True when the last <see cref="OnKeyDown"/> was spent on unsent text:
    /// the box deleted it before the key reached the caller, so the key must
    /// not also go to the surface.
    /// </summary>
    public bool KeyConsumed { get; private set; }

    /// <summary>
    /// True while the discard on record came from the quiet guess rather than
    /// from a cancel the user asked for. An Ended that follows a quiet discard
    /// is that composition's result and may be delivered; an Ended that
    /// follows a cancel is the text the user threw away.
    /// </summary>
    public bool DiscardWasQuiet => _discarded;

    /// <summary>How much of <see cref="SinkText"/> has been forwarded.</summary>
    public int Forwarded { get; private set; }

    /// <summary>The display-only tail of the sink, or null when there is none.</summary>
    public string? Preedit { get; private set; }

    /// <summary>Committed text, in order, ready for SurfaceText / the preview sink.</summary>
    public event Action<string>? ForwardedText;

    /// <summary>The preedit to display, or null to clear it.</summary>
    public event Action<string?>? PreeditChanged;

    /// <summary>
    /// Supplies the sink text the caller read before an event. Any rewrite
    /// below the watermark is resolved here, where it is observable, so the
    /// display paths never have to repair the commit watermark.
    /// </summary>
    public void SetSinkText(string text)
    {
        var next = text ?? string.Empty;
        if (_discardNextText)
        {
            if (next.Length == 0)
            {
                // An empty sync is not the write we are waiting for; keep
                // waiting rather than spending the guard on nothing.
                return;
            }
            if (string.Equals(next, _discardText, StringComparison.Ordinal))
            {
                // The text we cancelled came back: TSF owns the box while its
                // composition is live and reverts a managed clear. Drop it.
                SinkText = string.Empty;
                Forwarded = 0;
                Preedit = null;
                PreeditChanged?.Invoke(null);
                return;
            }
            // Something else is in the box, so this is real input.
            _discardNextText = false;
            _discardText = string.Empty;
        }
        var old = SinkText;
        if (!string.Equals(old, next, StringComparison.Ordinal))
        {
            var prefix = CommonPrefix(old, next);
            // Was the removed text already delivered? Compare against the
            // watermark as it stood before the repair below lowers it.
            var watermark = Forwarded;

            // The text the surface already has is the part of the old buffer
            // that survived unchanged. Everything after it is new input, even
            // when the buffer shrank or a character inside it was replaced.
            if (Forwarded > prefix) Forwarded = prefix;

            if (next.Length < old.Length)
            {
                _deletionPending = true;
                _deletionWasUnsent = prefix >= watermark;
            }
            else
            {
                _deletionPending = false;
                _deletionWasUnsent = false;
            }
        }
        SinkText = next;
    }

    /// <summary>
    /// Disarms the discard guard. A composition event proves the composition is
    /// alive, so the text it carries is a preedit and cannot be the redraw the
    /// guard was armed for. Callers clear before they read the sink.
    /// </summary>
    public void ClearDiscard()
    {
        _discardNextText = false;
        _discardText = string.Empty;
    }

    /// <summary>A composition started at <paramref name="startIndex"/>.</summary>
    public void OnCompositionStarted(int startIndex)
    {
        // A new composition has not earned the untracked mark; the Ended that
        // belongs to the previous one is what sets it. A new composition also
        // means nothing outstanding is being discarded.
        Untracked = false;
        ClearDiscard();
        _discarded = false;
        _discardForwarded = 0;
        _quietKeys = 0;
        _eventsSinceKey++;
        _compositions++;
        // Text before a new composition is final; a new Started arriving after
        // an Ended in one keystroke also forwards the old prefix here.
        ForwardRange(startIndex);
        UpdatePreedit();
    }

    /// <summary>The live composition changed; the tail is the preedit.</summary>
    public void OnCompositionChanged()
    {
        if (_compositions == 0)
        {
            // A Changed with no live composition is either the composition we
            // discarded speaking again, or a late write for one that already
            // ended. Only the first is a preedit.
            if (!_discarded) return;

            _discarded = false;
            _compositions = 1;
            // The buffer came back; the part the surface already has is not
            // new input.
            Forwarded = Math.Min(_discardForwarded, SinkText.Length);
            _discardForwarded = 0;
        }
        Untracked = false;
        _quietKeys = 0;
        _eventsSinceKey++;
        UpdatePreedit();
    }

    /// <summary>Re-reads the preedit from the sink without an event.</summary>
    public void RefreshPreedit()
    {
        var old = Preedit;
        var text = SinkText;
        var from = Math.Min(Forwarded, text.Length);
        var tail = from < text.Length ? text.Substring(from) : null;
        if (string.Equals(old, tail, StringComparison.Ordinal)) return;
        UpdatePreedit();
    }

    /// <summary>
    /// A composition ended: close the session without forwarding anything.
    /// TSF raises Ended while the sink still holds the reading and writes the
    /// selected text afterwards, so the reading is provisional; the caller
    /// commits the text that landed on the next settled turn. The
    /// count keeps a Started for the next composition from closing that next
    /// composition when both arrive in one keystroke; only the last End closes
    /// the session.
    /// </summary>
    public void OnCompositionEnded()
    {
        _eventsSinceKey++;
        _quietKeys = 0;
        // A quiet discard's result is still the user's when the composition
        // ends: the part the surface already has must not be typed again by
        // the settle that delivers it. A later Changed is a new composition's,
        // not this one's, so the mark itself dies here.
        if (_discarded) Forwarded = Math.Min(_discardForwarded, SinkText.Length);
        _discarded = false;
        _discardForwarded = 0;
        if (_compositions > 0) _compositions--;
        if (_compositions > 0)
        {
            Untracked = true;
            return;
        }
        // The last one closed, so there is nothing left to be untracked
        // about; leaving the mark standing lets it leak into the next
        // composition and keeps the poll timer alive with nothing to poll.
        Untracked = false;
        Preedit = null;
        PreeditChanged?.Invoke(null);
    }

    /// <summary>
    /// The commit decision a key press takes. The return value is advisory: the
    /// caller withholds the key itself while a tracked composition is live (the
    /// control's own composition check) and spends it only when
    /// <see cref="KeyConsumed"/> is set.
    /// <paramref name="deletionKey"/> is true for Backspace/Delete, which are
    /// the keys whose own action can shrink the sink on the tail.
    ///
    /// A composition that raises no event for two consecutive keys is treated
    /// as abandoned: its text is discarded and the next key flows. A live IME
    /// reports the keys it takes, but it also declines keys (and this control
    /// refuses some itself), so one quiet key is not evidence and two are a
    /// guess. The cost of the guess is the preedit while the composition stays
    /// silent: it is dropped, and its next Changed takes the composition back
    /// (with the prefix already delivered) before any of its text can be read
    /// as final. If the composition ends instead, its result is the user's and
    /// is delivered; only the part the surface already has is withheld. That
    /// window closes on the composition's own end, or on a commit that delivers
    /// text. The key itself is terminal input the IME declined, so it is
    /// forwarded.
    /// </summary>
    public bool OnKeyDown(bool deletionKey = false)
    {
        KeyConsumed = false;
        var deleted = _deletionPending;
        var unsent = _deletionWasUnsent;
        var hadEvent = _eventsSinceKey > 0;
        _deletionPending = false;
        _deletionWasUnsent = false;
        _eventsSinceKey = 0;

        if (Composing && !Untracked)
        {
            if (hadEvent)
            {
                // The IME reported something since the previous key, so it is
                // watching this composition and the key is withheld.
                _quietKeys = 0;
                return false;
            }

            // Nothing arrived since the previous key. A live IME reports the
            // keys it takes, but it also declines keys, and a declined key
            // reaches this surface with no event to show for it, so one quiet
            // key is not evidence. Two in a row are: the IME is not watching
            // this buffer.
            //
            // Drop the text rather than leaving it to be committed. A dead
            // composition's preedit was never deliverable, and a live one must
            // not be typed as final: discarding is the same choice Escape
            // makes, and it cannot put unconfirmed text at the prompt. The next
            // key flows normally, and if the IME comes back its text is new
            // input, delivered by the insertion path.
            if (++_quietKeys < 2) return false;
            // If the IME is merely quiet and not gone, it will write the same
            // buffer back; arm the discard with it so the redraw is dropped
            // rather than delivered as an insertion.
            var abandoned = SinkText;
            // Drop the text but keep the composition open: a dead IME's keys
            // must flow, and a live one is re-tracked by its next Changed.
            // Remember how much of it the surface already has, so a revival
            // does not send that prefix again.
            _discardForwarded = Forwarded;
            SinkText = string.Empty;
            Forwarded = 0;
            Preedit = null;
            PreeditChanged?.Invoke(null);
            _discarded = true;
            DiscardNextText(abandoned);
            _compositions = 0;
            Untracked = false;
            _eventsSinceKey = 0;
            _quietKeys = 0;
            return false;
        }

        // The IME is not reporting this composition, so the box has
        // already applied this editing key. A deletion that took text past
        // the watermark removed input that was never sent: spend the key
        // there. One that took text the surface already has must still reach
        // the shell, so the surviving unsent tail is committed and the key
        // goes through. Only a deletion key can own a shrink: a rewrite
        // between keys must not eat the next Space.
        if (Untracked && deleted && unsent && deletionKey) KeyConsumed = true;

        CommitCore();
        return true;
    }

    /// <summary>
    /// Drops <paramref name="text"/> if it comes back after a cancel. TSF owns
    /// the box while its composition is live and reverts a managed clear, so
    /// the text can reappear on a later sync; it was discarded deliberately.
    /// Empty syncs keep waiting, and any other text is real input and flows.
    /// </summary>
    public void DiscardNextText(string text)
    {
        _discardNextText = true;
        _discardText = text ?? string.Empty;
    }

    /// <summary>
    /// An ASCII character has been delivered through CharacterReceived. Naming
    /// it lets the commit skip that character wherever it sits in the sink; a
    /// bare count can only skip from the front, which spends the credit on a
    /// pending tail when the typed character is at the end.
    /// </summary>
    public void NoteAsciiDelivered(char? delivered = null)
    {
        if (delivered is { } ch && ch < (char)0x80) _asciiCreditChars += ch;
        else _asciiCredited++;
    }

    /// <summary>Forward the rest of the sink and empty it.</summary>
    public void Commit() => CommitCore();

    /// <summary>Drop a live preedit without forwarding it.</summary>
    public void Cancel()
    {
        SinkText = string.Empty;
        Forwarded = 0;
        _asciiCredited = 0;
        _asciiCreditChars = string.Empty;
        _compositions = 0;
        Untracked = false;
        _deletionPending = false;
        _deletionWasUnsent = false;
        _eventsSinceKey = 0;
        _quietKeys = 0;
        ClearDiscard();
        _discarded = false;
        _discardForwarded = 0;
        Preedit = null;
        PreeditChanged?.Invoke(null);
    }

    private void CommitCore()
    {
        // A commit that delivers text is a deliberate gesture and ends the
        // "this composition may come back" window. A key over an empty sink
        // forwards nothing, so it must not spend that proof -- it is exactly
        // the key the discard releases.
        var delivered = SinkText.Length > 0;
        if (delivered) ForwardRange(SinkText.Length);
        SinkText = string.Empty;
        Forwarded = 0;
        _asciiCredited = 0;
        _asciiCreditChars = string.Empty;
        _compositions = 0;
        Untracked = false;
        _deletionPending = false;
        _deletionWasUnsent = false;
        _eventsSinceKey = 0;
        _quietKeys = 0;
        // Only a commit that delivers text retires the redraw guard: a key over
        // an empty sink forwards nothing and is exactly the key the discard
        // releases, so it must not spend the guard the discard armed.
        if (delivered) ClearDiscard();
        if (delivered) _discarded = false;
        if (delivered) _discardForwarded = 0;
        Preedit = null;
        PreeditChanged?.Invoke(null);
    }

    private void ForwardRange(int end)
    {
        var text = SinkText;
        // SetSinkText repairs the watermark on a rewrite; this clamp is the
        // belt to that suspenders and must not rewind to zero, or a rewound
        // composition re-sends text the surface already has.
        var from = Math.Min(Forwarded, text.Length);
        end = Math.Min(end, text.Length);
        if (end <= from) return;

        var sb = new StringBuilder(end - from);
        // A named credit belongs to the character the box appended at the
        // caret, so it is the range's LAST occurrence of that character: an
        // earlier copy is the composition's own text (ju + j forwards ju).
        var skip = new bool[end - from];
        var creditsUsed = 0;
        var searchEnd = end;
        while (creditsUsed < _asciiCreditChars.Length && searchEnd > from)
        {
            var wanted = _asciiCreditChars[_asciiCreditChars.Length - 1 - creditsUsed];
            var idx = text.LastIndexOf(wanted, searchEnd - 1, searchEnd - from);
            if (idx < from) break;
            skip[idx - from] = true;
            creditsUsed++;
            searchEnd = idx;
        }
        for (var i = from; i < end; i++)
        {
            if (skip[i - from]) continue;
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

        if (creditsUsed > 0) _asciiCreditChars = _asciiCreditChars[..^creditsUsed];
        Forwarded = end;
        if (sb.Length > 0) ForwardedText?.Invoke(sb.ToString());
    }

    private void UpdatePreedit()
    {
        // Display only. Repairing the commit watermark from here would let a
        // repaint re-send delivered text.
        var text = SinkText;
        var from = Math.Min(Forwarded, text.Length);
        var tail = from < text.Length ? text.Substring(from) : null;
        Preedit = tail;
        PreeditChanged?.Invoke(tail);
    }

    private static int CommonPrefix(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && a[i] == b[i]) i++;
        return i;
    }
}
