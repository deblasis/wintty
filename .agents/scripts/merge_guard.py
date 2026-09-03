#!/usr/bin/env python3
"""The merge guard: squash-merge a PR and file the resignoff ceremony.

Policy (issue #969, decided 2026-09-03): a green signoff does not block on
head movement. When `windows` moved after the record was taken, merge
anyway and carry the risk explicitly in a `resignoff-required` issue
instead of re-gating inline. The guard is that ceremony: it refuses on a
missing or bad record, measures what the branch gained since the record's
base, merges, and files the issue with the delta and the risks in words.

Why a script and not a rule: enforcement has to work the way the
signoff-per-head rule works. Agents read AGENTS.md and follow it because a
script makes anything else the hard path; the pr_gate hook denies a raw
`gh pr merge` whose signoff window has moved and names this script's
recipe (`just merge-checked <pr>`). The guard is also the only place that
knows how to build the issue body, which is what keeps the ceremony from
drifting per-agent or per-memory. The hook's own docstring lists the paths
this does not cover (the web UI, a bare `git push`, hosts without the
hook); the guard closes none of those either.

The delta is measured from the record's `base` (the merge base with
origin/windows stamped when the record was written) to the current
origin/windows head, first-parent only: sync-publish lands real merge
commits, and a plain walk would disappear into upstream's history. A
record written before base recording exists is base-unknown; the base is
then estimated at merge time, which equals the original base whenever the
branch only gained commits. The guard fetches `origin windows` itself
before measuring, because a window measured against a stale ref computes
a zero delta out of thin air; a failed fetch demotes the measurement to
possibly-stale, which is printed loudly and carried into the filed issue.

Refusals (nothing is mutated when one fires): no local record for the PR
head, a red record, a deferred record the ledger no longer permits, a
record whose file set or legs no longer match the PR (the same
revalidation the hook does: window movement is forgiven, a record that no
longer describes this PR is not), a checkout that is not this fork, a PR
that is not open and mergeable or that does not target `windows`, and a
window that cannot be measured. The policy forgives head movement only;
it never forgives a bad or absent run.

Modes:
  <pr>             validate, squash-merge, read back the squash sha, verify
                   it against a second fetch, file the resignoff-required
                   issue when the window moved.
  --dry-run <pr>   print the resolved inputs, the delta, the risks and the
                   would-be issue body; mutate nothing. Also the recovery
                   preview for a PR that already merged (it accepts MERGED
                   and uses the recorded mergeCommit as the squash).
  --file-only <pr> compute the delta and file the issue, never merge. The
                   recovery tool for a merge that landed outside the guard
                   (or whose squash sha could not be read back): the owed
                   resignoff issue still needs filing.
  --self-test      replay injected gh/git sessions: the guard must refuse
                   on every refusal path, merge without filing on an
                   unmoved window, file the #970 template (with a
                   structured delta) on a moved one, and mutate nothing
                   under --dry-run.

Exit codes: 0 merged or filed as asked; 1 the environment or a gh/git call
failed part-way (said loudly, because the merge may already have landed);
2 a refusal, nothing mutated - including a PR that could not be loaded,
since nothing has been touched at that point either.

Run with: just merge-checked <pr>
"""

import contextlib
import io
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402
import pr_gate  # noqa: E402  (the repo predicate must agree with the hook's)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
# One repo name and one branch name, aliased from pr_gate: a hook and a
# guard that must agree on "which repo, which branch, how far did it move"
# cannot be allowed to carry divergent copies of either answer.
REPO = pr_gate.REPO
BASE_BRANCH = pr_gate.BASE_BRANCH
BASE_REF = pr_gate.BASE_REF
LABEL = "resignoff-required"
PR_FIELDS = "number,title,headRefOid,state,mergeable,baseRefName,mergeCommit,files"

# Rendering caps. A rewritten base can turn `base..head` into hundreds of
# commits, and an issue nobody reads protects nothing; the full list stays
# one git command away and the body says it was capped.
MAX_COMMIT_BULLETS = 15
MAX_COMMIT_FILES = 12
MAX_RISK_COMMITS = 10

# The hand-filed #970 names commits with 9-character prefixes; the template
# keeps that spelling so a filed issue and its hand-written ancestor read
# alike side by side.
SHORT_SHA = 9
_NUM_WORDS = {2: "two", 3: "three", 4: "four", 5: "five", 6: "six", 7: "seven",
              8: "eight", 9: "nine", 10: "ten"}


class GuardError(Exception):
    """A gh/git call failed in a way the caller should print verbatim."""


class Out:
    """The slice of subprocess.CompletedProcess the runners' callers read.
    The fakes return these so the guard code cannot tell a fake from a real
    child process."""

    def __init__(self, returncode, stdout="", stderr=""):
        self.returncode = returncode
        self.stdout = stdout
        self.stderr = stderr


def git_run(args, cwd=None):
    return subprocess.run(["git"] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True, timeout=60)


def gh_run(args, cwd=None):
    return subprocess.run(["gh"] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True, timeout=120)


def resolve_common_dir(git):
    """The absolute git common dir, or None when it cannot be trusted. The
    records live there, shared across worktrees; a dying session can kill
    the git child and leave empty output, and acting on a guess would read
    (or store) the record in the wrong place."""
    out = git(["rev-parse", "--git-common-dir"])
    common = out.stdout.strip()
    if out.returncode != 0 or not common:
        return None
    if not os.path.isabs(common):
        common = os.path.join(REPO_ROOT, common)
    return common if os.path.isdir(common) else None


def checkout_slug(git):
    """The slug of this checkout's origin remote, or None when git cannot
    answer; both cases refuse, they do not guess. Parsing is pr_gate's
    normalize_slug, the same normalizer the hook's repo predicate runs, so
    a host-qualified remote cannot pass the hook and slip the guard."""
    out = git(["remote", "get-url", "origin"])
    if out.returncode != 0:
        return None
    return pr_gate.normalize_slug(out.stdout.strip())


def fetch_windows(git):
    """`git fetch origin windows`, True on success. The guard fetches on
    its own because the entire point is measuring the window at merge
    time, and a local ref is only as fresh as the last fetch; the hook
    stays fast and local instead, which is exactly why its same-window
    allowance is worded as "as fresh as your last fetch". A failed fetch
    does not stop the flow (an offline host may still have a correct
    local ref), it demotes the measurement to possibly-stale, which is
    printed loudly and carried into the filed issue."""
    return git(["fetch", "origin", BASE_BRANCH]).returncode == 0


def gh_json(args, gh):
    """Run gh and parse its JSON, raising GuardError on failure or on
    unparseable output rather than returning a half-shaped PR."""
    out = gh(args)
    if out.returncode != 0:
        raise GuardError((out.stderr or out.stdout).strip())
    try:
        return json.loads(out.stdout)
    except ValueError as e:
        raise GuardError(f"gh printed unparseable JSON: {e}") from e


def load_pr(number, gh):
    """The PR as the guard sees it: identity, mergeability, the target
    branch, the head sha the record must be keyed to, and (for an already
    merged PR) the squash sha the recovery modes file against."""
    return gh_json(["pr", "view", str(number), "--repo", REPO, "--json", PR_FIELDS], gh)


