"""No stray terminal windows: scan agent-reachable spawn sites (wintty).

Every spawn that can reach a Windows desktop must suppress its console
window AND keep its output: a console-subsystem child of a console-less
parent gets a fresh VISIBLE console from Windows, its focus steal happens
at creation, and redirected stdio does NOT prevent it (probes on this
machine). See AGENTS.md, "No stray windows".

Rules mirror wintty-release's tests/test_no_stray_windows.py:
  * PowerShell: Start-Process carries -WindowStyle Hidden (GUI launches are
    allowlisted by exact path); ProcessStartInfo sets UseShellExecute=$false
    AND CreateNoWindow=$true; no script writes INCODA_HELD.
  * Python: subprocess calls pass creationflags (CREATE_NO_WINDOW on
    Windows) or run through a winspawn-style helper.
  * C#: ProcessStartInfo sets CreateNoWindow = true.

tests/ is out of scope: product tests already pin their own launches
(CreateNoWindow = true throughout), and heavy test runs ride a lane.

shape fails the self-test even when the tree is clean.
"""
from __future__ import annotations

import ast
import datetime
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

# .agents/scripts/x.py -> repo root is two levels up from scripts, three from the file
REPO = Path(__file__).resolve().parent.parent.parent
ALLOW = REPO / "windows" / "scripts" / "spawn-allowlist.txt"

AREA_PREFIXES = ("windows/scripts/", ".agents/scripts/", "windows/Ghostty.Bench/")
# PowerShell modules are PowerShell: parse them with the same rules.
NO_PARSER_HOSTS = {".cmd", ".bat", ".pyw", ".fsx", ".vb"}
PS_FILES = {".ps1", ".psm1", ".psd1"}
PY_FILES = {".py"}
CS_FILES = {".cs"}

_PS_SPAWN = re.compile(r"\b(Start-Process|saps)\b", re.IGNORECASE)
_PS_PSI = re.compile(r"new\s+System\.Diagnostics\.ProcessStartInfo|\[System\.Diagnostics\.ProcessStartInfo\]::new|\[Diagnostics\.ProcessStartInfo\]::new|\[(?:System\.)?Diagnostics\.ProcessStartInfo\]\s*@\{|New-Object\s+System\.Diagnostics\.ProcessStartInfo", re.IGNORECASE)
_PS_TYPE_START = re.compile(r"\[(System\.)?Diagnostics\.Process\]::Start\(", re.IGNORECASE)
_PS_HELD_WRITE = re.compile(r"\$env:INCODA_HELD\s*=|\[(System\.)?Environment\]::SetEnvironmentVariable\s*\(\s*['\"]INCODA_HELD|\bsetx\s+INCODA_HELD\b", re.IGNORECASE)
_RE_CNW_TRUE = re.compile(r"CreateNoWindow\s*=\s*\$?true\b", re.IGNORECASE)
_RE_USE_FALSE = re.compile(r"UseShellExecute\s*=\s*\$?false\b", re.IGNORECASE)
_PS_HEAVY_BARE = re.compile(
    r"""(?:^\s*|(?:[;{(})=]|&&|\|\|)\s*)&\s+(?:\$?\([^)]*(?:zig|dotnet)[^)]*\)|[^\s;]*?(?:zig|dotnet)[^\s;]*)""",
    re.IGNORECASE)
# A quoted command argument is invisible to the string-stripped rule above,
# so it is matched on the raw text: the token straight after &, quoted.
_PS_HEAVY_QUOTED = re.compile(
    r"""(?:^\s*|(?:[;{(})=]|&&|\|\|)\s*)&\s+(?:"[^"]*(?:zig|dotnet)[^"]*"|'[^']*(?:zig|dotnet)[^']*'|\$?\([^)]*["'][^"']*(?:zig|dotnet)[^"']*["'][^)]*\))""",
    re.IGNORECASE)


def _heavy_ps(stmt: str) -> bool:
    return bool(_PS_HEAVY_BARE.search(_strip_ps(stmt))
                or _PS_HEAVY_QUOTED.search(_strip_ps_comments(stmt)))
_CS_PSI = re.compile(r"new\s+(System\.Diagnostics\.)?ProcessStartInfo\b|ProcessStartInfo\s*\{")


class Finding:
    __slots__ = ("path", "line", "mechanism", "fix")

    def __init__(self, path, line, mechanism, fix):
        self.path, self.line, self.mechanism, self.fix = path, line, mechanism, fix

    def __str__(self):
        return f"{self.path}:{self.line}: {self.mechanism}\n    fix: {self.fix}"


def repo_files():
    # jj workspaces report the whole repo as new (git ls-files is empty
    # there); fall back to walking the tree, which is what the scan means
    # anyway. .gitignore filtering comes from git when available.
    out = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
        cwd=REPO, capture_output=True, text=True,
        creationflags=0x08000000 if sys.platform == "win32" else 0).stdout
    if not out.strip():
        # A jj workspace (or a fresh tree) is enumerated by walking itself:
        # no parent checkout, and no jj lock to wait on while other sessions
        # are running jj commands. Directories are pruned, not filtered
        # after descent: the build trees hold six figures of files.
        skip = {".git", ".jj", ".zig-cache", "zig-out", "__pycache__", "node_modules",
                ".claude", "bin", "obj", ".vs", ".pytest_cache", "TestResults"}
        seen = set()
        for root, dirs, files in os.walk(REPO):
            dirs[:] = [d for d in dirs if d not in skip]
            for name in files:
                seen.add((Path(root) / name).relative_to(REPO).as_posix())
        out = "\n".join(sorted(seen))
    for raw in out.splitlines():
        p = Path(raw)
        s = p.as_posix()
        if not (s.startswith(AREA_PREFIXES) or (p.parent == Path(".") and p.suffix in PS_FILES)):
            continue
        if any(part in {"test", "tests", "__pycache__"} for part in p.parts):
            continue
        if p.name.endswith(".Tests.ps1"):  # pwsh test files quote spawn syntax as data
            continue
        yield p, s


def allow_entries():
    entries = []
    if not ALLOW.exists():
        return entries
    for raw in ALLOW.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if not line:
            continue
        parts = [p.strip() for p in line.split("|")]
        if len(parts) != 5:
            raise ValueError(f"allowlist entry needs path|anchor|mechanism|owner|expiry: {raw}")
        path, anchor, mechanism, owner, expiry = parts
        if "*" in path or "?" in path:
            raise ValueError(f"allowlist glob refused: {raw}")
        if not owner or not mechanism:
            raise ValueError(f"allowlist entry needs owner and mechanism: {raw}")
        if datetime.date.fromisoformat(expiry) < datetime.date.today():
            raise ValueError(f"allowlist entry expired: {raw}")
        target = REPO / path
        if target.suffix.lower() in PS_FILES and target.exists():
            hits = [st for _, st in _logical_statements(target.read_text(encoding="utf-8", errors="replace")) if anchor in st]
            if len(hits) != 1:
                raise ValueError(f"allowlist anchor must name exactly one statement in {path} (matched {len(hits)}): {anchor!r}")
        entries.append((path, anchor, mechanism))
    return entries


ALLOWED = None


def allowed(path: str, anchor: str, mechanism: str) -> bool:
    global ALLOWED
    if ALLOWED is None:
        ALLOWED = allow_entries()
    return any(p == path and a in anchor and m == mechanism for p, a, m in ALLOWED)


def _exempt(rel: str, stmt: str, mechanisms) -> bool:
    return any(allowed(rel, stmt, m) for m in mechanisms)


