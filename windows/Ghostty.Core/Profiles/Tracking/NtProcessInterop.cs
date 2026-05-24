using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Ghostty.Core.Profiles.Tracking;

/// <summary>
/// Reads another process's command line via the documented
/// <c>NtQueryInformationProcess</c>(ProcessCommandLineInformation, class 60)
/// path. Available since Windows 8.1 and far simpler than the alternative
/// PEB + ReadProcessMemory walk: one syscall, returns a UNICODE_STRING
/// header followed inline by the wide-char data.
///
/// CsWin32's ntdll metadata coverage is thin (class 60 in particular is
/// not surfaced as a named PROCESSINFOCLASS constant in older Win32
/// metadata packs), so we keep ntdll's surface as a hand-written DllImport
/// here and only borrow OpenProcess / CloseHandle from CsWin32's
/// <c>DWritePInvoke</c> generated class.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static partial class NtProcessInterop
{
    // ProcessInformationClass value documented on MSDN for retrieving the
    // target process's command line in one call. Added in Windows 8.1.
    private const int ProcessCommandLineInformation = 60;

    // NTSTATUS sentinel: the initial probing call passes a zero-length
    // buffer to discover the required size, which legitimately fails with
    // this status. Treat it as "size known, retry" rather than an error.
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    // LibraryImport (not DllImport) so the source-generated stub is
    // forward-compatible with [assembly: DisableRuntimeMarshalling] on
    // Ghostty.Core. The "out uint" parameter is blittable and needs no
    // marshalling, so the generated stub is a thin wrapper.
    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        uint ProcessInformationLength,
        out uint ReturnLength);

    // UNICODE_STRING is laid out as { ushort Length, ushort MaximumLength,
    // IntPtr Buffer } on every Windows architecture. The Buffer field
    // points at the inline wide-char data that follows the header in the
    // returned block. Length is in BYTES, not chars.
    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    /// <summary>
    /// Opens <paramref name="pid"/> with PROCESS_QUERY_LIMITED_INFORMATION
    /// (sufficient for class 60), queries the command line, and closes the
    /// handle. Returns null on any failure - exited process, access denied
    /// (e.g. protected processes like csrss.exe), or empty cmdline.
    /// </summary>
    public static string? TryGetCommandLine(uint pid)
    {
        var handle = DWritePInvoke.OpenProcess(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            false,
            pid);
        if (handle == default(HANDLE) || handle.IsNull) return null;
        try
        {
            return GetCommandLine(handle);
        }
        finally
        {
            DWritePInvoke.CloseHandle(handle);
        }
    }

    /// <summary>
    /// Queries class 60 on an already-open handle. Two-call pattern: probe
    /// for required length, allocate, then read.
    /// </summary>
    private static unsafe string? GetCommandLine(HANDLE handle)
    {
        // HANDLE.Value is a void* with useSafeHandles=false; convert to
        // IntPtr for the hand-written p/invoke signature.
        var ntHandle = (IntPtr)handle.Value;

        // Probe: zero-length call returns STATUS_INFO_LENGTH_MISMATCH and
        // sets ReturnLength to the buffer size we need.
        var status = NtQueryInformationProcess(
            ntHandle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var required);
        if (status != STATUS_INFO_LENGTH_MISMATCH && status != 0) return null;
        if (required == 0) return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            status = NtQueryInformationProcess(
                ntHandle, ProcessCommandLineInformation, buffer, required, out required);
            if (status != 0) return null;

            var u = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            if (u.Length == 0 || u.Buffer == IntPtr.Zero) return null;
            // Length is in bytes; PtrToStringUni wants char count.
            return Marshal.PtrToStringUni(u.Buffer, u.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
