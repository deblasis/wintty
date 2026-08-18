using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Input;

public class ImeCompositionWiringTests
{
    [Fact]
    public void TerminalControl_Uses_ImeSink_TextBox_And_SurfacePreedit()
    {
        var code = ReadEmbedded("TerminalControl.xaml.cs");
        Assert.Contains("ImeSink", code);
        Assert.Contains("OnImeCompositionStarted", code);
        Assert.Contains("OnImeCompositionChanged", code);
        Assert.Contains("OnImeCompositionEnded", code);
        Assert.Contains("_imeComposing", code);
        Assert.Contains("UpdateSurfacePreedit", code);
        Assert.Contains("SurfacePreedit", code);
        Assert.Contains("Composing = (byte)(_imeComposing ? 1 : 0)", code);
        Assert.Contains("ImeSink.Focus", code);
        Assert.DoesNotContain("TextCompositionStarted +=", code);

        var xamlPath = Path.Combine(
            RepoRoot(),
            "windows", "Ghostty", "Controls", "TerminalControl.xaml");
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains("x:Name=\"ImeSink\"", xaml);
        Assert.Contains("TextCompositionStarted=\"OnImeCompositionStarted\"", xaml);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
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
