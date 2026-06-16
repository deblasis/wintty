#if DEMO
using Ghostty.Core.Demo;
using Xunit;

namespace Ghostty.Tests.Demo;

public class DemoScriptParserTests
{
    [Fact]
    public void Parse_ReadsBeatsAndDefaults()
    {
        const string json = """
        {
          "title": "Tour",
          "typeDelayMs": 30,
          "beats": [
            { "type": "caption", "text": "Hi", "durationMs": 1500 },
            { "type": "action",  "key": "split_vertical" },
            { "type": "type",    "text": "ls", "enter": true }
          ]
        }
        """;

        var script = DemoScriptParser.Parse(json);

        Assert.Equal("Tour", script.Title);
        Assert.Equal(30, script.TypeDelayMs);
        Assert.Equal(700, script.BeatGapMs); // default retained
        Assert.Equal(3, script.Beats.Count);
        Assert.Equal("caption", script.Beats[0].Type);
        Assert.Equal(1500, script.Beats[0].DurationMs);
        Assert.True(script.Beats[2].Enter);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => DemoScriptParser.Parse("{ not json"));
    }

    [Fact]
    public void Parse_EmptyBeats_ReturnsEmptyList()
    {
        var script = DemoScriptParser.Parse("""{ "beats": [] }""");
        Assert.Empty(script.Beats);
    }
}
#endif
