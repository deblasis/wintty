using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Session;

/// <summary>
/// Isolated jumplist fuzz launched cmd.exe on the first window even
/// with <c>default-profile = pwsh</c>: TabManager seeded snapshot null,
/// and CreateForNewTab attached the snapshot after the factory had
/// already spawned the leaf. The snapshot has to reach the factory.
/// </summary>
public class ColdStartDefaultProfileTests
{
    [Fact]
    public void MainWindow_SeedsDefaultProfileIntoTabManager()
    {
        var main = ReadEmbedded("MainWindow.xaml.cs");
        Assert.Contains("SessionProfileResolver.ResolveDefault", main);
        Assert.Contains("initialSnapshot:", main);
    }

    [Fact]
    public void CreateForNewTab_DoesNotAttachAfterFactorySpawn()
    {
        var main = ReadEmbedded("MainWindow.xaml.cs");
        // Late AttachProfileSnapshot on ActiveTab after new MainWindow()
        // does not update TerminalControl.Snapshot; OnLoaded already
        // read the factory's null. The snapshot must go through the ctor.
        Assert.Contains("initialSnapshot: initialSnapshot", main);
        Assert.DoesNotContain(
            "window._tabManager.ActiveTab.AttachProfileSnapshot(initialSnapshot)",
            main);
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
