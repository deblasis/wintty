#!/usr/bin/env python3
"""Merge quality gate for PRs into deblasis/ghostty.

Runs as a Claude Code PreToolUse hook on Bash/PowerShell commands and blocks
merge commands (`gh pr merge`, and `gh api` calls to the pulls merge
endpoint) unless the PR passes:

  size            countable changed lines <= 900 (warn above 500). Countable
                  excludes licence texts, lockfiles, vendored and generated
                  files. A line matching `Size-override: <reason>` in the PR
                  body downgrades the block to a warning, so mechanical bulk
                  changes stay mergeable with an auditable reason.
  body            non-empty (>= 200 chars) when the countable diff is > 50
                  lines: a large change must say what it is and how it was
                  verified.
  boxes           no unchecked task items: a merged PR with an unticked test
                  plan is ambiguous evidence.
  closes-spacing  `Closes # N` does not auto-close on GitHub; only
                  `Closes #N` does.
  signoff         a green local run recorded by `just signoff` for the PR's
                  exact head commit. Local runners are the merge authority
                  when CI is unavailable: no fresh full-suite run, no merge.

The size thresholds fit this repository's merge history: focused
single-concern PRs stay comfortably under the block line, and the warn line
marks where splitting into a stack is usually worth it.

Known limits, accepted deliberately: a hook can only gate what spawns it, so
a missing interpreter means no gate (the hook host does not fail closed on
spawn errors), and command matching is textual, so `gh` aliases or variable
indirection can sidestep it. The matcher normalizes line continuations and
quoting and covers the `gh.exe` and `gh api` spellings; anything cleverer is
out of scope for a string scan.

Modes:
  --hook          PreToolUse hook: read tool-call JSON on stdin, deny or allow.
  --check-pr N    check a live PR and print the verdict (exit 1 on errors).
  --self-test     replay recorded PRs in fixtures/ and verify the gate goes
                  red on each failure class and green on clean ones, and
                  that the matcher and parser survive the known escape
                  spellings.
"""

import json
import os
import re
import shlex
import subprocess
import sys
from fnmatch import fnmatch

REPO = "deblasis/ghostty"
WARN_LINES = 500
BLOCK_LINES = 900
BODY_MIN_CHARS = 200
BODY_REQUIRED_ABOVE = 50
SIGNOFF_REQUIRED_ABOVE = 50

# Exemptions are anchored to basenames (or explicit directory shapes) so a
# source file that merely contains "license" in its name stays countable.
BASENAME_EXEMPT = [
    "license", "license.*", "licence", "licence.*",
    "copying", "copying.*", "notice", "notice.*",
    "third_party_notices", "third_party_notices.*",
    "packages.lock.json", "build.zig.zon*", "*.lock",
]
PATH_EXEMPT = ["vendor/*", "po/*.po"]
LICENCE_DIR_NAMES = ("licenses", "licences", "license", "licence")
LICENCE_DIR_SUFFIXES = (".txt", ".md")

MERGE_RE = re.compile(r"\bgh(?:\.exe)?\s+pr\s+merge\b", re.I)
API_MERGE_RE = re.compile(r"\bgh(?:\.exe)?\s+api\b[^|;&]*?/pulls/(\d+)/merge\b", re.I)
API_REPO_RE = re.compile(r"repos/([\w.-]+/[\w.-]+)/pulls/\d+/merge\b", re.I)
BAD_CLOSES_RE = re.compile(r"\b(close[sd]?|fix(?:es|ed)?|resolve[sd]?)\s+#\s", re.I)
OVERRIDE_RE = re.compile(r"^Size-override:\s+\S.{15,}", re.M)
UNCHECKED_BOX_RE = re.compile(r"^\s*[-*+]\s+\[ \]", re.M)

# gh pr merge flags that consume a value; the PR number is the first purely
# numeric positional token once these and their values are skipped.
VALUE_FLAGS = {
    "--repo", "-R", "--body", "-b", "--body-file", "-F",
    "--subject", "-t", "--match-head-commit", "--author-email", "-A",
}


def normalize_command(command):
    """Join shell line continuations so multi-line spellings match."""
    return re.sub(r"[\\`]\r?\n", " ", command)


def matchable(command):
    """A quote-stripped form, so quoting cannot split the words apart."""
    return normalize_command(command).replace('"', " ").replace("'", " ")


def is_exempt(path):
    p = path.replace("\\", "/").lower()
    base = p.rsplit("/", 1)[-1]
    if any(fnmatch(base, g) for g in BASENAME_EXEMPT):
        return True
    if any(fnmatch(p, g) for g in PATH_EXEMPT):
        return True
    dirs = p.split("/")[:-1]
    if any(d in LICENCE_DIR_NAMES for d in dirs) and base.endswith(LICENCE_DIR_SUFFIXES):
        return True
    return False


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

    boxes = len(UNCHECKED_BOX_RE.findall(body))
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


def is_merge_command(command):
    m = matchable(command)
    return bool(MERGE_RE.search(m) or API_MERGE_RE.search(m))