def load_record(common, head):
    """(record, expected path). The record is keyed by the PR's exact head
    sha, like the hook's own lookup; None means no run was recorded here,
    which the policy treats as missing evidence, not as a pass. A corrupt
    file reads as absent for the same reason: raising out of a guard is a
    denial of the guard, not a failure of the run."""
    d = os.path.join(common, "pr-signoff")
    path = os.path.join(d, f"{head}.json")
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f), path
    except (OSError, ValueError):
        return None, path


def windows_head(git):
    """The local position of origin/windows, read after fetch_windows.
    Deliberately a local ref and not a gh call: the delta is measured in
    THIS checkout's history, where the record's base lives."""
    out = git(["rev-parse", "--verify", BASE_REF])
    sha = out.stdout.strip()
    return sha if out.returncode == 0 and sha else None


def record_base(rec, head, git):
    """(base, estimated). The recorded base is authoritative; a legacy
    record without one is base-unknown and the base is re-derived from the
    merge base, which is stable under ordinary branch movement (commits
    landing on windows do not move the merge base of an unchanged PR head)
    and errs toward a larger, never smaller, delta when history was
    rewritten."""
    base = (rec or {}).get("base")
    if base:
        return base, False
    out = git(["merge-base", head, BASE_REF])
    if out.returncode != 0:
        return None, True
    return out.stdout.strip() or None, True


def _paths(text):
    return sorted(p.strip().replace("\\", "/") for p in text.splitlines() if p.strip())


def commit_files(sha, git):
    """Files one delta commit touched. First-parent is the honest answer for
    a merge commit: the diff against what the branch actually gained. Plain
    diff-tree answers a merge with nothing, which would read as an empty,
    risk-free commit; older gits without --diff-merges fall back to the
    plain form and under-report a merge rather than mis-report it."""
    out = git(["diff-tree", "-r", "--name-only", "--no-commit-id",
               "--diff-merges=first-parent", sha])
    if out.returncode != 0:
        out = git(["diff-tree", "-r", "--name-only", "--no-commit-id", sha])
        if out.returncode != 0:
            return []
    return _paths(out.stdout)


def delta_between(base, win, git):
    """{commits, files}: what the branch gained between the record's base
    and the merge-time head. First-parent is required, not cosmetic: the
    commits that matter are the ones windows itself advanced by (PR
    squashes, sync-publish merges), and a plain rev-list would walk every
    upstream commit a sync merged in and drown the risk in noise."""
    out = git(["rev-list", "--first-parent", f"{base}..{win}"])
    if out.returncode != 0:
        raise GuardError(f"rev-list failed: {out.stderr.strip()}")
    shas = [s.strip() for s in out.stdout.splitlines() if s.strip()]
    shas.reverse()  # oldest first, so the filed issue reads in landing order
    commits = []
    for sha in shas:
        s = git(["show", "-s", "--format=%s", sha])
        subject = s.stdout.strip().splitlines()[0] if s.stdout.strip() else "(no subject)"
        commits.append({"sha": sha, "subject": subject, "files": commit_files(sha, git)})
    d = git(["diff-tree", "-r", "--name-only", base, win])
    if d.returncode != 0:
        raise GuardError(f"diff-tree failed: {d.stderr.strip()}")
    return {"commits": commits, "files": _paths(d.stdout)}


def scope_overlap(rec, delta_files, pr_files=None):
    """Where the delta and the compared scope meet, in the three terms the
    ceremony cares about: the same files, the same top-level directories,
    the same signoff legs. The legs are recomputed from the delta's files
    with gate_scope, so the question asked is exactly the one the record
    answered, only against the branch as it now stands. When the record
    predates scoping (no scope.paths), the PR's own file list stands in as
    the comparison set: an unscoped record ran the whole ladder, but the
    intersections still say something worth reading."""
    scope = (rec or {}).get("scope") or {}
    rec_paths = scope.get("paths")
    if rec_paths is None:
        rec_paths = pr_files or []
    rec_legs = set(scope.get("legs_run") or [])

    def top_dirs(paths):
        return {p.split("/")[0] for p in paths}

    required = set(gate_scope.required_legs(delta_files))
    return {
        "files": sorted(set(delta_files) & set(rec_paths)),
        "dirs": sorted(top_dirs(delta_files) & top_dirs(rec_paths)),
        "legs": sorted(required & rec_legs),
        "unknown": gate_scope.unknown_paths(delta_files),
    }


def risk_lines(overlap, delta_commits, delta_files):
    """The risks in words, numbered for the issue. The first risk is the
    delta itself and always fires; the rest fire only when an intersection
    is non-empty, and when nothing intersects that is said outright, because
    a silent risks section and a computed "none" are different claims. The
    commit list inside the first risk is capped like the body: a rewritten
    base must not turn the headline risk into a wall of shas."""
    shown = delta_commits[:MAX_RISK_COMMITS]
    commits_note = "; ".join(f"`{short(c['sha'])}` {c['subject']}" for c in shown)
    hidden = len(delta_commits) - len(shown)
    if hidden:
        commits_note += f"; and {hidden} more"
    out = [
        f"The delta itself: {len(delta_commits)} commit(s) ({commits_note}) landed on `windows` "
        f"after the record's base, touching {len(delta_files)} file(s), and none of it was "
        "exercised by the signed-off run."
    ]
    if overlap["files"]:
        out.append("Same files as the record's scope: " +
                   ", ".join(f"`{p}`" for p in overlap["files"]) +
                   ". The green run covered these paths in their older state.")
    if overlap["dirs"]:
        out.append("Same top-level directories as the record's scope: " +
                   ", ".join(f"`{d}/`" for d in overlap["dirs"]) +
                   ". Both sides of the merge land in one place and were never run together.")
    if overlap["legs"]:
        out.append("Same signoff legs as the record: " +
                   ", ".join(f"`{l}`" for l in overlap["legs"]) +
                   ". The record ran them green against the old branch state; the result may "
                   "no longer hold.")
    if overlap["unknown"]:
        out.append("The delta touches path(s) no scoping rule classifies: " +
                   ", ".join(f"`{p}`" for p in overlap["unknown"][:5]) +
                   ", so it could affect any leg.")
    if len(out) == 1:
        out.append("No file, directory or signoff-leg overlap between the delta and the "
                   "record's scope was found; the residual risk is only that the signed-off "
                   "code was not re-run against the branch as it now stands.")
    return out


def short(sha):
    """A sha prefix at the template's length, or the value verbatim: the
    placeholders the dry-run and the unreadable-squash path use are not
    shas and must not be cut mid-word."""
    return sha[:SHORT_SHA] if len(sha) > SHORT_SHA else sha


def issue_title(pr, squash):
    """One scannable line for the label's issue list: what merged, as what
    squash, and why this issue exists."""
    return (f"resignoff-required: PR #{pr.get('number', '?')} ({pr.get('title', 'untitled')}) "
            f"squashed as {short(squash)} on a moved windows window")


