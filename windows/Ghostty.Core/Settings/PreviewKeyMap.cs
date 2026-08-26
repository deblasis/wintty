namespace Ghostty.Core.Settings;

/// <summary>
/// Virtual-key mapping for preview surfaces: which raw keys the fake DOS
/// shell (<see cref="DosShellCore"/>) consumes, and which fall through to
/// be delivered as characters. Takes the Win32 VK code as an int because
/// Ghostty.Core has no WinUI reference; the values are the stable VK
/// codes behind Windows.System.VirtualKey.
///
/// Bare keys only, with Ctrl+C as the one chord, mirroring the website's
/// key handler: it ignores every other modified key. Shift+letter must
/// NOT map, or capitals could never be typed.
/// </summary>
internal static class PreviewKeyMap
{
    private const int VkEnter = 0x0D;
    private const int VkBack = 0x08;
    private const int VkUp = 0x26;
    private const int VkDown = 0x28;
    private const int VkEscape = 0x1B;
    private const int VkInsert = 0x2D;
    private const int VkC = 0x43;

    public static bool TryMap(int virtualKey, bool ctrl, bool shift, bool alt, out DosShellKey key)
    {
        if (ctrl && !shift && !alt && virtualKey == VkC)
        {
            key = DosShellKey.CtrlC;
            return true;
        }
        if (ctrl || shift || alt)
        {
            key = default;
            return false;
        }
        switch (virtualKey)
        {
            case VkEnter: key = DosShellKey.Enter; return true;
            case VkBack: key = DosShellKey.Backspace; return true;
            case VkUp: key = DosShellKey.Up; return true;
            case VkDown: key = DosShellKey.Down; return true;
            case VkEscape: key = DosShellKey.Escape; return true;
            case VkInsert: key = DosShellKey.Insert; return true;
            default: key = default; return false;
        }
    }
}
