#!/usr/bin/env python3
"""Local test signoff for quality control.

Runs the test legs a change actually needs and records the result against
the current HEAD commit, in <git-common-dir>/pr-signoff/<sha>.json. The
pr_gate merge hook requires a green record covering a PR's exact head
commit before it lets a merge proceed, which makes local runners the merge
authority when CI is unavailable: the evidence is a recorded run, not a
claim.

Which legs run comes from gate_scope, computed over the paths this branch
changes against origin/windows. A change touching no Zig and no C# does not
pay for the Zig suite; a change touching anything unclassified pays for
everything. The record stores the paths it was computed from, so the gate
can tell a record that covers the PR from one that does not, and refuses
the second.

The record is keyed by commit sha, so it survives worktree switches and
cannot vouch for code that was changed after the run. Rerun after any
amend or rebase.

Each record also carries `base`, the merge base with origin/windows at
the time the run was taken. The merge guard (#969) reads it to measure
what moved on the branch between the run and the merge, so a green record
whose window has since moved can merge anyway with the risk filed instead
of re-gating inline. Records written before this field existed have no
base; downstream readers must treat that as base-unknown and re-estimate,
never as "the window did not move".

Usage: just signoff          (scoped to what changed)
       just signoff-full     (every leg, whatever changed)
"""

import datetime
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BASE_REF = "origin/windows"

LEG_COMMANDS = {
    gate_scope.LEG_FMT: ["zig", "fmt", "--check", "src"],
    gate_scope.LEG_ZIG: ["just", "test"],
    gate_scope.LEG_WIN: ["just", "test-win"],
    gate_scope.LEG_GATES: ["just", "gates-selftest"],
    gate_scope.LEG_RELEASE_GATE: ["just", "release-gate-check"],
}


def run(cmd, **kw):
    return subprocess.run(cmd, cwd=REPO_ROOT, text=True, capture_output=True, **kw)


def resolve_common_dir():
    """The absolute git common dir, or None if it cannot be trusted (a dying
    session can kill the git child and leave empty output)."""
    out = run(["git", "rev-parse", "--git-common-dir"])
    common = out.stdout.strip()
    if out.returncode != 0 or not common:
        return None
    if not os.path.isabs(common):
        common = os.path.join(REPO_ROOT, common)
    return common if os.path.isdir(common) else None


def merge_base():
    out = run(["git", "merge-base", "HEAD", BASE_REF])
    return out.stdout.strip() if out.returncode == 0 else None


def changed_paths(base):
    out = run(["git", "diff", "--name-only", f"{base}...HEAD"])
    if out.returncode != 0:
        return None
    return sorted(p.strip().replace("\\", "/") for p in out.stdout.splitlines() if p.strip())


def _hunk_lines(diff_text, side):
    """Line numbers touched on one side of a unified diff (side: '-' or '+')."""
    lines = set()
    for m in re.finditer(r"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", diff_text, re.M):
        start = int(m.group(1) if side == "-" else m.group(3))
        count = m.group(2) if side == "-" else m.group(4)
        count = 1 if count is None else int(count)
        for i in range(count):
            lines.add(start + i)
    return lines


def _recipe_at(content_lines, lineno):
    """Name of the recipe owning a 1-based line, or None for file preamble.

    Body lines are indented, so the owning recipe is the nearest header at
    column 0 above. A changed line that is itself at column 0 and not a
    recipe header (a `set` directive, an assignment) belongs to no recipe
    and is treated as preamble, which forces a full run.
    """
    idx = lineno - 1
    if idx < 0 or idx >= len(content_lines):
        return None
    # Parameters may be bare (`pr-gate pr:`), defaulted (`splash-race
    # args="":`) or variadic (`+args`), and the trailing colon must not be
    # the `:=` of a `set` directive or an assignment.
    header = re.compile(
        r"^([a-zA-Z_][\w-]*)"
        r"(?:\s+[+*]?[\w-]+(?:\s*=\s*(?:\"[^\"]*\"|'[^']*'))?)*"
        r"\s*:(?!=)"
    )
    line = content_lines[idx]
    if line and not line[0].isspace():
        m = header.match(line)
        return m.group(1) if m else None
    for i in range(idx, -1, -1):
        cur = content_lines[i]
        if not cur or cur[0].isspace():
            continue
        m = header.match(cur)
        return m.group(1) if m else None
    return None


