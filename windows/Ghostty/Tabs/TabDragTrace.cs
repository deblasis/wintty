using System;
using System.IO;

namespace Ghostty.Tabs;

/// <summary>
/// The drag oracle both tab directions write. The env var names one file
/// per run, so concurrent instances never interleave one file, and the
/// trace is inert (a null check) when the variable is unset. Lines pair
/// DRAG start/completed per gesture, and COMMIT lines name what each
/// drop actually landed -- a move, a join, a reconcile repair -- so a
/// COMMIT with no gesture around it, or a completed gesture with no
/// COMMIT under it, is the fact the next fix starts from.
/// </summary>
internal static class TabDragTrace
{
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable("WINTTY_TABDRAG_TRACE");

    /// <summary>Whether a Line would write, so callers can skip building
    /// trace-only strings on hot paths.</summary>
    internal static bool Enabled => TracePath is not null;

    internal static void Line(string message)
    {
        if (TracePath is null) return;
        try
        {
            File.AppendAllText(TracePath, message + Environment.NewLine);
        }
        catch
        {
            // A locked or unwritable log must never take the drag down.
        }
    }
}
