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
    public void AddBar_SetsAutomationNameFromNoticeTitle() =>
        Assert.Contains("AutomationProperties.SetName(bar, notice.Title)", ReadHostSource());

    /// <summary>
    /// The id as well as the name. shader-notice-fuzz.ps1 finds banners by
    /// AutomationId, because Name is the visible copy: reword it and a harness
    /// matching on copy reports "no banner", which reads as the feature
    /// working. Losing the id would only show up in a five-launch desktop run.
    /// </summary>
    [Fact]
    public void AddBar_SetsAutomationIdFromDedupKey()
    {
        var source = ReadHostSource();
        Assert.Contains("AutomationProperties.SetAutomationId(", source);
        Assert.Contains("\"Notice_\" + (string.IsNullOrEmpty(notice.DedupKey)", source);
    }

    private static string ReadHostSource()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("NotificationHost.xaml.cs", System.StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
