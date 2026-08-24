#!/usr/bin/env python3
"""Detect cross-session interference in a shared working copy.

Multiple agent sessions can end up in the same worktree and silently modify
each other's files; the damage usually surfaces only when one session commits
the other's work. This guard makes that visible instead of letting a commit
proceed on the assumption that the tree only contains this session's changes.

Two hook modes:

  --track   PostToolUse on Edit|Write|NotebookEdit: records the touched file
            path under this session's id in a per-worktree state file
            (<git-dir>/claude-workspace-guard.json).

  --check   PreToolUse on Bash|PowerShell: before `git commit`, `git push`,
            `gh pr create` or `gh pr merge`, lists tracked files that are
            dirty in this worktree but were never touched by this session.
            If any exist, the command is denied with the list, since another
            session (or a human) may be mid-work here. To proceed anyway
            after reviewing, include GUARD_ACK=1 in the command.

The guard only inspects tracked changes (git status letters, not untracked
noise) and prunes session entries older than 7 days.

Self-test: --self-test exercises the classification and matchers.
"""

import json
import os
import re
import subprocess
import sys
import time

PUBLISH_RE = re.compile(r"\bgit\s+(commit|push)\b|\bgh\s+pr\s+(create|merge)\b")
ACK_RE = re.compile(r"\bGUARD_ACK=1\b")
STATE_MAX_AGE = 7 * 86400


def git_dir(cwd):
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--git-dir"],
            cwd=cwd or None, capture_output=True, text=True, timeout=15,
        )
        if out.returncode == 0:
            p = out.stdout.strip()
            return p if os.path.isabs(p) else os.path.join(cwd or os.getcwd(), p)
    except Exception:
        pass
    return None


def state_path(cwd):
    d = git_dir(cwd)
    return os.path.join(d, "claude-workspace-guard.json") if d else None


def load_state(path):
    try:
        with open(path, encoding="utf-8") as f:
            state = json.load(f)
    except Exception:
        state = {}
    now = time.time()
    state = {
        sid: rec for sid, rec in state.items()
        if now - rec.get("updated", 0) < STATE_MAX_AGE
    }
    return state


def save_state(path, state):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f)
    os.replace(tmp, path)


def norm(path, cwd):
    p = path.replace("\\", "/")
    root = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        cwd=cwd or None, capture_output=True, text=True, timeout=15,
    ).stdout.strip().replace("\\", "/")
    if root and p.lower().startswith(root.lower() + "/"):
        p = p[len(root) + 1:]
    return p


def dirty_tracked(cwd):
    out = subprocess.run(
        ["git", "status", "--porcelain"],
        cwd=cwd or None, capture_output=True, text=True, timeout=30,
    )
    files = []
    for line in out.stdout.splitlines():
        if not line or line.startswith("??"):
            continue
        path = line[3:].strip().strip('"')
        if " -> " in path:
            path = path.split(" -> ", 1)[1]
        files.append(path.replace("\\", "/"))
    return files


def foreign_paths(dirty, touched):
    touched_l = {t.lower() for t in touched}
    return [d for d in dirty if d.lower() not in touched_l]


def track_main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        sys.exit(0)
    fp = (payload.get("tool_input") or {}).get("file_path")
    sid = payload.get("session_id") or "unknown"
    cwd = payload.get("cwd") or os.getcwd()
    if not fp:
        sys.exit(0)
    path = state_path(cwd)
    if not path:
        sys.exit(0)
    state = load_state(path)
    rec = state.setdefault(sid, {"touched": [], "updated": 0})
    p = norm(fp, cwd)
    if p not in rec["touched"]:
        rec["touched"].append(p)
    rec["updated"] = time.time()
    try:
        save_state(path, state)
    except OSError:
        pass
    sys.exit(0)


def check_main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        sys.exit(0)
    command = (payload.get("tool_input") or {}).get("command") or ""
    if not PUBLISH_RE.search(command) or ACK_RE.search(command):
        sys.exit(0)
    sid = payload.get("session_id") or "unknown"
    cwd = payload.get("cwd") or os.getcwd()
    path = state_path(cwd)
    if not path:
        sys.exit(0)
    state = load_state(path)
    touched = state.get(sid, {}).get("touched", [])
    foreign = foreign_paths(dirty_tracked(cwd), touched)
    if foreign:
        listing = "\n- ".join(foreign[:20])
        more = f"\n(+{len(foreign) - 20} more)" if len(foreign) > 20 else ""
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "deny",
                "permissionDecisionReason": (
                    "workspace-guard: this worktree has tracked changes this "
                    "session did not make - another session or a human may be "
                    "mid-work here:\n- " + listing + more +
                    "\nReview them before publishing. Ask the user how to "
                    "proceed; to override after review, prefix the command "
                    "with GUARD_ACK=1."
                ),
            }
        }))
    sys.exit(0)


def self_test():
    failed = False

    cases = [
        (["src/a.zig", "windows/B.cs"], ["src/a.zig"], ["windows/B.cs"]),
        (["src/a.zig"], ["SRC/A.ZIG"], []),
        ([], [], []),
        (["x.md"], [], ["x.md"]),
    ]
    for dirty, touched, expect in cases:
        got = foreign_paths(dirty, touched)
        ok = got == expect
        failed |= not ok
        print(f"{'ok ' if ok else 'FAIL'} foreign({dirty},{touched}) -> {got}")

    matcher_cases = [
        ("git commit -m x", True),
        ("cd w && git push origin HEAD", True),
        ("gh pr create --fill", True),
        ("gh pr merge 12 --squash", True),
        ("git status", False),
        ("git log --oneline", False),
        ("GUARD_ACK=1 git commit -m x", True),  # matches, but ACK short-circuits in check_main
    ]
    for cmd, expect in matcher_cases:
        got = bool(PUBLISH_RE.search(cmd))
        ok = got == expect
        failed |= not ok
        print(f"{'ok ' if ok else 'FAIL'} matcher: {cmd!r} -> {got}")

    ack_cases = [("GUARD_ACK=1 git commit -m x", True), ("git commit -m 'GUARD_ACK=10'", False)]
    for cmd, expect in ack_cases:
        got = bool(ACK_RE.search(cmd))
        ok = got == expect
        failed |= not ok
        print(f"{'ok ' if ok else 'FAIL'} ack: {cmd!r} -> {got}")

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    args = sys.argv[1:]
    if "--track" in args:
        track_main()
    elif "--check" in args:
        check_main()
    elif "--self-test" in args:
        self_test()
    else:
        print(__doc__)
        sys.exit(2)
