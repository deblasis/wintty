#!/usr/bin/env python3
"""Environment doctor for the quality gates.

The merge gate and workspace guard are hooks, and a hook that fails to
spawn does not fail closed - it is simply absent. This doctor makes that
absence loud instead of silent: a SessionStart hook runs it in --session
mode at the start of every session in this repo, and it reports anything
the gates depend on that is broken (missing tools, missing scripts, hook
wiring pointing at files that do not exist, unparseable settings). When
everything is healthy it prints nothing.

If the interpreter itself is missing, this script cannot run - but the
hook host surfaces the spawn error at session start, which is the same
loud signal by a different route.

Modes:
  --session    fast checks, JSON systemMessage output only when something
               is wrong (quiet when healthy). Warnings about optional
               tools are suppressed so cross-platform clones do not get
               nagged about Windows-only legs.
  (default)    full human-readable report, including the optional tools
               (signoff ladder, nightly), on Windows whether the nightly
               scheduled task is registered, and where incoda is present
               whether the heavy job lanes match what lanes.ps1 records.
  --self-test  exercise the check logic with injected resolvers.

Run with: just doctor
"""

import json
import os
import shutil
import subprocess
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

# Required: the gates and the daily PR flow cannot work without these.
REQUIRED_TOOLS = ["git", "gh", "just"]
# Optional: the signoff ladder and the nightly appliance need these; their
# absence is a note, not a failure, so non-Windows clones stay quiet.
OPTIONAL_TOOLS = ["zig", "dotnet", "pwsh"]
GATE_SCRIPTS = ["pr_gate.py", "workspace_guard.py", "signoff.py", "merge_guard.py",
                "resignoff_bot.py", "leg_cache.py"]


def check(which=shutil.which, settings_path=None, script_dir=SCRIPT_DIR):
    """Returns (problems, notes)."""
    problems, notes = [], []

    for t in REQUIRED_TOOLS:
        if not which(t):
            problems.append(f"required tool missing from PATH: {t}")
    for t in OPTIONAL_TOOLS:
        if not which(t):
            notes.append(f"optional tool missing (signoff/nightly legs need it): {t}")

    for s in GATE_SCRIPTS:
        if not os.path.isfile(os.path.join(script_dir, s)):
            problems.append(f"gate script missing: {s}")

    if settings_path is None:
        settings_path = os.path.join(REPO_ROOT, ".claude", "settings.json")
    if not os.path.isfile(settings_path):
        problems.append(".claude/settings.json missing: the merge gate and workspace guard are not wired")
    else:
        try:
            with open(settings_path, encoding="utf-8") as f:
                cfg = json.load(f)
            for event in (cfg.get("hooks") or {}).values():
                for matcher in event:
                    for hook in matcher.get("hooks", []):
                        for arg in hook.get("args") or []:
                            if arg.startswith("${CLAUDE_PROJECT_DIR}/"):
                                rel = arg.split("}/", 1)[1]
                                if not os.path.isfile(os.path.join(REPO_ROOT, rel)):
                                    problems.append(f"hook points at a missing file: {rel}")
        except ValueError:
            problems.append(".claude/settings.json is not valid JSON; ALL hooks from it are silently disabled")

    return problems, notes


def nightly_task_registered():
    """Windows only; returns True/False/None (None = could not determine)."""
    if os.name != "nt":
        return None
    try:
        out = subprocess.run(
            ["pwsh", "-NoProfile", "-Command",
             "[bool](Get-ScheduledTask -TaskName wintty-nightly-quality -ErrorAction SilentlyContinue)"],
            capture_output=True, text=True, timeout=15,
        )
        return out.stdout.strip() == "True"
    except Exception:
        return None


def incoda_path(which=shutil.which, environ=os.environ):
    """The justfile's lookup: PATH, then the installer's location."""
    found = which("incoda")
    if found:
        return found
    local = environ.get("LOCALAPPDATA")
    if local:
        candidate = os.path.join(local, "Programs", "incoda", "incoda.exe")
        if os.path.isfile(candidate):
            return candidate
    return None