def justfile_legs(base):
    """Legs whose meaning a justfile edit could have changed.

    Editing the recipe a leg runs through invalidates that leg only; a
    changed line outside every recipe (the shell preamble, a variable) can
    reach any of them and forces all. Adding an unrelated recipe forces
    nothing. Comment-only and blank-line changes never count, since a guard
    that demands an hour of tests for a typo fix is the kind that gets
    switched off. Both sides of the diff are inspected so a deleted recipe
    cannot slip through by leaving no new lines.
    """
    diff = run(["git", "diff", "--unified=0", base, "HEAD", "--", "justfile"])
    if diff.returncode != 0:
        return set(gate_scope.ALL_LEGS)
    if not diff.stdout.strip():
        return set()

    new_content = run(["git", "show", "HEAD:justfile"])
    old_content = run(["git", "show", f"{base}:justfile"])
    if new_content.returncode != 0 or old_content.returncode != 0:
        return set(gate_scope.ALL_LEGS)

    legs = set()
    for side, blob in (("+", new_content.stdout), ("-", old_content.stdout)):
        content_lines = blob.splitlines()
        for lineno in _hunk_lines(diff.stdout, side):
            if lineno - 1 >= len(content_lines):
                return set(gate_scope.ALL_LEGS)
            text = content_lines[lineno - 1].strip()
            if not text or text.startswith("#"):
                continue
            recipe = _recipe_at(content_lines, lineno)
            if recipe is None:
                return set(gate_scope.ALL_LEGS)
            legs.update(gate_scope.RECIPE_LEGS.get(recipe, ()))
    return legs


def plan(full=False):
    """Returns (legs, paths, justfile_legs, reason, base). `base` is the
    merge base with origin/windows, recorded in the payload so the merge
    guard can later measure what moved; it is resolved even for a full run,
    which scoping does not need but the record should still carry."""
    every = sorted(gate_scope.ALL_LEGS)
    base = merge_base()
    if full:
        return every, None, every, "--full requested", base
    if not base:
        return every, None, every, f"could not resolve a merge base with {BASE_REF}", None
    paths = changed_paths(base)
    if paths is None:
        return every, None, every, "could not list changed paths", base
    if not paths:
        return every, [], every, "no changes against the base; nothing to scope", base
    jf = sorted(justfile_legs(base)) if "justfile" in paths else []
    legs = gate_scope.required_legs(paths, justfile_legs=jf)
    unknown = gate_scope.unknown_paths(paths)
    if unknown:
        reason = f"unclassified path(s) force every leg: {', '.join(unknown[:5])}"
    elif jf:
        reason = f"scoped to {len(paths)} changed path(s); justfile edit touches {', '.join(jf)}"
    else:
        reason = f"scoped to {len(paths)} changed path(s)"
    return legs, paths, jf, reason, base


def record_payload(head, base, steps, ok, legs, paths, jf, reason, full):
    """The record shape both write paths share. `base` rides at the top
    level next to `sha` because the guard reads the pair together: the run
    vouches for `sha` against a branch that was at `base`. A None base is
    legal (git could not resolve one) and means base-unknown downstream,
    never "the window did not move"."""
    return {
        "sha": head,
        "base": base,
        "created": datetime.datetime.now(datetime.timezone.utc).isoformat(),
        "steps": steps,
        "pass": ok,
        "scope": {
            "legs_run": legs,
            "paths": paths,
            "justfile_legs": jf,
            "reason": reason,
            "full": bool(full),
        },
    }


def signoff_dir():
    common = resolve_common_dir()
    if not common:
        return None
    d = os.path.join(common, "pr-signoff")
    os.makedirs(d, exist_ok=True)
    return d


def write_ledger(entries):
    d = signoff_dir()
    if not d:
        return False
    tmp = gate_scope.ledger_path(d) + f".{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(entries, f, indent=2)
    os.replace(tmp, gate_scope.ledger_path(d))
    return True


