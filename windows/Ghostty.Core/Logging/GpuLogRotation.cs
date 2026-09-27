using System;
using System.IO;

namespace Ghostty.Core.Logging;

/// <summary>
/// The gpu.log rotation: at startup the previous launch's gpu.log moves to
/// gpu.prev.log beside it, so the relaunch a support round-trip asks for
/// no longer destroys the evidence it is asked about (#968). Exactly one
/// previous launch is kept - the one support would ask about.
///
/// Best-effort throughout, reporting success as a bool. The caller opens
/// the new log with OPEN_ALWAYS for exactly the failure cases, so a failed
/// rotation degrades to appending instead of to the CREATE_ALWAYS
/// truncation this rotation replaces. Failure itself deletes nothing: the
/// replace is a single File.Move with overwrite, which either lands or
/// leaves both files exactly as they were.
/// </summary>
internal static class GpuLogRotation
{
    /// <summary>The fixed name of the kept-previous log, in the same
    /// directory as the current one.</summary>
    public const string PreviousFileName = "gpu.prev.log";

    /// <summary>gpu.prev.log in <paramref name="currentPath"/>'s directory.</summary>
    public static string PreviousPathFor(string currentPath) =>
        Path.Combine(Path.GetDirectoryName(currentPath)!, PreviousFileName);

    /// <summary>
    /// Move <paramref name="currentPath"/> to gpu.prev.log. True on success,
    /// including the no-op: a launch with no current log (the first, or one
    /// whose redirect never ran) has nothing to carry aside, and any older
    /// gpu.prev.log stays in place - history is only ever replaced by a
    /// newer previous, never deleted on the way to nothing.
    /// </summary>
    public static bool Rotate(string currentPath)
    {
        try
        {
            if (!File.Exists(currentPath))
                return true;

            // One replace-move, no delete of our own. The current log may
            // be held by another Wintty instance (RedirectStderrToFile runs
            // before the single-instance election, so a second launch
            // reaches this while the primary still holds gpu.log open with
            // no FILE_SHARE_DELETE) and the stored previous may be held by
            // a tailer. Both shapes fail the move outright, which is the
            // property everything here is built on: a failed move leaves
            // the source and the destination exactly as they were, so the
            // stored previous can never be lost to a rotation that did not
            // complete. Replacing, when it happens, is the move itself.
            File.Move(currentPath, PreviousPathFor(currentPath), overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
