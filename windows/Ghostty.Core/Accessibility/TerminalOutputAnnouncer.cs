using System;
using System.Text;

namespace Ghostty.Core.Accessibility;

/// <summary>
/// Decides what new terminal output to announce to a screen reader, given successive
/// snapshots of the screen text. Pure and deterministic. Works off the screen text
/// (append-only until a clear or redraw); announces only complete lines, holds a
/// trailing partial line until its newline arrives, batches new lines into one
/// string, and summarizes large bursts. On a non-prefix change (clear, redraw,
/// scroll-out) it re-baselines silently to avoid speaking garbage.
/// </summary>
public sealed class TerminalOutputAnnouncer
{
    private readonly int _maxLines;
    private readonly int _maxChars;
    private string _announced = "";
    private bool _seeded;

    public TerminalOutputAnnouncer(int maxLines = 20, int maxChars = 1000)
    {
        _maxLines = maxLines;
        _maxChars = maxChars;
    }

    /// <summary>
    /// Observe the current screen text. Returns the text to announce, or null when
    /// there is nothing new to speak yet.
    /// </summary>
    public string? Observe(string screenText)
    {
        screenText ??= "";
        if (!_seeded)
        {
            _announced = screenText;
            _seeded = true;
            return null;
        }
        if (screenText.Length == _announced.Length && screenText == _announced) return null;
        if (!screenText.StartsWith(_announced, StringComparison.Ordinal))
        {
            _announced = screenText; // diverged: re-baseline silently
            return null;
        }
        var delta = screenText.Substring(_announced.Length);
        var lastNewline = delta.LastIndexOf('\n');
        if (lastNewline < 0) return null; // only a partial new line so far; hold
        var settled = delta.Substring(0, lastNewline + 1);
        _announced += settled;
        return Summarize(settled);
    }

    /// <summary>Adopt <paramref name="screenText"/> as the baseline without announcing.</summary>
    public void Reseed(string screenText)
    {
        _announced = screenText ?? "";
        _seeded = true;
    }

    private string? Summarize(string settled)
    {
        var lines = settled.Split('\n');
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Trim().Length > 0) count++;
        if (count == 0) return null;
        if (count > _maxLines || settled.Length > _maxChars)
            return count + " new lines";

        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        return sb.ToString();
    }
}
