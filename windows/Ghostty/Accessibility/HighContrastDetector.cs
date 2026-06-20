using System;
using System.Runtime.InteropServices;

namespace Ghostty.Accessibility;

/// <summary>
/// Detects whether Windows is in a High Contrast theme via
/// SystemParametersInfo(SPI_GETHIGHCONTRAST). Mirrors ScreenReaderDetector:
/// a blittable Win32 query with no WinRT thread affinity.
/// </summary>
internal static partial class HighContrastDetector
{
    private const uint SPI_GETHIGHCONTRAST = 0x0042;
    private const uint HCF_HIGHCONTRASTON = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIGHCONTRAST
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr lpszDefaultScheme;
    }

    // Returns a Win32 BOOL (int): nonzero on success. HIGHCONTRAST is
    // blittable, so passing it by ref is fine under DisableRuntimeMarshalling.
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(
        uint uiAction, uint uiParam, ref HIGHCONTRAST pvParam, uint fWinIni);

    public static bool IsActive()
    {
        var hc = new HIGHCONTRAST { cbSize = (uint)Marshal.SizeOf<HIGHCONTRAST>() };
        if (SystemParametersInfo(SPI_GETHIGHCONTRAST, hc.cbSize, ref hc, 0) == 0)
            return false;
        return (hc.dwFlags & HCF_HIGHCONTRASTON) != 0;
    }
}