def _norm(text: str) -> str:
    """One space between tokens: continuation indentation and backtick pads
    are formatting, and an anchor must not depend on either."""
    return re.sub(r"\s+", " ", text).strip()


def _logical_statements(text):
    """Join backtick continuations, skipping pure comment lines: a Start-Process
    mentioned in a comment is not a spawn."""
    stmts, buf, start = [], [], None
    for i, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        if start is None:
            start = i
        # The trailing backtick is syntax, not part of the command text.
        buf.append(line.rstrip().rstrip("`").rstrip())
        if line.rstrip().endswith("`"):
            continue
        stmts.append((start, _norm(" ".join(buf))))
        buf, start = [], None
    if buf:
        stmts.append((start, _norm(" ".join(buf))))
    return stmts


def _has_prologue(text: str) -> bool:
    """The helper call as an assignment, outside comments: a mention in a
    block comment, a trailing comment or a string is not a guard."""
    in_block = False
    for line in text.splitlines():
        st = line.strip()
        if in_block:
            if "#>" in st:
                in_block = False
            continue
        if st.startswith("<#"):
            if "#>" not in st:
                in_block = True
            continue
        if st.startswith("#"):
            continue
        code = st.split(" #", 1)[0]
        if re.match(r'^\$[A-Za-z_]\w*\s*=\s*@?\(\s*Get-IncodaHeldKeys\b', code):
            return True
    return False


def _strip_cs(text: str) -> str:
    """Comments and string literals removed: a comment that says
    CreateNoWindow = true is not a setting."""
    out = []
    i, n = 0, len(text)
    while i < n:
        if text.startswith("//", i):
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text.startswith("/*", i):
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
        elif text[i] == '"':
            i += 1
            while i < n and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
            i += 1
        elif text[i] == "'":
            i += 1
            while i < n and text[i] != "'":
                i += 2 if text[i] == "\\" else 1
            i += 1
        else:
            out.append(text[i]); i += 1
    return "".join(out)


def _strip_ps(text: str) -> str:
    """Comments and quoted strings removed from a PowerShell span."""
    out = []
    i, n = 0, len(text)
    while i < n:
        if text.startswith("<#", i):
            j = text.find("#>", i + 2)
            i = n if j < 0 else j + 2
        elif text[i] == "#":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text[i] in "\"'":
            q = text[i]; i += 1
            while i < n and text[i] != q:
                i += 2 if text[i] == "`" else 1
            i += 1
        else:
            out.append(text[i]); i += 1
    return "".join(out)


def _strip_ps_comments(text: str) -> str:
    """Only comments removed: quoted heavy commands must stay visible."""
    out = []
    i, n = 0, len(text)
    while i < n:
        if text.startswith("<#", i):
            j = text.find("#>", i + 2)
            end = n if j < 0 else j + 2
            out.append("\n" * text.count("\n", i, end)); i = end
        elif text[i] == "#":
            j = text.find("\n", i)
            i = n if j < 0 else j
        elif text[i] in "\"'":
            q = text[i]; out.append(q); i += 1
            while i < n and text[i] != q:
                out.append(text[i]); i += 1
            if i < n: out.append(q); i += 1
        else:
            out.append(text[i]); i += 1
    return "".join(out)


def scan_ps(rel: str, text: str):
    findings = []
    has_prologue = _has_prologue(text)
    # Whole-file comment strip before splitting: a block comment can span
    # statements, and the splitter has no block state. Newlines are kept so
    # line numbers stay exact.
    code = _strip_ps_comments(text)
    for lineno, stmt in _logical_statements(code):
        spawn = _PS_SPAWN.search(stmt)
        if spawn:
            name = spawn.group(1)
            hidden = "-WindowStyle" in stmt and "Hidden" in stmt
            out = "-RedirectStandardOutput" in stmt
            err = "-RedirectStandardError" in stmt
            if "-NoNewWindow" in stmt and "-WindowStyle" in stmt:
                findings.append(Finding(rel, lineno, f"{name} mixes -NoNewWindow with -WindowStyle (invalid parameter set)", "keep exactly one: -WindowStyle Hidden (+ redirects) for a windowless console"))
            # Anchors name the statement, never the command: an entry that
            # said only Start-Process would exempt every launch in the file.
            if not hidden and not allowed(rel, stmt, "app-under-test") and not allowed(rel, stmt, "intentional-gui") and not allowed(rel, stmt, "user-launcher"):
                findings.append(Finding(rel, lineno, f"{name} without -WindowStyle Hidden", "add -WindowStyle Hidden (or allowlist the intentional window)"))
            if not (out and err) and not allowed(rel, stmt, "app-under-test") and not allowed(rel, stmt, "intentional-gui") and not allowed(rel, stmt, "user-launcher") and not allowed(rel, stmt, "daemon-self-logs") and not allowed(rel, stmt, "one-shot-no-output"):
                findings.append(Finding(rel, lineno, f"{name} without -RedirectStandardOutput/-Error", "redirect both streams (or allowlist a self-logging/one-shot tool)"))
        if _PS_PSI.search(stmt):
            span = _strip_ps(stmt + "\n" + "\n".join(text.splitlines()[lineno:lineno + 24]))
            if not (_RE_CNW_TRUE.search(span) and _RE_USE_FALSE.search(span)):
                findings.append(Finding(rel, lineno, "ProcessStartInfo without CreateNoWindow=true/UseShellExecute=false", "set UseShellExecute=$false and CreateNoWindow=$true before Process.Start"))
        if _PS_TYPE_START.search(stmt):
            if _PS_PSI.search(text) is None and not allowed(rel, stmt, "app-under-test"):
                findings.append(Finding(rel, lineno, "[Diagnostics.Process]::Start without a ProcessStartInfo", "use System.Diagnostics.Process with CreateNoWindow"))
        if (_heavy_ps(stmt) and not stmt.lstrip().startswith("#")
                and not has_prologue and not allowed(rel, stmt, "lane-exempt")):
            findings.append(Finding(rel, lineno, "heavy & spawn without the lane prologue", "refuse unless INCODA_HELD carries wintty-build (AGENTS.md)"))
        if _PS_HELD_WRITE.search(stmt):
            findings.append(Finding(rel, lineno, "writes INCODA_HELD", "never set INCODA_HELD in a script; incoda exports it"))
    return findings


def _call_name(node: ast.Call) -> str:
    f = node.func
    parts = []
    while isinstance(f, ast.Attribute):
        parts.append(f.attr)
        f = f.value
    if isinstance(f, ast.Name):
        parts.append(f.id)
    return ".".join(reversed(parts))


def _captures_output(node: ast.Call, text: str) -> bool:
    """A captured stream, by value: capture_output=False, stdout=None and
    DEVNULL are the same silent loss as no keyword at all."""
    for k in node.keywords:
        if k.arg == "capture_output":
            return (ast.get_source_segment(text, k.value) or "").strip() == "True"
    streams = [k for k in node.keywords if k.arg in {"stdout", "stderr"}]
    if not streams:
        return False
    for k in streams:
        seg = (ast.get_source_segment(text, k.value) or "").strip()
        if seg in {"None", "False"} or seg.endswith("DEVNULL"):
            return False
    return True


