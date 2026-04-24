using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Win32 known-folder selector. Local enum so Ghostty.Core does not
/// depend on Win32 SHGetKnownFolderPath types.
/// </summary>
public enum KnownFolderId
{
    System,           // %SystemRoot%\System32
    ProgramFiles,     // %ProgramFiles%
    ProgramFilesX86,  // %ProgramFiles(x86)%
    LocalAppData,     // %LOCALAPPDATA%
    UserProfile,      // %USERPROFILE%
}

/// <summary>
/// Filesystem and known-folder access. Production wrapper uses
/// System.IO + SHGetKnownFolderPath; tests use FakeFileSystem.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    string? GetKnownFolder(KnownFolderId id);
    Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct);
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct);
}
