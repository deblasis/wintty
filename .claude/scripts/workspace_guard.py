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

  --check   PreToolUse on Bash|PowerShell: before commit, push, or PR
            create/merge commands, lists tracked files that are dirty in
            this worktree but were never touched by this session. If any
            exist, the command is denied with the list, since another
            session (or a human) may be mid-work here. To proceed anyway
            after reviewing, start the command with GUARD_ACK=1 (bash) or
            $env:GUARD_ACK=1; (PowerShell) - the override must lead the
            command, so quoting it in a commit message does not count.

Known limits, accepted deliberately: only edits made through the Edit/Write
tools are tracked, so files changed via shell redirection read as foreign
(a false denial that the override resolves), and untracked new files are not
inspected at all. The state file is advisory, not tamper-proof.

Self-test: --self-test exercises the classification and matchers.
"""

import json
import os
import re
import subprocess
import sys
import time

# The verb may be separated from git by global options (-C <path>, -c <kv>,
# --git-dir=...), and Windows spells the binaries git.exe / gh.exe.
PUBLISH_RE = re.compile(
    r"\bgit(?:\.exe)?\s+(?:-C\s+\S+\s+|-c\s+\S+\s+|--git-dir=\S+\s+)*(commit|push)(?=\s|$)"
    r"|\bgh(?:\.exe)?\s+pr\s+(create|merge)(?=\s|$)",
    re.I,
)
ACK_POSIX_RE = re.compile(r"^(?:\w+=\S+\s+)*GUARD_ACK=1\b")
ACK_PWSH_RE = re.compile(r"^\$env:GUARD_ACK\s*=\s*['\"]?1\b", re.I)
STATE_MAX_AGE = 7 * 86400
LOCK_TIMEOUT = 2.0


def normalize_command(command):
    return re.sub(r"[\\`]\r?\n", " ", command)


def matchable(command):
    return normalize_command(command).replace('"', " ").replace("'", " ")


def has_ack(command):
    """The override only counts when it leads a command segment, so a commit
    message that merely quotes the guard's docs cannot disable it."""
    for seg in re.split(r"&&|\|\||;|\|", normalize_command(command)):
        s = seg.strip()
        if ACK_POSIX_RE.match(s) or ACK_PWSH_RE.match(s):
            return True
    return False


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
    except OSError:
        return {}
    except ValueError:
        # Corrupt state (e.g. interleaved writes): set it aside for
        # inspection rather than silently absorbing it forever.
        try:
            os.replace(path, path + ".corrupt")
        except OSError:
            pass
        return {}
    now = time.time()
    return {
        sid: rec for sid, rec in state.items()
        if now - rec.get("updated", 0) < STATE_MAX_AGE
    }


def save_state(path, state):
    tmp = f"{path}.{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(state, f)
    os.replace(tmp, path)


def locked_update(path, mutate):
    """Serialize read-modify-write across concurrent sessions with a simple
    lock file; after a bounded wait, proceed unlocked (best effort beats a
    hung hook)."""
    lock = path + ".lock"
    fd = None
    deadline = time.time() + LOCK_TIMEOUT
    while fd is None and time.time() < deadline:
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        except FileExistsError:
            time.sleep(0.05)
    try:
        state = load_state(path)
        mutate(state)
        save_state(path, state)
    finally:
        if fd is not None:
            os.close(fd)
            try:
                os.remove(lock)
            except OSError:
                pass


def norm(path, cwd):
    p = path.replace("\\", "/")
    try:
        root = subprocess.run(
            ["git", "rev-parse", "--show-toplevel"],
            cwd=cwd or None, capture_output=True, text=True, timeout=15,
        ).stdout.strip().replace("\\", "/")
    except Exception:
        root = ""
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
    """A touched entry may be absolute (when the repo root could not be
    resolved at track time), so suffix matches count as owned too."""
    tl = [t.lower() for t in touched]
    tset = set(tl)
    out = []
    for d in dirty:
        dl = d.lower()
        if dl in tset or any(t.endswith("/" + dl) for t in tl):
            continue
        out.append(d)
    return out


def track_main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        sys.exit(0)
    ti = payload.get("tool_input") or {}
    fp = ti.get("file_path") or ti.get("notebook_path")
    sid = payload.get("session_id") or "unknown"
    cwd = payload.get("cwd") or os.getcwd()
    if not fp:
        sys.exit(0)
    path = state_path(cwd)
    if not path:
        sys.exit(0)
    p = norm(fp, cwd)

    def mutate(state):
        rec = state.setdefault(sid, {"touched": [], "updated": 0})
        if p not in rec["touched"]:
            rec["touched"].append(p)
        rec["updated"] = time.time()

    try:
        locked_update(path, mutate)
    except OSError:
        pass
    sys.exit(0)


def check_main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        sys.exit(0)
    command = (payload.get("tool_input") or {}).get("command") or ""
    if not PUBLISH_RE.search(matchable(command)) or has_ack(command):
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
                    "proceed; to override after review, start the command "
                    "with GUARD_ACK=1 (bash) or $env:GUARD_ACK=1; (PowerShell)."
                ),
            }
        }))
    sys.exit(0)


def self_test():
    failed = False

    def report(ok, label, detail):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}: {detail}")

    cases = [
        (["src/a.zig", "windows/B.cs"], ["src/a.zig"], ["windows/B.cs"]),
        (["src/a.zig"], ["SRC/A.ZIG"], []),
        ([], [], []),
        (["x.md"], [], ["x.md"]),
        # An absolute touched path still owns its relative dirty entry.
        (["src/a.zig"], ["c:/repo/src/a.zig"], []),
    ]
    for dirty, touched, expect in cases:
        got = foreign_paths(dirty, touched)
        report(got == expect, "foreign", f"({dirty},{touched}) -> {got}")

    matcher_cases = [
        ("git commit -m x", True),
        ("cd w && git push origin HEAD", True),
        ("gh pr create --fill", True),
        ("gh pr merge 12 --squash", True),
        ("git.exe commit -m x", True),
        ("git -C sub commit -m x", True),
        ("git -c user.name=x commit", True),
        ("git --git-dir=.git push", True),
        ("gh.exe pr create", True),
        ("git commit \\\n -m x", True),
        ("git status", False),
        ("git log --oneline", False),
        ("git push-notes", False),
    ]
    for cmd, expect in matcher_cases:
        got = bool(PUBLISH_RE.search(matchable(cmd)))
        report(got == expect, "matcher", f"{cmd!r} -> {got}")

    ack_cases = [
        ("GUARD_ACK=1 git commit -m x", True),
        ("FOO=bar GUARD_ACK=1 git push", True),
        ("cd w && GUARD_ACK=1 git commit -m x", True),
        ("$env:GUARD_ACK=1; git commit -m x", True),
        ("$env:GUARD_ACK = '1'; git push", True),
        ("git commit -m 'GUARD_ACK=10'", False),
        # Quoting the override inside a message must not disable the guard.
        ('git commit -m "GUARD_ACK=1 discussed"', False),
        ("git commit -m 'use GUARD_ACK=1 to override'", False),
    ]
    for cmd, expect in ack_cases:
        got = has_ack(cmd)
        report(got == expect, "ack", f"{cmd!r} -> {got}")

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