def parse_merge_command(command):
    """Returns (repo, pr_number_or_None) for a merge command."""
    joined = normalize_command(command)
    m = matchable(command)

    api = API_MERGE_RE.search(m)
    if api:
        repo_m = API_REPO_RE.search(m)
        return (repo_m.group(1) if repo_m else REPO), int(api.group(1))

    repo = REPO
    url = re.search(r"github\.com/([\w.-]+/[\w.-]+)/pull/(\d+)", joined)
    if url:
        return url.group(1), int(url.group(2))

    try:
        tokens = shlex.split(joined, posix=True)
    except ValueError:
        tokens = joined.split()

    number = None
    merge_seen = False
    skip_next = False
    for i, tok in enumerate(tokens):
        stripped = tok.strip('"\'')
        if skip_next:
            skip_next = False
            if merge_seen and (tokens[i - 1].strip('"\'') in ("--repo", "-R")) and "/" in stripped:
                repo = stripped
            continue
        if stripped.startswith("--repo="):
            repo = stripped.split("=", 1)[1]
            continue
        if not merge_seen:
            if stripped == "merge" and i >= 2:
                prev = [t.strip('"\'') for t in tokens[max(0, i - 2):i]]
                if "pr" in prev:
                    merge_seen = True
            continue
        if stripped in VALUE_FLAGS:
            skip_next = True
            continue
        if stripped.startswith("-"):
            continue
        if number is None and stripped.isdigit():
            number = int(stripped)
    return repo, number


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
    if not is_merge_command(command):
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
    checks are asserted separately. Matcher, parser and exemption cases come
    from spellings that defeated earlier revisions of this gate."""
    fixtures = os.path.join(os.path.dirname(os.path.abspath(__file__)), "fixtures")

    def load(n):
        with open(os.path.join(fixtures, f"pr{n}.json"), encoding="utf-8") as f:
            return json.load(f)

    green_signoff = lambda sha: {"pass": True, "steps": {}}
    no_signoff = lambda sha: None
    red_signoff = lambda sha: {"pass": False, "steps": {"zig-tests": {"rc": 1}}}

    failed = False

    def report(ok, label, detail):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}: {detail}")

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
    for n, signoff, expected in expectations:
        errors, _ = check_pr(load(n), signoff)
        got = {c for c, _ in errors}
        report(got == expected, f"pr{n}", f"expected {sorted(expected) or ['clean']}, got {sorted(got) or ['clean']}")

    matcher_cases = [
        ("gh pr merge 123 --squash", True),
        ("cd windows && gh pr merge 123 --squash --repo deblasis/ghostty", True),
        ("git pull && gh   pr   merge 45", True),
        ("gh.exe pr merge 123", True),
        ('gh "pr" merge 123', True),
        ("gh pr \\\n  merge 123", True),
        ("gh pr `\n  merge 123", True),
        ("gh api -X PUT repos/deblasis/ghostty/pulls/123/merge", True),
        ("gh pr view 123", False),
        ("gh pr create --title x", False),
        ("gh api repos/deblasis/ghostty/pulls/123", False),
    ]
    for cmd, expect in matcher_cases:
        report(is_merge_command(cmd) == expect, "matcher", f"{cmd!r} -> {is_merge_command(cmd)}")

    parse_cases = [
        ("gh pr merge 123 --squash", (REPO, 123)),
        ("gh pr merge --squash 123", (REPO, 123)),
        ('gh pr merge --body "fixes 500 things" 123', (REPO, 123)),
        ("gh pr merge -t 'chore: 42' 123", (REPO, 123)),
        ("gh pr merge https://github.com/deblasis/ghostty/pull/9 -s", ("deblasis/ghostty", 9)),
        ("gh pr merge 7 --repo deblasis/other-repo", ("deblasis/other-repo", 7)),
        ("gh pr merge 7 --repo=deblasis/other-repo", ("deblasis/other-repo", 7)),
        ("gh api -X PUT repos/deblasis/ghostty/pulls/55/merge", ("deblasis/ghostty", 55)),
        ("gh pr merge --squash", (REPO, None)),
    ]
    for cmd, expect in parse_cases:
        got = parse_merge_command(cmd)
        report(got == expect, "parse", f"{cmd!r} -> {got}")

    exempt_cases = [
        ("LICENSE", True),
        ("windows/THIRD_PARTY_NOTICES.md", True),
        ("dist/licenses/harfbuzz.txt", True),
        ("build.zig.zon.json", True),
        ("flake.lock", True),
        ("vendor/glfw/src/init.c", True),
        ("src/license_manager.zig", False),   # code named like a licence stays countable
        ("windows/LicenseValidator.cs", False),
        ("licenses/realcode.go", False),
        ("src/main.zig", False),
    ]
    for path, expect in exempt_cases:
        report(is_exempt(path) == expect, "exempt", f"{path} -> {is_exempt(path)}")

    box_cases = [
        ("- [ ] task", 1),
        ("+ [ ] task", 1),
        ("-  [ ] task", 1),
        ("- [x] done", 0),
        ("no boxes here", 0),
    ]
    for body, expect in box_cases:
        got = len(UNCHECKED_BOX_RE.findall(body))
        report(got == expect, "boxes", f"{body!r} -> {got}")

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