def legs_note(rec):
    """How the record's legs are described in the issue, matching the
    hand-filed #970's wording ("all four legs rc=0"), which is why small
    counts are spelled out. A red record never reaches this point, so the
    only cases are a counted green run and the two flavours of record that
    carry no per-step detail."""
    steps = rec.get("steps") or {}
    if steps:
        n = len(steps)
        return f"all {_NUM_WORDS.get(n, n)} legs rc=0"
    if rec.get("deferred"):
        return "deferred record, no per-leg detail"
    return "no per-leg detail recorded (legacy record)"


def display_path(sha):
    """The record's path as public output names it: the canonical
    `.git/pr-signoff/<sha>.json`, never the machine's absolute path, which
    leaks a home directory into a filed issue and varies per clone. The
    hand-filed #970 used exactly this canonical form."""
    return f".git/pr-signoff/{sha}.json"


def issue_body(pr, rec, rec_path, base, base_estimated, win, squash, delta, risks,
               notes=(), backlog=None):
    """The issue body: the #970 template, with a structured delta. The
    hand-filed original proved the shape; computing it here is what keeps
    the next filing from drifting per-agent or per-memory. Caps keep a
    rewritten-history merge from producing a body nobody reads, the fixed
    last risk names the one thing no re-run can ever cover (the squash
    commit itself was never signed off), and `notes` carries the
    measurement caveats (a stale fetch, a rewritten base, movement in
    flight) that would otherwise live only in the agent's console."""
    lines = [
        "Filed by the merge guard (issue #969): this PR merged on a green signoff whose "
        "`windows` window had already moved. Nothing was re-gated; the risk is carried here "
        "instead.",
        "",
        f"- PR: #{pr.get('number', '?')}, squashed as `{short(squash)}` on `windows`",
        f"- Signed off: `{pr.get('headRefOid', '?')}` (record at `{rec_path}`, {legs_note(rec)})",
    ]
    base_line = f"- `windows` base at signoff time: `{short(base)}`"
    if base_estimated:
        base_line += " (estimated at merge time: the record predates base recording)"
    lines.append(base_line)
    lines.append(f"- `windows` head at merge time: `{short(win)}`")
    if notes:
        lines += ["", "## Notes", ""] + [f"- {n}" for n in notes]
    lines += [
        "",
        "## Delta (what merged between the record and the squash)",
        "",
        f"{len(delta['commits'])} commit(s) touching {len(delta['files'])} file(s):",
        "",
    ]
    shown = delta["commits"][:MAX_COMMIT_BULLETS]
    for c in shown:
        files = c["files"][:MAX_COMMIT_FILES]
        rendered = ", ".join(f"`{p}`" for p in files)
        more = len(c["files"]) - len(files)
        if more:
            rendered += f", +{more} more file(s)"
        lines.append(f"- `{short(c['sha'])}` {c['subject']} "
                     f"[{len(c['files'])} file(s): {rendered}]")
    hidden = len(delta["commits"]) - len(shown)
    if hidden:
        lines.append(f"- and {hidden} more commit(s) (capped; `git log --first-parent "
                     f"{short(base)}..{short(win)}` has the full list)")
    lines += ["", "## Risks", ""]
    lines += [f"{i}. {r}" for i, r in enumerate(risks, 1)]
    lines.append(
        f"{len(risks) + 1}. The squash commit itself was never signed off: the green record "
        f"covers `{short(pr.get('headRefOid', '?'))}` and the squash rewrote that history "
        f"into `{short(squash)}`.")
    lines += [
        "",
        "## Status",
        "",
        f"Resignoff for `{short(squash)}`: not started at filing time. Check "
        "`incoda status --queue wintty` before queuing a run; the #969 bot owns this lane "
        "once it lands.",
        f"Squash commit: `{squash}`" + (" (full sha)" if len(squash) == 40 else ""),
    ]
    if backlog:
        lines.append(backlog)
    return "\n".join(lines) + "\n"


def backlog_line(gh):
    """One `gh issue list` call at filing time, as 'Outstanding ... (oldest
    #M)'. The count is taken before this issue is created, so the number is
    the backlog the filer saw, not one that already includes itself. None
    when gh cannot answer: the backlog line is context, not evidence, and
    must not be allowed to fail a merge that already landed."""
    out = gh(["issue", "list", "--repo", REPO, "--label", LABEL,
              "--state", "open", "--json", "number,createdAt"])
    if out.returncode != 0:
        return None
    try:
        issues = json.loads(out.stdout)
    except ValueError:
        return None
    if not issues:
        return None
    oldest = min(issues, key=lambda i: i.get("createdAt", ""))
    return (f"Outstanding `{LABEL}` issues at filing time: {len(issues)} "
            f"(oldest #{oldest.get('number', '?')}).")


def refusals(pr, rec, ledger, mode="merge"):
    """Every reason the guard refuses before touching anything, as
    (code, message) pairs. All of them are reported together, so one run
    tells the agent everything to fix rather than only the first thing.
    The scope revalidation is the hook's own two checks: a record whose
    file set or legs no longer match the PR must be re-taken even though
    the window policy forgives movement. The recovery modes forgive the
    closed-state checks instead, because their whole point is a PR that
    already merged."""
    out = []
    head = pr.get("headRefOid", "?")
    n = pr.get("number", "?")
    if rec is None:
        out.append(("signoff-missing",
                    f"no local signoff record for head {head[:10]} (expected at "
                    f"{display_path(head)}). Run 'just signoff' on the PR branch; the guard "
                    "merges on recorded evidence, never on a claim (records live in this "
                    "clone's git dir; a signoff run on another machine does not count here)."))
    else:
        if not rec.get("deferred") and not rec.get("pass"):
            failed = [k for k, v in (rec.get("steps") or {}).items() if v.get("rc") != 0]
            out.append(("signoff-red",
                        f"the signoff for {head[:10]} is red (failed: "
                        f"{', '.join(failed) or 'unknown'}). The window policy forgives head "
                        "movement, never a bad run; fix and rerun 'just signoff'."))
        if rec.get("deferred"):
            blockers = gate_scope.ledger_blockers(ledger)
            if blockers:
                out.append(("signoff-defer-blocked",
                            "the deferred signoff for " + head[:10] + " is refused - " +
                            "; ".join(blockers) +
                            ". Settle the debt with 'just signoff-full' first; the guard does "
                            "not extend credit the ledger refuses."))
        scope = rec.get("scope")
        if scope is not None and rec.get("pass") and not rec.get("deferred"):
            pr_paths = sorted(f["path"].replace("\\", "/") for f in pr.get("files", []))
            rec_paths = scope.get("paths")
            if rec_paths is not None and sorted(rec_paths) != pr_paths:
                out.append(("signoff-stale",
                            f"the signoff for {head[:10]} was computed over a different file "
                            f"set than this PR reports ({len(rec_paths)} vs {len(pr_paths)} "
                            "paths). Re-run 'just signoff'; the guard files the delta for "
                            "branch movement, never for a PR that changed under its record."))
            else:
                required = set(gate_scope.required_legs(
                    pr_paths, justfile_legs=scope.get("justfile_legs")))
                missing = sorted(required - set(scope.get("legs_run") or []))
                if missing:
                    out.append(("signoff-scope",
                                f"the signoff for {head[:10]} did not run {', '.join(missing)},"
                                " which this PR's files require. Re-run 'just signoff' (or "
                                "'just signoff-full')."))
    if mode == "merge":
        if pr.get("state") != "OPEN":
            out.append(("pr-not-open",
                        f"PR #{n} is {pr.get('state') or 'in an unknown state'}, not OPEN; if "
                        "it is MERGED, the owed resignoff issue still needs filing - "
                        f"`--dry-run {n}` regenerates the body, `--file-only {n}` files it."))
        if pr.get("mergeable") != "MERGEABLE":
            retry = " (GitHub may still be computing it; retry)" \
                if pr.get("mergeable") == "UNKNOWN" else ""
            out.append(("pr-not-mergeable",
                        f"PR #{n} reports mergeable={pr.get('mergeable') or 'unknown'}{retry}."))
    else:
        # Recovery modes: MERGED is their whole point, OPEN is the normal
        # path's business, anything else is unreasonable.
        if pr.get("state") not in ("MERGED", "OPEN"):
            out.append(("pr-not-open",
                        f"PR #{n} is {pr.get('state') or 'in an unknown state'}; only an OPEN "
                        "or MERGED PR can be reasoned about."))
        if mode == "file-only" and pr.get("state") == "OPEN":
            out.append(("pr-still-open",
                        f"PR #{n} is still open; `--file-only` exists for merges that already "
                        f"landed. Use `just merge-checked {n}` to merge, or `--dry-run {n}` to "
                        "preview."))
    if pr.get("baseRefName") != BASE_BRANCH:
        out.append(("base-branch",
                    f"PR #{n} targets `{pr.get('baseRefName') or 'nothing resolvable'}`, not "
                    f"`{BASE_BRANCH}`; the guard only knows the {BASE_BRANCH} ceremony."))
    return out


