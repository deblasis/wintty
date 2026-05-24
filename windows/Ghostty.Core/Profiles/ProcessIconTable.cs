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
            ["pwsh.exe"]       = _ => new IconSpec.BrandKey("pwsh", null),
            ["powershell.exe"] = _ => new IconSpec.BrandKey("pwsh", null),
            ["cmd.exe"]        = _ => new IconSpec.BrandKey("cmd", null),
            ["bash.exe"]       = _ => new IconSpec.BrandKey("bash", null),
            ["fish.exe"]       = _ => new IconSpec.BrandKey("fish", null),
            ["nu.exe"]         = _ => new IconSpec.BrandKey("nu", null),
            ["zsh.exe"]        = _ => new IconSpec.BrandKey("zsh", null),
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
