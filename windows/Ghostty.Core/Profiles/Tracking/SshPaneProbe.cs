using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ghostty.Core.Ssh;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Answers "is this pane inside an ssh session, and to where?" on demand:
/// one process snapshot, the outermost <c>ssh.exe</c> under the pane's
/// shell, and that process's command line. Asked when the user drops a
/// file or opens the pane menu, so it is not polled.
///
/// Only a Windows <c>ssh.exe</c> is visible this way. An ssh started
/// inside WSL is a Linux process and reads as "not ssh".
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class SshPaneProbe
{
    public static SshTarget? Find(int shellPid)
    {
        if (shellPid <= 0) return null;
        var snapshot = Snapshot();
        if (snapshot.Count == 0) return null;
        if (SshProcessFinder.FindSsh(snapshot, (uint)shellPid) is not { } sshPid) return null;
        return SshCommandLine.Parse(NtProcessInterop.TryGetCommandLine(sshPid));
    }

    private static List<ProcessEntry> Snapshot()
    {
        var entries = new List<ProcessEntry>(256);
        var snapshot = DWritePInvoke.CreateToolhelp32Snapshot(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0);
        if (snapshot == (HANDLE)new IntPtr(-1)) return entries;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!DWritePInvoke.Process32FirstW(snapshot, ref entry)) return entries;
            do
            {
                entries.Add(new ProcessEntry(
                    entry.th32ProcessID,
                    entry.th32ParentProcessID,
                    entry.szExeFile.ToString().TrimEnd('\0')));
            }
            while (DWritePInvoke.Process32NextW(snapshot, ref entry));
        }
        finally
        {
            DWritePInvoke.CloseHandle(snapshot);
        }
        return entries;
    }
}
