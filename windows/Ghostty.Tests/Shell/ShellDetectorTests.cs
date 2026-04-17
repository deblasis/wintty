using Ghostty.Core.Shell;
using Xunit;

namespace Ghostty.Tests.Shell;

/// <summary>
/// Unit tests for <see cref="ShellDetector"/>. The classifier is pure
/// logic (no I/O); tests exhaustively cover the recognition table,
/// boundary inputs, case-insensitivity, and path-shape invariance.
/// </summary>
public sealed class ShellDetectorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Boundary_inputs_return_unknown_with_empty_name(string? input)
    {
        var result = ShellDetector.Detect(input!);

        Assert.Equal(ShellCapability.Unknown, result.Capability);
        Assert.Equal(string.Empty, result.NormalizedFileName);
        Assert.False(result.IsKnown);
    }

    [Theory]
    [InlineData("customshell.exe", "customshell.exe")]
    [InlineData("C:\\tools\\customshell.exe", "customshell.exe")]
    [InlineData(".\\customshell.exe", "customshell.exe")]
    [InlineData("mybin", "mybin")]
    [InlineData("C:\\Windows\\System32\\", "")]
    public void Unknown_binary_returns_unknown_with_normalized_name(string input, string expectedName)
    {
        var result = ShellDetector.Detect(input);

        Assert.Equal(ShellCapability.Unknown, result.Capability);
        Assert.Equal(expectedName, result.NormalizedFileName);
        Assert.False(result.IsKnown);
    }

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("wsl.exe")]
    [InlineData("ssh.exe")]
    [InlineData("bash.exe")]
    [InlineData("nu.exe")]
    [InlineData("zsh.exe")]
    [InlineData("fish.exe")]
    [InlineData("elvish.exe")]
    [InlineData("xonsh.exe")]
    public void Vt_aware_shells_classify_as_vt_aware(string fileName)
    {
        var result = ShellDetector.Detect(fileName);

        Assert.Equal(ShellCapability.VtAware, result.Capability);
        Assert.Equal(fileName, result.NormalizedFileName);
        Assert.True(result.IsKnown);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    public void Console_api_shells_classify_as_console_api(string fileName)
    {
        var result = ShellDetector.Detect(fileName);

        Assert.Equal(ShellCapability.ConsoleApi, result.Capability);
        Assert.Equal(fileName, result.NormalizedFileName);
        Assert.True(result.IsKnown);
    }

    [Theory]
    [InlineData("PWSH.EXE")]
    [InlineData("Pwsh.Exe")]
    [InlineData("pwsh.EXE")]
    [InlineData("CMD.EXE")]
    [InlineData("Cmd.Exe")]
    public void Classification_is_case_insensitive_and_normalizes_to_lowercase(string fileName)
    {
        var result = ShellDetector.Detect(fileName);

        Assert.True(result.IsKnown);
        Assert.Equal(fileName.ToLowerInvariant(), result.NormalizedFileName);
    }

    [Theory]
    [InlineData("pwsh.exe")]
    [InlineData("C:\\Program Files\\PowerShell\\7\\pwsh.exe")]
    [InlineData(".\\pwsh.exe")]
    [InlineData("..\\bin\\pwsh.exe")]
    [InlineData("C:/Program Files/PowerShell/7/pwsh.exe")]
    [InlineData("\\\\server\\share\\pwsh.exe")]
    public void Path_shape_does_not_affect_classification(string path)
    {
        var result = ShellDetector.Detect(path);

        Assert.Equal(ShellCapability.VtAware, result.Capability);
        Assert.Equal("pwsh.exe", result.NormalizedFileName);
        Assert.True(result.IsKnown);
    }

    [Theory]
    [InlineData("powershell_ise.exe")]
    [InlineData("git-bash.exe")]
    [InlineData("ubuntu.exe")]
    [InlineData("debian.exe")]
    [InlineData("notepad.exe")]
    public void Unsupported_binaries_classify_as_unknown(string fileName)
    {
        var result = ShellDetector.Detect(fileName);

        Assert.Equal(ShellCapability.Unknown, result.Capability);
        Assert.Equal(fileName, result.NormalizedFileName);
        Assert.False(result.IsKnown);
    }

}
