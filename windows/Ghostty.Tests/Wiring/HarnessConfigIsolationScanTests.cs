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
/// A LAUNCH SITE is clean when the file arms the guard UNCONDITIONALLY at
/// an earlier position: an arming line inside an if/else/switch blesses
/// nothing (that was the -RealConfig hole: one armed branch blessed the
/// unarmed branch of the same file). Arming inside a function or loop is
/// fine; those always run.
///
/// The launch forms are held honest by a synthetic fixture corpus
/// (<c>ScanFixtures/</c>): every fixture named <c>violation-*.ps1</c> must
/// be flagged, every <c>clean-*.ps1</c> must not be, so a form going
/// invisible again fails the suite instead of quietly narrowing coverage.
///
/// The allowlist bar: the launch must be provably unable to reach the
/// config, or provably not a test. "Stable when unisolated" is not on the
/// bar.
/// </summary>
public class HarnessConfigIsolationScanTests
{
    /// <summary>
    /// Launchers that never touch config state, or are not tests, each
    /// with the proof. Adding an entry means claiming the launch cannot
    /// read, create or write the config, or is the deliberate user
    /// launcher; the burden is on the entry, not on the scan.
    /// </summary>
    private static readonly Dictionary<string, string> Allowlist = new()
    {
        // `+crash` is intercepted in Program.MainImpl before InitGhostty
        // and exits, so no config is ever read, created or written; the
        // crash reporting probes only exercise the failure backend.
        ["crash-canary.ps1"] = "+crash exits before InitGhostty; config is never reached",
        ["crash-matrix.ps1"] = "+crash exits before InitGhostty; config is never reached",

        // The USER launcher behind run-win/run-win-release (founder rule):
        // real config by default for humans, ISOLATED_CONFIG=1 opt-in,
        // isolated by default inside an agent session (CLAUDECODE), and
        // REAL_CONFIG=1 as a loud, interactively-confirmed override. Its
        // modes are pinned by Run_Win_Launcher_Pins_The_Founder_Rule; a
        // scan pass cannot express "armed except in one deliberate mode",
        // and the user launcher must keep its unarmed human default.
        ["run-win-launch.ps1"] = "user launcher; modes pinned by Run_Win_Launcher tests",
    };

    // ---- launch-form detection -----------------------------------------

