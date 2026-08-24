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
               (signoff ladder, nightly) and, on Windows, whether the
               nightly scheduled task is registered.
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
GATE_SCRIPTS = ["pr_gate.py", "workspace_guard.py", "signoff.py"]


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


def session_main():
    problems, _ = check()
    if problems:
        print(json.dumps({
            "systemMessage": "doctor: quality gates are impaired - " + "; ".join(problems)
        }))
    sys.exit(0)


def full_main():
    problems, notes = check()
    for p in problems:
        print(f"FAIL {p}")
    for n in notes:
        print(f"warn {n}")
    reg = nightly_task_registered()
    if reg is True:
        print("ok   nightly task registered")
    elif reg is False:
        print("warn nightly task not registered on this machine (pwsh .agents/scripts/register_nightly_fuzz.ps1 from the main checkout)")
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
