using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Panes;

/// <summary>
/// The pane context menu is taller than a 580px-tall window. WinUI clips
/// MenuFlyout to the root by default, so Change Tab Title / Close Pane
/// vanish off the bottom. ShouldConstrainToRootBounds=false lets the
/// flyout overflow the hwnd.
/// </summary>
public class PaneContextMenuFlyoutTests
{
    [Fact]
    public void Builder_DoesNotConstrainFlyoutToRootBounds()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("PaneContextMenuBuilder.cs", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("ShouldConstrainToRootBounds = false", source);
    }
}
