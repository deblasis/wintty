using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Production IFileSystem. File I/O via System.IO; known-folder paths
/// via SHGetKnownFolderPath through CsWin32.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal sealed class WindowsFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        // Mirrors the rest of this wrapper's best-effort contract: stat
        // failures (ACL, symlink loops, transient I/O) must not fail a
        // cache lookup - the caller treats null as "unknown mtime" and
        // falls back to the un-mtimed cache key.
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken ct)
        => await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

    public async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
    }

    public unsafe string? GetKnownFolder(KnownFolderId id)
    {
        var guid = ToWin32Guid(id);
        // The generated class name is pinned to DWritePInvoke in
        // NativeMethods.json ("className") to avoid colliding with the
        // Windows.Win32.PInvoke class used by the shell assembly.
        // The CsWin32 friendly overload takes `in Guid` and `out PWSTR`.
        var hr = DWritePInvoke.SHGetKnownFolderPath(
            guid,
            (KNOWN_FOLDER_FLAG)0,
            default,
            out var pwstr);
        // MSDN doesn't guarantee pwstr is null on failure; free defensively.
        if (hr.Failed)
        {
            if (pwstr.Value is not null) Marshal.FreeCoTaskMem((IntPtr)pwstr.Value);
            return null;
        }
        try
        {
            return pwstr.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((IntPtr)pwstr.Value);
        }
    }

    // Known-folder GUIDs from knownfolders.h. Hard-coded rather than
    // pulling in Windows.Win32.UI.Shell.FOLDERID_* constants so the
    // enum-to-GUID mapping lives next to the enum definition.
    private static Guid ToWin32Guid(KnownFolderId id) => id switch
    {
        KnownFolderId.System        => new Guid("1AC14E77-02E7-4E5D-B744-2EB1AE5198B7"),
        KnownFolderId.ProgramFiles  => new Guid("905E63B6-C1BF-494E-B29C-65B732D3D21A"),
        KnownFolderId.ProgramFilesX86 => new Guid("7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E"),
        KnownFolderId.LocalAppData  => new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091"),
        KnownFolderId.UserProfile   => new Guid("5E6C858F-0E22-4760-9AFE-EA3317B67173"),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