    /// <summary>
    /// Every verb shape that can start the app. The call-operator,
    /// ::Start, Invoke-Item, cmd start, explorer and dotnet-run forms were
    /// all invisible to the first scan revision and each has a fixture.
    /// </summary>
    private static readonly Regex[] LaunchVerbs =
    {
        new(@"Start-Process", RegexOptions.Compiled),
        new(@"ProcessStartInfo", RegexOptions.Compiled),
        new(@"Process\]::Start\(", RegexOptions.Compiled),
        new(@"Process\.Start\(", RegexOptions.Compiled),
        new(@"Invoke-Item", RegexOptions.Compiled),
        new(@"(?<!\S)&\s*\$", RegexOptions.Compiled),
        new(@"explorer\.exe", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bcmd\b.*\bstart\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"dotnet\s+run", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// A variable whose name mentions the exe: $ExePath, $exe, $Exe,
    /// $WinttyExe, $script:ExeFull.
    /// </summary>
    private static readonly Regex ExeNamedVar = new(
        @"\$[A-Za-z0-9_:.]*[Ee]xe[A-Za-z0-9_]*", RegexOptions.Compiled);

    /// <summary>
    /// The app's own binary, spelled literally (the only spellings this
    /// product ships). Tooling literals (pwsh, cdb, wsl...) are NOT app
    /// launches and are excluded during target resolution.
    /// </summary>
    private static readonly Regex AppLiteral = new(
        @"(?i)(wintty|ghostty)\.exe", RegexOptions.Compiled);

    private static readonly Regex ProjectReference = new(
        @"(?i)wintty|ghostty", RegexOptions.Compiled);

    /// <summary>
    /// Tooling binaries a harness legitimately launches: shells, the
    /// debugger, and this repo's own capture helpers. A resolved target
    /// matching one of these is not an app launch.
    /// </summary>
    private static readonly Regex ToolingLiteral = new(
        @"(?i)\b(pwsh|powershell|cmd|wsl|cdb|windbg|procdump|python|py|node|npm|git|msbuild|bash|tar|curl|robocopy|xcopy|backdropstage|windowcapture|ffmpeg)\.(exe|com|bat|cmd)\b",
        RegexOptions.Compiled);

    /// <summary>
    /// A tooling name as a bare quoted value ($psi.FileName = 'pwsh'),
    /// the spelling the ProcessStartInfo helpers use.
    /// </summary>
    private static readonly Regex ToolingBareQuoted = new(
        "^\\s*['\"](pwsh|powershell|cmd|wsl|cdb|windbg|procdump|python|node|npm|git|msbuild|bash)['\"]\\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Quoted spans, removed before verb matching so a manifest prose
    /// string mentioning "cmd" and "start" is not read as a launch.
    /// Target evidence still sees the raw line, because the app exe
    /// arrives quoted more often than not.
    /// </summary>
    private static readonly Regex QuotedSpan = new(
        "'[^']*'|\"[^\"]*\"", RegexOptions.Compiled);

    private static readonly Regex AnyVar = new(@"\$(\w+)", RegexOptions.Compiled);

    private static readonly Regex ArmingLine = new(
        @"WINTTY_TEST_CONFIG['\]\s]*=", RegexOptions.Compiled);

    private static readonly Regex HelperCall = new(
        @"Enter-WinttyTestConfig|Start-SeamSession", RegexOptions.Compiled);

    private static readonly Regex ConditionalHeader = new(
        @"^\s*(if|elseif|else|switch)\b", RegexOptions.Compiled);

    private static bool IsCode(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length > 0 && trimmed[0] != '#';
    }

    private sealed record LaunchSite(string File, int Line, string Text);

    /// <summary>
    /// Whether the target a launch line names is the app. Resolution
    /// follows same-line evidence first (an exe-named variable, a literal
    /// app path), then the variable's assignment or the psi FileName /
    /// FilePath property assignments, so a harness that launches through
    /// an intermediate variable is judged by what the variable holds.
    /// Anything the rules cannot resolve fails CLOSED: the harness must
    /// name its target legibly or arm the guard, and an unresolved launch
    /// is flagged rather than trusted.
    /// </summary>
    private static bool TargetsApp(
        string line, string[] lines, Dictionary<string, List<string>> functions,
        bool callOperatorOnly)
    {
        // Target evidence reads the raw line: the app exe arrives quoted
        // more often than not.
        if (AppLiteral.IsMatch(line)) return true;
        if (Regex.IsMatch(line, @"dotnet\s+run", RegexOptions.IgnoreCase) &&
            ProjectReference.IsMatch(line)) return true;

        var stripped = QuotedSpan.Replace(line, "");
        var sawAny = false;
        var sawApp = false;
        var sawTooling = false;
        var sawEvidence = false;

        foreach (var name in AnyVar.Matches(stripped)
                     .Cast<System.Text.RegularExpressions.Match>()
                     .Select(m => m.Groups[1].Value)
                     .Distinct())
        {
            sawAny = true;
            var evidence = new List<string>();
            evidence.AddRange(DefinitionsOf(lines, name)
                .Select(d => d.Rhs));
            evidence.AddRange(PropertyTargets(lines, name));

            // Follow in-file functions the definitions call, and one level
            // of variable indirection (psi -> FileName = $exe -> $exe's
            // definitions), the shape lib/backdrop-stage.ps1 uses to reach
            // its own built helper exe.
            for (var hop = 0; hop < 2; hop++)
            {
                foreach (var value in evidence.ToList())
                {
                    foreach (var (function, body) in functions)
                        if (value.Contains(function, StringComparison.Ordinal))
                            evidence.AddRange(body);
                    var indirect = Regex.Match(value, @"^\s*\$([A-Za-z_]\w*)\s*$");
                    if (indirect.Success)
                    {
                        evidence.AddRange(DefinitionsOf(lines,
                            indirect.Groups[1].Value).Select(d => d.Rhs));
                        evidence.AddRange(PropertyTargets(lines,
                            indirect.Groups[1].Value));
                    }
                }
            }

            foreach (var value in evidence)
            {
                if (AppLiteral.IsMatch(value)) sawApp = true;
                else if (ToolingLiteral.IsMatch(value) ||
                         ToolingBareQuoted.IsMatch(value))
                    sawTooling = true;
            }
            if (evidence.Count > 0) sawEvidence = true;
        }

        if (sawApp) return true;
        if (sawTooling) return false;
        if (!sawAny) return false;

        // No app and no tooling evidence: the variable's own name decides,
        // except the call-operator form, which is how scriptblocks are
        // invoked and needs positive evidence.
        if (callOperatorOnly) return false;
        if (sawEvidence) return true;
        return AnyVar.Matches(stripped)
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Any(n => ExeNamedVar.IsMatch("$" + n));
    }

    private static string StripLiterals(string line) =>
        QuotedSpan.Replace(line, "");

    private static Dictionary<string, List<string>> FunctionBodiesFor(
        string[] lines)
    {
        var bodies = new Dictionary<string, List<string>>();
        string? current = null;
        foreach (var line in lines)
        {
            var header = FunctionDef.Match(line);
            if (header.Success)
            {
                current = header.Groups[1].Value;
                bodies[current] = new List<string>();
                continue;
            }
            if (current is not null) bodies[current].Add(line);
        }
        return bodies;
    }

    private static List<(int Line, string Rhs)> DefinitionsOf(
        string[] lines, string name)
    {
        // One pattern covers both plain assignments and parameter defaults
        // (`[string]$OutDir = ...`).
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

    private static List<string> PropertyTargets(string[] lines, string name)
    {
        var pattern = new Regex(
            @"\$" + Regex.Escape(name) + @"\.(?:FileName|FilePath)\s*=\s*(.+?)(?:\s*#.*)?$");
        var found = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!IsCode(lines[i])) continue;
            var m = pattern.Match(lines[i]);
            if (m.Success) found.Add(m.Groups[1].Value);
        }
        return found;
    }

    /// <summary>
    /// The positions of arming lines (env assignment or helper call) that
    /// run unconditionally: inside a function or a loop is unconditional,
    /// inside an if/else/switch is not. Tracked with a brace stack keyed
    /// by the block-opening keyword; hashtable and scriptblock braces
    /// count as their own non-conditional kind, and here-strings are
    /// skipped entirely (their C#/JSON braces are not code).
    /// </summary>
    private static HashSet<int> UnconditionalArmLines(string[] lines)
    {
        var result = new HashSet<int>();
        var blocks = new Stack<string>();
        var pendingConditional = false;
        var inHereString = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (inHereString)
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("'@") || trimmed.StartsWith("\"@"))
                    inHereString = false;
                continue;
            }

            var hereStringOpener = line.IndexOf("@'", StringComparison.Ordinal);
            if (hereStringOpener < 0)
                hereStringOpener = line.IndexOf("@\"", StringComparison.Ordinal);
            if (hereStringOpener >= 0)
            {
                // A here-string opened and terminated on the same line is
                // ordinary code; one still open skips to its terminator.
                var opener = line.Contains("@'") ? "'@" : "\"@";
                var terminator = line.IndexOf(opener, hereStringOpener + 2,
                    StringComparison.Ordinal);
                if (terminator < 0) inHereString = true;
                continue;
            }

            if (IsCode(line))
            {
                var conditionalHere =
                    ConditionalHeader.IsMatch(line) || blocks.Contains("cond");
                if ((ArmingLine.IsMatch(line) || HelperCall.IsMatch(line)) &&
                    !conditionalHere)
                {
                    result.Add(i);
                }
            }

            // Track block nesting for the NEXT lines' conditionality.
            pendingConditional = ConditionalHeader.IsMatch(line);
            foreach (var c in line)
            {
                if (c == '{')
                {
                    blocks.Push(pendingConditional ? "cond" : "other");
                    pendingConditional = false;
                }
                else if (c == '}')
                {
                    if (blocks.Count > 0) blocks.Pop();
                }
            }
            pendingConditional = false;
        }