def verify_squash_parent(squash, win, number, git, notes):
    """Second fetch + parent check after the squash is read back. GitHub put
    the squash on `windows` while this flow was running; if the branch moved
    again in flight, the measured window is already behind the merge and the
    filed delta understates it. The remedy is a re-measure against the
    squash's actual parent and an amend of the issue, so both are said out
    loud and the caveat rides in the body too."""
    fetch_windows(git)
    out = git(["rev-parse", f"{squash}^"])
    parent = out.stdout.strip() if out.returncode == 0 else None
    if not parent or parent == win:
        return
    print("merge-guard: WARNING: `windows` moved in flight: the squash's parent is "
          f"{short(parent)}, not the {short(win)} the delta was measured from. Re-run "
          f"`just merge-checked {number} --dry-run` against that parent and amend the "
          "filed issue.")
    notes.append("`windows` moved in flight: the squash's parent is `" + short(parent) +
                 "`, not the measured `" + short(win) + "`. Re-run "
                 f"`just merge-checked {number} --dry-run` against that parent and amend "
                 "this issue.")


def file_issue(pr, rec, head, base, base_estimated, win, squash, delta, risks, notes, gh):
    """Render the body and file the labelled issue, printing the body for a
    hand filing when gh refuses. Returns this step's exit code: 0 filed,
    1 not filed. The body is on stdout either way, so a human (or a follow
    -up `gh issue create`) is never blocked by a transient gh failure."""
    body = issue_body(pr, rec, display_path(head), base, base_estimated, win, squash,
                      delta, risks, notes=notes, backlog=backlog_line(gh))
    out = gh(["issue", "create", "--repo", REPO, "--label", LABEL,
              "--title", issue_title(pr, squash), "--body", body])
    if out.returncode != 0:
        print(f"merge-guard: filing the {LABEL} issue failed (rc={out.returncode}): "
              f"{(out.stderr or out.stdout).strip()}")
        print("merge-guard: file it by hand with this body:")
        print(body)
        return 1
    url = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else "(no url printed)"
    print(f"merge-guard: filed {LABEL}: {url}")
    return 0


