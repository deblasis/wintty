#!/usr/bin/env python3
"""Merge quality gate for PRs into deblasis/ghostty.

Runs as a Claude Code PreToolUse hook on Bash/PowerShell commands and blocks
`gh pr merge` unless the PR passes:

  size            countable changed lines <= 900 (warn above 500). Countable
                  excludes licence texts, lockfiles, vendored and generated
                  files. A line matching `Size-override: <reason>` in the PR
                  body downgrades the block to a warning, so mechanical bulk
                  changes stay mergeable with an auditable reason.
  body            non-empty (>= 200 chars) when the countable diff is > 50
                  lines: a large change must say what it is and how it was
                  verified.
  boxes           no unchecked `- [ ]` task items: a merged PR with an
                  unticked test plan is ambiguous evidence.
  closes-spacing  `Closes # N` does not auto-close on GitHub; only
                  `Closes #N` does.
  signoff         a green local run recorded by `just signoff` for the PR's
                  exact head commit. Local runners are the merge authority
                  when CI is unavailable: no fresh full-suite run, no merge.

The size thresholds fit this repository's merge history: focused
single-concern PRs stay comfortably under the block line, and the warn line
marks where splitting into a stack is usually worth it.

Modes:
  --hook          PreToolUse hook: read tool-call JSON on stdin, deny or allow.
  --check-pr N    check a live PR and print the verdict (exit 1 on errors).
  --self-test     replay recorded PRs in fixtures/ and verify the gate goes
                  red on each failure class and green on clean ones.
"""

import json
import os
import re
import subprocess
import sys
from fnmatch import fnmatch

REPO = "deblasis/ghostty"
WARN_LINES = 500
BLOCK_LINES = 900
BODY_MIN_CHARS = 200
BODY_REQUIRED_ABOVE = 50
SIGNOFF_REQUIRED_ABOVE = 50

# Paths whose churn is mechanical bulk, not review surface. Matched with
# fnmatch against the repo-relative path, case-insensitively.
EXEMPT_GLOBS = [
    "vendor/*",
    "*license*",
    "*licence*",
    "*third_party_notices*",
    "*.lock",
    "*packages.lock.json",
    "build.zig.zon*",
    "po/*.po",
]

MERGE_RE = re.compile(r"\bgh\s+pr\s+merge\b")
BAD_CLOSES_RE = re.compile(r"\b(close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s+#\s", re.I)
OVERRIDE_RE = re.compile(r"^Size-override:\s+\S.{15,}", re.M)


def is_exempt(path):
    p = path.replace("\\", "/").lower()
    return any(fnmatch(p, g) for g in EXEMPT_GLOBS)


def countable_lines(pr):
    return sum(
        f["additions"] + f["deletions"]
        for f in pr.get("files", [])
        if not is_exempt(f["path"])
    )


def check_pr(pr, signoff_lookup=None):
    """Returns (errors, warnings): lists of (code, message)."""
    errors, warnings = [], []
    body = pr.get("body") or ""
    n = pr.get("number", "?")
    lines = countable_lines(pr)

    if lines > BLOCK_LINES:
        msg = (
            f"PR #{n} has {lines} countable changed lines (limit {BLOCK_LINES}). "
            "Split it into a stack of single-concern PRs, or if the bulk is "
            "mechanical, add a 'Size-override: <reason>' line to the body."
        )
        if OVERRIDE_RE.search(body):
            warnings.append(("size-override", f"PR #{n}: size override in use ({lines} lines)."))
        else:
            errors.append(("size", msg))
    elif lines > WARN_LINES:
        warnings.append(
            ("size", f"PR #{n} has {lines} countable changed lines; consider splitting (>{WARN_LINES}).")
        )

    if lines > BODY_REQUIRED_ABOVE and len(body.strip()) < BODY_MIN_CHARS:
        errors.append(
            ("body-empty", f"PR #{n} changes {lines} lines but the body has {len(body.strip())} chars. Describe the change.")
        )

    boxes = len(re.findall(r"^\s*[-*] \[ \]", body, re.M))
    if boxes:
        errors.append(
            ("unchecked-boxes", f"PR #{n} body has {boxes} unchecked task item(s). Tick them or delete them; an unticked plan is ambiguous evidence.")
        )

    if BAD_CLOSES_RE.search(body):
        errors.append(
            ("closes-spacing", f"PR #{n} body contains 'Closes # N' with a space; GitHub will not auto-close. Use 'Closes #N'.")
        )

    if signoff_lookup is not None and lines > SIGNOFF_REQUIRED_ABOVE:
        head = pr.get("headRefOid", "")
        rec = signoff_lookup(head)
        if rec is None:
            errors.append(
                ("signoff-missing", f"No local signoff for head {head[:10]}. Run 'just signoff' on the PR branch (full test suite) and retry.")
            )
        elif not rec.get("pass"):
            failed = [k for k, v in rec.get("steps", {}).items() if v.get("rc") != 0]
            errors.append(
                ("signoff-failed", f"Signoff for {head[:10]} is red (failed: {', '.join(failed) or 'unknown'}). Fix and rerun 'just signoff'.")
            )

    return errors, warnings