        return result;
    }

    /// <summary>
    /// Every launch site in a file whose target resolves to the app and
    /// that no unconditional arming line precedes.
    /// </summary>
    private static List<LaunchSite> UnarmedLaunches(string rel, string[] lines)
    {
        var found = new List<LaunchSite>();
        if (lines.Length == 0) return found;
        var arms = UnconditionalArmLines(lines);
        var functions = FunctionBodiesFor(lines);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsCode(line)) continue;

            // The verb must survive literal stripping: a manifest prose
            // string that mentions "cmd" and "start" is not a launch.
            var stripped = StripLiterals(line);
            var matching = LaunchVerbs.Where(v => v.IsMatch(line)).ToList();
            if (matching.Count == 0) continue;
            if (!matching.Any(v => v.IsMatch(stripped))) continue;

            var callOperatorOnly = matching.Count == 1 &&
                matching[0].ToString().Contains("&");
            if (!TargetsApp(line, lines, functions, callOperatorOnly))
                continue;

            // An arm later in the file blesses nothing, and one inside a
            // conditional blesses nothing at all.
            if (arms.Any(j => j < i)) continue;

            found.Add(new LaunchSite(rel, i + 1, line.Trim()));
        }

        return found;
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

            if (Allowlist.TryGetValue(name, out var reason))
            {
                // Still prove the file launches the app the allowlist says
                // it does; an allowlisted name that stopped launching would
                // silently keep covering whatever replaced it.
                Assert.NotEmpty(UnarmedLaunches(rel, lines));
                continue;
            }

            violations.AddRange(UnarmedLaunches(rel, lines)
                .Select(l => $"{l.File}:{l.Line}: {l.Text}"));
        }

        Assert.True(
            violations.Count == 0,
            "app launch(es) that neither arm WINTTY_TEST_CONFIG nor go through " +
            "an isolated-config helper (lib/test-config.ps1 or lib/seam-client.ps1). " +
            "Stage a random temp XDG root and set WINTTY_TEST_CONFIG=1 for the " +
            "child (unconditionally, before the launch), or add a justified " +
            "allowlist entry:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// The synthetic corpus: every launch form the scan claims to see, as
    /// a fixture that MUST be flagged, plus the shapes that must NOT be.
    /// If a form goes invisible (a regex narrows, a verb is forgotten),
    /// its fixture stops being flagged and this fails naming it.
    /// </summary>
    [Fact]
    public void The_Scan_Still_Sees_Every_Launch_Form()
    {
        var root = RepoRoot();
        var fixtureDir = Path.Combine(
            root, "windows", "Ghostty.Tests", "Wiring", "ScanFixtures");
        Assert.True(Directory.Exists(fixtureDir), "ScanFixtures not found");

        var expectedViolations = new HashSet<string>(StringComparer.Ordinal)
        {
            "violation-call-operator.ps1",
            "violation-invoke-item.ps1",
            "violation-process-start-static.ps1",
            "violation-literal-start-process.ps1",
            "violation-cmd-start.ps1",
            "violation-explorer.ps1",
            "violation-dotnet-run.ps1",
            "violation-psi-indirect-var.ps1",
            "violation-psi-indirect-literal.ps1",
            "violation-unresolved-var.ps1",
            "violation-conditional-arm.ps1",
        };
        var expectedClean = new HashSet<string>(StringComparer.Ordinal)
        {
            "clean-armed-launch.ps1",
            "clean-tooling-psi.ps1",
            "clean-tooling-debugger.ps1",
        };

        var flagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(fixtureDir, "*.ps1")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (UnarmedLaunches(name, File.ReadAllLines(file)).Count > 0)
                flagged.Add(name);
        }

        var missing = expectedViolations.Except(flagged).ToList();
        Assert.True(
            missing.Count == 0,
            "launch form(s) the scan can no longer see (the fixture is not " +
            "flagged): " + string.Join(", ", missing));

        var falseFlags = expectedClean.Intersect(flagged).ToList();
        Assert.True(
            falseFlags.Count == 0,
            "shape(s) the scan flags that must stay clean: " +
            string.Join(", ", falseFlags));
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

        // ...and, inside an agent session, confirmed by a human at the
        // keyboard: a typed REAL. A non-interactive console (every agent
        // shell) must refuse instead.
        Assert.Contains(lines, l =>
            IsCode(l) && l.Contains("Read-Host", StringComparison.Ordinal));
        Assert.Contains(lines, l =>
            IsCode(l) && l.Contains("IsInputRedirected", StringComparison.Ordinal));
        Assert.Contains(lines, l =>
            IsCode(l) && l.Contains("cannot be used from an agent session",
                StringComparison.Ordinal));

        // The window outlives the recipe: exiting the test-config session
        // would delete the root the live app is still holding. The helper's
        // 24h sweep is what reaps it instead.
        Assert.DoesNotContain(lines, l =>
            IsCode(l) && l.Contains("Exit-WinttyTestConfig", StringComparison.Ordinal));
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
}