def scan_py(rel: str, text: str):
    findings = []
    tree = ast.parse(text, filename=rel)
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        name = _call_name(node)
        target = name.split(".")[-1]
        if name.startswith("subprocess.") and target in {"run", "Popen", "call", "check_output", "check_call"}:
            seg = ast.get_source_segment(text, node) or ""
            # A call that streams to the console it was given has no window of
            # its own, so CREATE_NO_WINDOW is not what keeps it quiet: measured
            # on Windows, shell=True with 0x08000000 discarded the child's
            # inherited stdout when the parent's was a pipe, and the same call
            # without it printed. The allowlist has to name such a site
            # streams-to-parent, so this stays fail-closed.
            streaming = target != "check_output" and not _captures_output(node, text)
            if not any(k.arg == "creationflags" for k in node.keywords):
                if not (streaming and _exempt(rel, seg, ("streams-to-parent",))):
                    findings.append(Finding(rel, node.lineno, f"{name} without creationflags", "pass creationflags=CREATE_NO_WINDOW on Windows"))
            else:
                src = ""
                for k in node.keywords:
                    if k.arg == "creationflags":
                        src = ast.get_source_segment(text, k.value) or ""
                        if src.isidentifier():
                            import re as _re
                            for m in _re.finditer(rf"^\s*{_re.escape(src)}\s*=\s*(.+)$", text, _re.M):
                                src += " " + m.group(1)
                if ("CREATE_NO_WINDOW" not in src and "0x08000000" not in src) or "CREATE_NEW_CONSOLE" in src or "DETACHED_PROCESS" in src:
                    findings.append(Finding(rel, node.lineno, f"{name} creationflags does not request CREATE_NO_WINDOW", "pass creationflags=CREATE_NO_WINDOW (0x08000000), never DETACHED_PROCESS or CREATE_NEW_CONSOLE"))
            if target != "check_output" and not _captures_output(node, text):
                seg = ast.get_source_segment(text, node) or ""
                if not _exempt(rel, seg, ("fire-and-forget", "streams-to-parent", "wrapper")):
                    findings.append(Finding(rel, node.lineno, f"{name} without capture/redirect", "capture stdout/stderr (or allowlist a deliberate fire-and-forget)"))
        if name in {"os.system", "os.popen", "os.spawnv", "os.spawnl"}:
            findings.append(Finding(rel, node.lineno, f"{name}", "use subprocess with creationflags"))
    return findings


def scan_cs(rel: str, text: str):
    findings = []
    lines = text.splitlines()
    for i, line in enumerate(lines, 1):
        if _CS_PSI.search(line):
            window = _strip_cs("\n".join(lines[i - 1:i + 10]))
            stmt = line.strip()
            if re.search(r"CreateNoWindow\s*=\s*false\b", window, re.IGNORECASE):
                findings.append(Finding(rel, i, "CreateNoWindow = false", "set CreateNoWindow = true"))
            elif "CreateNoWindow" not in window and not allowed(rel, stmt, "app-under-test"):
                findings.append(Finding(rel, i, "ProcessStartInfo without CreateNoWindow", "set CreateNoWindow = true"))
            if re.search(r"UseShellExecute\s*=\s*true\b", window, re.IGNORECASE):
                findings.append(Finding(rel, i, "UseShellExecute = true", "set UseShellExecute = false"))
            if not re.search(r"RedirectStandard(?:Output|Error)\s*=\s*true\b", window) and not _exempt(rel, stmt, ("app-under-test", "fire-and-forget", "streams-to-parent")):
                findings.append(Finding(rel, i, "ProcessStartInfo without a stream redirect", "redirect at least one stream by value (or allowlist a deliberate fire-and-forget)"))
            if ("RedirectStandardOutput" not in window and "RedirectStandardError" not in window) and not _exempt(rel, "ProcessStartInfo", ("app-under-test", "fire-and-forget")):
                findings.append(Finding(rel, i, "ProcessStartInfo without a stream redirect", "redirect at least one stream (or allowlist a deliberate fire-and-forget)"))
    return findings


