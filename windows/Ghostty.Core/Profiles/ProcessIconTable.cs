using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Maps a foreground process's executable basename (and, when relevant,
/// its command line) to the <see cref="IconSpec"/> a tab should show
/// while that process is in the foreground. Pure dictionary, no
/// platform deps; the runtime tracker passes data in.
///
/// Unknown executables return null so the caller can revert to the
/// tab's profile icon rather than show a placeholder.
/// </summary>
public static class ProcessIconTable
{
    private static readonly FrozenDictionary<string, Func<string?, IconSpec>> _table =
        new Dictionary<string, Func<string?, IconSpec>>(StringComparer.OrdinalIgnoreCase)
        {
            // Shells
            ["pwsh.exe"]       = _ => new IconSpec.BrandKey("pwsh", null),
            ["powershell.exe"] = _ => new IconSpec.BrandKey("pwsh", null),
            ["cmd.exe"]        = _ => new IconSpec.BrandKey("cmd", null),
            ["bash.exe"]       = _ => new IconSpec.BrandKey("bash", null),
            ["fish.exe"]       = _ => new IconSpec.BrandKey("fish", null),
            ["nu.exe"]         = _ => new IconSpec.BrandKey("nu", null),
            ["zsh.exe"]        = _ => new IconSpec.BrandKey("zsh", null),

            // Languages
            ["python.exe"]     = _ => new IconSpec.BrandKey("python", null),
            ["python3.exe"]    = _ => new IconSpec.BrandKey("python", null),
            ["node.exe"]       = _ => new IconSpec.BrandKey("node", null),
            ["deno.exe"]       = _ => new IconSpec.BrandKey("deno", null),
            ["bun.exe"]        = _ => new IconSpec.BrandKey("bun", null),
            ["cargo.exe"]      = _ => new IconSpec.BrandKey("rust", null),
            ["rustc.exe"]      = _ => new IconSpec.BrandKey("rust", null),
            ["dotnet.exe"]     = _ => new IconSpec.BrandKey("dotnet", null),
            ["go.exe"]         = _ => new IconSpec.BrandKey("go", null),

            // Tools
            ["vim.exe"]        = _ => new IconSpec.BrandKey("vim", null),
            ["nvim.exe"]       = _ => new IconSpec.BrandKey("vim", null),
            ["git.exe"]        = _ => new IconSpec.BrandKey("git", null),
            ["ssh.exe"]        = _ => new IconSpec.BrandKey("ssh", null),
            ["docker.exe"]     = _ => new IconSpec.BrandKey("docker", null),
            ["kubectl.exe"]    = _ => new IconSpec.BrandKey("k8s", null),
            ["make.exe"]       = _ => new IconSpec.BrandKey("make", null),

            // System monitors (all map to the same generic glyph)
            ["htop.exe"]       = _ => new IconSpec.BrandKey("monitor", null),
            ["btop.exe"]       = _ => new IconSpec.BrandKey("monitor", null),
            ["top.exe"]        = _ => new IconSpec.BrandKey("monitor", null),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IconSpec? TryMap(string exeBasename) =>
        TryMap(exeBasename, commandLine: null);

    public static IconSpec? TryMap(string exeBasename, string? commandLine)
    {
        if (string.IsNullOrEmpty(exeBasename)) return null;
        return _table.TryGetValue(exeBasename, out var factory)
            ? factory(commandLine)
            : null;
    }
}
