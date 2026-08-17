using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// Profile SettingsCards put an icon picker plus two labeled ToggleSwitches
/// in the Control slot. A horizontal stack plus a wide Auto column truncates
/// "Track foreground process". Vertical stacks the controls so labels fit.
/// </summary>
public class ProfileRowLayoutTests
{
    [Fact]
    public void BuildRow_StacksControlsVertically()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("ProfilesPage.xaml.cs", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("Orientation = Orientation.Vertical", source);
    }
}
