using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.Logging;

/// <summary>
/// Minimal filesystem abstraction so rotation and retention tests can
/// observe behavior without touching the real disk. Production impl is
/// <see cref="RealFileSystem"/>.
/// </summary>
internal interface IFileSystem
{
    void CreateDirectory(string path);
    bool DirectoryExists(string path);

    /// <summary>
    /// Returns full paths of files in <paramref name="path"/> matching
    /// <paramref name="searchPattern"/>. Consumers pass the returned
    /// strings to <see cref="DeleteFile"/> and to
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/>; fakes
    /// MUST return full paths, not bare file names.
    /// </summary>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);

    void DeleteFile(string path);
    Stream OpenAppend(string path);
    long FileLength(string path);
}
