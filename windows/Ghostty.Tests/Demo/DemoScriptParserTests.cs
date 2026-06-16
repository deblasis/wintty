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

    [Fact]
    public void Resolve_PrefersEnvPathWhenFileExists()
    {
        var path = DemoScriptParser.ResolveScriptPath(
            envValue: @"C:\scripts\my.json",
            exeDir: @"C:\app",
            configDir: @"C:\cfg",
            fileExists: p => p == @"C:\scripts\my.json");

        Assert.Equal(@"C:\scripts\my.json", path);
    }

    [Fact]
    public void Resolve_FallsBackToExeAdjacent()
    {
        var path = DemoScriptParser.ResolveScriptPath(
            envValue: null,
            exeDir: @"C:\app",
            configDir: @"C:\cfg",
            fileExists: p => p == @"C:\app\demo.json");

        Assert.Equal(@"C:\app\demo.json", path);
    }

    [Fact]
    public void Resolve_FallsBackToConfigDir()
    {
        var path = DemoScriptParser.ResolveScriptPath(
            envValue: "1", // present but not a file path
            exeDir: @"C:\app",
            configDir: @"C:\cfg",
            fileExists: p => p == @"C:\cfg\wintty\demo.json");

        Assert.Equal(@"C:\cfg\wintty\demo.json", path);
    }

    [Fact]
    public void Resolve_ReturnsNullWhenNothingExists()
    {
        var path = DemoScriptParser.ResolveScriptPath(
            envValue: "1",
            exeDir: @"C:\app",
            configDir: @"C:\cfg",
            fileExists: _ => false);

        Assert.Null(path);
    }
}
#endif
