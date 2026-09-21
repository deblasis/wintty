using System;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// What one known process is: its executable's basename, and its command
/// line when that can be read.
///
/// Used for the pid a pane just spawned -- the root of its pty's process
/// tree -- so a tab with no profile can be named after the thing it is
/// actually running. That is a different question from the one
/// <see cref="ProcessTreeWalker"/> answers: the walker finds the INNERMOST
/// descendant, which is what is in front right now, and it pays a
/// machine-wide snapshot to find it. This is the root itself, asked once,
/// against one handle this class opens and closes.
///
/// The two halves fail independently and neither throws. A handle this
/// process cannot open, an image path it cannot read, a child that has
/// already exited: each reads as null, and the caller falls to whatever it
/// had. They are reported separately on purpose -- a caller that needs both
/// can tell a complete answer from half of one, which
/// <see cref="Ghostty.Core.Tabs.TabModel.OnPaneLaunched"/> depends on.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class PaneLaunchImage
{
    // One buffer, sized for the longest path the NT namespace admits
    // (32767 wide chars plus the NUL), and no retry.
    //
    // The obvious shape here is MAX_PATH and a grow on
    // ERROR_INSUFFICIENT_BUFFER, and it was written that way first. The
    // grow is the only interesting branch in this file and it is
    // unreachable from any test: every process a suite can resolve has a
    // path far under 260 characters, so the retry never runs in CI and
    // only ever runs on a user's machine -- a deep Scoop or Dev Drive
    // install, a redirected UNC profile. It also made the result depend on
    // reading the last error correctly, which is one refactor (any managed
    // call slipped between the P/Invoke and the read) away from silently
    // returning null and leaving every no-profile tab on the generic name
    // with the whole suite green.
    //
    // 64 KiB transient, once per no-profile tab, buys that branch's
    // deletion. It is not a hot path and never will be: the caller asks at
    // most once per tab, and only for a tab no profile names.
    private const int PathChars = 32768;

    /// <summary>
    /// The basename of <paramref name="pid"/>'s executable (with its
    /// extension, the way <see cref="ProcessDisplayName"/> keys its table)
    /// and that process's raw command line, or null for either when it
    /// cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both reads go through one handle opened with
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, which is the minimum for
    /// each and survives an integrity-level difference between this
    /// process and an elevated child of the same user.
    /// </para>
    /// <para>
    /// The command line comes from <c>ProcessCommandLineInformation</c>,
    /// not from a PEB walk: the string is returned inline in this
    /// process's own buffer, so nothing here depends on the layout of a
    /// structure in the other process's address space.
    /// </para>
    /// <para>
    /// Holding the handle across both reads also pins the pid. The kernel
    /// will not recycle a pid while any handle to it is open, so the
    /// second read cannot land on a different process than the first, and
    /// a caller resolving the pid a pane reports (whose surface holds its
    /// own handle to the same child) is not racing pid reuse at all.
    /// </para>
    /// </remarks>
    public static (string? ExeBasename, string? CommandLine) TryResolve(uint pid)
    {
        var handle = DWritePInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid);
        // OpenProcess returns NULL (not INVALID_HANDLE_VALUE) on failure.
        if (handle.IsNull) return (null, null);
        try
        {
            return (TryImageBasename(handle), NtProcessInterop.GetCommandLine(handle));
        }
        finally
        {
            DWritePInvoke.CloseHandle(handle);
        }
    }

    private static string? TryImageBasename(HANDLE handle)
    {
        var buffer = new char[PathChars];
        uint size = PathChars;
        var ok = DWritePInvoke.QueryFullProcessImageName(
            handle, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer.AsSpan(), ref size);
        // On success `size` is the length written, excluding the NUL.
        if (!ok || size is 0 or > PathChars) return null;

        // The last segment, by the same rule that names a tab after the
        // folder its shell reported. An image path and a working directory
        // are the same shape of string and must not disagree about where
        // the last segment starts.
        return TabLabel.FolderName(new string(buffer, 0, (int)size));
    }
}
