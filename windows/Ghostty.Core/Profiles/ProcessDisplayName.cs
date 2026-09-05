using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using Ghostty.Core.Tabs;

namespace Ghostty.Core.Profiles;

/// <summary>
/// What a person calls the process behind an exe basename. The tab's icon
/// draws the brand; this is the word its tooltip uses. Keyed the same way
/// as <see cref="ProcessIconTable"/>, so the two agree on which exes are
/// shells.
/// </summary>
public static class ProcessDisplayName
{
    // wsl.exe is absent on purpose: its name carries the distro and is
    // composed in For.
    private static readonly FrozenDictionary<string, string> Names =
        new Dictionary<string, string>
        {
            ["pwsh.exe"] = "PowerShell",
            ["powershell.exe"] = "Windows PowerShell",
            ["cmd.exe"] = "Command Prompt",
            ["bash.exe"] = "Bash",
            ["zsh.exe"] = "Zsh",
            ["fish.exe"] = "Fish",
            ["nu.exe"] = "Nushell",
            ["python.exe"] = "Python",
            ["python3.exe"] = "Python",
            ["node.exe"] = "Node.js",
            ["deno.exe"] = "Deno",
            ["bun.exe"] = "Bun",
            ["cargo.exe"] = "Cargo",
            ["rustc.exe"] = "rustc",
            ["dotnet.exe"] = ".NET",
            ["go.exe"] = "Go",
            ["vim.exe"] = "Vim",
            ["nvim.exe"] = "Neovim",
            ["git.exe"] = "Git",
            ["ssh.exe"] = "SSH",
            ["docker.exe"] = "Docker",
            ["kubectl.exe"] = "kubectl",
            ["make.exe"] = "make",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // The interpreters a profile can launch a tab into. Anything else in the
    // first token of a command (winpty, env, a tool) is not the shell.
    private static readonly FrozenSet<string> Shells = new[]
    {
        "pwsh.exe", "powershell.exe", "cmd.exe", "bash.exe", "zsh.exe", "fish.exe", "nu.exe", "wsl.exe",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The name for <paramref name="exeBasename"/>: a WSL launch names its
    /// distro the way the WSL profile probe does ("WSL: Ubuntu-24.04"),
    /// a known exe its product name, and anything else its basename with
    /// the extension dropped, which is how a person says "vim", not
    /// "vim.exe". The distro token comes off a process's own command line,
    /// so one that is not plain text is not shown.
    /// </summary>
    public static string For(string exeBasename, string? commandLine)
    {
        if (exeBasename.Equals("wsl.exe", StringComparison.OrdinalIgnoreCase))
        {
            var distro = ProcessIconTable.ParseWslDistro(commandLine);
            return distro.Length == 0 || !TabLabel.IsPlain(distro) ? "WSL" : $"WSL: {distro}";
        }
        return Names.TryGetValue(exeBasename, out var name)
            ? name
            : Path.GetFileNameWithoutExtension(exeBasename);
    }

    /// <summary>
    /// The shell a profile <paramref name="command"/> launches, or null when
    /// its first token is not one: the caller then has nothing better than
    /// the profile's own name.
    /// </summary>
    public static string? Shell(string? command)
    {
        var exe = ProfileOrderResolver.CommandBasename(command);
        return exe is not null && Shells.Contains(exe) ? For(exe, command) : null;
    }
}