def git_common_dir(cwd):
    try:
        out = subprocess.run(
            ["git", "rev-parse", "--git-common-dir"],
            cwd=cwd or None, capture_output=True, text=True, timeout=15,
        )
        if out.returncode == 0:
            path = out.stdout.strip()
            if not os.path.isabs(path):
                path = os.path.join(cwd or os.getcwd(), path)
            return path
    except Exception:
        pass
    return None


def signoff_lookup_factory(cwd):
    common = git_common_dir(cwd)

    def lookup(sha):
        if not common or not sha:
            return None
        path = os.path.join(common, "pr-signoff", f"{sha}.json")
        try:
            with open(path, encoding="utf-8") as f:
                return json.load(f)
        except OSError:
            return None

    return lookup


def fetch_pr(number, repo, cwd=None):
    out = subprocess.run(
        ["gh", "pr", "view", str(number), "--repo", repo, "--json",
         "number,title,body,additions,deletions,files,headRefOid"],
        cwd=cwd or None, capture_output=True, text=True, timeout=60,
    )
    if out.returncode != 0:
        raise RuntimeError(f"gh pr view failed: {out.stderr.strip()}")
    return json.loads(out.stdout)


def parse_merge_command(command):
    """Returns (repo, pr_number_or_None) for a gh pr merge command."""
    repo = REPO
    m = re.search(r"(?:--repo|-R)[=\s]+([\w.-]+/[\w.-]+)", command)
    if m:
        repo = m.group(1)
    m = re.search(r"github\.com/([\w.-]+/[\w.-]+)/pull/(\d+)", command)
    if m:
        return m.group(1), int(m.group(2))
    m = re.search(r"\bgh\s+pr\s+merge\s+(?:[^\s]*\s+)*?(\d+)\b", command)
    if m:
        return repo, int(m.group(1))
    return repo, None


def deny(reason):
    print(json.dumps({
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "deny",
            "permissionDecisionReason": reason,
        }
    }))
    sys.exit(0)


def hook_main():
    try:
        payload = json.load(sys.stdin)
    except Exception:
        sys.exit(0)
    command = (payload.get("tool_input") or {}).get("command") or ""
    if not MERGE_RE.search(command):
        sys.exit(0)
    cwd = payload.get("cwd") or os.getcwd()

    repo, number = parse_merge_command(command)
    if repo.lower() != REPO:
        sys.exit(0)  # other repos are out of this gate's scope
    if number is None:
        try:
            out = subprocess.run(
                ["gh", "pr", "view", "--json", "number", "-q", ".number"],
                cwd=cwd, capture_output=True, text=True, timeout=60,
            )
            number = int(out.stdout.strip())
        except Exception:
            deny("pr-gate: could not resolve which PR this merge targets; pass the PR number explicitly.")

    try:
        pr = fetch_pr(number, repo, cwd)
    except Exception as e:
        deny(f"pr-gate: could not fetch PR #{number} to validate it ({e}). Refusing to merge unvalidated.")

    errors, warnings = check_pr(pr, signoff_lookup_factory(cwd))
    if errors:
        deny("pr-gate blocked this merge:\n- " + "\n- ".join(m for _, m in errors))
    if warnings:
        print(json.dumps({
            "hookSpecificOutput": {
                "hookEventName": "PreToolUse",
                "permissionDecision": "allow",
                "permissionDecisionReason": "pr-gate warnings",
            },
            "systemMessage": "pr-gate: " + " | ".join(m for _, m in warnings),
        }))
    sys.exit(0)


