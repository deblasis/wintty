using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Tabs;

/// <summary>
/// TabHost and VerticalTabHost used to copy a hardcoded
/// confirm-close-multi-pane=true. The shared helper plus the
/// upstream key must stay the only close-confirm policy.
/// </summary>
public class TabCloseConfirmationWiringTests
{
    [Fact]
    public void Hosts_DelegateToSharedHelper()
    {
        var tabHost = ReadEmbedded(@"Tabs\TabHost.xaml.cs");
        var vertical = ReadEmbedded("VerticalTabHost.xaml.cs");
        var helper = ReadEmbedded("TabCloseConfirmation.cs");

        Assert.Contains("TabCloseConfirmation.RequestAsync", tabHost);
        Assert.Contains("TabCloseConfirmation.RequestAsync", vertical);
        Assert.DoesNotContain("const bool confirmCloseMultiPane", tabHost);
        Assert.DoesNotContain("const bool confirmCloseMultiPane", vertical);
        Assert.Contains("ConfirmCloseSurfaceParser", helper);
        Assert.DoesNotContain("const bool confirmCloseMultiPane", helper);
    }

    [Fact]
    public void TitleBarCoordinator_GuardsLiveTitleAgainstClosedTab()
    {
        var src = ReadEmbedded("TitleBarCoordinator.cs");
        Assert.Contains("LiveTitleGuard.Accepts", src);
        Assert.Contains("_titleHookedTab", src);
    }

    private static string ReadEmbedded(string suffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
