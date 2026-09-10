using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Layer 3 of the config-isolation rule: no script or recipe under
/// windows/scripts or the justfile may launch the app without either
/// arming <c>WINTTY_TEST_CONFIG</c> or going through an isolated-config
/// helper.
///
/// Layers 1 and 2 are runtime enforcement (the app's guard refuses a
/// non-temp config root loudly). This one is review-time enforcement: a
/// new harness that forgets the env var fails <c>just test-win</c> with
/// the file and line, before anyone spends a desktop lane discovering it.
/// The check is textual on purpose: PowerShell has no Roslyn, and the
/// property being pinned (a visible token next to a visible launch) is
/// exactly what a reader of the harness needs to see anyway.
///
/// A file passes when ANY of these hold:
/// <list type="number">
/// <item>it assigns <c>WINTTY_TEST_CONFIG</c> on a non-comment line (the
/// app then polices the root itself at run time, so an arming script can
/// never silently taint);</item>
/// <item>it goes through a helper that owns the arming:
/// <c>Enter-WinttyTestConfig</c> (lib/test-config.ps1) or
/// <c>Start-SeamSession</c> (lib/seam-client.ps1);</item>
/// <item>it is on the allowlist below, each entry with its reason.</item>
/// </list>
///
/// The allowlist bar: the launch must be provably unable to reach the
/// config, or provably not a test. "Stable when unisolated" is not on the
/// bar; that argument is what opted-in isolation was for, and the default
/// is isolated now.
/// </summary>
public class HarnessConfigIsolationScanTests
{
    /// <summary>
    /// Launchers that never touch config state, with the proof. Adding an
    /// entry means claiming the launch cannot read, create or write the
    /// config; the burden is on the entry, not on the scan.
    /// </summary>
    private static readonly Dictionary<string, string> Allowlist = new()
    {
        // `+crash` is intercepted in Program.MainImpl before InitGhostty
        // and exits, so no config is ever read, created or written; the
        // crash reporting probes only exercise the failure backend.
        ["crash-canary.ps1"] = "+crash exits before InitGhostty; config is never reached",
        ["crash-matrix.ps1"] = "+crash exits before InitGhostty; config is never reached",
    };

    /// <summary>
    /// justfile recipes that execute the exe directly, with the proof.
    /// </summary>
    private static readonly Dictionary<string, string> JustfileAllowlist = new()
    {
        // Interactive dev runs of the developer's own environment: the
        // point of the recipe is the real config. Not a test; the fuzz and
        // seam recipes all route through scripts that arm the guard.
        ["run-win"] = "interactive dev launch, not a test",
        ["run-win-release"] = "interactive dev launch, not a test",
    };

    private static readonly Regex LaunchKeyword = new(
        @"Start-Process|ProcessStartInfo", RegexOptions.Compiled);

    /// <summary>
    /// A variable whose name mentions the exe: $ExePath, $exe, $Exe,
    /// $WinttyExe, $script:ExeFull. What it deliberately does not match:
    /// $cdb (a debugger), $pwsh, $argLine, $ProcId. Detecting the launch
    /// of the app is what the scan is for; launches of tooling are not
    /// app launches even when they sit on the same line.
    /// </summary>
    private static readonly Regex AppExeToken = new(
        @"\$[A-Za-z0-9_:.]*[Ee]xe[A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>
    /// An arming assignment on a code line:
    /// <c>$env:WINTTY_TEST_CONFIG = '1'</c> or
    /// <c>$psi.EnvironmentVariables['WINTTY_TEST_CONFIG'] = '1'</c>.
    /// Requiring the <c>=</c> keeps prose mentions of the variable from
    /// reading as arming.
    /// </summary>
    private static readonly Regex ArmingLine = new(
        @"WINTTY_TEST_CONFIG['\]\s]*=", RegexOptions.Compiled);

    private static readonly Regex HelperCall = new(
        @"Enter-WinttyTestConfig|Start-SeamSession", RegexOptions.Compiled);

    private static bool IsCode(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] != '#';
    }