def lane_config(run=subprocess.run, which=shutil.which, environ=os.environ):
    """Returns (problems, notes) for the heavy job lanes (AGENTS.md).

    incoda's queue configuration lives in its own state dir, where nothing
    in the repo can see it, so a lane that quietly lost its slots or its
    close would otherwise stay wrong until a collision found it. lanes.ps1
    is the record; its -Check exits 1 with one line per drift, 2 when it
    could not read the machine. A missing incoda is a note, like the other
    optional tools, so a clone that is not the development machine stays
    quiet; drift where incoda is present is a problem."""
    problems, notes = [], []
    inc = incoda_path(which, environ)
    if not inc:
        notes.append("optional tool missing (heavy job lanes need it): incoda")
        return problems, notes
    try:
        version = run([inc, "version"], capture_output=True, text=True, timeout=15)
        first = (version.stdout or "").strip().splitlines()
        notes.append(f"{first[0] if first else 'incoda (version unknown)'} at {inc}")
    except Exception as e:
        problems.append(f"incoda at {inc} could not report its version: {e}")
        return problems, notes
    if not which("pwsh"):
        notes.append("lane config not checked: pwsh missing, lanes.ps1 needs it")
        return problems, notes
    try:
        out = run(["pwsh", "-NoProfile", "-File", os.path.join(SCRIPT_DIR, "lanes.ps1"), "-Check"],
                  capture_output=True, text=True, timeout=60)
    except Exception as e:
        problems.append(f"lane config could not be checked: {e}")
        return problems, notes
    lines = [l.strip() for l in (out.stdout or "").splitlines() if l.strip()]
    if out.returncode == 0:
        notes.append("lanes match .agents/scripts/lanes.ps1")
    elif out.returncode == 1:
        drift = [l for l in lines if l.startswith("drift ")]
        for l in drift:
            problems.append(f"lane config {l} (just lanes)")
        if not drift:
            # lanes.ps1 exits 1 for drift, but so does any unhandled error in
            # it, and that one prints nothing on stdout: the reason is on
            # stderr, which nothing here used to read. "lane config drifted"
            # is then the wrong diagnosis for a machine that was never read
            # at all, and it sends the reader to `just lanes`, which fails
            # the same way.
            said = " ".join((out.stderr or "").split()) or " ".join(lines)
            problems.append("lane config drifted from .agents/scripts/lanes.ps1 (just lanes)"
                            + (f"; lanes.ps1 said: {said}" if said else ""))
    else:
        problems.append("lane config could not be checked: " + (" ".join(lines) or f"lanes.ps1 exited {out.returncode}"))
    return problems, notes


def deferred_debt():
    """Outstanding deferred signoffs. Reported at session start because a
    skip nobody is reminded of is indistinguishable from a pass."""
    sys.path.insert(0, SCRIPT_DIR)
    try:
        import gate_scope
    except ImportError:
        return []
    out = subprocess.run(["git", "rev-parse", "--git-common-dir"],
                         cwd=REPO_ROOT, capture_output=True, text=True, timeout=15)
    common = out.stdout.strip()
    if out.returncode != 0 or not common:
        return []
    if not os.path.isabs(common):
        common = os.path.join(REPO_ROOT, common)
    return gate_scope.load_ledger(os.path.join(common, "pr-signoff"))


def session_main():
    problems, _ = check()
    messages = []
    if problems:
        messages.append("quality gates are impaired - " + "; ".join(problems))
    try:
        debt = deferred_debt()
    except Exception:
        debt = []
    if debt:
        messages.append(f"{len(debt)} deferred signoff(s) outstanding; settle with 'just signoff-full' "
                        f"(oldest: {debt[0].get('reason', '')[:60]})")
    if messages:
        print(json.dumps({"systemMessage": "doctor: " + "; ".join(messages)}))
    sys.exit(0)


# The dependencies are injectable so the self-test can drive the whole
# report: the roll-up below is a line of its own, and without it doctor
# prints FAIL for a lane problem and still exits 0, which is a green gate
# over a red machine.
def full_main(checker=check, lanes=lane_config, debt_source=deferred_debt,
              nightly=nightly_task_registered):
    problems, notes = checker()
    for p in problems:
        print(f"FAIL {p}")
    for n in notes:
        print(f"warn {n}")
    try:
        debt = debt_source()
    except Exception:
        debt = []
    for e in debt:
        print(f"warn deferred signoff outstanding: {e.get('sha', '?')[:10]}  {e.get('reason', '')}")
    reg = nightly()
    if reg is True:
        print("ok   nightly task registered")
    elif reg is False:
        print("warn nightly task not registered on this machine (pwsh .agents/scripts/register_nightly_fuzz.ps1 from the main checkout)")
    lane_problems, lane_notes = lanes()
    for n in lane_notes:
        print(f"{'warn' if ('missing' in n or 'not checked' in n) else 'ok  '} {n}")
    for p in lane_problems:
        print(f"FAIL {p}")
    problems += lane_problems
    if not problems:
        print("ok   gates wired and required tools present")
    sys.exit(1 if problems else 0)