def scan():
    findings = []
    for p, rel in repo_files():
        suffix = p.suffix.lower()
        if suffix in PS_FILES:
            findings += scan_ps(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in PY_FILES:
            findings += scan_py(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in CS_FILES:
            findings += scan_cs(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in NO_PARSER_HOSTS:
            findings.append(Finding(rel, 1, f"unparseable spawn host {suffix}", "rewrite in a parsed language (.ps1/.psm1/.py/.cs); no allowlist escape"))
    return findings


CORPUS = [
    ("ps", "scripts/x.ps1", "Start-Process pwsh -RedirectStandardOutput a -RedirectStandardError b", "WindowStyle"),
    ("ps", "scripts/x.ps1", "saps notepad.exe", "WindowStyle"),
    ("ps", "scripts/x.ps1", '[Diagnostics.Process]::Start("notepad.exe")', "Diagnostics.Process"),
    ("ps", "scripts/x.ps1", "$env:INCODA_HELD = 'wintty-build'", "INCODA_HELD"),
    ("ps", "scripts/x.ps1", '& "zig" build', "lane prologue"),
    ("ps", "scripts/x.ps1", '& "$env:ZIG_HOME\\zig.exe" build', "lane prologue"),
    ("ps", "scripts/x.ps1", '& "$zigPath" build', "lane prologue"),
    ("ps", "scripts/x.ps1", '& $env:ZIG_EXE build', "lane prologue"),
    ("ps", "scripts/x.ps1", '& (Get-Command zig).Source build', "lane prologue"),
    ("ps", "scripts/x.ps1", '# Get-IncodaHeldKeys is the guard\n& zig build install', "lane prologue"),
    ("ps", "scripts/x.ps1", "$p = New-Object System.Diagnostics.ProcessStartInfo", "CreateNoWindow"),
    ("py", "tools/x.py", "import subprocess\nsubprocess.Popen(['git','fetch'], stdout=subprocess.PIPE)", "creationflags"),
    ("js-cs", "tools/X.cs", 'var psi = new ProcessStartInfo { FileName = "x" };', "CreateNoWindow"),
]


def self_test() -> int:
    failures = []
    for kind, rel, snippet, needle in CORPUS:
        if kind == "ps":
            found = scan_ps(rel, snippet)
        elif kind == "py":
            found = scan_py(rel, snippet)
        else:
            found = scan_cs(rel, snippet)
        if not any(needle in f.mechanism for f in found):
            failures.append(f"corpus case not flagged: {snippet[:50]}")
    # negative cases
    if scan_ps("scripts/x.ps1", "Start-Process $exe -WindowStyle Hidden -PassThru -RedirectStandardOutput a -RedirectStandardError b"):
        failures.append("a hidden, captured Start-Process must not be flagged")
    if scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000, capture_output=True)"):
        failures.append("windowless captured call must not be flagged")
    if not scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000)"):
        failures.append("a windowless call that discards its output must be flagged")
    if scan_ps("scripts/x.ps1", "# & zig build install\n"):
        failures.append("a commented heavy command was flagged")
    if not scan_ps("scripts/x.ps1", "$out = & zig build install\n"):
        failures.append("a mid-statement heavy spawn was missed")
    if not scan_ps("scripts/x.ps1", "if ($x) { & zig build }\n"):
        failures.append("a braced heavy spawn was missed")
    if not scan_ps("scripts/x.ps1", "($x) & zig build\n"):
        failures.append("a closing-paren heavy spawn was missed")
    if scan_ps("scripts/x.ps1", "<#\n& zig build\n#>\n"):
        failures.append("a multi-line block-commented heavy command was flagged")
    if scan_ps("scripts/x.ps1", "$s = \"& zig build\"\n"):
        failures.append("a string literal was a heavy spawn")
    if scan_ps("scripts/x.ps1", "Write-Host \"run: $out = & zig build\"\n"):
        failures.append("a mid-statement operator inside a string was a spawn")
    if not scan_ps("scripts/x.ps1", '& ("C:\\Program Files\\zig\\zig.exe") build\n'):
        failures.append("a parenthesised quoted heavy exe was missed")
    if scan_ps("scripts/x.ps1", '& (Join-Path $PSScriptRoot "shard-runner.ps1") run\n'):
        failures.append("a non-heavy parenthesised path was flagged")
    if not scan_ps("scripts/x.ps1", "# Get-IncodaHeldKeys is the guard\n& zig build install\n"):
        failures.append("a comment mentioning the helper satisfied the lane rule")
    if not scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000, capture_output=False)"):
        failures.append("capture_output=False satisfied the capture rule")
    if not scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000, stdout=None)"):
        failures.append("stdout=None satisfied the capture rule")
    if scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000, stdout=subprocess.PIPE)"):
        failures.append("a piped stream was flagged")
    # the private's guard: first position, right key, comment-only is not a dep
    if _guard_problem("_test-in-lane", '_test-in-lane: # _require-lane "test"', set()) is None:
        failures.append("a comment-only guard satisfied the first-dependency rule")
    if _guard_problem("_fuzz-in-lane", '_fuzz-in-lane: (_require-lane "fuzz" "wintty-build")', {"_fuzz-in-lane"}) is None:
        failures.append("a desktop private accepted the build key")
    if _guard_problem("_fuzz-in-lane", '_fuzz-in-lane: (_require-lane "fuzz" "wintty-desktop")', {"_fuzz-in-lane"}) is not None:
        failures.append("the correct desktop guard was flagged")
    if _guard_problem("_test-in-lane", '_test-in-lane: test-configure (_require-lane "test")', set()) is not None:
        pass  # not-first is the point
    else:
        failures.append("a guard after a real dependency passed")
    # The shape check must fail when either branch drops its arguments, and
    # when the exit tail that carries incoda's status is gone.
    lane_body = (
        '. (Join-Path "{{justfile_directory()}}" ".agents\\scripts\\incoda-lane.ps1"); '
        "$env:WINTTY_LANE_DECISION = ''; "
        "if (Test-LaneReady -Keys @('wintty-build')) { $env:WINTTY_LANE_DECISION = 'lane'; "
        '& $inc run --queue wintty-build --reason "{{reason}}" --wait {{budget}} '
        '-- just {{private}} {{args}} } else { $env:WINTTY_LANE_DECISION = \'bare\'; '
        'Write-LaneFallback -Keys @(\'wintty-build\'); just {{private}} {{args}} }; '
        'exit ($LASTEXITCODE ?? 1)')
    if _lane_shape_findings(lane_body):
        failures.append("the good lane shape was flagged")
    if not _lane_shape_findings(lane_body.replace("just {{private}} {{args}} };", "just {{private}} };")):
        failures.append("a fallback branch without {{args}} passed the shape check")
    if not _lane_shape_findings(lane_body.replace("exit ($LASTEXITCODE ?? 1)", "")):
        failures.append("a missing exit-status tail passed the shape check")
    # The usable-side and desktop probes must fail when their evidence is
    # missing, not merely be present in the source.
    if not _decides_on("if (Test-LaneReady -Keys @('wintty-desktop'))", "wintty-desktop"):
        failures.append("a desktop decision was not recognised")
    if _decides_on("if (Test-LaneReady -Keys @('wintty-build'))", "wintty-desktop"):
        failures.append("a build-key decision passed as a desktop decision")
    good_laned = "run --queue wintty-build -- just _build-dll-in-lane"
    good_bare = "just _splash-race-in-lane SENTINEL123"
    good_build = "run --queue wintty-build -- just build-win"
    good_warning = "warning: desktop lane"
    if not _usable_probe_ok(good_laned, "ok", False):
        failures.append("a real laned run was not recognised")
    for label, args in (
        ("missing incoda run", ("", "ok", False)),
        ("a warning", (good_laned, good_warning, False)),
        ("a bare build", (good_laned, "ok", True)),
        ("the wrong queue", ("run --queue wintty-desktop", "ok", False)),
    ):
        if _usable_probe_ok(*args):
            failures.append(f"a usable-side probe with {label} passed")
    if not _desktop_probe_ok(good_bare, good_build, good_warning):
        failures.append("a desktop bare run was not recognised")
    for label, args in (
        ("no bare phase", ("", good_build, good_warning)),
        ("a desktop lane", (good_bare, "run --queue wintty-desktop", good_warning)),
        ("no warning", (good_bare, good_build, "")),
        ("two warnings", (good_bare, good_build, good_warning + "\n" + good_warning)),
    ):
        if _desktop_probe_ok(*args):
            failures.append(f"a desktop probe with {label} passed")
    # The generic caller probe must fail on each clause on its own, including
    # an arg that survives in the reason but is dropped from the private argv.
    tok = "SENTINEL123"
    private = "_splash-race-in-lane"
    argv = f'run --queue wintty-desktop --reason "x {tok}" -- just {private} "{tok}"'
    shim = f"just {private} {tok}"
    if not _argv_carries(argv, private, tok):
        failures.append("an anchored private argv was not recognised")
    if _argv_carries(f'run --queue wintty-desktop --reason "x {tok}" -- just {private}', private, tok):
        failures.append("a token in the reason passed as a private argv")
    ok_usable = (0, "ok", argv, shim, "wintty-desktop", "usable", private, tok)
    ok_closed = (0, "warning: desktop", "", shim, "wintty-desktop", "closed", private, tok)
    if not _caller_probe_ok(*ok_usable) or not _caller_probe_ok(*ok_closed):
        failures.append("a correct caller probe was rejected")
    for label, args in (
        ("a failed recipe", (1, "ok", argv, "", "wintty-desktop", "usable", private, tok)),
        ("a warning in the usable path", (0, "warning: x", argv, "", "wintty-desktop", "usable", private, tok)),
        ("no lane in the usable path", (0, "ok", "just " + private, "", "wintty-desktop", "usable", private, tok)),
        ("no args in the usable path", (0, "ok", f"run --queue wintty-desktop -- just {private}", shim, "wintty-desktop", "usable", private, tok)),
        ("no shimmed phase in the usable path", (0, "ok", argv, "", "wintty-desktop", "usable", private, tok)),
        ("a lane in the closed path", (0, "warning: x", "run --queue wintty-desktop", shim, "wintty-desktop", "closed", private, tok)),
        ("no bare phase", (0, "warning: x", "", "", "wintty-desktop", "closed", private, tok)),
        ("no args in the bare phase", (0, "warning: x", "", f"just {private}", "wintty-desktop", "closed", private, tok)),
        ("no warning in the closed path", (0, "ok", "", shim, "wintty-desktop", "closed", private, tok)),
        ("two warnings in the closed path", (0, "warning: a\nwarning: b", "", shim, "wintty-desktop", "closed", private, tok)),
        ("a duplicate bare phase", (0, "warning: a", "", shim + "\n" + shim, "wintty-desktop", "closed", private, tok)),
        ("a duplicate in-lane phase", (0, "ok", argv, shim + "\n" + shim, "wintty-desktop", "usable", private, tok)),
    ):
        if _caller_probe_ok(*args):
            failures.append(f"a caller probe with {label} passed")
    # allowlist validator refuses globs
    import tempfile
    with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False) as tf:
        tf.write("scripts/*.ps1 | Start-Process | app-under-test | me | 2099-01-01\n")
        tmp = tf.name
    saved = globals()["ALLOW"]
    globals()["ALLOW"] = Path(tmp)
    globals()["ALLOWED"] = None
    try:
        try:
            allow_entries()
            failures.append("allowlist glob was accepted")
        except ValueError:
            pass
    finally:
        globals()["ALLOW"] = saved
        globals()["ALLOWED"] = None
    # The allowlist is a reviewed surface: its CODEOWNERS entry must exist.
    owners = REPO / "CODEOWNERS"
    if not owners.exists() or not any(
            ln.strip().startswith("/windows/scripts/spawn-allowlist.txt") and "@" in ln
            for ln in owners.read_text(encoding="utf-8").splitlines()):
        failures.append("windows/scripts/spawn-allowlist.txt has no CODEOWNERS entry")
    if failures:
        for f in failures:
            print(f"no-stray-windows selftest FAIL: {f}")
        return 1
    print("no-stray-windows selftest ok")
    return 0


