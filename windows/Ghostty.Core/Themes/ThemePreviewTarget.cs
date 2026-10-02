using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Ghostty.Core.Themes;

/// <summary>
/// Which running Wintty a theme CLI (<c>+list-themes</c>, the TUI preview
/// client) talks to, and which clients a preview server accepts. The server
/// listens on <c>ghostty-theme-preview-{pid}</c>; both ends require the
/// other to run from the same resolved image path, so a dev build and an
/// installed app running side by side never drive each other's window.
/// </summary>
/// <remarks>
/// The value compared (called the identity below) is the final path of the
/// file at a process's image path, not the path as spelled: each side's
/// image path is read from the kernel, the file now at that path is opened,
/// and <c>GetFinalPathNameByHandle</c> resolves it. A junction, a subst
/// drive, a mapped drive or an 8.3 spelling of the same path all collapse
/// to one value, and both sides are read the same way. It is not a file id:
/// two hard links of one file compare unequal, and a process whose image
/// was replaced in place compares as whatever file now sits at its path.
/// A side whose identity cannot be read matches nothing.
///
/// When this process's image is local, a candidate whose image is on a
/// network share is skipped without opening it: it cannot be the same
/// path, and opening a file on an unreachable share blocks for the
/// redirector's timeout.
///
/// The server answers an accepted client with one <see cref="Ack"/> byte
/// before it reads anything, and the client sends nothing until it has
/// that byte. The client therefore stays alive while the server reads its
/// identity (a client that wrote and exited could no longer be identified,
/// and would be dropped as foreign), and a client the server drops knows
/// it, so it falls back to the TUI instead of reporting success.
///
/// No same-executable process means no target, even when a Wintty from
/// another path is serving: the CLI then takes its "no running instance"
/// path and runs the TUI on its own, which is what it does with no app at
/// all. A foreign exe can carry a different config, theme set and protocol
/// revision, so previewing into it is the cross-install drive this class
/// exists to prevent, not a degraded success.
///
/// Several same-executable processes (single-instance off) still resolve to
/// the first one with a live pipe in enumeration order, as before; choosing
/// the one the user is looking at is #737.
/// </remarks>
public static partial class ThemePreviewTarget
{
    public const string PipePrefix = "ghostty-theme-preview-";

    public static string PipeNameFor(int pid) => $"{PipePrefix}{pid}";

    /// <summary>The byte the server writes to a client it accepted.</summary>
    public const byte Ack = 0x06; // ASCII ACK

    /// <summary>
    /// How long a client waits for <see cref="Ack"/>. Covers the server's
    /// thread-pool hop and its two image reads on a busy machine; past it
    /// the client runs the TUI on its own, which is the safe direction.
    /// </summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Wait for the server's <see cref="Ack"/> on a connected client pipe.
    /// False on a dropped connection, any other byte, or no answer within
    /// <paramref name="budget"/>. The pipe must be opened asynchronous so
    /// an abandoned read does not hold the handle's close.
    /// </summary>
    public static bool AwaitAck(Stream pipe, TimeSpan budget) =>
        AwaitAck(pipe, budget, out _);

