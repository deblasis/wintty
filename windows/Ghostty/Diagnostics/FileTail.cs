using System.IO;

namespace Ghostty.Diagnostics;

/// <summary>
/// The one way to read the newest bytes of a file another process or
/// instance may hold open: crash.log is appended by every instance, and
/// native-stderr.log is held for append by its capturing instance. One
/// helper instead of per-caller copies, because a concurrency fix
/// applied to one copy silently misses the others.
/// </summary>
internal static class FileTail
{
    /// <summary>
    /// The last at most <paramref name="maxBytes"/> of the file, or null
    /// when it does not exist. A line cut by the window boundary is the
    /// caller's to tolerate.
    /// </summary>
    internal static byte[]? Read(string path, long maxBytes)
    {
        if (!File.Exists(path)) return null;

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > maxBytes)
            stream.Seek(-maxBytes, SeekOrigin.End);
        using var tail = new MemoryStream((int)maxBytes);
        stream.CopyTo(tail);
        return tail.ToArray();
    }
}
