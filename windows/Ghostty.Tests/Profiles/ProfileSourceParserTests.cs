using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

public sealed class ProfileSourceParserTests
{
    [Fact]
    public void Parse_SingleProfile_AllRequiredKeys()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            """;

        var result = ProfileSourceParser.Parse(config);

        Assert.Single(result.Profiles);
        var p = result.Profiles["pwsh"];
        Assert.Equal("pwsh", p.Id);
        Assert.Equal("PowerShell", p.Name);
        Assert.Equal("pwsh.exe", p.Command);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Parse_MultipleProfiles_AllReturned()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.cmd.name = Command Prompt
            profile.cmd.command = cmd.exe
            """;

        var result = ProfileSourceParser.Parse(config);

        Assert.Equal(2, result.Profiles.Count);
        Assert.True(result.Profiles.ContainsKey("pwsh"));
        Assert.True(result.Profiles.ContainsKey("cmd"));
    }

    [Fact]
    public void Parse_VisualOverrides_PopulatedOnVisuals()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.pwsh.theme = GruvboxDark
            profile.pwsh.background-opacity = 0.85
            profile.pwsh.font-family = CaskaydiaCove Nerd Font
            profile.pwsh.font-size = 13.5
            profile.pwsh.cursor-style = block
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["pwsh"];

        Assert.Equal("GruvboxDark", p.Visuals.Theme);
        Assert.Equal(0.85, p.Visuals.BackgroundOpacity);
        Assert.Equal("CaskaydiaCove Nerd Font", p.Visuals.FontFamily);
        Assert.Equal(13.5, p.Visuals.FontSize);
        Assert.Equal("block", p.Visuals.CursorStyle);
    }

    [Fact]
    public void Parse_OptionalKeys_PopulateOnDef()
    {
        const string config = """
            profile.wsl.name = WSL Ubuntu
            profile.wsl.command = wsl -d Ubuntu
            profile.wsl.working-directory = /home/me
            profile.wsl.tab-title = Ubuntu
            profile.wsl.hidden = false
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["wsl"];

        Assert.Equal("/home/me", p.WorkingDirectory);
        Assert.Equal("Ubuntu", p.TabTitle);
        Assert.False(p.Hidden);
    }

    [Fact]
    public void Parse_IgnoresNonProfileLines()
    {
        const string config = """
            font-family = Cascadia Code
            theme = GruvboxDark
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            background-opacity = 0.9
            """;

        var result = ProfileSourceParser.Parse(config);

        Assert.Single(result.Profiles);
        Assert.True(result.Profiles.ContainsKey("pwsh"));
    }

    [Fact]
    public void Parse_HiddenTrueInterpretedCorrectly()
    {
        const string config = """
            profile.wsl-debian.name = Debian
            profile.wsl-debian.command = wsl -d Debian
            profile.wsl-debian.hidden = true
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["wsl-debian"];

        Assert.True(p.Hidden);
    }

    [Fact]
    public void Parse_MissingCommand_DropsProfileAndWarns()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            """;

        var result = ProfileSourceParser.Parse(config);

        Assert.Empty(result.Profiles);
        Assert.Single(result.Warnings);
        Assert.Contains("pwsh", result.Warnings[0]);
        Assert.Contains("command", result.Warnings[0]);
    }

    [Fact]
    public void Parse_MissingName_DropsProfileAndWarns()
    {
        const string config = """
            profile.pwsh.command = pwsh.exe
            """;

        var result = ProfileSourceParser.Parse(config);

        Assert.Empty(result.Profiles);
        Assert.Single(result.Warnings);
        Assert.Contains("name", result.Warnings[0]);
    }

    [Fact]
    public void Parse_DuplicateKeys_LastWins()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.pwsh.command = pwsh-preview.exe
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["pwsh"];
        Assert.Equal("pwsh-preview.exe", p.Command);
    }

    [Theory]
    [InlineData("PWSH")]
    [InlineData("pwsh-7")]
    [InlineData("wsl-debian-2204")]
    public void Parse_IdNormalization_LowercasedAndKept(string id)
    {
        var config = $"profile.{id}.name = X\nprofile.{id}.command = x\n";

        var result = ProfileSourceParser.Parse(config);

        Assert.Single(result.Profiles);
        Assert.True(result.Profiles.ContainsKey(id.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("with space")]
    [InlineData("under_score")]
    [InlineData("dot.in.id")]
    public void Parse_InvalidIdFormat_LineIgnored(string id)
    {
        var config = $"profile.{id}.name = X\nprofile.{id}.command = x\n";

        var result = ProfileSourceParser.Parse(config);

        Assert.Empty(result.Profiles);
    }

    [Fact]
    public void Parse_CRLFLineEndings_ParsedSameAsLF()
    {
        const string config = "profile.pwsh.name = PowerShell\r\nprofile.pwsh.command = pwsh.exe\r\n";

        var result = ProfileSourceParser.Parse(config);

        Assert.Single(result.Profiles);
        Assert.Equal("PowerShell", result.Profiles["pwsh"].Name);
    }

    [Fact]
    public void Parse_BomPrefix_StrippedFromFirstLine()
    {
        var config = "\uFEFFprofile.pwsh.name = PowerShell\nprofile.pwsh.command = pwsh.exe\n";

        var result = ProfileSourceParser.Parse(config);

        Assert.Single(result.Profiles);
    }

    [Fact]
    public void Parse_CommentLines_Ignored()
    {
        const string config = """
            # this is a comment
            profile.pwsh.name = PowerShell
            # another comment between keys
            profile.pwsh.command = pwsh.exe
            """;

        var result = ProfileSourceParser.Parse(config);
        Assert.Single(result.Profiles);
    }

    [Fact]
    public void Parse_BlankLines_Ignored()
    {
        const string config = """

            profile.pwsh.name = PowerShell

            profile.pwsh.command = pwsh.exe

            """;

        var result = ProfileSourceParser.Parse(config);
        Assert.Single(result.Profiles);
    }

    [Fact]
    public void Parse_WhitespaceAroundEquals_Tolerated()
    {
        const string config = """
            profile.pwsh.name=PowerShell
            profile.pwsh.command   =   pwsh.exe
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["pwsh"];
        Assert.Equal("PowerShell", p.Name);
        Assert.Equal("pwsh.exe", p.Command);
    }

    [Fact]
    public void Parse_UnknownSubKey_DroppedSilently()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.pwsh.invented-key = whatever
            """;

        var result = ProfileSourceParser.Parse(config);
        Assert.Single(result.Profiles);
    }

    [Fact]
    public void Parse_IconKey_PathVariant()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.pwsh.icon = C:\icons\pwsh.ico
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["pwsh"];
        var path = Assert.IsType<IconSpec.Path>(p.Icon);
        Assert.Equal(@"C:\icons\pwsh.ico", path.FilePath);
    }

    [Fact]
    public void Parse_IconKey_Mdl2TokenVariant()
    {
        const string config = """
            profile.pwsh.name = PowerShell
            profile.pwsh.command = pwsh.exe
            profile.pwsh.icon = mdl2:E756
            """;

        var p = ProfileSourceParser.Parse(config).Profiles["pwsh"];
        var token = Assert.IsType<IconSpec.Mdl2Token>(p.Icon);
        Assert.Equal(0xE756, token.CodePoint);
    }
}
