using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles.Fakes;

/// <summary>
/// In-memory filesystem fake. Files are byte-array values keyed by
/// path. Known folders are configured via <see cref="SetKnownFolder"/>.
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new();
    private readonly Dictionary<KnownFolderId, string> _knownFolders = new();

    public void AddFile(string path, byte[] content) => _files[path] = content;
    public void AddFile(string path) => _files[path] = [];
    public void SetKnownFolder(KnownFolderId id, string path) => _knownFolders[id] = path;

    public bool FileExists(string path) => _files.ContainsKey(path);

    public string? GetKnownFolder(KnownFolderId id)
        => _knownFolders.TryGetValue(id, out var p) ? p : null;

    public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
    {
        if (!_files.TryGetValue(path, out var bytes))
            throw new FileNotFoundException(path);
        return Task.FromResult(bytes);
    }

    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        _files[path] = bytes;
        return Task.CompletedTask;
    }
}