    /// <summary>
    /// As <see cref="AwaitAck(Stream, TimeSpan)"/>, and says whether the
    /// answer was a timeout (a server that never acknowledges, such as an
    /// older build at the same path) rather than a refusal.
    /// </summary>
    public static bool AwaitAck(Stream pipe, TimeSpan budget, out bool timedOut)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        timedOut = false;
        var buffer = new byte[1];
        var read = pipe.ReadAsync(buffer, 0, 1);
        try
        {
            if (!read.Wait(budget))
            {
                // Observe the eventual fault of the read the caller's
                // dispose is about to abandon.
                _ = read.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                timedOut = true;
                return false;
            }
            return read.Result == 1 && buffer[0] == Ack;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first pid in <paramref name="pids"/>, other than
    /// <paramref name="ownPid"/>, whose identity equals
    /// <paramref name="ownPid"/>'s and which has a live pipe, or null.
    /// Pure apart from the two readers, which tests replace.
    /// </summary>
    /// <param name="readIdentity">
    /// The identity of a pid, or null when it cannot be read
    /// (access denied, exited). Null never matches, on either side: a
    /// process whose file cannot be confirmed is not known to be ours.
    /// </param>
    /// <param name="onForeign">
    /// Told about each readable candidate skipped for running a different
    /// executable, for diagnostics.
    /// </param>
    public static int? Select(
        int ownPid,
        IEnumerable<int> pids,
        Func<int, string?> readIdentity,
        Func<int, bool> hasPipe,
        Action<int, string>? onForeign = null)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(readIdentity);
        ArgumentNullException.ThrowIfNull(hasPipe);

        var own = NormalizeIdentity(readIdentity(ownPid));
        if (own is null) return null;

        foreach (var pid in pids)
        {
            if (pid == ownPid) continue;
            var identity = NormalizeIdentity(readIdentity(pid));
            if (identity is null) continue;
            if (!string.Equals(identity, own, StringComparison.OrdinalIgnoreCase))
            {
                onForeign?.Invoke(pid, identity);
                continue;
            }
            if (hasPipe(pid)) return pid;
        }
        return null;
    }

    /// <summary>
    /// True when both identities are readable and equal.
    /// </summary>
    public static bool SameIdentity(string? a, string? b)
    {
        var left = NormalizeIdentity(a);
        var right = NormalizeIdentity(b);
        return left is not null && right is not null
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A final path in comparable form: device prefixes removed (with
    /// <c>\\?\UNC\</c> mapped back to <c>\\</c>, not stripped into a
    /// relative path), backslash-separated, no trailing separator. Null for
    /// an empty or not fully qualified input; a relative spelling would
    /// otherwise resolve against whatever the working directory is.
    /// </summary>
    internal static string? NormalizeIdentity(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase))
            p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal)
            || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            p = p[4..];

