using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Reads the security descriptor Windows actually holds on a named-pipe
/// handle.
///
/// The managed accessor for this (<c>PipeStreamAclExtensions.GetAccessControl</c>)
/// lives in a package this test project does not carry, and asking the kernel
/// is the stronger question anyway: the claim under test is what the OS
/// granted, not what .NET believes it asked for. Two calls, no state, Windows
/// only.
/// </summary>
internal static class PipeSecurityProbe
{
    private const int SE_KERNEL_OBJECT = 6;
    private const int OWNER_SECURITY_INFORMATION = 0x1;
    private const int GROUP_SECURITY_INFORMATION = 0x2;
    private const int DACL_SECURITY_INFORMATION = 0x4;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(
        IntPtr handle, int objectType, int securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(
        IntPtr securityDescriptor, uint revision, int securityInfo,
        out IntPtr sddl, out int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>The handle's owner, group and DACL, in SDDL.</summary>
    public static string Sddl(SafeHandle handle)
    {
        const int wanted = OWNER_SECURITY_INFORMATION
            | GROUP_SECURITY_INFORMATION
            | DACL_SECURITY_INFORMATION;

        var rc = GetSecurityInfo(
            handle.DangerousGetHandle(), SE_KERNEL_OBJECT, wanted,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            out var descriptor);
        if (rc != 0) throw new InvalidOperationException($"GetSecurityInfo failed: {rc}");

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(
                    descriptor, 1, wanted, out var sddl, out _))
            {
                throw new InvalidOperationException(
                    "ConvertSecurityDescriptorToStringSecurityDescriptor failed: "
                    + Marshal.GetLastWin32Error());
            }

            try
            {
                return Marshal.PtrToStringUni(sddl)!;
            }
            finally
            {
                LocalFree(sddl);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }
}
