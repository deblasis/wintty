using System;
using System.IO;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Thin helper around SHGetFileInfoW + GetIconInfo to extract a small
/// icon from an .exe and encode it as a 16x16 PNG. Separate class so
/// the P/Invoke surface is explicit and the non-Windows build graph
/// still compiles WindowsIconResolver (the Windows-only code paths are
/// guarded by OperatingSystem.IsWindows()).
///
/// The generated CsWin32 class is <c>DWritePInvoke</c> (see
/// NativeMethods.json "className") to avoid colliding with the
/// Windows.Win32.PInvoke class used elsewhere in the project. All
/// SHGetFileInfoW / DestroyIcon calls go through that symbol.
/// </summary>
[SupportedOSPlatform("windows6.0.6000")]
internal static class Win32IconExtractor
{
    public static byte[] ExtractAsPng16(string exePath)
    {
        var info = default(SHFILEINFOW);
        var flags = SHGFI_FLAGS.SHGFI_ICON | SHGFI_FLAGS.SHGFI_SMALLICON;
        // Friendly overload: takes string + ref SHFILEINFOW and computes
        // cbFileInfo internally from the ref. No unsafe needed at the
        // call site; CsWin32 handles the PCWSTR marshaling and the
        // sizeof transparently.
        var ret = DWritePInvoke.SHGetFileInfo(exePath, 0, ref info, flags);
        if (ret == 0 || info.hIcon.IsNull)
            throw new InvalidOperationException($"SHGetFileInfo returned no icon for '{exePath}'");

        try
        {
            return IconHandleToPng16(info.hIcon);
        }
        finally
        {
            DWritePInvoke.DestroyIcon(info.hIcon);
        }
    }

    // System.Drawing.Icon is available on net10.0 via the
    // System.Drawing.Common package. Trimming/AOT analyzers flag the
    // Icon.FromHandle -> Bitmap -> PNG-encode chain because GDI+
    // registration uses reflection; the suppressions below keep the
    // warnings local to this path. Phase 2 should swap this for a
    // direct GetIconInfo + WIC encoder path and drop the dependency.
    // TODO: Phase 2 - migrate to direct WIC encoder to drop System.Drawing.Common.
#pragma warning disable IL2026, IL3050, CA1416
    // Must be unsafe: HICON.Value is a void* (CsWin32-generated), so the
    // cast to IntPtr for Icon.FromHandle is a pointer-to-nint conversion.
    private static unsafe byte[] IconHandleToPng16(HICON hIcon)
    {
        using var icon = System.Drawing.Icon.FromHandle((IntPtr)hIcon.Value);
        using var bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }
#pragma warning restore IL2026, IL3050, CA1416
}
