#!/usr/bin/env python3
"""Merge quality gate for PRs into deblasis/ghostty.

Runs as a Claude Code PreToolUse hook on Bash/PowerShell commands and blocks
merge commands (`gh pr merge`, and `gh api` calls to the pulls merge
endpoint) unless the PR passes:

  size            countable changed lines <= 900 (warn above 500). Countable
                  excludes licence texts, lockfiles, vendored and generated
                  files. A line matching `Size-override: <reason>` in the PR
                  body downgrades the block to a warning: mechanical bulk is
                  the common case, but any reason is accepted because the
                  reason stays in the body permanently, which is worth more
                  than a rule nobody can satisfy honestly. State what is
                  actually true; "it is big" is not a reason.
  body            non-empty (>= 200 chars) when the countable diff is > 50
                  lines: a large change must say what it is and how it was
                  verified.
  boxes           no unchecked task items: a merged PR with an unticked test
                  plan is ambiguous evidence.
  closes-spacing  `Closes # N` does not auto-close on GitHub; only
                  `Closes #N` does.
  signoff         a green local run recorded by `just signoff` for the PR's
                  exact head commit, which ran every leg this PR's files
                  require (see gate_scope). Local runners are the merge
                  authority when CI is unavailable: no fresh run, no merge.
                  Scoping keeps that affordable, and the gate recomputes the
                  requirement here so a cheap record cannot stand in for an
                  expensive one.

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

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402

REPO = "deblasis/ghostty"
WARN_LINES = 500
BLOCK_LINES = 900
BODY_MIN_CHARS = 200
BODY_REQUIRED_ABOVE = 50

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


def check_signoff(pr, signoff_lookup, ledger=None):
    """A signoff must exist for this exact head, be green, and have run every
    leg the PR's own files require. Returns (errors, warnings).

    The scope check is what keeps a cheap run from standing in for an
    expensive one: the legs are recomputed here from the PR's file list, so a
    record that scoped itself narrowly and a PR that later grew Zig changes
    cannot agree. A record written before scoping existed has no scope block
    and ran the whole ladder, which satisfies any requirement.

    A deferred record is credit rather than evidence, so it merges with a
    warning while the ledger has room, and stops merging entirely once the
    limits are hit. That is the difference between batching a ladder run and
    quietly abandoning it.
    """
    head = pr.get("headRefOid", "")
    rec = signoff_lookup(head)
    if rec is None:
        return ([("signoff-missing",
                  f"No local signoff for head {head[:10]}. Run 'just signoff' on the PR branch and retry.")], [])

    errors, warnings = [], []

    if rec.get("deferred"):
        entries = ledger or []
        blockers = gate_scope.ledger_blockers(entries)
        if blockers:
            errors.append(("signoff-defer-limit",
                           "Deferred signoff refused - " + "; ".join(blockers) +
                           ". Run 'just signoff-full' on the merged branch to settle the debt first."))
        else:
            warnings.append(("signoff-deferred",
                             f"merging {head[:10]} on a DEFERRED signoff ({len(entries)} outstanding): "
                             f"{rec.get('reason', 'no reason recorded')}. Settle with 'just signoff-full'."))
        return errors, warnings

    if not rec.get("pass"):
        failed = [k for k, v in rec.get("steps", {}).items() if v.get("rc") != 0]
        errors.append(("signoff-failed",
                       f"Signoff for {head[:10]} is red (failed: {', '.join(failed) or 'unknown'}). Fix and rerun 'just signoff'."))

    scope = rec.get("scope")
    if scope is not None:
        pr_paths = sorted(f["path"].replace("\\", "/") for f in pr.get("files", []))
        rec_paths = scope.get("paths")
        legs_run = set(scope.get("legs_run") or [])
        if rec_paths is not None and sorted(rec_paths) != pr_paths:
            errors.append(("signoff-stale",
                           f"Signoff for {head[:10]} was computed over a different file set than this PR "
                           f"({len(rec_paths)} vs {len(pr_paths)} paths). Rerun 'just signoff'."))
        else:
            required = set(gate_scope.required_legs(
                pr_paths, justfile_legs=scope.get("justfile_legs")))
            missing = sorted(required - legs_run)
            if missing:
                errors.append(("signoff-scope",
                               f"Signoff for {head[:10]} did not run {', '.join(missing)}, which this PR's files "
                               "require. Rerun 'just signoff' (or 'just signoff-full')."))
    return errors, warnings


def check_pr(pr, signoff_lookup=None, ledger=None):
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

    if signoff_lookup is not None:
        sign_errors, sign_warnings = check_signoff(pr, signoff_lookup, ledger)
        errors.extend(sign_errors)
        warnings.extend(sign_warnings)

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


def ledger_for(cwd):
    common = git_common_dir(cwd)
    if not common:
        return []
    return gate_scope.load_ledger(os.path.join(common, "pr-signoff"))


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

    errors, warnings = check_pr(pr, signoff_lookup_factory(cwd), ledger_for(cwd))
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
    errors, warnings = check_pr(pr, signoff_lookup_factory(os.getcwd()), ledger_for(os.getcwd()))
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

    # Scope rules: what a change must run, and the fail-closed default.
    scope_cases = [
        (["docs/guide.md"], False, []),
        ([".gitignore"], False, []),
        ([".agents/scripts/pr_gate.py"], False, ["gates-selftest"]),
        (["src/main.zig"], False, ["zig-fmt", "zig-tests"]),
        (["src/build/GitVersion.zig"], False, ["gates-selftest", "zig-fmt", "zig-tests"]),
        (["src/build/Config.zig"], False, ["gates-selftest", "zig-fmt", "zig-tests"]),
        (["windows/App.xaml.cs"], False, ["windows-tests"]),
        # The release gate is narrow on purpose: ordinary shell source does
        # not pull it in, but anything that can define a constant or set the
        # gate properties does. The .csproj case is the one a plain suffix
        # rule could not express -- the windows/ prefix matches first, and
        # suffixes are only consulted when no prefix did.
        (["windows/Ghostty/Ghostty.csproj"], False, ["release-gate", "windows-tests"]),
        (["windows/Directory.Build.targets"], False, ["release-gate", "windows-tests"]),
        (["windows/Ghostty.Tests/Demo/ShippingBuildGateTests.cs"], False,
         ["release-gate", "windows-tests"]),
        ([".agents/scripts/release_gate_check.ps1"], False, ["release-gate"]),
        # A response file under windows/ turns its contents into command-line
        # arguments, i.e. GLOBAL properties -- the one shape that satisfies
        # the gate's own opt-in test while also compiling the runtime guard
        # out. It must not resolve to windows-tests alone.
        (["windows/Directory.Build.rsp"], False, ["release-gate", "windows-tests"]),
        (["windows/Ghostty.sln"], False, ["release-gate", "windows-tests"]),
        # The SDK pin is the MSBuild that evaluates the gate.
        (["global.json"], False, ["release-gate", "windows-tests"]),
        (["src/a.zig", "windows/b.cs"], False, ["windows-tests", "zig-fmt", "zig-tests"]),
        (["brand/new/dir/thing.bin"], False, sorted(gate_scope.ALL_LEGS)),
        (["justfile"], list(gate_scope.ALL_LEGS), sorted(gate_scope.ALL_LEGS)),
        (["justfile"], [], []),
    ]
    for paths, jf, expect in scope_cases:
        got = gate_scope.required_legs(paths, justfile_legs=jf)
        report(got == expect, "scope", f"{paths} (jf={jf}) -> {got}")

    # A scoped record only satisfies a PR whose files it actually covers.
    pr694 = load(694)
    win_paths = sorted(f["path"].replace("\\", "/") for f in pr694["files"])

    def scoped(legs, paths=None):
        rec = {"pass": True, "steps": {}, "scope": {
            "legs_run": legs,
            "paths": win_paths if paths is None else paths,
            "justfile_legs": [],
        }}
        return lambda sha: rec

    signoff_scope_cases = [
        (scoped(["windows-tests"]), set(), "covers the required leg"),
        (scoped(["windows-tests", "zig-tests"]), set(), "superset is fine"),
        (scoped([]), {"signoff-scope"}, "skipped the required leg"),
        (scoped(["zig-tests"]), {"signoff-scope"}, "ran a different leg"),
        (scoped(["windows-tests"], ["docs/unrelated.md"]), {"signoff-stale"}, "different file set"),
    ]
    for lookup, expect, label in signoff_scope_cases:
        errs, _w = check_pr(pr694, lookup)
        got = {c for c, _ in errs}
        report(got == expect, "signoff-scope", f"{label} -> {sorted(got) or ['clean']}")

    # Deferral is credit: it merges while the ledger has room and stops when
    # the limits are reached, so it cannot quietly become the normal path.
    import datetime
    deferred = lambda sha: {"pass": True, "deferred": True, "reason": "batching four settings PRs",
                            "steps": {}, "scope": {"paths": win_paths, "legs_run": list(gate_scope.ALL_LEGS)}}
    now = datetime.datetime.now(datetime.timezone.utc)
    fresh = [{"sha": "a" * 40, "created": now.isoformat(), "reason": "r"}]
    at_limit = [{"sha": f"{i}" * 40, "created": now.isoformat(), "reason": "r"}
                for i in range(gate_scope.DEFER_MAX_OUTSTANDING)]
    stale = [{"sha": "b" * 40,
              "created": (now - datetime.timedelta(days=gate_scope.DEFER_MAX_AGE_DAYS + 1)).isoformat(),
              "reason": "r"}]

    defer_cases = [
        ([], set(), True, "deferred with an empty ledger warns and merges"),
        (fresh, set(), True, "deferred within the limit still merges"),
        (at_limit, {"signoff-defer-limit"}, False, "deferred at the outstanding limit is refused"),
        (stale, {"signoff-defer-limit"}, False, "deferred with stale debt is refused"),
    ]
    for ledger, expect, expect_warn, label in defer_cases:
        errs, warns = check_pr(pr694, deferred, ledger)
        got = {c for c, _ in errs}
        warned = any(c == "signoff-deferred" for c, _ in warns)
        report(got == expect and warned == expect_warn, "defer", f"{label} -> {sorted(got) or ['clean']}, warn={warned}")

    ledger_cases = [
        ([], []),
        (fresh, []),
        (at_limit, ["outstanding"]),
        (stale, ["days old"]),
    ]
    for entries, expect_fragments in ledger_cases:
        blockers = gate_scope.ledger_blockers(entries, now)
        ok = len(blockers) == len(expect_fragments) and all(
            any(frag in b for b in blockers) for frag in expect_fragments)
        report(ok, "ledger", f"{len(entries)} entries -> {blockers or ['clear']}")

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
