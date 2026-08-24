#!/usr/bin/env python3
"""Local test signoff for quality control.

Runs the full local test ladder and records the result against the current
HEAD commit, in <git-common-dir>/pr-signoff/<sha>.json. The pr_gate merge
hook requires a green record for a PR's exact head commit before it lets
`gh pr merge` proceed, which makes local runners the merge authority when
CI is unavailable: the evidence is a recorded run, not a claim.

The record is keyed by commit sha, so it survives worktree switches and
cannot vouch for code that was changed after the run. Rerun after any
amend or rebase.

Usage: just signoff   (or: python .claude/scripts/signoff.py)
"""

import datetime
import json
import os
import subprocess
import sys
import time

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

STEPS = [
    ("zig-fmt", ["zig", "fmt", "--check", "src"]),
    ("zig-tests", ["just", "test"]),
    ("windows-tests", ["just", "test-win"]),
]


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=REPO_ROOT, text=True, capture_output=True, **kw)


def main():
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    dirty = run(["git", "status", "--porcelain"]).stdout.strip()
    if dirty:
        print("signoff: working tree is dirty; commit first so the record vouches for the exact code under review.")
        print(dirty)
        return 1

    results = {}
    ok = True
    for name, cmd in STEPS:
        print(f"signoff: running {name}: {' '.join(cmd)}", flush=True)
        start = time.monotonic()
        try:
            r = subprocess.run(cmd, cwd=REPO_ROOT, shell=(os.name == "nt"))
            rc = r.returncode
        except FileNotFoundError as e:
            print(f"signoff: {name} could not run: {e}")
            rc = 127
        duration = round(time.monotonic() - start, 1)
        results[name] = {"rc": rc, "seconds": duration}
        print(f"signoff: {name} -> rc={rc} ({duration}s)")
        if rc != 0:
            ok = False

    common = run(["git", "rev-parse", "--git-common-dir"]).stdout.strip()
    if not os.path.isabs(common):
        common = os.path.join(REPO_ROOT, common)
    outdir = os.path.join(common, "pr-signoff")
    os.makedirs(outdir, exist_ok=True)
    record = {
        "sha": head,
        "created": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "steps": results,
        "pass": ok,
    }
    path = os.path.join(outdir, f"{head}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
    print(f"signoff: {'PASS' if ok else 'FAIL'} recorded for {head[:10]} at {path}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
