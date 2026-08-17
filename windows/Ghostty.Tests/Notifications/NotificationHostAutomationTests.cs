using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Notifications;

/// <summary>
/// InfoBar.Title is visual. Without SetName, Find-Name 'Inspector unavailable'
/// misses the banner even when Toggle Inspector showed it.
/// </summary>
public class NotificationHostAutomationTests
{
    [Fact]
    public void AddBar_SetsAutomationNameFromNoticeTitle()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("NotificationHost.xaml.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("AutomationProperties.SetName(bar, notice.Title)", source);
    }
}
