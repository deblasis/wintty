using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Ghostty.Tests.Commands;

/// <summary>
/// Scrollback search is Ctrl+Shift+F, which is also a Cursor chord, so the
/// fuzz harness cannot SendInput it. The palette must expose OpenSearch so
/// a real user (and the harness) can open the bar without that chord.
/// </summary>
public class PaletteOpenSearchTests
{
    [Fact]
    public void BuiltInCommandSource_OffersOpenSearch()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("BuiltInCommandSource.cs", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var source = reader.ReadToEnd();
        Assert.Contains("PaneAction.OpenSearch", source);
        // ModeLabel.Text defaults to "Search". A command titled the same
        // string makes Find-Name / narrator hit the header TextBlock, not
        // the ListItem — the fuzz click landed on the mode label and the
        // search bar never opened.
        Assert.Contains("PaneAction.OpenSearch, \"Search Scrollback\"", source);
    }
}