def merge_flow(number, mode="merge", gh=None, git=None, sleep=time.sleep):
    """The whole sequence: fetch, validate, measure, merge, read back,
    verify, file. mode is "merge", "dry-run" or "file-only". Returns the
    process exit code (see the module docstring). gh, git and sleep are
    injectable so the self-test can replay whole sessions without touching
    GitHub or the clock."""
    gh = gh or gh_run
    git = git or git_run
    number = int(number)

    common = resolve_common_dir(git)
    if not common:
        print("merge-guard: could not resolve the git common dir; refusing to act in a "
              "half-dead session.")
        return 2
    slug = checkout_slug(git)
    if not slug or not pr_gate.is_our_repo(slug):
        print(f"merge-guard: wrong-repo: this checkout's origin is {slug or 'unresolvable'}; "
              f"the guard only merges {REPO}. Clone {REPO} (or point this checkout's origin "
              "at it) and re-run.")
        return 2
    if not fetch_windows(git):
        print(f"merge-guard: WARNING: `git fetch origin {BASE_BRANCH}` failed; the delta is "
              f"measured against a possibly stale {BASE_REF}. Re-check against a fresh fetch "
              "and amend the filed issue if it changed.")
        stale = True
    else:
        stale = False
    win = windows_head(git)
    if not win:
        print(f"merge-guard: could not resolve {BASE_REF} in this checkout; fetch first. "
              "The delta cannot be measured without it.")
        return 2

    try:
        pr = load_pr(number, gh)
    except GuardError as e:
        # rc 2, not 1: nothing has been touched, so this is a refusal in
        # every sense the exit codes describe.
        print(f"merge-guard: could not load PR #{number} from {REPO}: {e}")
        return 2

    head = pr.get("headRefOid") or ""
    rec, _rec_path = load_record(common, head)
    ledger = gate_scope.load_ledger(os.path.join(common, "pr-signoff"))
    problems = refusals(pr, rec, ledger, mode)
    if problems:
        print("merge-guard: refusing (nothing was mutated):")
        for code, msg in problems:
            print(f"merge-guard:   [{code}] {msg}")
        return 2

    base, estimated = record_base(rec, head, git)
    if not base:
        print("merge-guard: window-unknown: the record carries no base and no merge base is "
              "resolvable; refusing rather than filing a blind issue.")
        return 2
    ancestor = git(["merge-base", "--is-ancestor", base, win]).returncode == 0
    if not ancestor:
        print("merge-guard: WARNING: the record's base is not an ancestor of the merge-time "
              f"{BASE_REF} head (history was rewritten); the delta below is capped and may "
              "not be a faithful 'what landed' list. Re-run 'just signoff' if in doubt.")
    try:
        delta = delta_between(base, win, git)
    except GuardError as e:
        print(f"merge-guard: could not measure the delta: {e}")
        return 1
    moved = bool(delta["commits"])
    est = " (estimated)" if estimated else ""

    notes = []
    if stale:
        notes.append(f"`git fetch origin {BASE_BRANCH}` failed before measuring, so this "
                     f"delta is computed against a possibly stale {BASE_REF}; re-run "
                     f"`just merge-checked {number} --dry-run` after a fresh fetch and amend "
                     "this issue if it changed.")
    if not ancestor:
        notes.append("the record's base is not an ancestor of the merge-time `windows` head "
                     "(history was rewritten); the delta below is capped and may not be a "
                     "faithful 'what landed' list.")
    pr_files = [f["path"].replace("\\", "/") for f in pr.get("files", [])]
    risks = risk_lines(scope_overlap(rec, delta["files"], pr_files),
                       delta["commits"], delta["files"])

    if mode == "dry-run":
        squash = (pr.get("mergeCommit") or {}).get("oid") or "<squash>"
        print(f"merge-guard: PR #{pr.get('number')} {pr.get('title', '')}")
        print(f"merge-guard: head            {head}")
        print(f"merge-guard: record          {display_path(head)} "
              f"(pass={rec.get('pass')}, deferred={bool(rec.get('deferred'))})")
        print(f"merge-guard: record base     {short(base)}{est}")
        print(f"merge-guard: {BASE_REF} at merge time {short(win)}")
        print(f"merge-guard: delta           {len(delta['commits'])} commit(s), "
              f"{len(delta['files'])} file(s)")
        for c in delta["commits"]:
            print(f"merge-guard:   {short(c['sha'])} {c['subject']} "
                  f"[{len(c['files'])} file(s)]")
        if not moved:
            print("merge-guard: the window has not moved; a merge through the guard would "
                  "file nothing.")
            print("merge-guard: dry run: nothing merged, nothing filed.")
            return 0
        if pr.get("state") == "MERGED":
            print("merge-guard: this PR is already MERGED; the body below is the recovery "
                  "body (file it with `--file-only`).")
        else:
            print("merge-guard: would merge with: gh pr merge "
                  f"{number} --repo {REPO} --squash --delete-branch")
        print(f"merge-guard: would file issue titled: {issue_title(pr, squash)}")
        print("merge-guard: with this body:")
        # The backlog line is in the preview too, so the dry-run body is
        # what filing would actually produce, backlog included.
        print(issue_body(pr, rec, display_path(head), base, estimated, win, squash,
                         delta, risks, notes=notes, backlog=backlog_line(gh)))
        print("merge-guard: dry run: nothing merged, nothing filed.")
        return 0

    if mode == "file-only":
        squash = (pr.get("mergeCommit") or {}).get("oid")
        if not squash:
            print(f"merge-guard: GitHub reports no mergeCommit for PR #{number} yet; nothing "
                  "to file against. Retry shortly, or file by hand from a --dry-run body.")
            return 1
        verify_squash_parent(squash, win, number, git, notes)
        if not moved:
            print("merge-guard: the measured delta is empty; filing anyway because "
                  "--file-only was asked for explicitly (delete the issue if that was "
                  "wrong).")
        return file_issue(pr, rec, head, base, estimated, win, squash, delta, risks,
                          notes, gh)

    # mode == "merge": the real thing. From here on a failure is a 1, not
    # a 2, because the merge may already have landed.
    out = gh(["pr", "merge", str(number), "--repo", REPO, "--squash", "--delete-branch"])
    if out.returncode != 0:
        print(f"merge-guard: the merge command failed (rc={out.returncode}): "
              f"{(out.stderr or out.stdout).strip()}")
        return 1

    # The squash sha only exists once GitHub finishes the merge, so it is
    # polled rather than assumed; without it the issue cannot be filed in
    # the #970 template, which names the squash.
    squash = None
    for _ in range(6):
        try:
            data = gh_json(["pr", "view", str(number), "--repo", REPO, "--json", "mergeCommit"],
                           gh)
            squash = (data.get("mergeCommit") or {}).get("oid")
        except GuardError:
            squash = None
        if squash:
            break
        sleep(2)
    if not squash:
        print(f"merge-guard: the squash sha never appeared; the {LABEL} issue was NOT filed. "
              f"File it with `python .agents/scripts/merge_guard.py --file-only {number}` "
              "once GitHub reports the mergeCommit, or by hand with this body:")
        print(issue_body(pr, rec, display_path(head), base, estimated, win, "<squash>",
                         delta, risks, notes=notes))
        return 1
    print(f"merge-guard: squashed as {short(squash)}")
    verify_squash_parent(squash, win, number, git, notes)

    if not moved:
        print("merge-guard: the window had not moved since the record was taken; "
              "nothing to file.")
        return 0
    return file_issue(pr, rec, head, base, estimated, win, squash, delta, risks,
                      notes, gh)


class FakeGit:
    """A router that answers only the spellings the guard issues, so a
    scenario names what git would say and nothing more. An unhandled call
    fails loudly instead of guessing, which is what keeps a self-test from
    passing against a command the real flow never runs."""

    def __init__(self, common, win, remote=None, merge_base=None, commits=None,
                 all_files=None, fetch_ok=True, ancestor_ok=True, squash_parent=None):
        self.calls = []
        self.common = common
        self.win = win
        self.remote = remote
        self.merge_base = merge_base
        self.commits = commits or []      # landing order, oldest first
        self.all_files = all_files or []
        self.fetch_ok = fetch_ok
        self.ancestor_ok = ancestor_ok
        self.squash_parent = squash_parent

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        a = args
        if a[0] == "rev-parse" and "--git-common-dir" in a:
            return Out(0, self.common + "\n")
        if a[:3] == ["rev-parse", "--verify", BASE_REF]:
            return Out(0, self.win + "\n")
        if a[0] == "rev-parse" and len(a) == 2 and a[1].endswith("^"):
            # The squash's parent: the measured window unless the scenario
            # says the branch moved again in flight.
            return Out(0, (self.squash_parent or self.win) + "\n")
        if a[0] == "remote":
            return Out(0, (self.remote or "") + "\n")
        if a[0] == "fetch":
            return Out(0, "") if self.fetch_ok else Out(1, "", "could not resolve host\n")
        if a[:2] == ["merge-base", "--is-ancestor"]:
            return Out(0, "") if self.ancestor_ok else Out(1, "")
        if a[0] == "merge-base":
            if self.merge_base:
                return Out(0, self.merge_base + "\n")
            return Out(1, "")
        if a[0] == "rev-list":
            lo = a[-1].split("..")[0]
            shas = [] if lo == self.win else [c["sha"] for c in reversed(self.commits)]
            return Out(0, "".join(s + "\n" for s in shas))
        if a[:3] == ["show", "-s", "--format=%s"]:
            sha = a[3]
            for c in self.commits:
                if c["sha"] == sha:
                    return Out(0, c["subject"] + "\n")
            return Out(1, "")
        # Per-commit form first: it is a prefix-shape of the two-commit one,
        # and the order of these branches is the difference between a per
        # commit file list and the whole delta.
        if a[0] == "diff-tree" and "--no-commit-id" in a:
            sha = a[-1]
            for c in self.commits:
                if c["sha"] == sha:
                    return Out(0, "".join(p + "\n" for p in c["files"]))
            return Out(0, "")
        if a[0] == "diff-tree" and len(a) == 5:
            return Out(0, "".join(p + "\n" for p in self.all_files))
        return Out(1, "", f"unexpected git call: {a}")


