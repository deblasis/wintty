using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ghostty.Core.Version;

/// <summary>
/// Build information about the loaded libghostty.dll. Sourced from
/// the <c>ghostty_build_info</c> FFI export.
/// </summary>
public sealed record LibGhosttyBuildInfo(
    string Version,
    string VersionString,
    string Commit,
    string Channel,
    string ZigVersion,
    string BuildMode);

/// <summary>
/// P/Invoke bridge for the ghostty_build_info FFI. NativeAOT-friendly:
/// blittable struct of pointers, no delegates, no marshaller attributes
/// beyond LibraryImport's defaults.
/// </summary>
public static partial class LibGhosttyBuildInfoBridge
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public nint Version;
        public nint VersionString;
        public nint Commit;
        public nint Channel;
        public nint ZigVersion;
        public nint BuildMode;
    }

    [LibraryImport("ghostty", EntryPoint = "ghostty_build_info")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void GhosttyBuildInfo(out Native info);

    /// <summary>
    /// Read build info from libghostty. The native strings have static
    /// lifetime so we marshal them into managed copies and return.
    /// </summary>
    public static LibGhosttyBuildInfo Read()
    {
        GhosttyBuildInfo(out var native);
        return new LibGhosttyBuildInfo(
            Version:       Marshal.PtrToStringUTF8(native.Version) ?? string.Empty,
            VersionString: Marshal.PtrToStringUTF8(native.VersionString) ?? string.Empty,
            Commit:        Marshal.PtrToStringUTF8(native.Commit) ?? string.Empty,
            Channel:       Marshal.PtrToStringUTF8(native.Channel) ?? string.Empty,
            ZigVersion:    Marshal.PtrToStringUTF8(native.ZigVersion) ?? string.Empty,
            BuildMode:     Marshal.PtrToStringUTF8(native.BuildMode) ?? string.Empty);
    }
}