        p = p.TrimEnd('\\');
        return Path.IsPathFullyQualified(p) ? p : null;
    }

    /// <summary>
    /// The pid of the Wintty this process should drive, or null.
    /// Candidates are the processes sharing this executable's file name,
    /// since a process with the same resolved image path necessarily shares it.
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    public static int? FindTarget(Action<int, string>? onForeign = null)
    {
        var ownImage = TryReadImagePath((uint)Environment.ProcessId);
        if (ownImage is null) return null;

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ownImage));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            var pids = new int[procs.Length];
            for (var i = 0; i < procs.Length; i++) pids[i] = procs[i].Id;

            return Select(
                Environment.ProcessId,
                pids,
                p => TryReadIdentity((uint)p, ownImage),
                p => File.Exists($@"\\.\pipe\{PipeNameFor(p)}"),
                onForeign);
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> runs from the same resolved image
    /// path as this process. For the server's check of a connected client
    /// and the client's check of the server it reached.
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    public static bool RunsOwnExecutable(int pid)
    {
        if (pid <= 0) return false;
        var ownImage = TryReadImagePath((uint)Environment.ProcessId);
        if (ownImage is null) return false;
        return SameIdentity(
            TryReadIdentity((uint)Environment.ProcessId, ownImage),
            TryReadIdentity((uint)pid, ownImage));
    }

    /// <summary>The pid of the client connected to a server pipe, or null.</summary>
    public static int? ClientPid(SafePipeHandle pipe) =>
        GetNamedPipeClientProcessId(pipe, out var pid) && pid != 0 ? (int)pid : null;

    /// <summary>The pid of the server a client pipe connected to, or null.</summary>
    public static int? ServerPid(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) && pid != 0 ? (int)pid : null;

    /// <summary>
    /// The identity of <paramref name="pid"/>: the final path of the file at
    /// its kernel image path, or null.
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    internal static string? TryReadIdentity(uint pid) => TryReadIdentity(pid, ownImage: null);

    /// <summary>
    /// As <see cref="TryReadIdentity(uint)"/>, but null without opening the
    /// file when <paramref name="ownImage"/> is local and the pid's image is
    /// remote (see <see cref="ReadCandidateIdentity"/>).
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    internal static string? TryReadIdentity(uint pid, string? ownImage) =>
        ReadCandidateIdentity(
            ownImage,
            TryReadImagePath(pid),
            TryReadFileIdentity,
            image => IsRemoteImagePath(image, IsNetworkDrive));

    /// <summary>
    /// The identity of <paramref name="candidateImage"/>, or null. A
    /// remote candidate of a local <paramref name="ownImage"/> is null
    /// without calling <paramref name="readFileIdentity"/>: a local and a
    /// remote path cannot resolve to the same final path, and opening a
    /// file on an unreachable share stalls for the redirector's timeout,
    /// in the CLI before it connects and in the server's accept loop.
    /// </summary>
    internal static string? ReadCandidateIdentity(
        string? ownImage,
        string? candidateImage,
        Func<string, string?> readFileIdentity,
        Func<string, bool> isRemote)
    {
        if (candidateImage is null) return null;
        if (ownImage is not null && !isRemote(ownImage) && isRemote(candidateImage)) return null;
        return readFileIdentity(candidateImage);
    }

    /// <summary>
    /// True for a UNC path, a path on a drive <paramref name="isNetworkDrive"/>
    /// reports as remote, or a spelling this cannot classify (which is then
    /// not opened either).
    /// </summary>
    internal static bool IsRemoteImagePath(string path, Func<string, bool> isNetworkDrive)
    {
        var p = NormalizeIdentity(path);
        if (p is null) return true;
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        var root = Path.GetPathRoot(p);
        return string.IsNullOrEmpty(root) || isNetworkDrive(root);
    }

    // GetDriveType answers from the drive letter's mapping and does not
    // contact the share, so it is safe on a disconnected mapped drive.
    private static bool IsNetworkDrive(string root)
    {
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Same buffer reasoning as PaneLaunchImage: the NT path limit once, no
    // grow-and-retry branch that no test reaches.
    private const int PathChars = 32768;

    /// <summary>
    /// The full image path of <paramref name="pid"/>, or null. Reads
    /// through <c>PROCESS_QUERY_LIMITED_INFORMATION</c> rather than
    /// <see cref="Process.MainModule"/>, which needs VM read access and
    /// throws for an elevated process of the same user.
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    internal static string? TryReadImagePath(uint pid)
    {
        var handle = DWritePInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid);
        if (handle.IsNull) return null;
        try
        {
            var buffer = new char[PathChars];
            uint size = PathChars;
            var ok = DWritePInvoke.QueryFullProcessImageName(
                handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer.AsSpan(), ref size);
            if (!ok || size is 0 or > PathChars) return null;
            return new string(buffer, 0, (int)size);
        }
        finally
        {
            DWritePInvoke.CloseHandle(handle);
        }
    }

    private const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4; // read, write, delete
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_NAME_NORMALIZED = 0;
    private const uint VOLUME_NAME_DOS = 0;

    /// <summary>
    /// The final path of the file at <paramref name="path"/>, or null.
    /// Opened with no access rights and full sharing, which a running image
    /// permits; the object manager then answers with reparse points, drive
    /// substitution and short names all resolved.
    /// </summary>
    internal static unsafe string? TryReadFileIdentity(string path)
    {
        var handle = CreateFileW(
            path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle == new IntPtr(-1) || handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[PathChars];
            fixed (char* p = buffer)
            {
                var length = GetFinalPathNameByHandleW(
                    handle, p, PathChars, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
                if (length is 0 or >= PathChars) return null;
                return NormalizeIdentity(new string(buffer, 0, (int)length));
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetFinalPathNameByHandleW(
        IntPtr hFile,
        char* lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