def check_pr_main(number, repo):
    pr = fetch_pr(number, repo)
    errors, warnings = check_pr(pr, signoff_lookup_factory(os.getcwd()))
    for _, m in warnings:
        print(f"WARN  {m}")
    for _, m in errors:
        print(f"ERROR {m}")
    if not errors and not warnings:
        print(f"PR #{number} passes the gate.")
    sys.exit(1 if errors else 0)


def self_test():
    """Replay recorded PRs from fixtures/; the gate must go red on every
    failure class it exists for and green on the clean ones. Signoff is
    simulated so the fixture cases isolate the body/size checks; the signoff
    checks are asserted separately."""
    fixtures = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures")

    def load(n):
        with open(os.path.join(fixtures, f"pr{n}.json"), encoding="utf-8") as f:
            return json.load(f)

    green_signoff = lambda sha: {"pass": True, "steps": {}}
    no_signoff = lambda sha: None
    red_signoff = lambda sha: {"pass": False, "steps": {"zig-tests": {"rc": 1}}}

    expectations = [
        # (fixture, signoff, expected error codes)
        (311, green_signoff, {"size", "body-empty"}),       # large diff, empty body
        (638, green_signoff, {"size", "unchecked-boxes"}),  # large diff, unticked boxes
        (186, green_signoff, {"size", "unchecked-boxes"}),  # unticked manual test plan
        (684, green_signoff, {"closes-spacing"}),           # spaced issue reference
        (694, green_signoff, set()),                        # clean
        (486, green_signoff, set()),                        # clean small fix
        (694, no_signoff, {"signoff-missing"}),
        (694, red_signoff, {"signoff-failed"}),
    ]

    failed = False
    for n, signoff, expected in expectations:
        errors, _ = check_pr(load(n), signoff)
        got = {c for c, _ in errors}
        status = "ok " if got == expected else "FAIL"
        if got != expected:
            failed = True
        print(f"{status} pr{n}: expected {sorted(expected) or ['clean']}, got {sorted(got) or ['clean']}")

    # The matcher must catch compound and flag-bearing spellings of the merge
    # command, and must not fire on unrelated gh commands.
    matcher_cases = [
        ("gh pr merge 123 --squash", True),
        ("cd windows && gh pr merge 123 --squash --repo deblasis/ghostty", True),
        ("git pull && gh   pr   merge 45", True),
        ("gh pr view 123", False),
        ("gh pr create --title x", False),
    ]
    for cmd, expect in matcher_cases:
        got = bool(MERGE_RE.search(cmd))
        status = "ok " if got == expect else "FAIL"
        if got != expect:
            failed = True
        print(f"{status} matcher: {cmd!r} -> {got}")

    parse_cases = [
        ("gh pr merge 123 --squash", (REPO, 123)),
        ("gh pr merge --squash 123", (REPO, 123)),
        ("gh pr merge https://github.com/deblasis/ghostty/pull/9 -s", ("deblasis/ghostty", 9)),
        ("gh pr merge 7 --repo deblasis/wintty-release", ("deblasis/wintty-release", 7)),
        ("gh pr merge --squash", (REPO, None)),
    ]
    for cmd, expect in parse_cases:
        got = parse_merge_command(cmd)
        status = "ok " if got == expect else "FAIL"
        if got != expect:
            failed = True
        print(f"{status} parse: {cmd!r} -> {got}")

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    args = sys.argv[1:]
    if "--hook" in args:
        hook_main()
    elif "--self-test" in args:
        self_test()
    elif "--check-pr" in args:
        i = args.index("--check-pr")
        number = int(args[i + 1])
        repo = REPO
        if "--repo" in args:
            repo = args[args.index("--repo") + 1]
        check_pr_main(number, repo)
    else:
        print(__doc__)
        sys.exit(2)