# --- justfile lane shape (design D1/D3(a)) -----------------------------------
# Every heavy recipe routes through the one-line _lane wrapper on Windows and
# its private body carries the lane guard as its FIRST dependency. Read through
# `just` itself: both repo parsers merge same-name bodies, so raw text cannot
# tell the [windows] variant from the [unix] one.
HEAVY = (
    "test", "test-configure", "test-lib-vt", "test-full", "test-pkg",
    "test-reachability", "test-examples", "build-dll", "build-dll-release",
    "build-win", "build-win-release", "test-win", "release-gate-check",
    "signoff", "signoff-full", "signoff-fresh", "default",
)
PRIVATE = {name: ("_" + name + "-in-lane") for name in HEAVY}
PRIVATE.update({"test": "_test-in-lane", "default": "_default-in-lane",
                "signoff": "_signoff-in-lane", "signoff-full": "_signoff-in-lane",
                "signoff-fresh": "_signoff-in-lane"})


def _just(*args, env=None):
    return subprocess.run(
        ["just", *args], cwd=REPO, capture_output=True, text=True, env=env,
        creationflags=0x08000000 if sys.platform == "win32" else 0)


def _guard_problem(name, header, desktop_privates):
    """The first dependency must be the lane guard, with the right key."""
    deps = header.split(":", 1)[1].strip() if ":" in header else ""
    m = re.match(r'\(_require-lane "([^"]+)"(?: "([^"]+)")?\)', deps)
    expected_key = "wintty-desktop" if name in desktop_privates else "wintty-build"
    if not m:
        return Finding("justfile", 0, f"{name} lacks the lane guard as its first dependency",
                       "first dependency: (_require-lane \"<public recipe>\" [\"<key>\"])")
    key = m.group(2) or "wintty-build"
    if key != expected_key:
        return Finding("justfile", 0, f"{name} guards the wrong lane key ({key})",
                       f"first dependency: (_require-lane \"{m.group(1)}\" \"{expected_key}\")")
    return None


def _normalize_show(text):
    """just --show reprints interpolations with spaces inside the braces."""
    return re.sub(r"\s+\}\}", "}}", re.sub(r"\{\{\s+", "{{", text))


DESKTOP_WRAPPERS = ("splash-race", "search-fuzz", "shader-notice-fuzz",
                    "frame-style-fuzz", "seam-theme-resolution", "theme-matrix", "fuzz")


def _decides_on(body, key):
    """Does this normalized recipe body ask readiness about the named key?"""
    return f"Test-LaneReady -Keys @('{key}')" in _normalize_show(body)


def _usable_probe_ok(incoda_log, transcript, zig_ran):
    """A usable lane was taken: incoda ran, nothing warned, no bare build."""
    return ("run --queue wintty-build" in incoda_log
            and "warning:" not in transcript
            and not zig_ran)


def _desktop_probe_ok(just_log, incoda_log, transcript):
    """A closed desktop lane went bare: its phase ran, no desktop lane taken."""
    return ("_splash-race-in-lane" in just_log
            and "--queue wintty-desktop" not in incoda_log
            and transcript.count("warning:") == 1)


def _argv_carries(log, private, token):
    """The token follows the private recipe in the argv, not just the reason."""
    marker = "just " + private
    return marker in log and token in log.split(marker, 1)[1]


def _caller_probe_ok(rc, transcript, incoda_log, shim_log, key, mode, private, token):
    """One shape for every lane caller: the lane and its args, or the fallback."""
    if rc != 0:
        return False
    if mode == "usable":
        if token:
            taken = (f"--queue {key}" in incoda_log
                     and shim_log.count(f"just {private}") == 1
                     and _argv_carries(incoda_log, private, token)
                     and _argv_carries(shim_log, private, token))
        else:
            taken = f"--queue {key}" in incoda_log and f"just {private}" in incoda_log
        return taken and "warning:" not in transcript
    if not private:
        return f"--queue {key}" not in incoda_log and transcript.count("warning:") == 1
    return (f"--queue {key}" not in incoda_log
            and shim_log.count(f"just {private}") == 1
            and _argv_carries(shim_log, private, token)
            and transcript.count("warning:") == 1)


def _lane_shape_findings(body):
    """What is wrong with a normalized windows _lane body; empty when sound."""
    bad = []
    anchored = 'Join-Path "{{justfile_directory()}}" ".agents\\scripts\\incoda-lane.ps1"'
    if anchored not in body:
        bad.append("the helper is not dot-sourced from {{justfile_directory()}}")
    if "Test-LaneReady -Keys @('wintty-build')" not in body:
        bad.append("the readiness decision is missing")
    if "Write-LaneFallback" not in body or "WINTTY_LANE_DECISION" not in body:
        bad.append("the fallback warning or its recorded decision is missing")
    if not body.rstrip().endswith("exit ($LASTEXITCODE ?? 1)"):
        bad.append("the exit-status tail is missing")
    lane_marker = "$env:WINTTY_LANE_DECISION = 'lane';"
    bare_marker = "$env:WINTTY_LANE_DECISION = 'bare';"
    if lane_marker not in body or bare_marker not in body:
        bad.append("one of the decision branches is missing")
    else:
        lane_branch = body.split(lane_marker, 1)[1].split("} else {", 1)[0]
        bare_branch = body.split(bare_marker, 1)[1]
        if "just {{private}} {{args}}" not in lane_branch:
            bad.append("the laned invocation drops {{args}}")
        if "just {{private}} {{args}}" not in bare_branch:
            bad.append("the fallback invocation drops {{args}}")
        if "--queue wintty-build" not in lane_branch:
            bad.append("the laned invocation has no build queue")
    return bad


# Values whose windows-body interpolation has been reviewed: raw ARGS
# passthrough, fixed paths and numbers. Function calls (replace(...),
# justfile_directory()) are safe by construction.
WINDOWS_INTERPOLATION = {
    "args", "budget", "exe", "iterations", "key", "name",
    "private", "reason", "recipes", "run", "TEST_SEED_BOUND", "TEST_VERSION",
    "sha", "pr",
}


