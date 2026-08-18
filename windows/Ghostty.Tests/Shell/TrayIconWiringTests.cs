using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Shell;

public class TrayIconWiringTests
{
    [Fact]
    public void App_Owns_TrayIconService()
    {
        var app = ReadEmbedded("App.xaml.cs");
        Assert.Contains("TrayIconService", app);
        Assert.Contains("ShowOrFocusWindowsFromTray", app);

        var tray = ReadEmbedded("TrayIconService.cs");
        Assert.Contains("Shell_NotifyIconW", tray);
        Assert.Contains("StaticWndProc", tray);
        Assert.Contains("s_wndProc", tray);
    }

    [Fact]
    public void InspectorWindow_CtrlWheel_Zooms_Before_Scroll()
    {
        var code = ReadEmbedded("InspectorWindow.xaml.cs");
        var wheel = code[code.IndexOf("OnPointerWheelChanged")..];
        Assert.Contains("VirtualKey.Control", wheel);
        Assert.Contains("InspectorZoomBy", wheel);
        Assert.Contains("InspectorMouseScroll", wheel);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