def self_test():
    failed = False

    def report(ok, label):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}")

    all_present = lambda name: "/usr/bin/" + name
    problems, notes = check(which=all_present)
    report(not any("tool missing" in p for p in problems), "no tool problems when all tools resolve")

    missing_just = lambda name: None if name == "just" else "/usr/bin/" + name
    problems, _ = check(which=missing_just)
    report(any("just" in p for p in problems), "missing required tool is a problem")

    missing_zig = lambda name: None if name == "zig" else "/usr/bin/" + name
    problems, notes = check(which=missing_zig)
    report(not any("zig" in p for p in problems) and any("zig" in n for n in notes),
           "missing optional tool is a note, not a problem")

    import tempfile
    with tempfile.TemporaryDirectory() as td:
        bad = os.path.join(td, "settings.json")
        with open(bad, "w", encoding="utf-8") as f:
            f.write("{not json")
        problems, _ = check(which=all_present, settings_path=bad)
        report(any("not valid JSON" in p for p in problems), "corrupt settings is a problem")

        good = os.path.join(td, "ok.json")
        with open(good, "w", encoding="utf-8") as f:
            json.dump({"hooks": {"PreToolUse": [{"hooks": [
                {"args": ["${CLAUDE_PROJECT_DIR}/.agents/scripts/no_such_gate.py"]}]}]}}, f)
        problems, _ = check(which=all_present, settings_path=good)
        report(any("no_such_gate.py" in p for p in problems), "dangling hook path is a problem")

        problems, _ = check(which=all_present, settings_path=good, script_dir=td)
        report(any("gate script missing" in p for p in problems), "missing gate script is a problem")

    # The lane check, with lanes.ps1 and incoda stood in for: what the
    # doctor makes of each exit code is the contract, since a drift that
    # lands as a note instead of a problem keeps `just doctor` green.
    def fake_run(rc, stdout, stderr=""):
        def run(argv, **kw):
            if argv[1:] == ["version"]:
                return subprocess.CompletedProcess(argv, 0, stdout="incoda vX.Y.Z\ncommit: none\n", stderr="")
            return subprocess.CompletedProcess(argv, rc, stdout=stdout, stderr=stderr)
        return run

    problems, notes = lane_config(run=fake_run(0, "ok   lanes match\n"), which=all_present)
    report(not problems and any("lanes match" in n for n in notes) and any("incoda vX.Y.Z" in n for n in notes),
           "matching lanes are a note carrying incoda's version")

    problems, _ = lane_config(run=fake_run(1, "drift wintty-build: slots 2, want 3\ndrift wintty: not closed\nrun 'just lanes'\n"),
                              which=all_present)
    report(len(problems) == 2 and all("drift" in p and "just lanes" in p for p in problems),
           "each drift line is its own problem naming the fix")

    problems, _ = lane_config(run=fake_run(2, "incoda status --all --json exited 3: boom\n"), which=all_present)
    report(len(problems) == 1 and "could not be checked" in problems[0],
           "an unreadable machine is a problem, not a pass")

    # lanes.ps1 exits 1 for drift AND for any unhandled error in itself, and
    # the second kind prints nothing on stdout. Reported as bare drift it
    # sends the reader to `just lanes`, which dies the same way; the reason
    # was on stderr the whole time.
    problems, _ = lane_config(run=fake_run(1, "", "lanes.ps1: Index operation failed; the array index evaluated to null.\n"),
                              which=all_present)
    report(len(problems) == 1 and "array index evaluated to null" in problems[0],
           "exit 1 with no drift lines carries lanes.ps1's stderr, not a bare 'drifted'")

    no_incoda = lambda name: None if name == "incoda" else "/usr/bin/" + name
    problems, notes = lane_config(run=fake_run(0, ""), which=no_incoda, environ={})
    report(not problems and any("incoda" in n for n in notes), "missing incoda is a note, not a problem")

    # The whole report, not just check(): the lane problems are collected in
    # their own list and rolled into the exit status by one line, and
    # dropping that line leaves doctor printing FAIL and exiting 0 - a gate
    # that is green over a machine it just called broken.
    import contextlib
    import io

    def run_full(lane_result):
        buf = io.StringIO()
        try:
            with contextlib.redirect_stdout(buf):
                full_main(checker=lambda: ([], []), lanes=lambda: lane_result,
                          debt_source=lambda: [], nightly=lambda: None)
        except SystemExit as e:
            return e.code, buf.getvalue()
        return None, buf.getvalue()

    code, out = run_full((["lane config drift wintty: not closed (just lanes)"], []))
    report(code == 1 and "FAIL" in out, "a lane problem alone makes `just doctor` exit nonzero")
    code, out = run_full(([], ["lanes match .agents/scripts/lanes.ps1"]))
    report(code == 0 and "FAIL" not in out, "a healthy machine still exits 0")

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    args = sys.argv[1:]
    if "--session" in args:
        session_main()
    elif "--self-test" in args:
        self_test()
    else:
        full_main()