def _interpolation_findings():
    """Windows-body interpolation sources. Text-only: runs on any host."""
    findings = []
    attributes = set()
    body_platform = "windows"
    for i, line in enumerate((REPO / "justfile").read_text(encoding="utf-8").splitlines(), 1):
        st = line.strip()
        if line[:1] == "[":
            attributes |= set(re.findall(r"[a-z]+", " ".join(re.findall(r"\[([^\]]*)\]", st.split("#", 1)[0])).lower()))
            continue
        if st and not line[:1].isspace() and not st.startswith("#"):
            # a recipe header consumes the attributes seen so far
            if attributes & {"unix", "linux", "macos"}:
                body_platform = "unix"
            else:
                body_platform = "windows"
            attributes = set()
            continue
        if line[:1].isspace() and st.startswith("#!"):
            body_platform = "unix" if ("bash" in st or st.endswith("/sh")) else "windows"
        if body_platform != "windows":
            continue
        if "{{TEST_SEED}}" in line:
            findings.append(Finding("justfile", i, "bare {{TEST_SEED}} in a windows body", "single-quote bind it (TEST_SEED_BOUND) or read $env:WINTTY_TEST_SEED in the body"))
        for m in re.finditer(r"\{\{\s*(.+?)\s*\}\}", line):
            expr = m.group(1)
            # Declared sources only: a new interpolation is a deliberate
            # decision (a value with a space or quote rewrites the command
            # line before PowerShell parses it).
            if (expr in WINDOWS_INTERPOLATION or expr == "justfile_directory()"
                    or expr.startswith("replace(")):
                continue
            findings.append(Finding("justfile", i, f"undeclared interpolation {{{{{expr}}}}} in a windows body", "add the value to the declared set, or pass it through the environment"))
    return findings


