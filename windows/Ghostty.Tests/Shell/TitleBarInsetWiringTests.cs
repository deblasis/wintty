using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Horizontal tab-strip footer used a hardcoded 146 DIP MinWidth.
/// Caption inset must follow AppWindow.TitleBar.RightInset like
/// the vertical title bar already does.
/// </summary>
public class TitleBarInsetWiringTests
{
    [Fact]
    public void HorizontalDragRegion_FollowsRightInset()
    {
        var coord = ReadEmbedded("TitleBarCoordinator.cs");
        Assert.Contains("RightInset", coord);
        Assert.Contains("drag.MinWidth = dip", coord);
        Assert.DoesNotContain("TODO(titlebar)", coord);
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
