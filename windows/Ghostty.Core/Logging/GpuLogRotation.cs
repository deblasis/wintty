using System;
using System.IO;

namespace Ghostty.Core.Logging;

/// <summary>
/// The gpu.log rotation: at startup the previous launch's gpu.log moves to
/// gpu.prev.log beside it, so the relaunch a support round-trip asks for
/// no longer destroys the evidence it is asked about (#968). Exactly one
/// previous launch is kept - the one support would ask about.
///
/// Best-effort throughout, reporting success as a bool: a rotation that
/// fails (the previous log held open by a tailer, revoked ACLs) leaves both
/// files where they are. The caller opens the new log with OPEN_ALWAYS for
/// exactly this case, so a failed rotation degrades to appending instead of
/// to the CREATE_ALWAYS truncation this rotation replaces.
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

            var previous = PreviousPathFor(currentPath);
            try
            {
                if (File.Exists(previous))
                    File.Delete(previous);
            }
            catch (Exception)
            {
                // A previous log we cannot delete is one the move below
                // cannot replace; report the failure and leave both files
                // alone rather than deleting our way to a gap.
                return false;
            }

            File.Move(currentPath, previous);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