class FakeGh:
    """Records every call and answers the spellings the flow uses: pr view
    (fields, and mergeCommit after the merge), pr merge, issue list, and
    issue create."""

    def __init__(self, pr, squash="e" * 40, merge_rc=0, issue_rc=0, backlog=(),
                 fail_view=False):
        self.calls = []
        self.pr = pr
        self.squash = squash
        self.merge_rc = merge_rc
        self.issue_rc = issue_rc
        self.backlog = list(backlog)
        self.fail_view = fail_view

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        j = " ".join(args)
        if j.startswith("pr view") and self.fail_view:
            return Out(1, "", "gh: could not resolve to a pull request\n")
        # The read-back poll asks for mergeCommit alone; the fields fetch
        # also contains the word, so match the poll by its exact tail.
        if j.startswith("pr view") and j.endswith("--json mergeCommit"):
            return Out(0, json.dumps({"mergeCommit": {"oid": self.squash}}))
        if j.startswith("pr view"):
            return Out(0, json.dumps(self.pr))
        if j.startswith("pr merge"):
            return Out(0, "squashed\n") if self.merge_rc == 0 \
                else Out(1, "", "merge refused by gh\n")
        if j.startswith("issue list"):
            return Out(0, json.dumps(self.backlog))
        if j.startswith("issue create"):
            return Out(0, "https://github.com/deblasis/wintty/issues/999\n") \
                if self.issue_rc == 0 else Out(1, "", "issue create failed\n")
        return Out(1, "", f"unexpected gh call: {j}")


def make_pr(number, head, state="OPEN", mergeable="MERGEABLE", base_ref="windows",
            merge_commit=None, files=()):
    """A PR shaped the way gh returns it, with only the fields the guard
    reads; the fixtures carry the real-world equivalents."""
    return {"number": number, "title": f"synthetic pr {number}", "headRefOid": head,
            "state": state, "mergeable": mergeable, "baseRefName": base_ref,
            "mergeCommit": {"oid": merge_commit} if merge_commit else None,
            "files": [{"path": p} for p in files]}


