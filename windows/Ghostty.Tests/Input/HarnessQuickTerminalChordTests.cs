using System;
using System.IO;
using System.Text.RegularExpressions;
using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

/// <summary>
/// The quick-terminal chord every seam harness stages
/// (windows/scripts/lib/wintty-process.ps1, Get-WinttyHarnessQuickTerminalKey)
/// has to be one the app parses. A value <see cref="QuickTerminalKeyChord.Parse"/>
/// rejects falls back to <see cref="QuickTerminalKeyChord.Default"/>, Ctrl+`,
/// which is exactly the user's hotkey the harness stages a chord to stay off.
/// </summary>
public class HarnessQuickTerminalChordTests
{
    [Fact]
    public void The_Harness_Chord_Parses_And_Is_Not_The_Default()
    {
        var lib = Path.Combine(RepoRoot(), "windows", "scripts", "lib", "wintty-process.ps1");
        var match = Regex.Match(
            File.ReadAllText(lib),
            @"function Get-WinttyHarnessQuickTerminalKey \{ return '([^']+)' \}");
        Assert.True(match.Success, $"Get-WinttyHarnessQuickTerminalKey not found in {lib}");

        var chord = QuickTerminalKeyChord.Parse(match.Groups[1].Value);
        Assert.NotNull(chord);
        Assert.NotEqual(QuickTerminalKeyChord.Default, chord!.Value);
        // F24: no key on a real keyboard, so no user binds it.
        Assert.Equal(0x87u, chord.Value.VirtualKey);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build.zig")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException("repo root not found");
    }
}
