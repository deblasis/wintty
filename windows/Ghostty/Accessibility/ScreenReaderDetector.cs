using System.Runtime.InteropServices;

namespace Ghostty.Accessibility;

/// <summary>
/// Reports whether the OS has a screen reader active, via the SPI_GETSCREENREADER
/// system parameter. Used to keep output announcements completely inert unless an
/// assistive technology is present. BOOL is marshalled as int per the project's
/// DisableRuntimeMarshalling convention.
/// </summary>
internal static partial class ScreenReaderDetector
{
    private const uint SPI_GETSCREENREADER = 0x0046;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    public static bool IsRunning()
    {
        int enabled = 0;
        return SystemParametersInfo(SPI_GETSCREENREADER, 0, ref enabled, 0) != 0 && enabled != 0;
    }
}
