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
///
/// KNOWN, ACCEPTED LIMITS (deliberately obfuscated spellings the scan does
/// not chase; ruled acceptable because constructing them is evasion on
/// purpose, and the BOUNDARY is the app-side WINTTY_TEST_CONFIG guard with
/// its env-independent known-folder temp anchor, which no spelling here
/// can move): helper-name shadowing (a local function redefining
/// New-WinttyTestConfigRoot), same-line randomness-marker smuggling, and
/// Invoke-Expression/iex on a built string; conhost.exe and rundll32 as
/// launchers; launch through a scriptblock VARIABLE whose block references
/// a variable rather than a literal; the COM object created in a
/// dot-sourced helper so the file-level COM gate closes; and staging
/// values arriving through dot-sourced files. If a shape on this list
/// shows up in a harness review, treat it as a finding anyway: the list
/// is what the AUTOMATION accepts, not what the project welcomes.
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
        // PowerShell itself is case-insensitive; mixed-case verbs and
        // aliases (STArt-Process, START, SAPS) are ordinary spellings, not
        // obfuscation, so every verb matches case-insensitively. Prose is
        // kept out by matching the literal-stripped line only, and the
        // bare alias form rejects a hyphen continuation so Start-Job and
        // friends are not launches by mere word shape.
        new(@"Start-Process", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\b(?:saps|start)\b(?!-)", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"ProcessStartInfo", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Process\]::Start\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Process\.Start\(", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"Invoke-Item", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // The call operator, with a variable OR a literal/interpolated path:
        // the verb matches the stripped line too ('& ' survives stripping),
        // while target evidence reads the raw line where the path lives.
        new(@"(?<!\S)&\s*"),
        new(@"explorer\.exe", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"\bcmd\b.*\bstart\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"dotnet\s+run", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // COM launches, gated on the file actually creating a shell object
        // (see UsesComShell); the method call is the verb.
        new(@"\.(?:Run|Exec|ShellExecute)\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    /// <summary>
    /// Whether any line in the file creates the COM shell object whose
    /// Run/Exec/ShellExecute methods launch programs.
    /// </summary>
    private static bool UsesComShell(string[] lines) =>
        lines.Any(l => IsCode(l) && Regex.IsMatch(
            l, @"WScript\.Shell|Shell\.Application",
            RegexOptions.IgnoreCase));

    /// <summary>
    /// The debugger attach flags: a debugger whose arguments attach to an
    /// existing process (-p pid, -pn name) launches nothing and is exempt.
    /// </summary>
    private static readonly Regex AttachFlag = new(
        @"-(?:p|pn|pid)\b", RegexOptions.Compiled);

    /// <summary>
    /// The debugger binaries themselves; a debugger that RUNS the app as
    /// its debuggee is an app launch and must isolate like any other.
    /// </summary>
    private static readonly Regex DebuggerToken = new(
        @"(?i)\b(cdb|windbg|procdump)", RegexOptions.Compiled);

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
    /// The positionally-decided target expressions of a launch line: the
    /// value after -FilePath, the argument of the call operator, the first
    /// argument of ::Start / Invoke-Item / a COM Run, the ProcessStartInfo
    /// constructor argument, and the first positional token of the
    /// Start-Process spellings. Target evidence is judged on THESE, not on
    /// the whole line, so an app path sitting in a debugger's -ArgumentList
    /// does not make the debugger an app launch (R1-4 runs the ruling the
    /// other way below).
    /// </summary>
    private static List<string> TargetCandidates(
        string line, string stripped, string[] lines)
    {
        // A candidate token ends at whitespace, a comma, a closing brace
        // or paren (so the -FilePath inside a scriptblock literal does not
        // swallow the block's closing brace), and tokens that are ONLY
        // braces or parens are dropped: a dropped candidate never counts
        // as a decided position, so the whole-line fallback still fires.
        var token = @"[^,\s)}\]]+";
        var candidates = new List<string>();
        foreach (var m in Regex.Matches(
                     stripped, @"-(?:FilePath|FileName|Path)\s+(" + token + @")",
                     RegexOptions.IgnoreCase))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);
        foreach (var m in Regex.Matches(line, @"(?<!\S)&\s*(" + token + @")"))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);
        foreach (var m in Regex.Matches(
                     line,
                     @"(?:Process\]::Start|Process\.Start|\.Run|\.Exec|\.ShellExecute)\s*\(\s*(" + token + @")",
                     RegexOptions.IgnoreCase))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);
        foreach (var m in Regex.Matches(
                     line, @"ProcessStartInfo\]::new\(\s*(" + token + @")",
                     RegexOptions.IgnoreCase))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);
        foreach (var m in Regex.Matches(
                     stripped, @"(?:^|\s)(?:Start-Process|saps)\s+(?!-)(" + token + @")",
                     RegexOptions.IgnoreCase))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);
        foreach (var m in Regex.Matches(
                     stripped, @"Invoke-Item\s+(" + token + @")",
                     RegexOptions.IgnoreCase))
            candidates.Add(((System.Text.RegularExpressions.Match)m).Groups[1].Value);

        // Splatting: @name passes the parameters of a hashtable; the
        // launch target is that hashtable's FilePath/FileName/Path member,
        // literal or variable. The member sits on the opener line itself
        // when the hashtable is written on one line, and a MERGED splat
        // (`$sp = $common + $extra`, `$sp += @{ ... }`) is followed through
        // each operand's own hashtable, one or more hops. An operand that
        // resolves to nothing leaves the splat unresolved, and an
        // unresolved splat contributes NO candidate: the whole-line
        // fallback below stays in force, so a merged splat is never
        // blessed silently.
        foreach (var m in Regex.Matches(line, @"@(\w+)"))
        {
            var name = ((System.Text.RegularExpressions.Match)m).Groups[1].Value;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            if (TrySplatMembers(name, lines, visited, 0, out var members))
                candidates.AddRange(members);
        }

        return candidates
            .Where(c => !Regex.IsMatch(c, @"^[)\}]+$"))
            .ToList();
    }

    /// <summary>
    /// The FilePath/FileName/Path members behind a splat variable, through
    /// direct hashtable definitions, `+=` hashtable appends, and `$a + $b`
    /// compositions whose operands each resolve the same way. False means
    /// some operand resolved to nothing, and the caller must fall back to
    /// the whole line rather than trust an empty member set.
    /// </summary>
    private static bool TrySplatMembers(
        string name, string[] lines, HashSet<string> visited, int hops,
        out List<string> members)
    {
        members = new List<string>();
        if (hops > 4) return false;
        if (!visited.Add(name)) return true;

        var sawDefinition = false;
        var fullyResolved = true;
        for (var i = 0; i < lines.Length; i++)
        {
            // A definition line: `= @{`, `+= @{`, or `= $a + $b`.
            var definition = Regex.Match(lines[i],
                @"^\s*\$" + Regex.Escape(name) + @"\s*(?:\+?=)\s*(.+?)(?:\s*#.*)?$");
            if (!IsCode(lines[i]) || !definition.Success) continue;
            sawDefinition = true;
            var rhs = definition.Groups[1].Value;

            if (rhs.TrimStart().StartsWith("@{", StringComparison.Ordinal))
            {
                // Hashtable literal: read members from this line onward,
                // through the closing brace.
                for (var j = i; j < Math.Min(i + 40, lines.Length); j++)
                {
                    var member = Regex.Match(
                        lines[j],
                        @"(?:^\s*|@\{\s*)(?:FilePath|FileName|Path)\s*=\s*(.+?)(?:\s*[}\]].*)?(?:\s*#.*)?$",
                        RegexOptions.IgnoreCase);
                    if (member.Success)
                        members.Add(member.Groups[1].Value);
                    if (Regex.IsMatch(lines[j], @"^\s*\}") ||
                        (Regex.IsMatch(lines[j], @"\}\s*$") && j > i))
                        break;
                }
                continue;
            }

            // Composition: every operand must itself resolve.
            foreach (var operand in Regex.Matches(rhs, @"\$(\w+)"))
            {
                var operandName =
                    ((System.Text.RegularExpressions.Match)operand).Groups[1].Value;
                if (TrySplatMembers(operandName, lines, visited, hops + 1,
                        out var operandMembers))
                    members.AddRange(operandMembers);
                else
                    fullyResolved = false;
            }
        }

        return sawDefinition && fullyResolved;
    }

    /// <summary>
    /// Whether the target a launch line names is the app. Resolution
    /// follows the positional candidates (an app literal among them, or a
    /// candidate whose definitions/properties/functions reach one), then
    /// the whole line when no position could be decided. A candidate that
    /// resolves to a DEBUGGER is judged by the ruling: running the app as
    /// its debuggee is an app launch; attaching to a pid is not.
    /// Anything the rules cannot resolve fails CLOSED: the harness must
    /// name its target legibly or arm the guard, and an unresolved launch
    /// is flagged rather than trusted.
    /// </summary>
    private static bool TargetsApp(
        string line, string[] lines, Dictionary<string, List<string>> functions,
        bool callOperatorOnly)
    {
        if (Regex.IsMatch(line, @"dotnet\s+run", RegexOptions.IgnoreCase) &&
            ProjectReference.IsMatch(line)) return true;

        var stripped = QuotedSpan.Replace(line, "");
        var candidates = TargetCandidates(line, stripped, lines);

        // Literal app paths: among the candidates when a position was
        // decided, on the whole line otherwise (fail closed).
        if (candidates.Count > 0)
        {
            if (candidates.Any(c => AppLiteral.IsMatch(c))) return true;
        }
        else if (AppLiteral.IsMatch(line)) return true;

        // The debugger ruling: a candidate that resolves to a debugger is
        // an app launch exactly when the app is its debuggee (an app
        // literal among the remaining arguments, with no attach flag).
        foreach (var candidate in candidates)
        {
            if (!ResolvesToDebugger(candidate, lines, functions)) continue;
            if (AppLiteral.IsMatch(line) && !AttachFlag.IsMatch(line))
                return true;
            return false;
        }

        var varSource = candidates.Count > 0
            ? string.Join(" ", candidates)
            : stripped;
        var sawAny = false;
        var sawApp = false;
        var sawTooling = false;
        var sawEvidence = false;

        foreach (var name in AnyVar.Matches(varSource)
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
        return AnyVar.Matches(varSource)
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Any(n => ExeNamedVar.IsMatch("$" + n));
    }

    /// <summary>
    /// Whether a positional candidate resolves to a debugger binary: the
    /// candidate itself names one, or its definitions/properties do.
    /// </summary>
    private static bool ResolvesToDebugger(
        string candidate, string[] lines,
        Dictionary<string, List<string>> functions)
    {
        if (DebuggerToken.IsMatch(candidate)) return true;
        foreach (var name in AnyVar.Matches(candidate)
                     .Cast<System.Text.RegularExpressions.Match>()
                     .Select(m => m.Groups[1].Value)
                     .Distinct())
        {
            if (DefinitionsOf(lines, name).Any(d => DebuggerToken.IsMatch(d.Rhs)))
                return true;
            if (PropertyTargets(lines, name).Any(d => DebuggerToken.IsMatch(d)))
                return true;
        }
        return false;
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
        // One pattern covers plain assignments, parameter defaults
        // (`[string]$OutDir = ...`), and the one-line param-block spelling
        // `param([string]$OutDir = ...)` whose prefix is not a definition
        // of its own.
        var definition = new Regex(
            @"^\s*(?:param\s*\(\s*)?(?:\[[^\]]*\]\s*)?\$" + Regex.Escape(name) +
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
        // The COM method verb only counts in a file that creates the COM
        // shell object; .Run( exists on many innocuous objects.
        var comVerbs = LaunchVerbs.Where(v =>
            v.ToString().Contains("ShellExecute")).ToList();
        var effectiveVerbs = UsesComShell(lines)
            ? LaunchVerbs.ToList()
            : LaunchVerbs.Except(comVerbs).ToList();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!IsCode(line)) continue;

            // The verb must survive literal stripping: a manifest prose
            // string that mentions "cmd" and "start" is not a launch.
            var stripped = StripLiterals(line);
            var matching = effectiveVerbs.Where(v => v.IsMatch(line)).ToList();
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
            "violation-call-operator-literal.ps1",
            "violation-call-operator-interp.ps1",
            "violation-saps.ps1",
            "violation-com-run.ps1",
            "violation-cdb-launches-app.ps1",
            "violation-splatting.ps1",
            "violation-splat-composed.ps1",
            "violation-splat-plus-equals.ps1",
            "violation-scriptblock-launch.ps1",
            "violation-mixed-case.ps1",
        };
        var expectedClean = new HashSet<string>(StringComparer.Ordinal)
        {
            "clean-armed-launch.ps1",
            "clean-tooling-psi.ps1",
            "clean-tooling-debugger.ps1",
            "clean-cdb-attach.ps1",
            "clean-splatting-armed.ps1",
            "clean-splat-composed-armed.ps1",
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
    /// The staging side of the fixture corpus: every non-random shape the
    /// re-review proved the chain-follower blessed (direct fixed names,
    /// prev-named variables that are not restores, 'preview' literals, PID
    /// keys, clock keys, and the classic intermediate variable), plus a
    /// green control for each allowed form (guid, GetRandomFileName, the
    /// helper, and a genuine paired save/restore).
    /// </summary>
    [Fact]
    public void The_Scan_Still_Refuses_Every_NonRandom_Staging_Shape()
    {
        var root = RepoRoot();
        var fixtureDir = Path.Combine(
            root, "windows", "Ghostty.Tests", "Wiring", "ScanFixtures");
        Assert.True(Directory.Exists(fixtureDir), "ScanFixtures not found");

        var expectedViolations = new HashSet<string>(StringComparer.Ordinal)
        {
            "staging-direct-fixed.ps1",
            "staging-prevname-var.ps1",
            "staging-preview-literal.ps1",
            "staging-pid.ps1",
            "staging-clock.ps1",
            "staging-classic-var.ps1",
            "staging-param-default.ps1",
            "staging-set-item.ps1",
            "staging-setenvvar.ps1",
            "staging-member-wrong-restore.ps1",
        };
        var expectedClean = new HashSet<string>(StringComparer.Ordinal)
        {
            "staging-green-guid.ps1",
            "staging-green-randomfile.ps1",
            "staging-green-helper.ps1",
            "staging-green-restore.ps1",
        };

        var flagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(fixtureDir, "staging-*.ps1")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            var functions = FunctionBodies(lines);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!IsCode(line)) continue;
                if (!TryXdgAssignment(line, out var rhs)) continue;
                if (UnrandomizedStaging(
                        rhs, lines, functions,
                        new HashSet<string>(), 0) is not null)
                {
                    flagged.Add(name);
                    break;
                }
            }
        }

        var missing = expectedViolations.Except(flagged).ToList();
        Assert.True(
            missing.Count == 0,
            "staging shape(s) the scan wrongly blesses (fixture not " +
            "flagged): " + string.Join(", ", missing));

        var falseFlags = expectedClean.Intersect(flagged).ToList();
        Assert.True(
            falseFlags.Count == 0,
            "allowed staging shape(s) the scan flags: " +
            string.Join(", ", falseFlags));
    }

    /// <summary>
    /// Assignments of the config root, every spelling the process env can
    /// be written with: the dollar-env drive (process-wide or per-child
    /// psi), Set-Item on the env drive, and the .NET SetEnvironmentVariable
    /// call. All go through the same positive-proof randomness rule.
    /// </summary>
    private static readonly Regex[] XdgAssignments =
    {
        new(@"(?:\$env:XDG_CONFIG_HOME|EnvironmentVariables\['XDG_CONFIG_HOME'\])\s*=\s*(.+)$",
            RegexOptions.Compiled),
        new(@"Set-Item\s+Env:\\?XDG_CONFIG_HOME\s+(.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"SetEnvironmentVariable\(\s*['""]XDG_CONFIG_HOME['""]\s*,\s*([^,)]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private static bool TryXdgAssignment(string line, out string rhs)
    {
        foreach (var pattern in XdgAssignments)
        {
            var m = pattern.Match(line);
            if (m.Success)
            {
                rhs = m.Groups[1].Value;
                return true;
            }
        }
        rhs = "";
        return false;
    }

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
    /// <summary>
    /// Sources that are proof of randomness in a staging name. Anything
    /// else in the name-building position fails: the founder rule wants a
    /// randomly generated name per run.
    /// </summary>
    private static readonly string[] RandomnessMarkers =
    {
        "[guid]::NewGuid", "New-Guid", "GetRandomFileName",
        "RandomNumberGenerator", "Enter-WinttyTestConfig",
        "Start-SeamSession", "New-WinttyTestConfigRoot", "New-SeamToken",
    };

    /// <summary>
    /// Name sources that are positively NOT random, whatever else the line
    /// spells: the process id (reused across reboots), and any clock
    /// formatting (a within-a-minute collision class of its own).
    /// </summary>
    private static readonly Regex NonRandomNameSource = new(
        @"\$PID\b|Get-Date|HHmmss|yyyyMMdd|mmss",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Whether the expression builds a path with a fixed NAME in it: a
    /// quoted literal riding a temp base (the lazy direct form), or a
    /// quoted literal that is itself a path. Bare quoted segments with no
    /// path context ('primary', 'true', the fixed 'wintty' SUBDIRECTORY
    /// under an already-random root) are not root names and must not fire
    /// this.
    /// </summary>
    private static bool HasFixedName(string rhs)
    {
        var tempBase = rhs.Contains("$env:TEMP", StringComparison.Ordinal) ||
                       rhs.Contains("GetTempPath", StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in
                     QuotedSpan.Matches(rhs))
        {
            var body = m.Value.Substring(1, m.Value.Length - 2);
            if (body.Length == 0) continue;
            if (tempBase) return true;
            if (body.Contains('\\') || body.Contains('/') ||
                body.Contains(':')) return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the rhs is an exact save/restore: it assigns back a value
    /// provably SAVED FROM $env:XDG_CONFIG_HOME earlier in the file, paired
    /// by the variable or member, not by its name. A variable merely named
    /// $previousSomething whose definition is a fixed path is not a
    /// restore, and neither is a literal that happens to contain 'prev'.
    /// </summary>
    private static bool IsPairedRestore(string rhs, string[] lines)
    {
        var trimmed = rhs.Trim().TrimEnd('}', ';').Trim();
        var plain = Regex.Match(trimmed, @"^\$([A-Za-z_]\w*)$");
        if (plain.Success)
        {
            return DefinitionsOf(lines, plain.Groups[1].Value)
                .Any(d => d.Rhs.Contains(
                    "$env:XDG_CONFIG_HOME", StringComparison.Ordinal));
        }

        // The member shape ($Session.OrigXdg): the member was populated
        // from the env var inside a hashtable or object assignment.
        var member = Regex.Match(trimmed, @"^\$\w+[.:](\w+)$");
        if (member.Success)
        {
            var name = member.Groups[1].Value;
            var savedFrom = new Regex(
                @"^\s*" + Regex.Escape(name) +
                @"\s*=\s*(?:if\s*\(.*?\)\s*\{)?\s*[^#\r\n]*\$env:XDG_CONFIG_HOME");
            return lines.Any(l => IsCode(l) && savedFrom.IsMatch(l));
        }

        return false;
    }

    /// <summary>
    /// Randomness is a POSITIVE proof: the assigned expression, or every
    /// chain it is built from, must reach a random-name source. The
    /// assignment's own right-hand side carries the same tripwires the
    /// followed definitions do (a temp or fixed-literal base with no
    /// marker, a PID, a clock), so the direct lazy spelling cannot pass
    /// just because no variable is involved. The one exemption is the
    /// paired save/restore.
    /// </summary>
    private static string? UnrandomizedStaging(
        string rhs, string[] lines, Dictionary<string, List<string>> functions,
        HashSet<string> visited, int hops)
    {
        if (RandomnessMarkers.Any(m => rhs.Contains(m, StringComparison.Ordinal)))
            return null;

        if (IsPairedRestore(rhs, lines)) return null;

        // A member access whose pairing failed: if the base object is a
        // hashtable that SAVED the env var under a different member, this
        // is a wrong-variable restore, not inert data (R2-3). A base whose
        // definition is a randomness helper (the .Dir shape) is fine and
        // falls through to the normal variable follow below.
        var memberAccess = Regex.Match(
            rhs.Trim().TrimEnd('}', ';').Trim(), @"^\$(\w+)[.:](\w+)$");
        if (memberAccess.Success)
        {
            var baseName = memberAccess.Groups[1].Value;
            var opener = new Regex(
                @"^\s*\$" + Regex.Escape(baseName) + @"\s*=\s*@\{");
            for (var i = 0; i < lines.Length; i++)
            {
                if (!opener.IsMatch(lines[i])) continue;
                for (var j = i; j < Math.Min(i + 40, lines.Length); j++)
                {
                    if (lines[j].Contains(
                            "$env:XDG_CONFIG_HOME", StringComparison.Ordinal))
                        return $"member '{memberAccess.Groups[2].Value}' " +
                               $"is not the member the env var was saved under " +
                               $"(line {j + 1}: {lines[j].Trim()})";
                    if (Regex.IsMatch(lines[j], @"^\s*\}")) break;
                }
            }
        }

        if (hops > 6) return $"{rhs.Trim()} (chain too deep to verify)";

        // The own-RHS tripwires: reached without a marker, these are the
        // non-random staging shapes exactly as the re-review spelled them.
        if (NonRandomNameSource.IsMatch(rhs))
            return $"not a random name source: {rhs.Trim()}";
        if (rhs.Contains("$env:TEMP", StringComparison.Ordinal) ||
            rhs.Contains("GetTempPath", StringComparison.Ordinal) ||
            rhs.Contains("[System.IO.Path]::GetTempPath", StringComparison.Ordinal))
            return $"temp base with no random name: {rhs.Trim()}";
        if (HasFixedName(rhs))
            return $"fixed literal name: {rhs.Trim()}";

        // Follow variables assigned in this file.
        foreach (var match in VarRef.Matches(rhs).Cast<System.Text.RegularExpressions.Match>())
        {
            var name = match.Groups[1].Value;
            if (!visited.Add("$" + name)) continue;
            var defs = DefinitionsOf(lines, name);
            if (defs.Count == 0) continue;
            foreach (var def in defs)
            {
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
                if (!TryXdgAssignment(line, out var rhs)) continue;

                var offending = UnrandomizedStaging(
                    rhs, lines, functions, new HashSet<string>(), 0);
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
