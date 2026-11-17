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

--self-test runs the corpus; a rule that stops matching its own violation
shape fails the self-test even when the tree is clean.
"""
from __future__ import annotations

import ast
import datetime
import re
import subprocess
import sys
from pathlib import Path

# .agents/scripts/x.py -> repo root is two levels up from scripts, three from the file
REPO = Path(__file__).resolve().parent.parent.parent
ALLOW = REPO / "windows" / "scripts" / "spawn-allowlist.txt"

AREA_PREFIXES = ("windows/scripts/", ".agents/scripts/", "windows/Ghostty.Bench/")
NO_PARSER_HOSTS = {".psm1", ".psd1", ".cmd", ".bat", ".pyw", ".fsx", ".vb"}
PS_FILES = {".ps1"}
PY_FILES = {".py"}
CS_FILES = {".cs"}

_PS_SPAWN = re.compile(r"\b(Start-Process|saps)\b", re.IGNORECASE)
_PS_PSI = re.compile(r"new\s+System\.Diagnostics\.ProcessStartInfo|\[System\.Diagnostics\.ProcessStartInfo\]::new|\[Diagnostics\.ProcessStartInfo\]::new|New-Object\s+System\.Diagnostics\.ProcessStartInfo", re.IGNORECASE)
_PS_TYPE_START = re.compile(r"\[(System\.)?Diagnostics\.Process\]::Start\(", re.IGNORECASE)
_PS_HELD_WRITE = re.compile(r"\$env:INCODA_HELD\s*=|\[(System\.)?Environment\]::SetEnvironmentVariable\s*\(\s*['\"]INCODA_HELD|\bsetx\s+INCODA_HELD\b", re.IGNORECASE)
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
        cwd=REPO, capture_output=True, text=True).stdout
    if not out.strip():
        out = subprocess.run(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
            cwd=REPO.parent, capture_output=True, text=True).stdout
    if not out.strip():
        seen = set()
        for p in REPO.rglob("*"):
            if not p.is_file():
                continue
            s = p.relative_to(REPO).as_posix()
            if any(part in {".git", ".jj", ".zig-cache", "zig-out", "__pycache__", "node_modules", ".claude"} for part in p.parts):
                continue
            seen.add(s)
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
        assert len(parts) == 5, f"allowlist entry needs path|anchor|mechanism|owner|expiry: {raw}"
        path, anchor, mechanism, owner, expiry = parts
        assert "*" not in path and "?" not in path, f"allowlist glob refused: {raw}"
        assert owner and mechanism, f"allowlist entry needs owner and mechanism: {raw}"
        assert datetime.date.fromisoformat(expiry) >= datetime.date.today(), f"allowlist entry expired: {raw}"
        entries.append((path, anchor, mechanism))
    return entries


ALLOWED = None


def allowed(path: str, anchor: str, mechanism: str) -> bool:
    global ALLOWED
    if ALLOWED is None:
        ALLOWED = allow_entries()
    return any(p == path and a in anchor and m == mechanism for p, a, m in ALLOWED)


def _logical_statements(text):
    """Join backtick continuations, skipping pure comment lines: a Start-Process
    mentioned in a comment is not a spawn."""
    stmts, buf, start = [], [], None
    for i, line in enumerate(text.splitlines(), 1):
        if line.lstrip().startswith("#"):
            continue
        if start is None:
            start = i
        buf.append(line)
        if line.rstrip().endswith("`"):
            continue
        stmts.append((start, " ".join(buf)))
        buf, start = [], None
    if buf:
        stmts.append((start, " ".join(buf)))
    return stmts


def scan_ps(rel: str, text: str):
    findings = []
    for lineno, stmt in _logical_statements(text):
        spawn = _PS_SPAWN.search(stmt)
        if spawn:
            name = spawn.group(1)
            hidden = "-WindowStyle" in stmt and "Hidden" in stmt
            # The allowlist anchor is the anchor text appearing in the
            # statement (e.g. "Start-Process -FilePath $ExePath"), so pass
            # the whole statement, not just the command name.
            if not hidden and not allowed(rel, stmt, "app-under-test") and not allowed(rel, stmt, "intentional-gui") and not allowed(rel, stmt, "user-launcher"):
                findings.append(Finding(rel, lineno, f"{name} without -WindowStyle Hidden", "add -WindowStyle Hidden (or allowlist the intentional window)"))
        if _PS_PSI.search(stmt):
            # The properties are set on lines between the constructor and
            # the Start call; the span is the statement plus a bounded
            # lookahead, and both flags must appear in it.
            span = stmt + " " + " ".join(text.splitlines()[lineno:lineno + 24])
            if "CreateNoWindow" not in span or "UseShellExecute" not in span:
                findings.append(Finding(rel, lineno, "ProcessStartInfo without CreateNoWindow/UseShellExecute", "set UseShellExecute=$false and CreateNoWindow=$true before Process.Start"))
        if _PS_TYPE_START.search(stmt):
            # Start without any ProcessStartInfo in the file is a raw
            # ShellExecute-shaped path; with one, the per-file PSI check owns
            # flag compliance (the constructor and Start may be far apart).
            if _PS_PSI.search(text) is None and not allowed(rel, stmt, "app-under-test"):
                findings.append(Finding(rel, lineno, "[Diagnostics.Process]::Start without a ProcessStartInfo", "use System.Diagnostics.Process with CreateNoWindow"))
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


def scan_py(rel: str, text: str):
    findings = []
    tree = ast.parse(text, filename=rel)
    for node in ast.walk(tree):
        if not isinstance(node, ast.Call):
            continue
        name = _call_name(node)
        target = name.split(".")[-1]
        if name.startswith("subprocess.") and target in {"run", "Popen", "call", "check_output", "check_call"}:
            if not any(k.arg == "creationflags" for k in node.keywords):
                findings.append(Finding(rel, node.lineno, f"{name} without creationflags", "pass creationflags=CREATE_NO_WINDOW on Windows"))
        if name in {"os.system", "os.popen", "os.spawnv", "os.spawnl"}:
            findings.append(Finding(rel, node.lineno, f"{name}", "use subprocess with creationflags"))
    return findings


def scan_cs(rel: str, text: str):
    findings = []
    lines = text.splitlines()
    for i, line in enumerate(lines, 1):
        if _CS_PSI.search(line):
            window = "\n".join(lines[i - 1:i + 10])
            if "CreateNoWindow" not in window and not allowed(rel, "ProcessStartInfo", "app-under-test"):
                findings.append(Finding(rel, i, "ProcessStartInfo without CreateNoWindow", "set CreateNoWindow = true"))
    return findings


def scan():
    findings = []
    for p, rel in repo_files():
        suffix = p.suffix.lower()
        if suffix in PS_FILES:
            findings += scan_ps(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in PY_FILES:
            # the scanner's own git call is read-only and the scanner IS the rule
            if rel != ".agents/scripts/no_stray_windows.py":
                findings += scan_py(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in CS_FILES:
            findings += scan_cs(rel, (REPO / p).read_text(encoding="utf-8", errors="replace"))
        elif suffix in NO_PARSER_HOSTS:
            if not allowed(rel, "file", "passthrough"):
                findings.append(Finding(rel, 1, f"unparseable spawn host {suffix}", "refuse (no allowlist escape) or rewrite in a parsed language"))
    return findings


CORPUS = [
    ("ps", "scripts/x.ps1", "Start-Process pwsh -RedirectStandardOutput a -RedirectStandardError b", "WindowStyle"),
    ("ps", "scripts/x.ps1", "saps notepad.exe", "WindowStyle"),
    ("ps", "scripts/x.ps1", '[Diagnostics.Process]::Start("notepad.exe")', "Diagnostics.Process"),
    ("ps", "scripts/x.ps1", "$env:INCODA_HELD = 'wintty-build'", "INCODA_HELD"),
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
    if scan_ps("scripts/x.ps1", "Start-Process $exe -WindowStyle Hidden -PassThru"):
        failures.append("hidden Start-Process must not be flagged")
    if scan_py("tools/x.py", "import subprocess\nsubprocess.run(['x'], creationflags=0x08000000)"):
        failures.append("creationflags call must not be flagged")
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
        except AssertionError:
            pass
    finally:
        globals()["ALLOW"] = saved
        globals()["ALLOWED"] = None
    if failures:
        for f in failures:
            print(f"no-stray-windows selftest FAIL: {f}")
        return 1
    print("no-stray-windows selftest ok")
    return 0


def main(argv) -> int:
    if "--self-test" in argv:
        return self_test()
    findings = scan()
    for f in findings:
        print(f)
    if findings:
        print(f"\n{len(findings)} finding(s): every spawn suppresses its window AND captures output (AGENTS.md, No stray windows).")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