def defer(reason):
    """Record a deliberate skip so a batch of small PRs can share one later
    ladder run. Refused when the reason is not a reason, or when the ledger
    is already at its limit: credit has to be settled before more is given."""
    if len(reason.strip()) < gate_scope.DEFER_MIN_REASON_CHARS:
        print(f"signoff: give a real motivation (at least {gate_scope.DEFER_MIN_REASON_CHARS} chars); "
              "it is recorded in the ledger and read later when the debt is settled.")
        return 2
    d = signoff_dir()
    if not d:
        print("signoff: could not resolve the git common dir; NOT recording.")
        return 2
    entries = gate_scope.load_ledger(d)
    blockers = gate_scope.ledger_blockers(entries)
    if blockers:
        print("signoff: refusing to defer - " + "; ".join(blockers))
        print("signoff: settle the debt first with 'just signoff-full' on the merged branch, "
              "or let the nightly run settle it.")
        return 1

    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    legs, paths, jf, _, base = plan(False)
    created = datetime.datetime.now(datetime.timezone.utc).isoformat()
    # A deferral borrows against a future run, so its record needs the same
    # base a real one carries: the guard has to know which window the credit
    # was issued against when it later measures what moved.
    record = record_payload(head, base, {}, True, list(gate_scope.ALL_LEGS),
                            paths, jf, "deferred", False)
    record["created"] = created
    record["deferred"] = True
    record["reason"] = reason.strip()
    record["scope"]["legs_deferred"] = legs
    with open(os.path.join(d, f"{head}.json"), "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
    entries.append({"sha": head, "created": created, "reason": reason.strip(),
                    "legs_deferred": legs})
    write_ledger(entries)
    remaining = gate_scope.DEFER_MAX_OUTSTANDING - len(entries)
    print(f"signoff: DEFERRED for {head[:10]} - {reason.strip()}")
    print(f"signoff: {len(entries)} outstanding, {remaining} more available before the gate refuses. "
          "Settle with 'just signoff-full' once the batch has landed.")
    return 0


def post_github_status(record):
    """Advertise a finished signoff on GitHub as a commit status.

    The record on disk is the authority and the local merge gate reads it
    from there; this only puts the green/red tick and the one-line summary
    where the PR shows them, because GitHub runs nothing by default
    anymore (CI is dispatch/tag opt-in). The token is whatever `gh`
    already holds, the context is named so it can never be mistaken for
    the GitHub-side workflow, and no detail URL is invented - the full
    record lives in this machine's git dir, which has no URL.

    Best-effort by construction: an unpushed head (422 - status before
    push is the normal early-run case), a missing `gh`, or an offline box
    prints one warning and changes nothing about the signoff's verdict.
    Never called for deferrals - a deferred record is a borrow, not
    evidence, and a tick for it would lie.

    Returns True when the status was posted, False otherwise, so the
    --post path can exit non-zero without the ladder path caring.
    """
    try:
        remote = subprocess.run(["git", "remote", "get-url", "origin"],
                                cwd=REPO_ROOT, text=True,
                                capture_output=True, timeout=10).stdout
        if "deblasis/wintty" not in remote:
            return False
        legs = sorted(record["steps"])
        total = round(sum(s["seconds"] for s in record["steps"].values()))
        if record["pass"]:
            state, desc = "success", (
                f"PASS {len(legs)} leg(s) in {total}s: {', '.join(legs)}")
        else:
            bad = [f"{n} rc={s['rc']}" for n, s in sorted(record["steps"].items())
                   if s["rc"] != 0]
            state, desc = "failure", f"FAIL: {'; '.join(bad)}"
        p = subprocess.run(
            ["gh", "api", "--method", "POST",
             f"repos/deblasis/wintty/statuses/{record['sha']}",
             "-f", f"state={state}",
             "-f", "context=signoff/ladder",
             "-f", f"description={desc[:140]}"],
            cwd=REPO_ROOT, text=True, capture_output=True, timeout=20)
        if p.returncode != 0:
            print(f"signoff: GitHub status not posted (signoff unaffected): "
                  f"{p.stderr.strip()[:160]}")
            return False
        return True
    except Exception as e:  # noqa: BLE001 - advertising must never fail the gate
        print(f"signoff: GitHub status not posted (signoff unaffected): {e}")
        return False


def resolve_record(record_dir, sha):
    """Find the record file for `sha` (full hash or an unambiguous prefix).

    The records directory also holds runs for superseded heads - a stack
    rebuilt after a fix leaves the old SHAs behind - so callers pass an
    explicit sha rather than "everything", and a prefix that matches two
    records is an error naming both rather than a guess.
    """
    import glob as _glob
    matches = [
        p for p in _glob.glob(os.path.join(record_dir, "*.json"))
        if os.path.basename(p)[:-len(".json")].startswith(sha)
    ]
    if len(matches) == 1:
        return matches[0]
    if not matches:
        print(f"signoff: no record for {sha} under {record_dir}")
        return None
    print(f"signoff: {sha[:10]} is ambiguous; candidates:")
    for m in matches:
        print(f"  {os.path.basename(m)}")
    return None


def post_only(sha):
    """Re-advertise an already-recorded signoff without running any leg.

    For the run-then-push order: the ladder records against the exact
    head SHA, and the automatic post fails harmlessly when that SHA is
    not on GitHub yet. This closes the gap after the push, using the
    record as recorded - it never fabricates or refreshes anything, and
    it cannot post a deferral because deferrals live in the ledger, not
    as per-SHA records.
    """
    d = signoff_dir()
    if not d:
        print("signoff: could not resolve the git common dir.")
        return 2
    path = resolve_record(d, sha)
    if not path:
        return 1
    with open(path, encoding="utf-8") as f:
        record = json.load(f)
    ok = post_github_status(record)
    if ok:
        print(f"signoff: status posted for {record['sha'][:10]} "
              f"({'PASS' if record['pass'] else 'FAIL'} as recorded)")
    return 0 if ok else 1


def settle(note):
    d = signoff_dir()
    if not d:
        print("signoff: could not resolve the git common dir; nothing settled.")
        return 2
    entries = gate_scope.load_ledger(d)
    if not entries:
        print("signoff: no deferred signoffs outstanding.")
        return 0
    write_ledger([])
    print(f"signoff: settled {len(entries)} deferred signoff(s) - {note}")
    for e in entries:
        print(f"  {e.get('sha', '?')[:10]}  {e.get('reason', '')}")
    return 0


def report_debt():
    d = signoff_dir()
    entries = gate_scope.load_ledger(d) if d else []
    if not entries:
        return
    print(f"signoff: {len(entries)} deferred signoff(s) outstanding:")
    for e in entries:
        print(f"  {e.get('sha', '?')[:10]}  {e.get('created', '')[:16]}  {e.get('reason', '')}")


def main(argv):
    full = "--full" in argv
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    dirty = run(["git", "status", "--porcelain"]).stdout.strip()
    if dirty:
        print("signoff: working tree is dirty; commit first so the record vouches for the exact code under review.")
        print(dirty)
        return 1

    legs, paths, jf, reason, base = plan(full)
    print(f"signoff: {reason}")
    print(f"signoff: legs: {', '.join(legs) if legs else '(none needed)'}")

    results = {}
    ok = True
    for name in legs:
        cmd = LEG_COMMANDS[name]
        print(f"signoff: running {name}: {' '.join(cmd)}", flush=True)
        start = time.monotonic()
        try:
            rc = subprocess.run(cmd, cwd=REPO_ROOT, shell=(os.name == "nt")).returncode
        except FileNotFoundError as e:
            print(f"signoff: {name} could not run: {e}")
            rc = 127
        duration = round(time.monotonic() - start, 1)
        results[name] = {"rc": rc, "seconds": duration}
        print(f"signoff: {name} -> rc={rc} ({duration}s)")
        if rc != 0:
            ok = False

    common = resolve_common_dir()
    if not common:
        # Fail closed: without a resolvable git dir the record would land in
        # the working tree, dirtying it and hiding the result from the merge
        # gate. Exit distinctly so a wrapper can tell "environment broke"
        # from "tests failed".
        print("signoff: could not resolve the git common dir; NOT recording. Rerun in a healthy session.")
        return 2
    outdir = os.path.join(common, "pr-signoff")
    os.makedirs(outdir, exist_ok=True)
    record = record_payload(head, base, results, ok, legs, paths, jf, reason, full)
    path = os.path.join(outdir, f"{head}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f, indent=2)
    print(f"signoff: {'PASS' if ok else 'FAIL'} recorded for {head[:10]} at {path}")
    post_github_status(record)

    # A green run of every leg is what deferred merges were borrowing
    # against, so it settles the ledger. A scoped run proves nothing about
    # the code those merges carried and settles nothing.
    if ok and set(legs) == set(gate_scope.ALL_LEGS):
        entries = gate_scope.load_ledger(outdir)
        if entries:
            settle(f"full ladder green at {head[:10]}")
    else:
        report_debt()
    return 0 if ok else 1


def self_test():
    failed = False

    def report(ok, label, detail=""):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}{': ' + detail if detail else ''}")

    justfile = [
        "# preamble comment",
        'set windows-shell := ["pwsh.exe"]',
        "",
        "test: test-lib-vt",
        "    zig build test",
        "",
        "[windows]",
        "gallery-verify:",
        "    bash tools/gallery/verify.sh",
        "",
        "pr-gate pr:",                       # bare parameter
        "    python .agents/scripts/pr_gate.py",
        "",
        'splash-race args="": build-win',    # defaulted parameter and a dep
        "    pwsh -File x.ps1",
        "",
        "signoff-defer +reason:",            # variadic parameter
        "    python .agents/scripts/signoff.py",
    ]
    owner_cases = [
        (1, None),   # comment in the preamble
        (2, None),   # set directive, not a recipe called "set"
        (4, "test"),
        (5, "test"),
        (8, "gallery-verify"),
        (9, "gallery-verify"),
        (11, "pr-gate"),
        (12, "pr-gate"),
        (14, "splash-race"),
        (15, "splash-race"),
        (17, "signoff-defer"),
        (18, "signoff-defer"),
    ]
    for lineno, expect in owner_cases:
        got = _recipe_at(justfile, lineno)
        report(got == expect, "recipe-owner", f"line {lineno} -> {got}")

    # A pure insertion (-10,0) touches no old line, so only the new side
    # carries it; a replacement (-4 +4) appears on both.
    hunk = "@@ -10,0 +11,2 @@\n+new line\n+another\n@@ -4 +4 @@\n-old\n+new\n"
    report(_hunk_lines(hunk, "+") == {11, 12, 4}, "hunk-new", str(sorted(_hunk_lines(hunk, "+"))))
    report(_hunk_lines(hunk, "-") == {4}, "hunk-old", str(sorted(_hunk_lines(hunk, "-"))))

    deletion = "@@ -20,3 +19,0 @@\n-a\n-b\n-c\n"
    report(_hunk_lines(deletion, "-") == {20, 21, 22}, "hunk-deletion", str(sorted(_hunk_lines(deletion, "-"))))
    report(_hunk_lines(deletion, "+") == set(), "hunk-deletion-new", str(sorted(_hunk_lines(deletion, "+"))))

    # The record must carry the window it was taken against: the merge guard
    # reads `base` next to `sha` to measure what moved on the branch. A None
    # base must still be PRESENT in the payload, so a reader distinguishing
    # "unknown" from "did not move" can tell them apart by key, not by guess.
    payload = record_payload("a" * 40, "b" * 40, {"windows-tests": {"rc": 0}}, True,
                             ["windows-tests"], ["windows/x.cs"], [], "scoped", False)
    report(payload["base"] == "b" * 40 and payload["sha"] == "a" * 40, "record-base",
           f"sha/base recorded: {payload['sha'][:8]}/{payload['base'][:8]}")
    report("base" in record_payload("a" * 40, None, {}, True, [], None, [], "r", False),
           "record-base-null", "an unknown base is recorded as null, not omitted")

    # resolve_record: the records dir also holds superseded heads (a stack
    # rebuilt after a fix leaves the old SHAs behind), so a prefix must
    # match exactly one file, ambiguity is an error naming the candidates,
    # and a miss is reported rather than guessed at.
    import tempfile
    with tempfile.TemporaryDirectory() as td:
        exact = "a" * 40
        sibling = "a" + "b" * 39
        for sha in (exact, sibling, "c" * 40):
            with open(os.path.join(td, f"{sha}.json"), "w", encoding="utf-8") as f:
                f.write("{}")
        report(resolve_record(td, exact) == os.path.join(td, f"{exact}.json"),
               "resolve-exact", "a full sha resolves to its own file")
        report(resolve_record(td, "ab") == os.path.join(td, f"{sibling}.json"),
               "resolve-prefix", "an unambiguous prefix resolves")
        report(resolve_record(td, "a") is None,
               "resolve-ambiguous", "'a' matches two records and must refuse")
        report(resolve_record(td, "d") is None,
               "resolve-miss", "an unknown prefix reports no record")

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    return 1 if failed else 0


if __name__ == "__main__":
    argv = sys.argv[1:]
    if "--self-test" in argv:
        sys.exit(self_test())
    if "--plan" in argv:
        legs, paths, jf, reason, base = plan("--full" in argv)
        print(f"reason: {reason}")
        print(f"legs:   {', '.join(legs) if legs else '(none needed)'}")
        print(f"paths:  {len(paths) if paths is not None else 'unknown'}")
        print(f"base:   {base or 'unknown'}")
        sys.exit(0)
    if "--debt" in argv:
        report_debt()
        sys.exit(0)
    if "--post" in argv:
        i = argv.index("--post")
        if i + 1 >= len(argv):
            print("signoff: --post needs a commit sha (full or unique prefix)")
            sys.exit(2)
        sys.exit(post_only(argv[i + 1]))
    if "--defer" in argv:
        i = argv.index("--defer")
        sys.exit(defer(" ".join(argv[i + 1:])))
    if "--settle" in argv:
        i = argv.index("--settle")
        sys.exit(settle(" ".join(argv[i + 1:]) or "manual settle"))
    sys.exit(main(argv))
