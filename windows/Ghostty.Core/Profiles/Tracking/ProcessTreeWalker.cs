using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Innermost-descendant info returned by <see cref="ProcessTreeWalker"/>.
/// <c>ExeBasename</c> is always populated; <c>CommandLine</c> is the raw
/// command line of the same process when readable, else null (e.g. exited,
/// access denied, protected process).
/// </summary>
internal readonly record struct DescendantInfo(string ExeBasename, string? CommandLine);

/// <summary>
/// Walks the Windows process tree to find the innermost descendant of a
/// given root PID. "Innermost" = deepest by tree depth; ties broken by
/// the snapshot's natural iteration order (deterministic on stable input).
///
/// One snapshot per call. Caller invokes once per tracker tick (2 Hz).
///
/// The generated CsWin32 class is <c>DWritePInvoke</c> (see
/// NativeMethods.json "className") to avoid colliding with the
/// Windows.Win32.PInvoke class used elsewhere. NativeMethods.json also
/// pins useSafeHandles=false, so the toolhelp snapshot comes back as a
/// raw HANDLE struct and we close it explicitly via CloseHandle.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class ProcessTreeWalker
{
    // Broker / helper processes that are part of the Windows console
    // infrastructure rather than something the user is interacting with.
    // Filtered out so the walker reports the user-facing exe (e.g. cmd.exe
    // rather than its conhost child).
    private static readonly HashSet<string> IgnoredExes =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            "conhost.exe",
            "OpenConsole.exe",
            "wslhost.exe",
            "wslservice.exe",
            "wsl-vsock-relay.exe",
        };

    /// <summary>
    /// Returns the exe basename + command line of the innermost descendant
    /// of <paramref name="rootPid"/>. Returns null when the root has no
    /// descendants, has exited, or the snapshot fails. The command line
    /// component is null when it could not be read (access denied, race
    /// with process exit, etc.) but the basename is still reported.
    /// </summary>
    public static DescendantInfo? FindInnermostDescendant(uint rootPid)
    {
        var snapshot = DWritePInvoke.CreateToolhelp32Snapshot(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        // CreateToolhelp32Snapshot returns INVALID_HANDLE_VALUE (-1) on
        // failure when useSafeHandles=false. CsWin32 doesn't synthesize a
        // named constant for that, so compare against the raw -1 sentinel
        // via explicit IntPtr-to-HANDLE conversion.
        var invalid = (HANDLE)new IntPtr(-1);
        if (snapshot == invalid) return null;

        try
        {
            var byParent = new Dictionary<uint, List<(uint Pid, string ExeBasename)>>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };

            if (!DWritePInvoke.Process32FirstW(snapshot, ref entry)) return null;
            do
            {
                if (!byParent.TryGetValue(entry.th32ParentProcessID, out var list))
                {
                    list = new List<(uint, string)>();
                    byParent[entry.th32ParentProcessID] = list;
                }
                // szExeFile is a fixed-size inline char array; CsWin32 exposes
                // .ToString() for this kind of field. Trim any trailing NULs
                // defensively in case the conversion ever changes.
                var name = entry.szExeFile.ToString().TrimEnd('\0');
                list.Add((entry.th32ProcessID, name));
            }
            while (DWritePInvoke.Process32NextW(snapshot, ref entry));

            return DeepestDescendant(byParent, rootPid);
        }
        finally
        {
            DWritePInvoke.CloseHandle(snapshot);
        }
    }

    private static DescendantInfo? DeepestDescendant(
        IReadOnlyDictionary<uint, List<(uint Pid, string ExeBasename)>> byParent,
        uint rootPid)
    {
        if (!byParent.TryGetValue(rootPid, out var direct) || direct.Count == 0)
        {
            return null;
        }

        // BFS from the root; track the deepest level seen and pick the
        // last entry observed at that depth (deterministic on stable input).
        var queue = new Queue<(uint Pid, string ExeBasename, int Depth)>();
        foreach (var (pid, name) in direct)
        {
            queue.Enqueue((pid, name, 1));
        }

        string? bestName = null;
        uint bestPid = 0;
        int bestDepth = 0;
        while (queue.Count > 0)
        {
            var (pid, name, depth) = queue.Dequeue();
            // Update best only when this descendant isn't a broker/helper.
            // Brokers are visited so we still descend into THEIR children
            // (e.g. wslhost may not own much, but conhost is always a leaf).
            if (!IgnoredExes.Contains(name) && depth >= bestDepth)
            {
                bestDepth = depth;
                bestName = name;
                bestPid = pid;
            }
            if (byParent.TryGetValue(pid, out var kids))
            {
                foreach (var (kpid, kname) in kids)
                {
                    queue.Enqueue((kpid, kname, depth + 1));
                }
            }
        }

        if (bestName is null) return null;

        // Command line retrieval is best-effort: a NULL here just means we
        // fall back to exe-only icon mapping. Callers (e.g. wsl distro
        // disambiguation) already handle null gracefully.
        var cmdline = NtProcessInterop.TryGetCommandLine(bestPid);
        return new DescendantInfo(bestName, cmdline);
    }
}
