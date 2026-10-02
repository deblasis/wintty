using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.System.Threading;

namespace Ghostty.Core.Themes;

/// <summary>
/// Which running Wintty a theme CLI (<c>+list-themes</c>, the TUI preview
/// client) talks to. The server listens on
/// <c>ghostty-theme-preview-{pid}</c>; the client must reach a process
/// started from its OWN executable, so a dev build and an installed app
/// running side by side never drive each other's window.
/// </summary>
/// <remarks>
/// No same-path process means no target, even when a Wintty from another
/// path is serving: the CLI then takes its "no running instance" path and
/// runs the TUI on its own, which is what it does with no app at all. A
/// foreign exe can carry a different config, theme set and protocol
/// revision, so previewing into it is the cross-install drive this class
/// exists to prevent, not a degraded success.
///
/// Several same-path processes (single-instance off) still resolve to the
/// first one with a live pipe in enumeration order, as before; choosing
/// the one the user is looking at is #737.
/// </remarks>
public static class ThemePreviewTarget
{
    public const string PipePrefix = "ghostty-theme-preview-";

    public static string PipeNameFor(int pid) => $"{PipePrefix}{pid}";

    /// <summary>
    /// The first pid in <paramref name="pids"/>, other than
    /// <paramref name="ownPid"/>, whose executable is
    /// <paramref name="ownExePath"/> and which has a live pipe, or null.
    /// Pure apart from the two readers, which tests replace.
    /// </summary>
    /// <param name="readExePath">
    /// The image path of a pid, or null when it cannot be read (access
    /// denied, exited). Null never matches: a process whose path cannot be
    /// confirmed is not known to be ours.
    /// </param>
    public static int? Select(
        string? ownExePath,
        int ownPid,
        IEnumerable<int> pids,
        Func<int, string?> readExePath,
        Func<int, bool> hasPipe)
    {
        ArgumentNullException.ThrowIfNull(pids);
        ArgumentNullException.ThrowIfNull(readExePath);
        ArgumentNullException.ThrowIfNull(hasPipe);

        var own = NormalizeExePath(ownExePath);
        if (own is null) return null;

        foreach (var pid in pids)
        {
            if (pid == ownPid) continue;
            var path = NormalizeExePath(readExePath(pid));
            if (path is null) continue;
            if (!string.Equals(path, own, StringComparison.OrdinalIgnoreCase)) continue;
            if (hasPipe(pid)) return pid;
        }
        return null;
    }

    /// <summary>
    /// Absolute, backslash-separated, prefix-stripped form of an
    /// executable path with no trailing separator, or null for a path that
    /// is empty or cannot be made absolute. Case is left alone; the
    /// comparison in <see cref="Select"/> ignores it.
    /// </summary>
    internal static string? NormalizeExePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var stripped = path.Trim();
        if (stripped.StartsWith(@"\\?\", StringComparison.Ordinal)
            || stripped.StartsWith(@"\\.\", StringComparison.Ordinal))
            stripped = stripped[4..];

        try
        {
            stripped = Path.GetFullPath(stripped.Replace('/', '\\'));
        }
        catch (Exception ex) when (
            ex is ArgumentException or PathTooLongException or
            NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        return stripped.TrimEnd('\\');
    }

    /// <summary>
    /// The pipe name of the Wintty this process should drive, or null.
    /// Candidates are the processes sharing this executable's name, since a
    /// same-path process necessarily shares it.
    /// </summary>
    [SupportedOSPlatform("windows6.0.6000")]
    public static string? FindPipe()
    {
        var ownPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(ownPath)) return null;

        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ownPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        try
        {
            var pids = new int[procs.Length];
            for (var i = 0; i < procs.Length; i++) pids[i] = procs[i].Id;

            var pid = Select(
                ownPath,
                Environment.ProcessId,
                pids,
                p => TryReadImagePath((uint)p),
                p => File.Exists($@"\\.\pipe\{PipeNameFor(p)}"));
            return pid is int found ? PipeNameFor(found) : null;
        }
        finally
        {
            foreach (var proc in procs) proc.Dispose();
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
}