    private sealed record Launch(string File, int Line, string Text);

    private static IEnumerable<Launch> AppLaunches(string file, string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsCode(line)) continue;
            if (!LaunchKeyword.IsMatch(line)) continue;
            if (!AppExeToken.IsMatch(line)) continue;
            yield return new Launch(file, i + 1, line.Trim());
        }
    }

    private static bool FileIsClean(string[] lines) =>
        lines.Any(l => IsCode(l) && (ArmingLine.IsMatch(l) || HelperCall.IsMatch(l)));

    [Fact]
    public void Every_App_Launch_In_Scripts_Arms_The_Test_Config_Guard()
    {
        var root = RepoRoot();
        var scriptDir = Path.Combine(root, "windows", "scripts");
        Assert.True(Directory.Exists(scriptDir), "windows/scripts not found");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     scriptDir, "*.ps1", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // The selftest fixtures are config-fragment corpora for
            // fuzz-suite's layer checks; none of them launches anything.
            if (file.Replace('\\', '/').Contains("/lib/fuzz-selftest/")) continue;

            var rel = Path.GetRelativePath(scriptDir, file);
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            if (FileIsClean(lines)) continue;

            if (Allowlist.TryGetValue(name, out var reason))
            {
                // Still prove the file launches the app the allowlist says
                // it does; an allowlisted name that stopped launching would
                // silently keep covering whatever replaced it.
                Assert.NotEmpty(AppLaunches(rel, lines));
                continue;
            }

            violations.AddRange(AppLaunches(rel, lines)
                .Select(l => $"{l.File}:{l.Line}: {l.Text}"));
        }

        Assert.True(
            violations.Count == 0,
            "app launch(es) that neither arm WINTTY_TEST_CONFIG nor go through " +
            "an isolated-config helper (lib/test-config.ps1 or lib/seam-client.ps1). " +
            "Stage a random temp XDG root and set WINTTY_TEST_CONFIG=1 for the " +
            "child, or add a justified allowlist entry:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Justfile_Recipes_Execute_The_Exe_Only_Where_Isolation_Is_Intended()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "justfile");
        Assert.True(File.Exists(path), "justfile not found");

        var violations = new List<string>();
        var recipe = "?";
        foreach (var (line, index) in File.ReadAllLines(path)
                     .Select((l, i) => (l, i)))
        {
            var recipeHeader = Regex.Match(line, @"^([A-Za-z][\w-]*):");
            if (recipeHeader.Success)
            {
                recipe = recipeHeader.Groups[1].Value;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            // The exe as the line's first token is a direct execution; the
            // same path as an argument (-ExePath ...) is a script's input
            // and that script's own scan governs it.
            var firstToken = trimmed.Split(' ')[0];
            if (!firstToken.EndsWith("Wintty.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (JustfileAllowlist.ContainsKey(recipe)) continue;
            violations.Add($"justfile:{index + 1} (recipe {recipe}): {trimmed}");
        }

        Assert.True(
            violations.Count == 0,
            "justfile executes the app outside a script, without arming " +
            "WINTTY_TEST_CONFIG. Route the launch through a harness script " +
            "(they stage a random temp XDG root and arm the guard) or justify " +
            "an allowlist entry:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Seam_Client_Itself_Arms_The_Guard()
    {
        // Every Start-SeamSession consumer leans on this one file to arm,
        // so the first scan's helper pass would stay green even if the
        // arming were deleted out of seam-client. This pins the arming to
        // the place it actually happens.
        var root = RepoRoot();
        var path = Path.Combine(root, "windows", "scripts", "lib", "seam-client.ps1");
        Assert.True(File.Exists(path), "lib/seam-client.ps1 not found");

        Assert.Contains(File.ReadAllLines(path),
            l => IsCode(l) && ArmingLine.IsMatch(l));
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