def check_justfile():
    findings = []
    if sys.platform != "win32":
        # The lane recipes and their guards are [windows]; on another host
        # `just --show` resolves the unix bodies and the windows assertions
        # do not apply. The spawn scan and interpolation checks still run.
        return _interpolation_findings()
    for name in HEAVY:
        r = _just("--show", name)
        if r.returncode != 0:
            findings.append(Finding("justfile", 0, f"recipe {name} missing", "restore the recipe and its lane wrapper"))
            continue
        body = r.stdout
        # The wrapper is a dependency-only recipe: the run line lives in
        # _lane's own body, so this checks the edge, not the text.
        lane = re.search(r'\(_lane "([^"]+)" "([^"]+)"', body)
        if not lane:
            findings.append(Finding("justfile", 0, f"{name} does not depend on the lane wrapper", f"[windows] {name}: (_lane \"{name}\" \"{PRIVATE[name]}\" <budget>)"))
        elif lane.group(1) != name or lane.group(2) != PRIVATE[name]:
            findings.append(Finding("justfile", 0, f"{name} wraps the wrong recipe ({lane.group(1)} -> {lane.group(2)})", f"[windows] {name}: (_lane \"{name}\" \"{PRIVATE[name]}\" <budget>)"))
    r = _just("--show", "_lane")
    body = _normalize_show(r.stdout)
    bad_shape = _lane_shape_findings(body)
    if r.returncode != 0 or bad_shape:
        findings.append(Finding("justfile", 0,
                                "_lane is not the one-line queued shape: " + ("; ".join(bad_shape) or "show failed"),
                                "one line: dot-source the anchored incoda-lane.ps1; if ready & $inc run --queue wintty-build ... else warn and run bare; both branches pass {{args}}; exit ($LASTEXITCODE ?? 1)"))
    build = _just("--show", "_build-in-lane")
    build_body = _normalize_show(build.stdout)
    if (build.returncode != 0
            or "-- just {{recipes}} } else {" not in build_body
            or "; just {{recipes}} }; exit ($LASTEXITCODE ?? 1)" not in build_body
            or "{{reason}}" not in build_body):
        findings.append(Finding("justfile", 0, "_build-in-lane lost its build passthrough",
                                "both decision branches run just {{recipes}}; the reason reaches incoda; exit ($LASTEXITCODE ?? 1)"))
    guard = _just("--show", "_require-lane")
    guard_body = _normalize_show(guard.stdout)
    if (guard.returncode != 0
            or 'Join-Path "{{justfile_directory()}}" ".agents\\scripts\\incoda-lane.ps1"' not in guard_body
            or "WINTTY_LANE_DECISION" not in guard_body):
        findings.append(Finding("justfile", 0, "_require-lane does not use the anchored decision",
                                "dot-source (Join-Path \"{{justfile_directory()}}\" \".agents\\scripts\\incoda-lane.ps1\") and read WINTTY_LANE_DECISION"))
    anchored = 'Join-Path "{{justfile_directory()}}" ".agents\\scripts\\incoda-lane.ps1"'
    for name in DESKTOP_WRAPPERS:
        wrapper = _just("--show", name)
        wrapper_body = _normalize_show(wrapper.stdout)
        if wrapper.returncode != 0 or not _decides_on(wrapper.stdout, "wintty-desktop"):
            findings.append(Finding("justfile", 0, f"{name} decides on the wrong lane key",
                                    "the caller must read wintty-desktop, matching its guard"))
        if anchored not in wrapper_body:
            findings.append(Finding("justfile", 0, f"{name} dot-sources the lane helper from a relative path",
                                    f"dot-source {anchored}"))
    desktop_privates = {"_fuzz-in-lane", "_splash-race-in-lane", "_search-fuzz-in-lane",
                        "_shader-notice-fuzz-in-lane", "_frame-style-fuzz-in-lane",
                        "_theme-matrix-in-lane"}
    for name in sorted(set(PRIVATE.values()) | desktop_privates):
        r = _just("--show", name)
        if r.returncode != 0:
            findings.append(Finding("justfile", 0, f"in-lane body {name} missing", "keep the real body in _<name>-in-lane"))
            continue
        header = next((ln for ln in r.stdout.splitlines()
                       if ln.strip()
                       and not ln.strip().startswith(("[", "#"))), "")
        problem = _guard_problem(name, header, desktop_privates)
        if problem:
            findings.append(problem)
            continue
        dry = _just("--dry-run", name)
        order = [ln.strip() for ln in (dry.stdout + dry.stderr).splitlines()
                 if ln.strip() and not ln.strip().startswith(("#", "echo"))][:3]
        if not any("Get-IncodaHeldKeys" in ln for ln in order):
            findings.append(Finding("justfile", 0, f"{name}'s dry run does not execute the guard before any real work", "keep (_require-lane ...) as the first dependency (its own incoda check may run first)"))
    # The leg map must cover every heavy recipe: a new heavy recipe with no
    # entry would otherwise route by the ALL_LEGS default, silently.
    try:
        import gate_scope  # noqa: PLC0415 - the repo's own leg map
        for name in HEAVY:
            if name not in gate_scope.RECIPE_LEGS:
                findings.append(Finding("justfile", 0, f"{name} has no RECIPE_LEGS entry", "add the recipe's legs to .agents/scripts/gate_scope.py"))
    except Exception as exc:  # a broken import is itself a finding
        findings.append(Finding("justfile", 0, f"cannot read gate_scope.RECIPE_LEGS: {exc}", "keep .agents/scripts/gate_scope.py importable"))
    # Behavioural, both sides. The fake incoda reports whatever status file
    # the check writes, in a temp dir, so this reads no real queue state.
    import tempfile  # noqa: PLC0415 - only this Windows-only check needs it

    def read_log(path):
        p = Path(path)
        return p.read_text(encoding="ascii", errors="replace") if p.exists() else ""

    def has_incoda(entry):
        if not entry:
            return False
        try:
            return any(Path(entry).glob("incoda.*"))
        except OSError:
            return False

    def ready_query(env, *keys):
        return subprocess.run(
            ["pwsh", "-NoProfile", "-File", str(REPO / ".agents/scripts/incoda-lane.ps1"),
             "-Ready", "-Queue", *keys],
            cwd=REPO, capture_output=True, text=True, env=env,
            creationflags=0x08000000 if sys.platform == "win32" else 0)

    with tempfile.TemporaryDirectory() as td:
        base = {k: v for k, v in os.environ.items() if k.upper() not in ("INCODA_HELD", "WINTTY_LANE_DECISION")}
        base["LOCALAPPDATA"] = td
        base["PATH"] = td + os.pathsep + base.get("PATH", "")
        status_file = Path(td) / "status.json"
        (Path(td) / "incoda.cmd").write_text(
            '@echo off\nif /I "%1"=="status" (\n  type "' + str(status_file) + '"\n  exit /b 0\n)\nexit /b 1\n',
            encoding="ascii")
        build_ready = ('{"schema":1,"queues":[{"key":"wintty-build","exists":true,'
                       '"config":{"slots":3,"require_reason":true}}]}')
        status_file.write_text(build_ready, encoding="ascii")

        env = dict(base)
        r = _just("_require-lane", "scan-probe", env=env)
        if r.returncode == 0 or "Run: just" not in (r.stdout + r.stderr):
            findings.append(Finding("justfile", 0, "_require-lane does not refuse outside a usable lane",
                                    "with a usable lane the guard must exit nonzero naming the public recipe when INCODA_HELD lacks wintty-build"))
        if ready_query(env, "wintty-build").returncode != 0:
            findings.append(Finding("justfile", 0, "-Ready rejects a usable wintty-build lane",
                                    "Test-LaneReady must accept a queue that exists and is open"))

        desktop_closed = ('{"schema":1,"queues":[{"key":"wintty-build","exists":true,'
                          '"config":{"slots":3,"require_reason":true}},'
                          '{"key":"wintty-desktop","exists":true,"config":{"closed":"maintenance"}}]}')
        status_file.write_text(desktop_closed, encoding="ascii")
        if ready_query(env, "wintty-desktop").returncode == 0:
            findings.append(Finding("justfile", 0, "-Ready accepts a closed wintty-desktop lane",
                                    "Test-LaneReady must check every named key, not just wintty-build"))

        bare = dict(base)
        bare["PATH"] = os.pathsep.join(p for p in bare["PATH"].split(os.pathsep) if not has_incoda(p))
        r = _just("_require-lane", "scan-probe", env=bare)
        if r.returncode != 0:
            findings.append(Finding("justfile", 0, "_require-lane blocks the warned bare run",
                                    "with no usable lane the guard must exit 0 and let the caller's bare run through"))
        if ready_query(bare, "wintty-build").returncode == 0:
            findings.append(Finding("justfile", 0, "-Ready accepts a machine with no incoda",
                                    "Test-LaneReady must refuse when incoda is absent from PATH and LOCALAPPDATA"))

        # Behavioural, both decision branches. Text alone cannot tell a live
        # branch from one whose condition was neutralized.
        unlinked_status = (
            '{"schema":1,"queues":[{"key":"builds","exists":true,"is_pool":true,"config":{"slots":1}},'
            '{"key":"wintty-build","exists":true,"config":{"slots":3,"require_reason":true}}]}')

        def probe_env(name, payload, execute_child=False):
            root = Path(td) / name
            root.mkdir(parents=True, exist_ok=True)
            warnings = Path(td) / (name + "-warnings")
            warnings.mkdir(parents=True, exist_ok=True)
            env = {k: v for k, v in os.environ.items()
                   if k.upper() not in ("INCODA_HELD", "WINTTY_LANE_DECISION", "INCODA_DIR")}
            env["LOCALAPPDATA"] = str(root)
            env["PATH"] = str(root) + os.pathsep + env.get("PATH", "")
            env["WINTTY_WARNING_DIR"] = str(warnings)
            env["WINTTY_INCODA_WARNED"] = "0"
            env["ZIG_LOG"] = str(root / "zig.log")
            env["INC_LOG"] = str(root / "incoda.log")
            (root / "zig.cmd").write_text(
                '@echo off\necho STUB >> "%ZIG_LOG%"\nexit /b 0\n', encoding="ascii")
            payload_file = root / "status.json"
            payload_file.write_text(payload, encoding="ascii")
            if execute_child:
                # Emulate a real lane holder: log argv, then run the child
                # with a ticket so the inner guard sees the same decision.
                runner = root / "fake_incoda.py"
                runner.write_text(
                    'import os, subprocess, sys\n'
                    'args = sys.argv[1:]\n'
                    'if args and args[0] == "status":\n'
                    '    sys.stdout.write(open(os.environ["INC_STATUS_FILE"]).read())\n'
                    '    sys.exit(0)\n'
                    'with open(os.environ["INC_LOG"], "a") as log:\n'
                    '    log.write(" ".join(args) + "\\n")\n'
                    'if "--" in args:\n'
                    '    child_env = os.environ.copy()\n'
                    '    child_env["INCODA_HELD"] = "wintty-build=1-1.ticket,wintty-desktop=1-2.ticket"\n'
                    '    sys.exit(subprocess.call(" ".join(args[args.index("--") + 1:]), shell=True, env=child_env))\n'
                    'sys.exit(0)\n', encoding="ascii")
                env["INC_STATUS_FILE"] = str(payload_file)
                (root / "incoda.cmd").write_text(
                    '@echo off\npython "%~dp0fake_incoda.py" %*\nexit /b %ERRORLEVEL%\n',
                    encoding="ascii")
            else:
                (root / "incoda.cmd").write_text(
                    '@echo off\nif /I "%1"=="status" (\n  type "' + str(payload_file) + '"\n'
                    '  exit /b 0\n)\nif /I "%1"=="run" (\n  echo %* >> "%INC_LOG%"\n'
                    '  exit /b 0\n)\nexit /b 1\n', encoding="ascii")
            return env, root

        def check_build(env, label):
            Path(env["ZIG_LOG"]).unlink(missing_ok=True)
            r = _just("build-dll", env=env)
            text = r.stdout + r.stderr
            if r.returncode != 0 or text.count("warning:") != 1 or not Path(env["ZIG_LOG"]).exists():
                findings.append(Finding("justfile", 0, f"{label}: an unusable lane did not warn and build bare",
                                        "rc 0, exactly one warning, and the stubbed zig ran"))

        unlinked_env, _ = probe_env("unlinked-toolchain", unlinked_status)
        check_build(unlinked_env, "unlinked lane")

        # First status says unlinked, second says linked. The guard must not
        # re-decide from the second answer.
        flip_env, flip_root = probe_env("flip-toolchain", unlinked_status)
        linked_file = flip_root / "linked.json"
        linked_file.write_text(build_ready, encoding="ascii")
        flip_env["INC_COUNTER"] = str(flip_root / "seen")
        flip_env["INC_UNLINKED"] = str(flip_root / "status.json")
        flip_env["INC_LINKED"] = str(linked_file)
        (flip_root / "incoda.cmd").write_text(
            '@echo off\nif /I "%1"=="status" (\n  if exist "%INC_COUNTER%" (\n'
            '    type "%INC_LINKED%"\n  ) else (\n    echo 1 > "%INC_COUNTER%"\n'
            '    type "%INC_UNLINKED%"\n  )\n  exit /b 0\n)\nexit /b 0\n',
            encoding="ascii")
        check_build(flip_env, "state-flipped lane")

        # A recorded bare decision clears the guard even on a usable lane.
        bare_env, _ = probe_env("decision-toolchain", build_ready)
        bare_env["WINTTY_LANE_DECISION"] = "bare"
        if _just("_require-lane", "scan-probe", env=bare_env).returncode != 0:
            findings.append(Finding("justfile", 0, "a recorded bare decision did not clear the guard",
                                    "with WINTTY_LANE_DECISION=bare the guard must exit 0 even on a usable lane"))

        # The other side of the decision: a usable lane must actually be
        # taken. Logging incoda's argv catches an always-bare caller that
        # every textual assertion still accepts.
        linked = (
            '{"schema":1,"queues":[{"key":"builds","exists":true,"is_pool":true,"config":{"slots":1}},'
            '{"key":"wintty-build","exists":true,"config":{"slots":3,"require_reason":true,"pools":["builds"]}}]}')
        usable_env, _ = probe_env("usable-toolchain", linked)
        Path(usable_env["INC_LOG"]).unlink(missing_ok=True)
        Path(usable_env["ZIG_LOG"]).unlink(missing_ok=True)
        r = _just("build-dll", env=usable_env)
        incoda_log = read_log(usable_env["INC_LOG"])
        if r.returncode != 0 or not _usable_probe_ok(incoda_log, r.stdout + r.stderr,
                                                     Path(usable_env["ZIG_LOG"]).exists()):
            findings.append(Finding("justfile", 0, "a usable lane was not taken by just build-dll",
                                    "incoda run --queue wintty-build in the log, no warning, no bare zig"))

        # Desktop callers must read their own key. A shim named just records
        # the phase's child instead of launching a GUI harness.
        desktop_status = (
            '{"schema":1,"queues":[{"key":"builds","exists":true,"is_pool":true,"config":{"slots":1}},'
            '{"key":"wintty-build","exists":true,"config":{"slots":3,"require_reason":true,"pools":["builds"]}},'
            '{"key":"wintty-desktop","exists":true,"config":{"closed":"maintenance"}}]}')
        desktop_env, desktop_root = probe_env("desktop-toolchain", desktop_status)
        shim = desktop_root / "justshim"
        shim.mkdir()
        just_log = shim / "just.log"
        desktop_env["JUST_LOG"] = str(just_log)
        (shim / "just.cmd").write_text(
            '@echo off\necho %* >> "%JUST_LOG%"\nexit /b 0\n', encoding="ascii")
        desktop_env["PATH"] = str(shim) + os.pathsep + desktop_env["PATH"]
        real_just = shutil.which("just")
        r = subprocess.run([real_just, "splash-race"], cwd=REPO,
                           capture_output=True, text=True, env=desktop_env,
                           creationflags=0x08000000 if sys.platform == "win32" else 0)
        recorded_just = read_log(just_log)
        recorded_incoda = read_log(desktop_env["INC_LOG"])
        if r.returncode != 0 or not _desktop_probe_ok(recorded_just, recorded_incoda, r.stdout + r.stderr):
            findings.append(Finding("justfile", 0, "splash-race did not fall back on a closed desktop lane",
                                    "the desktop phase runs bare, no wintty-desktop lane is taken, exactly one warning"))

        # One harness for every caller, instead of a probe hand-written for
        # each recipe. A condition weakened in any caller fails its own row.
        def lane_status(closed_key=None):
            queues = [
                {"key": "builds", "exists": True, "is_pool": True, "config": {"slots": 1}},
                {"key": "computer-use", "exists": True, "is_pool": True, "config": {"slots": 1}},
                {"key": "wintty-build", "exists": True,
                 "config": {"slots": 3, "require_reason": True, "pools": ["builds"]}},
                {"key": "wintty-desktop", "exists": True,
                 "config": {"slots": 1, "require_reason": True, "pools": ["computer-use"]}},
            ]
            for q in queues:
                if q["key"] == closed_key:
                    q["config"]["closed"] = "maintenance"
            return json.dumps({"schema": 1, "queues": queues}, separators=(",", ":"))

        callers = [
            ("_lane", ["probe-caller", "_build-dll-in-lane", "1m"], "wintty-build", False),
            ("_build-in-lane", ["probe-caller", "build-dll"], "wintty-build", False),
        ] + [(name, ["SENTINEL123"], "wintty-desktop", True) for name in DESKTOP_WRAPPERS]

        for recipe, args, key, wrapper in callers:
            for mode in ("usable", "closed"):
                payload = lane_status(None if mode == "usable" else key)
                env, root = probe_env("caller-" + recipe + "-" + mode, payload,
                                      execute_child=(mode == "usable" and wrapper))
                shim = root / "justshim"
                shim.mkdir()
                just_log = shim / "just.log"
                env["JUST_LOG"] = str(just_log)
                (shim / "just.cmd").write_text(
                    '@echo off\necho just %* >> "%JUST_LOG%"\nexit /b 0\n', encoding="ascii")
                env["PATH"] = str(shim) + os.pathsep + env["PATH"]
                r = subprocess.run([real_just, recipe, *args], cwd=REPO,
                                   capture_output=True, text=True, env=env,
                                   creationflags=0x08000000 if sys.platform == "win32" else 0)
                private = "_" + recipe + "-in-lane" if wrapper else ""
                ok = _caller_probe_ok(
                    r.returncode, r.stdout + r.stderr,
                    read_log(env["INC_LOG"]), read_log(just_log),
                    key, mode, private, "SENTINEL123" if wrapper else "")
                if not ok:
                    findings.append(Finding(
                        "justfile", 0,
                        f"{recipe} did not take the {mode} path for {key}",
                        f"usable: run --queue {key} with no warning; closed: warn and run bare"))

        # Warning dedupe across sibling shells, where the environment marker
        # cannot travel between them.
        sibling = Path(td) / "sibling-probe"
        (sibling / ".agents" / "scripts").mkdir(parents=True)
        shutil.copyfile(REPO / ".agents/scripts/incoda-lane.ps1",
                        sibling / ".agents/scripts/incoda-lane.ps1")
        dot = '. (Join-Path "{{justfile_directory()}}" ".agents\\scripts\\incoda-lane.ps1")'
        (sibling / "justfile").write_text(
            'set windows-shell := ["pwsh.exe", "-NoLogo", "-NoProfile", "-Command"]\n'
            'a:\n    ' + dot + "; Write-LaneFallback -Keys @('wintty-build') -Label 'phase a'\n"
            'b: a\n    ' + dot + "; Write-LaneFallback -Keys @('wintty-build') -Label 'phase b'\n",
            encoding="utf-8")
        sib_env = dict(bare)
        sib_env["WINTTY_WARNING_DIR"] = str(Path(td) / "sibling-markers")
        sib_env["WINTTY_INCODA_WARNED"] = "0"
        r = subprocess.run([real_just, "-f", str(sibling / "justfile"), "b"],
                           cwd=REPO, capture_output=True, text=True, env=sib_env,
                           creationflags=0x08000000 if sys.platform == "win32" else 0)
        if r.returncode != 0 or (r.stdout + r.stderr).count("warning:") != 1:
            findings.append(Finding("justfile", 0, "sibling phases did not share one warning",
                                    "the parent-key marker must suppress the second warning"))
    return findings


def main(argv) -> int:
    if "--self-test" in argv:
        return self_test()
    findings = scan() + check_justfile()
    for f in findings:
        print(f)
    if findings:
        print(f"\n{len(findings)} finding(s): every spawn suppresses its window AND captures output (AGENTS.md, No stray windows).")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
