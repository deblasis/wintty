using System.Collections.Generic;
using System.IO;

namespace Ghostty.Core.Logging;

internal sealed class RealFileSystem : IFileSystem
{
    public static readonly RealFileSystem Instance = new();

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern)
        => Directory.EnumerateFiles(path, searchPattern);

    public void DeleteFile(string path) => File.Delete(path);

    public Stream OpenAppend(string path)
        => new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    public long FileLength(string path) => new FileInfo(path).Length;
}