def write_record(common, head, record):
    """Store a record where load_record will find it, so the self-test
    exercises the real file path rather than a bypassed loader."""
    d = os.path.join(common, "pr-signoff")
    os.makedirs(d, exist_ok=True)
    path = os.path.join(d, f"{head}.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(record, f)
    return path


def run_capture(fn, *a, **kw):
    """merge_flow's exit code and captured stdout, so assertions read the
    agent-facing output and not the return code alone."""
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rc = fn(*a, **kw)
    return rc, buf.getvalue()


def merged_calls(fgh):
    """Whether any gh call was a merge, in any spelling the flow uses. A
    bare pr view or issue list is not a mutation, so neither counts."""
    return any(c[0] == "pr" and c[1] == "merge" for c in fgh.calls)


def filed_calls(fgh):
    """The issue-create calls, for asserting what was (or was not) filed."""
    return [c for c in fgh.calls if c[0] == "issue" and c[1] == "create"]


def bodies_of(fgh):
    """The --body payload of every issue-create call."""
    return [c[c.index("--body") + 1] for c in filed_calls(fgh) if "--body" in c]


def self_test():
    """Replay injected sessions against the refusal matrix (including the
    hook's scope revalidation), the same-window merge, the moved-window
    filing against a golden body in the #970 template, the real #958
    numbers against #970's actual bullets, the estimated-base legacy
    record, the recovery modes, the fetch/verify warnings, the rendering
    caps, the dry-run's nothing-mutated promise, and the remote-slug
    parsing shared with the hook."""
    import shutil
    import tempfile

    failed = False

    def report(ok, label, detail=""):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}{': ' + detail if detail else ''}")

    head = "d" * 40
    base = "b" * 40
    win = "a" * 40
    c1, c2 = "1" * 40, "2" * 40
    commits = [
        {"sha": c1, "subject": "one: first landed change (#100)",
         "files": ["windows/scripts/a.ps1"]},
        {"sha": c2, "subject": "two: second landed change (#101)", "files": ["src/lib.zig"]},
    ]
    all_files = ["src/lib.zig", "windows/scripts/a.ps1"]
    green = {"sha": head, "base": base, "steps": {"windows-tests": {"rc": 0},
                                                  "zig-fmt": {"rc": 0}},
             "pass": True, "scope": {"paths": ["windows/scripts/a.ps1"],
                                     "legs_run": ["windows-tests", "zig-fmt"],
                                     "justfile_legs": [], "reason": "scoped", "full": False}}
    red = dict(green, **{"pass": False})
    deferred = dict(green, deferred=True, reason="batching small prs")
    legacy = {k: v for k, v in green.items() if k != "base"}
    files_ok = ("windows/scripts/a.ps1",)

    tmp = tempfile.mkdtemp(prefix="merge-guard-selftest-")
    try:
        # --- refusal matrix: each path refuses and issues no merge call.
        narrow_legs = dict(green, scope=dict(green["scope"], paths=["src/lib.zig"],
                                             legs_run=[]))
        cases = [
            ("signoff-missing", make_pr(700, head, files=files_ok), None, False),
            ("signoff-red", make_pr(700, head, files=files_ok), red, False),
            ("signoff-defer-blocked", make_pr(700, head, files=files_ok), deferred, True),
            ("pr-not-open", make_pr(700, head, state="CLOSED", files=files_ok), green, False),
            ("pr-not-mergeable", make_pr(700, head, mergeable="CONFLICTING",
                                         files=files_ok), green, False),
            ("base-branch", make_pr(700, head, base_ref="main", files=files_ok), green, False),
            # The guard revalidates scope exactly like the hook: a record
            # over a different file set, and a record that skipped a leg
            # the PR's own files require, both refuse.
            ("signoff-stale", make_pr(700, head, files=("other/file.zig",)), green, False),
            ("signoff-scope", make_pr(700, head, files=("src/lib.zig",)), narrow_legs, False),
        ]
        for label, pr, rec, with_ledger in cases:
            td = tempfile.mkdtemp(dir=tmp)
            if rec is not None:
                write_record(td, head, rec)
            if with_ledger:
                import datetime
                now = datetime.datetime.now(datetime.timezone.utc).isoformat()
                with open(os.path.join(td, "pr-signoff", gate_scope.LEDGER_NAME), "w",
                          encoding="utf-8") as f:
                    json.dump([{"sha": f"{i}" * 40, "created": now, "reason": "r"}
                               for i in range(gate_scope.DEFER_MAX_OUTSTANDING)], f)
            fgh = FakeGh(pr)
            fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                           merge_base=base, commits=commits, all_files=all_files)
            rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
            report(rc == 2 and not merged_calls(fgh) and f"[{label}]" in out,
                   "refuse-" + label, f"rc={rc}, merged={merged_calls(fgh)}")

        # wrong-repo: a checkout of some other project never reaches gh,
        # and the message names the remedy.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/other/fork.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 2 and not fgh.calls and "wrong-repo" in out
               and "clone deblasis/wintty" in out.lower(),
               "refuse-wrong-repo", f"rc={rc}, gh calls={len(fgh.calls)}")

        # window-unknown: a legacy record with no base and none derivable.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, legacy)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=None, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 2 and not merged_calls(fgh) and not filed_calls(fgh)
               and "window-unknown" in out,
               "refuse-window-unknown", f"rc={rc}")

        # pr load failure before anything is touched: a refusal (rc 2),
        # not a part-way failure.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head), merge_rc=0, fail_view=True)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=FakeGit(
            td, win, remote="https://github.com/deblasis/wintty.git",
            merge_base=base, commits=commits, all_files=all_files))
        report(rc == 2 and not merged_calls(fgh) and not filed_calls(fgh),
               "refuse-pr-unloadable", f"rc={rc}")

        # --- same window: merges, files nothing.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, dict(green, base=win))
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=win, commits=[], all_files=[])
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 0 and merged_calls(fgh) and not filed_calls(fgh)
               and "nothing to file" in out,
               "same-window", f"rc={rc}, merged={merged_calls(fgh)}, "
                              f"filed={len(filed_calls(fgh))}")

        # --- moved window: files the issue with the golden body.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        golden = (
            "Filed by the merge guard (issue #969): this PR merged on a green signoff whose "
            "`windows` window had already moved. Nothing was re-gated; the risk is carried "
            "here instead.\n"
            "\n"
            "- PR: #700, squashed as `eeeeeeeee` on `windows`\n"
            f"- Signed off: `{head}` (record at `.git/pr-signoff/{head}.json`, "
            "all two legs rc=0)\n"
            f"- `windows` base at signoff time: `{base[:SHORT_SHA]}`\n"
            f"- `windows` head at merge time: `{win[:SHORT_SHA]}`\n"
            "\n"
            "## Delta (what merged between the record and the squash)\n"
            "\n"
            "2 commit(s) touching 2 file(s):\n"
            "\n"
            f"- `{c1[:SHORT_SHA]}` one: first landed change (#100) "
            "[1 file(s): `windows/scripts/a.ps1`]\n"
            f"- `{c2[:SHORT_SHA]}` two: second landed change (#101) "
            "[1 file(s): `src/lib.zig`]\n"
            "\n"
            "## Risks\n"
            "\n"
            "1. The delta itself: 2 commit(s) (`111111111` one: first landed change (#100); "
            "`222222222` two: second landed change (#101)) landed on `windows` after the "
            "record's base, touching 2 file(s), and none of it was exercised by the "
            "signed-off run.\n"
            "2. Same files as the record's scope: `windows/scripts/a.ps1`. The green run "
            "covered these paths in their older state.\n"
            "3. Same top-level directories as the record's scope: `windows/`. Both sides of "
            "the merge land in one place and were never run together.\n"
            "4. Same signoff legs as the record: `windows-tests`, `zig-fmt`. The record ran "
            "them green against the old branch state; the result may no longer hold.\n"
            f"5. The squash commit itself was never signed off: the green record covers "
            f"`{head[:SHORT_SHA]}` and the squash rewrote that history into `eeeeeeeee`.\n"
            "\n"
            "## Status\n"
            "\n"
            "Resignoff for `eeeeeeeee`: not started at filing time. Check "
            "`incoda status --queue wintty` before queuing a run; the #969 bot owns this "
            "lane once it lands.\n"
            f"Squash commit: `{'e' * 40}` (full sha)\n"
        )
        report(rc == 0 and len(bodies) == 1 and bodies[0] == golden,
               "moved-window-golden-body",
               "body matches the #970 template" if rc == 0 else f"rc={rc}, out={out}")
        if bodies and bodies[0] != golden:
            report(False, "golden-diff",
                   "\n--- got ---\n" + bodies[0] + "\n--- want ---\n" + golden)
        filed = filed_calls(fgh)
        report(bool(filed) and "--label" in filed[0] and LABEL in filed[0],
               "issue-labelled", f"filed={len(filed)}")

        # The backlog line is one list call before the create, and names
        # the oldest issue the filer saw (this one is not yet among them).
        fgh2 = FakeGh(make_pr(700, head, files=files_ok), backlog=[
            {"number": 968, "createdAt": "2026-09-02T10:00:00Z"},
            {"number": 965, "createdAt": "2026-09-01T10:00:00Z"}])
        fgit2 = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                        merge_base=base, commits=commits, all_files=all_files)
        rc, _ = run_capture(merge_flow, 701, gh=fgh2, git=fgit2)
        bodies2 = bodies_of(fgh2)
        lists = [c for c in fgh2.calls if c[:2] == ["issue", "list"]]
        report(rc == 0 and len(lists) == 1 and bodies2
               and "Outstanding `resignoff-required` issues at filing time: 2 "
                   "(oldest #965)." in bodies2[0]
               and fgh2.calls.index(lists[0]) < fgh2.calls.index(filed_calls(fgh2)[0]),
               "backlog-line", f"rc={rc}, lists={len(lists)}")

        # --- the real #958 numbers against #970's actual identity bullets,
        # field for field: same fixture gh returned for the PR, the real
        # base/head/squash the hand-filed issue recorded, and the record as
        # the four-leg run it was.
        with open(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                               "fixtures", "pr958.json"), encoding="utf-8") as f:
            pr958 = json.load(f)
        # #970 keys the record to the head the run covered, not to the
        # merge-time headRefOid the fixture carries, so the bullet test
        # pins the signed-off sha explicitly, the way the hand filing did.
        pr970 = dict(pr958, headRefOid="dff953aac5c50c3f9286ac2035d0ec88ad7817d6")
        report(issue_title(pr970, "900e44bb44788e1b6bb7524cd5ad4cdc70f75e33")
               == "resignoff-required: PR #958 (osc: carry the shell's prompt state as one "
                  "versioned hex payload) squashed as 900e44bb4 on a moved windows window",
               "n970-title", issue_title(pr970, "900e44bb44788e1b6bb7524cd5ad4cdc70f75e33"))
        rec958 = {"pass": True,
                  "steps": {leg: {"rc": 0} for leg in
                            ("zig-fmt", "zig-tests", "windows-tests", "gates-selftest")},
                  "scope": {"paths": ["windows/scripts/ShellIntegrationPs1.Tests.ps1",
                                      "src/terminal/osc/parsers.zig"],
                            "legs_run": ["zig-fmt", "zig-tests", "windows-tests",
                                         "gates-selftest"],
                            "justfile_legs": [], "reason": "scoped", "full": False}}
        body958 = issue_body(
            pr970, rec958, display_path(pr970["headRefOid"]),
            "19110d76d2ee25345e6e9cfb2dca3dd93a9e9751", False,
            "791cf03445f21e02027bd882d0c7da23821215b3",
            "900e44bb44788e1b6bb7524cd5ad4cdc70f75e33",
            {"commits": [{"sha": "c" * 40,
                          "subject": "harnesses: SendInput retirement wave 0 - remain-title, "
                                     "settings, mica-dpi onto the seam (#966)",
                          "files": ["windows/scripts/fuzz-suite.ps1",
                                    "windows/scripts/mouse-fuzz-mica-dpi.ps1",
                                    "windows/scripts/mouse-fuzz-remain-title.ps1",
                                    "windows/scripts/mouse-fuzz-settings.ps1"]}],
             "files": ["windows/scripts/fuzz-suite.ps1",
                       "windows/scripts/mouse-fuzz-mica-dpi.ps1",
                       "windows/scripts/mouse-fuzz-remain-title.ps1",
                       "windows/scripts/mouse-fuzz-settings.ps1"]},
            risk_lines({"files": [], "dirs": ["windows"], "legs": ["windows-tests"],
                        "unknown": []},
                       [{"sha": "c" * 40, "subject": "harnesses"}],
                       ["windows/scripts/fuzz-suite.ps1"]),
        )
        for bullet in (
            "- PR: #958, squashed as `900e44bb4` on `windows`",
            "- Signed off: `dff953aac5c50c3f9286ac2035d0ec88ad7817d6` (record at "
            "`.git/pr-signoff/dff953aac5c50c3f9286ac2035d0ec88ad7817d6.json`, "
            "all four legs rc=0)",
            "- `windows` base at signoff time: `19110d76d`",
            "- `windows` head at merge time: `791cf0344`",
        ):
            report(bullet in body958, "n970-bullet", bullet[:72])

        # --- legacy record without a base: estimated, still files, and says so.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, legacy)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, _ = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        report(rc == 0 and bodies and "estimated at merge time" in bodies[0],
               "legacy-base-estimated", f"rc={rc}, filed={bool(bodies)}")

        # An unscoped record (predates scope.path recording) falls back to
        # the PR's own file list for the intersections.
        ov = scope_overlap({"scope": None}, ["src/lib.zig"],
                           pr_files=["src/lib.zig", "extra.txt"])
        report(ov["files"] == ["src/lib.zig"] and ov["dirs"] == ["src"],
               "scope-fallback", f"files={ov['files']}, dirs={ov['dirs']}")

        # --- fetch failed: loud warning, and the stale caveat rides in the
        # filed body.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files,
                       fetch_ok=False)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        report(rc == 0 and "possibly stale" in out and bodies
               and "## Notes" in bodies[0] and "possibly stale" in bodies[0],
               "stale-fetch-warns", f"rc={rc}, noted={bool(bodies and '## Notes' in bodies[0])}")

        # --- windows moved again in flight: the squash's parent is not the
        # measured head; say the remediation out loud and in the body.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files,
                       squash_parent="f" * 40)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        report(rc == 0 and "moved in flight" in out and bodies
               and "moved in flight" in bodies[0],
               "in-flight-warns", f"rc={rc}, noted={bool(bodies and 'moved in flight' in bodies[0])}")

        # --- rewritten base: not an ancestor, capped body, loud line.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files,
                       ancestor_ok=False)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        report(rc == 0 and "not an ancestor" in out and bodies
               and "not an ancestor" in bodies[0],
               "rewritten-base-warns", f"rc={rc}")

        # --- rendering caps: a rewritten history must not produce a wall.
        many = [{"sha": f"{i:02d}" * 20, "subject": f"change {i}",
                 "files": [f"src/gen{j}.zig" for j in range(30)] if i == 3
                 else ["src/one.zig"]}
                for i in range(20)]
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=many,
                       all_files=[f for c in many for f in c["files"]])
        rc, _ = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        bullets = [ln for ln in (bodies[0].splitlines() if bodies else [])
                   if ln.startswith("- `") and " change " in ln]
        report(rc == 0 and len(bullets) == MAX_COMMIT_BULLETS
               and "- and 5 more commit(s) (capped" in (bodies[0] if bodies else "")
               and "+18 more file(s)" in (bodies[0] if bodies else "")
               and "; and 10 more" in (bodies[0] if bodies else ""),
               "rendering-caps", f"rc={rc}, bullets={len(bullets)}")

        # --- dry run: the full picture, zero mutations.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, mode="dry-run", gh=fgh, git=fgit)
        mutated = merged_calls(fgh) or bool(filed_calls(fgh))
        report(rc == 0 and not mutated and "## Risks" in out and "not started" in out
               and "nothing merged, nothing filed" in out,
               "dry-run-mutates-nothing", f"rc={rc}, mutated={mutated}")

        # dry run over an unmoved window says nothing would be filed either.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, dict(green, base=win))
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=win, commits=[], all_files=[])
        rc, out = run_capture(merge_flow, 700, mode="dry-run", gh=fgh, git=fgit)
        report(rc == 0 and not merged_calls(fgh) and not filed_calls(fgh)
               and "file nothing" in out,
               "dry-run-same-window", f"rc={rc}")

        # --- recovery: a MERGED pr under --dry-run uses the recorded
        # mergeCommit as the squash and still mutates nothing.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, state="MERGED", mergeable="MERGEABLE",
                             merge_commit="e" * 40, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, mode="dry-run", gh=fgh, git=fgit)
        report(rc == 0 and not merged_calls(fgh) and not filed_calls(fgh)
               and "already MERGED" in out and "eeeeeeeee" in out,
               "dry-run-merged-recovery", f"rc={rc}")

        # --- recovery: --file-only files the owed issue and never merges.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, state="MERGED", merge_commit="e" * 40,
                             files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, mode="file-only", gh=fgh, git=fgit)
        report(rc == 0 and not merged_calls(fgh) and len(filed_calls(fgh)) == 1,
               "file-only-files", f"rc={rc}, merged={merged_calls(fgh)}, "
                                  f"filed={len(filed_calls(fgh))}")

        # --file-only on a still-open pr is refused with the pointer back
        # to the normal path.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, mode="file-only", gh=fgh, git=fgit)
        report(rc == 2 and not merged_calls(fgh) and not filed_calls(fgh)
               and "pr-still-open" in out,
               "file-only-open-refused", f"rc={rc}")

        # --- merge refused by gh: loud failure, nothing filed.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head, files=files_ok), merge_rc=1)
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 1 and not filed_calls(fgh) and "failed" in out,
               "merge-fails-loud", f"rc={rc}")

        # --- remote slug parsing, shared with the hook's repo predicate:
        # the checkout check goes through pr_gate.normalize_slug, so any
        # spelling the hook accepts, the guard accepts.
        slug_cases = [
            ("git@github.com:deblasis/wintty.git", "deblasis/wintty"),
            ("https://github.com/deblasis/wintty", "deblasis/wintty"),
            ("ssh://git@github.com/deblasis/ghostty.git", "deblasis/ghostty"),
            ("github.com/deblasis/wintty/", "deblasis/wintty"),
            ("https://github.com/other/fork.git", "other/fork"),
            ("", None),
        ]
        for url, expect in slug_cases:
            got = pr_gate.normalize_slug(url)
            report(got == expect, "remote-slug", f"{url!r} -> {got}")
        report(pr_gate.normalize_slug("git@github.com:deblasis/ghostty.git")
               in pr_gate.FORK_SLUGS,
               "slug-matches-hook-predicate", "the guard and the hook agree on the fork")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    return 1 if failed else 0


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if "--dry-run" in argv:
        mode = "dry-run"
    elif "--file-only" in argv:
        mode = "file-only"
    else:
        mode = "merge"
    positional = [a for a in argv if not a.startswith("-")]
    if len(positional) != 1 or not positional[0].isdigit():
        print(__doc__)
        return 2
    return merge_flow(int(positional[0]), mode=mode)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
