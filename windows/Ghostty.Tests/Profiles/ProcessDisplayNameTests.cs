using Ghostty.Core.Profiles;
using Xunit;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// What a person calls the process behind an exe basename. The icon says
/// which interpreter a tab runs; this is the word its tooltip uses.
/// </summary>
public class ProcessDisplayNameTests
{
    [Theory]
    [InlineData("pwsh.exe", null, "PowerShell")]
    [InlineData("PWSH.EXE", null, "PowerShell")]
    [InlineData("powershell.exe", null, "Windows PowerShell")]
    [InlineData("cmd.exe", null, "Command Prompt")]
    [InlineData("nu.exe", null, "Nushell")]
    [InlineData("wsl.exe", "wsl.exe -d Ubuntu-24.04", "WSL: Ubuntu-24.04")]
    [InlineData("wsl.exe", "wsl.exe --distribution=Debian", "WSL: Debian")]
    [InlineData("wsl.exe", "wsl.exe", "WSL")]
    [InlineData("wsl.exe", null, "WSL")]
    [InlineData("wsl.exe", "wsl.exe -d Ubu\nntu", "WSL")]
    [InlineData("nvim.exe", null, "Neovim")]
    [InlineData("node.exe", null, "Node.js")]
    public void For_IsTheNameAPersonUses(string exe, string? commandLine, string expected)
        => Assert.Equal(expected, ProcessDisplayName.For(exe, commandLine));

    [Theory]
    [InlineData("hx.exe", "hx")]
    [InlineData("SomeTool.EXE", "SomeTool")]
    [InlineData("lazygit", "lazygit")]
    public void For_FallsBackToTheBasename_WithoutItsExtension(string exe, string expected)
        => Assert.Equal(expected, ProcessDisplayName.For(exe, null));

    [Theory]
    [InlineData(@"""C:\Program Files\PowerShell\7\pwsh.exe""", "PowerShell")]
    [InlineData(@"C:\Windows\system32\cmd.exe", "Command Prompt")]
    [InlineData("pwsh", "PowerShell")]
    [InlineData("wsl.exe -d Ubuntu", "WSL: Ubuntu")]
    [InlineData(@"C:\Windows\System32\wsl.exe ~ -d Ubuntu", "WSL: Ubuntu")]
    [InlineData(@"""C:\Program Files\Git\bin\bash.exe"" --login -i", "Bash")]
    public void Shell_NamesTheInterpreterAProfileLaunches(string command, string expected)
        => Assert.Equal(expected, ProcessDisplayName.Shell(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\msys64\usr\bin\winpty.exe C:\msys64\usr\bin\bash.exe --login -i")]
    [InlineData("az interactive")]
    [InlineData("vim.exe")]
    public void Shell_IsNull_WhenTheFirstTokenIsNotAShell(string? command)
        => Assert.Null(ProcessDisplayName.Shell(command));
}
