using System.IO;
using System.Linq;
using Ghostty.Core.Config;
using Xunit;

namespace Ghostty.Tests.Config;

// Runtime proof of the custom-shader write against the real-world file
// shape that stopped picking up selections: several commented
// custom-shader lines and no active one.
public class RepeatableWriteTests
{
    [Fact]
    public void SettingAValueAfterAllCommentedLinesProducesOneActiveLine()
    {
        string[] lines =
        {
            "command = pwsh.exe",
            "# custom-shader = C:/old/path/crt.glsl",
            "# custom-shader = ",
            "# custom-shader = C:/other/cursor_tail.glsl",
            "theme = Catppuccin Mocha",
        };

        var result = ConfigFileParser.SetRepeatableValues(
            lines, "custom-shader", new[] { "C:/new/crt.glsl" });

        var active = result.Where(l =>
        {
            var p = ConfigLine.Parse(l);
            return !p.IsComment && p.Key == "custom-shader";
        }).ToList();
        Assert.Single(active);
        Assert.Contains("C:/new/crt.glsl", active[0]);
    }

    [Fact]
    public void ClearingWritesNoActiveLine()
    {
        string[] lines =
        {
            "custom-shader = C:/old/crt.glsl",
            "theme = Catppuccin Mocha",
        };
        var result = ConfigFileParser.SetRepeatableValues(
            lines, "custom-shader", new string[] { });
        Assert.DoesNotContain(result, l =>
        {
            var p = ConfigLine.Parse(l);
            return !p.IsComment && p.Key == "custom-shader";
        });
    }
}
