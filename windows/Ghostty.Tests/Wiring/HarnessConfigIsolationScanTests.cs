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
    /// No justfile allowlist, on purpose. run-win and run-win-release are
    /// the USER launcher (founder rule): real config by default for humans,
    /// ISOLATED_CONFIG=1 to opt into a random temp root, isolated by
    /// default inside an agent session (CLAUDECODE), REAL_CONFIG=1 to
    /// override loudly. All of that lives in run-win-launch.ps1, which this
    /// scan counts as armed because it contains the arming path. A bare
    /// `./Wintty.exe` line in any recipe would bypass the agent-session
    /// default, which is the exact April-taint path the launcher exists to
    /// close, so it must fail this scan rather than be allowlisted.
    /// </summary>

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
    public void Justfile_Recipes_Execute_The_Exe_Only_Through_A_Launcher()
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

            violations.Add($"justfile:{index + 1} (recipe {recipe}): {trimmed}");
        }

        Assert.True(
            violations.Count == 0,
            "justfile executes the app outside a launcher script. Tests arm " +
            "WINTTY_TEST_CONFIG through their harness; the user launcher " +
            "(run-win / run-win-release) routes through run-win-launch.ps1, " +
            "which isolates agent sessions by default. A bare exec line " +
            "bypasses both:\n  " + string.Join("\n  ", violations));
    }

    [Fact]
    public void Run_Win_Recipes_Route_Through_The_User_Launcher_Script()
    {
        var root = RepoRoot();
        var justfile = File.ReadAllLines(Path.Combine(root, "justfile"));

        foreach (var recipe in new[] { "run-win", "run-win-release" })
        {
            var start = Array.FindIndex(justfile,
                l => l.StartsWith(recipe + ":", StringComparison.Ordinal));
            Assert.True(start >= 0, $"no recipe '{recipe}' in the justfile");

            var body = new List<string>();
            for (var i = start + 1; i < justfile.Length; i++)
            {
                var line = justfile[i];
                if (Regex.IsMatch(line, @"^[A-Za-z][\w-]*:") || line.StartsWith('['))
                    break;
                body.Add(line);
            }

            Assert.Contains(body, l =>
                l.Contains("run-win-launch.ps1", StringComparison.Ordinal) &&
                !l.TrimStart().StartsWith('#'));
        }
    }

    [Fact]
    public void Run_Win_Launcher_Pins_The_Founder_Rule_For_The_User_Launcher()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, "windows", "scripts", "run-win-launch.ps1");
        Assert.True(File.Exists(path),
            "run-win-launch.ps1 not found; the run-win recipes depend on it");

        var lines = File.ReadAllLines(path);
        var text = string.Join('\n', lines);

        // The four markers of the rule: the explicit opt-ins, the agent
        // detector, and the guard's own variable.
        foreach (var marker in new[] { "CLAUDECODE", "ISOLATED_CONFIG",
                                       "REAL_CONFIG", "WINTTY_TEST_CONFIG" })
        {
            Assert.Contains(lines, l =>
                IsCode(l) && l.Contains(marker, StringComparison.Ordinal));
        }

        // Isolation rides the same helper every harness uses, and the mode
        // resolution is a function the harness side can probe without
        // launching anything.
        Assert.Contains(lines, l =>
            IsCode(l) && l.Contains("Enter-WinttyTestConfig", StringComparison.Ordinal));
        Assert.Contains("function Get-RunWinMode", text,
            StringComparison.Ordinal);

        // Precedence, pinned by position inside the mode function: an
        // explicit REAL_CONFIG beats everything, an explicit ISOLATED_CONFIG
        // beats the agent default, and CLAUDECODE only fills the default.
        // If the checks are reordered, this fails.
        var functionStart = text.IndexOf("function Get-RunWinMode", StringComparison.Ordinal);
        var functionEnd = text.IndexOf("function ", functionStart + 1, StringComparison.Ordinal);
        if (functionEnd < 0) functionEnd = text.Length;
        var body = text[functionStart..functionEnd];
        var real = body.IndexOf("REAL_CONFIG", StringComparison.Ordinal);
        var isolated = body.IndexOf("ISOLATED_CONFIG", StringComparison.Ordinal);
        var agent = body.IndexOf("CLAUDECODE", StringComparison.Ordinal);
        Assert.True(real >= 0 && isolated > real && agent > isolated,
            "Get-RunWinMode must check REAL_CONFIG before ISOLATED_CONFIG " +
            "before CLAUDECODE; found at " + $"{real}/{isolated}/{agent}");

        // REAL_CONFIG is the one spelling that runs the app against the
        // user's real config from inside an agent session, so it must be
        // announced loudly, not accepted in silence.
        Assert.Contains(lines, l =>
            IsCode(l) &&
            l.Contains("REAL_CONFIG", StringComparison.Ordinal) &&
            l.Contains("ForegroundColor Red", StringComparison.Ordinal));

        // The window outlives the recipe: exiting the test-config session
        // would delete the root the live app is still holding. The helper's
        // 24h sweep is what reaps it instead.
        Assert.DoesNotContain(lines, l =>
            IsCode(l) && l.Contains("Exit-WinttyTestConfig", StringComparison.Ordinal));
    }

    /// <summary>
    /// Spellings that make a staging root random: the .NET GUID spellings
    /// used across the harnesses, the crypto-random helpers, and the two
    /// library entry points that own randomness themselves.
    /// </summary>
    private static readonly string[] RandomnessMarkers =
    {
        "[guid]::NewGuid", "New-Guid", "GetRandomFileName",
        "RandomNumberGenerator", "Enter-WinttyTestConfig",
        "Start-SeamSession", "New-WinttyTestConfigRoot", "New-SeamToken",
    };

    /// <summary>
    /// Assignments of the config root: process-wide or per-child psi.
    /// </summary>
    private static readonly Regex XdgAssignment = new(
        @"(?:\$env:XDG_CONFIG_HOME|EnvironmentVariables\['XDG_CONFIG_HOME'\])\s*=\s*(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex VarRef = new(@"\$(\w+)", RegexOptions.Compiled);

    private static readonly Regex FunctionDef = new(
        @"^\s*function\s+([\w-]+)", RegexOptions.Compiled);

    /// <summary>
    /// A variable's definitions: plain assignments and parameter defaults
    /// (`[string]$OutDir = ...`), which is where the fixed staging names
    /// lived.
    /// </summary>
    private static List<(int Line, string Rhs)> DefinitionsOf(
        string[] lines, string name)
    {
        // One pattern covers both plain assignments and parameter defaults
        // (`[string]$OutDir = ...`), which is where the fixed names lived.
        var definition = new Regex(
            @"^\s*(?:\[[^\]]*\]\s*)?\$" + Regex.Escape(name) +
            @"\s*=\s*(.+?)(?:\s*#.*)?$");
        var found = new List<(int, string)>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsCode(lines[i])) continue;
            var m = definition.Match(lines[i]);
            if (m.Success) found.Add((i + 1, m.Groups[1].Value));
        }
        return found;
    }

    /// <summary>
    /// The function bodies of one file, for following a helper that builds
    /// the root (splash-race's New-ScratchConfig): the randomness may live
    /// a call away from the assignment.
    /// </summary>
    private static Dictionary<string, List<string>> FunctionBodies(string[] lines)
    {
        var bodies = new Dictionary<string, List<string>>();
        for (var i = 0; i < lines.Length; i++)
        {
            var header = FunctionDef.Match(lines[i]);
            if (!header.Success) continue;
            var body = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                if (FunctionDef.Match(lines[j]).Success) break;
                body.Add(lines[j]);
            }
            bodies[header.Groups[1].Value] = body;
        }
        return bodies;
    }

    /// <summary>
    /// Whether the chain of definitions behind <paramref name="rhs"/> ever
    /// reaches a randomness marker, or is provably inert (a restore of a
    /// saved value, or an empty default). Returns the offending definition
    /// when the chain reaches a temp path or literal without randomness.
    /// </summary>
    private static string? UnrandomizedStaging(
        string rhs, string[] lines, Dictionary<string, List<string>> functions,
        HashSet<string> visited, int hops)
    {
        if (RandomnessMarkers.Any(m => rhs.Contains(m, StringComparison.Ordinal)))
            return null;

        // Restores: the saved-original spellings, or reading the variable
        // back from the environment.
        if (rhs.Contains("$env:XDG_CONFIG_HOME", StringComparison.Ordinal) ||
            Regex.IsMatch(rhs, @"(?:orig|prev|previous)", RegexOptions.IgnoreCase))
            return null;

        if (hops > 6) return $"{rhs.Trim()} (chain too deep to verify)";

        // Follow variables assigned in this file.
        foreach (var match in VarRef.Matches(rhs).Cast<System.Text.RegularExpressions.Match>())
        {
            var name = match.Groups[1].Value;
            if (!visited.Add("$" + name)) continue;
            var defs = DefinitionsOf(lines, name);
            if (defs.Count == 0) continue;
            foreach (var def in defs)
            {
                if (RandomnessMarkers.Any(m => def.Rhs.Contains(m, StringComparison.Ordinal)))
                    return null;
                // A definition that names a temp path with no randomness is
                // the fixed/clock-keyed staging this rule exists for.
                if (def.Rhs.Contains("$env:TEMP", StringComparison.Ordinal) ||
                    def.Rhs.Contains("GetTempPath", StringComparison.Ordinal) ||
                    Regex.IsMatch(def.Rhs, @"""\w+wintty[\w-]*"""))
                {
                    return $"line {def.Line}: {def.Rhs.Trim()}";
                }
                var verdict = UnrandomizedStaging(
                    def.Rhs, lines, functions, visited, hops + 1);
                if (verdict is not null) return $"line {def.Line}: {verdict}";
            }
        }

        // Follow in-file functions the rhs calls.
        foreach (var (name, body) in functions)
        {
            if (!rhs.Contains(name, StringComparison.Ordinal)) continue;
            if (!visited.Add(name)) continue;
            foreach (var bodyLine in body)
            {
                if (!IsCode(bodyLine)) continue;
                if (RandomnessMarkers.Any(m =>
                        bodyLine.Contains(m, StringComparison.Ordinal)))
                    return null;
                var verdict = UnrandomizedStaging(
                    bodyLine, lines, functions, visited, hops + 1);
                if (verdict is not null) return $"{name}: {verdict}";
            }
        }

        // Inert rhs (empty default, $null): not a staging path.
        return null;
    }

    [Fact]
    public void Every_Staged_Config_Root_Is_Randomly_Named()
    {
        var root = RepoRoot();
        var scriptDir = Path.Combine(root, "windows", "scripts");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     scriptDir, "*.ps1", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (file.Replace('\\', '/').Contains("/lib/fuzz-selftest/")) continue;
            var rel = Path.GetRelativePath(scriptDir, file);
            var lines = File.ReadAllLines(file);
            var functions = FunctionBodies(lines);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsCode(line)) continue;
                var assignment = XdgAssignment.Match(line);
                if (!assignment.Success) continue;

                var offending = UnrandomizedStaging(
                    assignment.Groups[1].Value, lines, functions,
                    new HashSet<string>(), 0);
                if (offending is not null)
                {
                    violations.Add($"{rel}:{i + 1}: {line.Trim()}  <- {offending}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "config staging root(s) with a fixed or clock-keyed name. The " +
            "founder rule wants a randomly generated name per run (a fixed or " +
            "HHmmss name collides across runs and leaks state between them). " +
            "Stage through Enter-WinttyTestConfig / Start-SeamSession, or put " +
            "[guid]::NewGuid() in the root's name:\n  " +
            string.Join("\n  ", violations));
    }

    [Fact]
    public void Search_Fuzz_Has_No_Real_Config_Escape()
    {
        // A test harness must not be able to point the app at the real
        // config: the escape hatch was exactly how a stability workaround
        // became a standing taint path. If the isolated root ever proves
        // unstable (the historical 0xc000027b), that is a product bug to
        // capture and fix, not a mode to keep.
        var root = RepoRoot();
        var path = Path.Combine(root, "windows", "scripts", "search-fuzz.ps1");
        Assert.True(File.Exists(path), "search-fuzz.ps1 not found");

        var lines = File.ReadAllLines(path);
        Assert.DoesNotContain(lines, l =>
            IsCode(l) && l.Contains("RealConfig", StringComparison.Ordinal));
        Assert.Contains(lines, l =>
            IsCode(l) && l.Contains("Enter-WinttyTestConfig", StringComparison.Ordinal));
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
