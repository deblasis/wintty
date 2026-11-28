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
        // text leaves as key presses (see the commit test below).
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

    [Fact]
    public void Committed_Composition_Text_Leaves_As_A_Key_Not_A_Paste()
    {
        // ghostty_surface_text is the paste path when no key is pending, so
        // under bracketed paste every committed syllable left as a paste of
        // its own. A composition commit has no pending key -- its keys went
        // to the IME -- so it has to leave as a key press that carries the
        // text.
        var code = ReadEmbedded("TerminalControl.xaml.cs");
        var start = code.IndexOf("private void ForwardImeText(string text)", StringComparison.Ordinal);
        var end = code.IndexOf("private void UpdateSurfacePreedit", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var body = code[start..end];
        Assert.Contains("NativeMethods.SurfaceKey(_surface, key)", body);
        Assert.Contains("Action = GhosttyInputAction.Press", body);
        Assert.Contains("Composing = 0", body);

        // The two text exits ahead of it, in order: an insertion outside a
        // composition takes the paste policy, and a character typed on a
        // layout attaches to its own still-pending key press (a keyless
        // press would flush that key textless and emit a phantom CSI u
        // under the kitty protocol before the character).
        var pasteGate = body.IndexOf("if (_imeTextIsPaste)", StringComparison.Ordinal);
        var charsGate = body.IndexOf("if (_imeTextIsCharactersKey)", StringComparison.Ordinal);
        var keyExit = body.IndexOf("NativeMethods.SurfaceKey(_surface, key)", StringComparison.Ordinal);
        Assert.True(pasteGate >= 0 && charsGate > pasteGate && keyExit > charsGate);
        Assert.Contains("NativeMethods.SurfaceText(_surface, (IntPtr)p, (UIntPtr)chars.Length)", body);

        // The character exit is armed only around the character event's
        // own commit.
        Assert.Equal(1, Count(code, "_imeTextIsCharactersKey = true;"));
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
