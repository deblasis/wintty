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
        Assert.Contains("ImeForwarder", code);
        Assert.Contains("_imeForwarder", code);
        Assert.Contains("_imeForwarder.OnCompositionEnded", code);
        Assert.Contains("_imeForwarder.OnKeyDown", code);
        Assert.Contains("BeginImeComposition(e.StartIndex)", code);
        Assert.Contains("TestSeamImeStarted(int startIndex) => BeginImeComposition(startIndex)", code);
        Assert.Contains("internal void TestSeamImeEnded()", code);
        // ... and it runs the same disarm the routed handler does.
        Assert.Contains("if (_imeForwarder.DiscardWasQuiet) _imeForwarder.ClearDiscard();", code);
        Assert.Contains("EndImeComposition();", code);
        Assert.Contains("UpdateImeSinkPosition();", code);
        Assert.Contains("ImeSink.Margin = new Thickness", code);
        Assert.Contains("_imeForwarder.ForwardedText", code);
        Assert.Contains("Composing = (byte)(_imeForwarder.Composing ? 1 : 0)", code);
        Assert.DoesNotContain("_imeComposing", code);
        Assert.DoesNotContain("_imePreeditTimer", code);
        Assert.Contains("UpdateSurfacePreedit", code);
        Assert.Contains("SurfacePreedit", code);
        Assert.Contains("ImeSink.Focus", code);
        Assert.DoesNotContain("TextCompositionStarted +=", code);

        var xamlPath = Path.Combine(
            RepoRoot(),
            "windows", "Ghostty", "Controls", "TerminalControl.xaml");
        var xaml = File.ReadAllText(xamlPath);
        Assert.Contains("x:Name=\"ImeSink\"", xaml);
        Assert.Contains("TextCompositionStarted=\"OnImeCompositionStarted\"", xaml);

        // The sink is a real TextBox and has the pane's focus, so the box's
        // own edit commands would paste the clipboard into it. The guard is a
        // deny list, so its members are pinned, not just its existence.
        Assert.Contains("PreviewKeyDown=\"OnImeSinkPreviewKeyDown\"", xaml);
        Assert.Contains("ContextRequested=\"OnImeSinkContextRequested\"", xaml);
        Assert.Contains("OnImeSinkPreviewKeyDown", code);
        Assert.Contains("OnImeSinkContextRequested", code);
        Assert.Contains("_imeHeldModifiers.Add(key)", code);
        Assert.DoesNotContain("IsReadOnly=\"True\"", xaml);

        foreach (var member in new[] { "VirtualKey.V", "VirtualKey.X", "VirtualKey.A", "VirtualKey.Y", "VirtualKey.Z", "VirtualKey.Insert" })
            Assert.Contains(member, code);

        // The arm that spends a key has to be retired with the close arm, or a
        // spent Backspace whose character never arrives eats the next one.
        Assert.Contains("_suppressNextCharacter = false", code);

        // The IME key set covers the Japanese conversion pair, not just the
        // Korean mode keys.
        // The literal and its comment: pinning the name alone would stay green
        // if the constant changed under it.
        foreach (var vk in new[]
                 {
                     "0x17 /* VK_JUNJA */", "0x18 /* VK_FINAL */",
                     "0x1C /* VK_CONVERT */", "0x1D /* VK_NONCONVERT */",
                     "0x1E /* VK_ACCEPT */", "0x1F /* VK_MODECHANGE */",
                 })
            Assert.Contains(vk, code);

        // Non-composition insertion goes through the paste policy; composition
        // text must keep SurfaceText so it pairs with its key.
        Assert.Contains("SendImePaste", code);
        Assert.Contains("SurfacePasteText", code);
        // The wiring of the round-4 fixes: three cancel sites discard the text
        // they cancelled, and the settle commit has one owner per delivering
        // path. A call site that disappears is a silent regression, so they are
        // counted rather than searched for.
        Assert.Equal(3, Count(code, "_imeForwarder.DiscardNextText("));
        Assert.Contains("if (_imeSettledPending) CommitImeText();", code);
        // The round-7 sites: three composition events disarm before the sync,
        // and a settle never commits a live composition.
        Assert.Equal(5, Count(code, "_imeForwarder.ClearDiscard("));
        Assert.Contains("(settling && !_imeForwarder.Composing)", code);
        Assert.Equal(2, Count(code, "CommitImeTextAsPaste"));
        Assert.Contains("CommitImeTextAsPaste", code);
        var native = File.ReadAllText(Path.Combine(
            RepoRoot(), "windows", "Ghostty", "Interop", "NativeMethods.cs"));
        Assert.Contains("ghostty_surface_paste_text", native);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
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
