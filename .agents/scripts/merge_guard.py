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
drifting per-agent or per-memory.

The delta is measured from the record's `base` (the merge base with
origin/windows stamped when the record was written) to the current
origin/windows head, first-parent only: sync-publish lands real merge
commits, and a plain walk would disappear into upstream's history. A
record written before base recording exists is base-unknown; the base is
then estimated at merge time, which equals the original base whenever the
branch only gained commits.

Refusals (nothing is mutated when one fires): no local record for the PR
head, a red record, a deferred record the ledger no longer permits, a
checkout that is not this fork, a PR that is not open and mergeable or
that does not target `windows`, a window that cannot be measured. The new
policy forgives head movement only; it never forgives a bad or absent run.

Modes:
  <pr>            validate, squash-merge, read back the squash sha, file
                  the resignoff-required issue when the window moved.
  --dry-run <pr>  print the resolved inputs, the delta, the risks and the
                  would-be issue body; mutate nothing.
  --self-test     replay injected gh/git sessions: the guard must refuse on
                  every refusal path, merge without filing on an unmoved
                  window, file the #970-shaped body on a moved one, and
                  mutate nothing under --dry-run.

Exit codes: 0 merged (and filed, when owed); 1 the environment or a gh/git
call failed part-way (said loudly, because the merge may already have
landed); 2 a refusal, nothing mutated.

Run with: just merge-checked <pr>
"""

import contextlib
import io
import json
import os
import re
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402
import pr_gate  # noqa: E402  (the repo predicate must agree with the hook's)

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
REPO = "deblasis/wintty"
BASE_BRANCH = "windows"
BASE_REF = "origin/" + BASE_BRANCH
LABEL = "resignoff-required"
PR_FIELDS = "number,title,headRefOid,state,mergeable,baseRefName,files"


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


def remote_slug(url):
    """owner/name out of any remote spelling (ssh, https, a .git suffix), or
    None. The fork answers to two names - the current one and the ghostty
    name GitHub still redirects - and the checkout check must accept both,
    so this is the parsing half and pr_gate.is_our_repo is the decision."""
    m = re.search(r"[:/]([\w.-]+/[\w.-]+?)(?:\.git)?/?$", url or "")
    return m.group(1).lower() if m else None


def checkout_slug(git):
    """The slug of this checkout's origin remote, or None when git cannot
    answer; both cases refuse, they do not guess."""
    out = git(["remote", "get-url", "origin"])
    if out.returncode != 0:
        return None
    return remote_slug(out.stdout.strip())


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
    branch, and the head sha the record must be keyed to."""
    return gh_json(["pr", "view", str(number), "--repo", REPO, "--json", PR_FIELDS], gh)


def load_record(common, head):
    """(record, expected path). The record is keyed by the PR's exact head
    sha, like the hook's own lookup; None means no run was recorded here,
    which the policy treats as missing evidence, not as a pass."""
    d = os.path.join(common, "pr-signoff")
    path = os.path.join(d, f"{head}.json")
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f), path
    except (OSError, ValueError):
        return None, path


def windows_head(git):
    """The local position of origin/windows. Deliberately the local ref and
    not a gh call: the delta is measured in THIS checkout's history, where
    the record's base lives, and a stale ref simply means fetch first."""
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


def scope_overlap(rec, delta_files):
    """Where the delta and the record's scope meet, in the three terms the
    ceremony cares about: the same files, the same top-level directories,
    the same signoff legs. The legs are recomputed from the delta's files
    with gate_scope, so the question asked is exactly the one the record
    answered, only against the branch as it now stands."""
    scope = (rec or {}).get("scope") or {}
    rec_paths = scope.get("paths") or []
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
    a silent risks section and a computed "none" are different claims."""
    commits_note = "; ".join(f"`{c['sha'][:10]}` {c['subject']}" for c in delta_commits)
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


def issue_title(pr, squash):
    """One scannable line for the label's issue list: what merged, as what
    squash, and why this issue exists."""
    return (f"resignoff-required: PR #{pr.get('number', '?')} ({pr.get('title', 'untitled')}) "
            f"squashed as {squash[:10]} on a moved windows window")


def legs_note(rec):
    """How the record's legs are described in the issue, mirroring the
    hand-filed #970 wording. A red record never reaches this point, so the
    only cases are a counted green run and the two flavours of record that
    carry no per-step detail."""
    steps = rec.get("steps") or {}
    if steps:
        return f"all {len(steps)} legs rc=0"
    if rec.get("deferred"):
        return "deferred record, no per-leg detail"
    return "no per-leg detail recorded (legacy record)"


def issue_body(pr, rec, rec_path, base, base_estimated, win, squash, delta, risks):
    """The issue body, in exactly the shape of the hand-filed #970: an
    intro line, the four identity bullets, the delta with per-commit
    attribution, numbered risks, and the resignoff-in-flight status."""
    base_line = f"- `windows` base at signoff time: `{base[:10]}`"
    if base_estimated:
        base_line += " (estimated at merge time: the record predates base recording)"
    lines = [
        "Filed by the merge guard (issue #969): this PR merged on a green signoff whose "
        "`windows` window had already moved. Nothing was re-gated; the risk is carried here "
        "instead.",
        "",
        f"- PR: #{pr.get('number', '?')}, squashed as `{squash[:10]}` on `windows`",
        f"- Signed off: `{pr.get('headRefOid', '?')}` (record at `{rec_path}`, {legs_note(rec)})",
        base_line,
        f"- `windows` head at merge time: `{win[:10]}`",
        "",
        "## Delta (what merged between the record and the squash)",
        "",
        f"{len(delta['commits'])} commit(s) touching {len(delta['files'])} file(s):",
        "",
    ]
    for c in delta["commits"]:
        files = ", ".join(f"`{p}`" for p in c["files"])
        lines.append(f"- `{c['sha'][:10]}` {c['subject']} [{len(c['files'])} file(s): {files}]")
    lines += ["", "## Risks", ""]
    lines += [f"{i}. {r}" for i, r in enumerate(risks, 1)]
    lines += [
        "",
        "## Status",
        "",
        f"Resignoff for `{squash[:10]}`: not started. Phase 1 has no bot; when the #969 bot "
        "lands it owns the lane run and closes this issue with the evidence.",
    ]
    return "\n".join(lines) + "\n"


def refusals(pr, rec, rec_path, ledger):
    """Every reason the guard refuses before touching anything, as
    (code, message) pairs. All of them are reported together, so one run
    tells the agent everything to fix rather than only the first thing."""
    out = []
    head = pr.get("headRefOid", "?")
    n = pr.get("number", "?")
    if rec is None:
        out.append(("signoff-missing",
                    f"no local signoff record for head {head[:10]} (expected at {rec_path}). "
                    "Run 'just signoff' on the PR branch; the guard merges on recorded "
                    "evidence, never on a claim."))
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
    if pr.get("state") != "OPEN":
        out.append(("pr-not-open",
                    f"PR #{n} is {pr.get('state') or 'in an unknown state'}, not OPEN."))
    if pr.get("mergeable") != "MERGEABLE":
        retry = " (GitHub may still be computing it; retry)" \
            if pr.get("mergeable") == "UNKNOWN" else ""
        out.append(("pr-not-mergeable",
                    f"PR #{n} reports mergeable={pr.get('mergeable') or 'unknown'}{retry}."))
    if pr.get("baseRefName") != BASE_BRANCH:
        out.append(("base-branch",
                    f"PR #{n} targets `{pr.get('baseRefName') or 'nothing resolvable'}`, not "
                    f"`{BASE_BRANCH}`; the guard only knows the {BASE_BRANCH} ceremony."))
    return out


def display_path(path):
    """Forward-slashed and repo-relative when the record lives under the
    checkout, which is how AGENTS.md and the hand-filed #970 name records."""
    p = os.path.abspath(path).replace("\\", "/")
    root = REPO_ROOT.replace("\\", "/").rstrip("/") + "/"
    if p.lower().startswith(root.lower()):
        return p[len(root):]
    return p


def merge_flow(number, dry_run=False, gh=None, git=None, sleep=time.sleep):
    """The whole sequence: validate, measure, merge, read back, file.
    Returns the process exit code (see the module docstring). gh, git and
    sleep are injectable so the self-test can replay whole sessions without
    touching GitHub or the clock."""
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
              f"the guard only merges {REPO}.")
        return 2
    win = windows_head(git)
    if not win:
        print(f"merge-guard: could not resolve {BASE_REF} in this checkout; fetch first. "
              "The delta cannot be measured without it.")
        return 2

    try:
        pr = load_pr(number, gh)
    except GuardError as e:
        print(f"merge-guard: could not load PR #{number} from {REPO}: {e}")
        return 1

    head = pr.get("headRefOid") or ""
    rec, rec_path = load_record(common, head)
    ledger = gate_scope.load_ledger(os.path.join(common, "pr-signoff"))
    problems = refusals(pr, rec, rec_path, ledger)
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
    try:
        delta = delta_between(base, win, git)
    except GuardError as e:
        print(f"merge-guard: could not measure the delta: {e}")
        return 1
    moved = bool(delta["commits"])
    est = " (estimated)" if estimated else ""

    if dry_run:
        print(f"merge-guard: PR #{pr.get('number')} {pr.get('title', '')}")
        print(f"merge-guard: head            {head}")
        print(f"merge-guard: record          {display_path(rec_path)} "
              f"(pass={rec.get('pass')}, deferred={bool(rec.get('deferred'))})")
        print(f"merge-guard: record base     {base[:10]}{est}")
        print(f"merge-guard: {BASE_REF} at merge time {win[:10]}")
        print(f"merge-guard: delta           {len(delta['commits'])} commit(s), "
              f"{len(delta['files'])} file(s)")
        for c in delta["commits"]:
            print(f"merge-guard:   {c['sha'][:10]} {c['subject']} [{len(c['files'])} file(s)]")
        if not moved:
            print("merge-guard: the window has not moved; a merge through the guard would "
                  "file nothing.")
            print("merge-guard: dry run: nothing merged, nothing filed.")
            return 0
        risks = risk_lines(scope_overlap(rec, delta["files"]), delta["commits"], delta["files"])
        print("merge-guard: risks:")
        for i, r in enumerate(risks, 1):
            print(f"merge-guard:   {i}. {r}")
        print("merge-guard: would merge with: gh pr merge "
              f"{number} --repo {REPO} --squash --delete-branch")
        print(f"merge-guard: would file issue titled: {issue_title(pr, '<squash-sha>')}")
        print("merge-guard: with this body:")
        print(issue_body(pr, rec, display_path(rec_path), base, estimated, win,
                         "<squash-sha>", delta, risks))
        print("merge-guard: dry run: nothing merged, nothing filed.")
        return 0

    out = gh(["pr", "merge", str(number), "--repo", REPO, "--squash", "--delete-branch"])
    if out.returncode != 0:
        print(f"merge-guard: the merge command failed (rc={out.returncode}): "
              f"{(out.stderr or out.stdout).strip()}")
        return 1

    # The squash sha only exists once GitHub finishes the merge, so it is
    # polled rather than assumed; without it the issue cannot be filed in
    # the #970 shape, which names the squash.
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
    if squash:
        print(f"merge-guard: squashed as {squash[:10]}")
    else:
        print(f"merge-guard: the squash sha never appeared; the {LABEL} issue was NOT filed. "
              "File it by hand with this body:")
        print(issue_body(pr, rec, display_path(rec_path), base, estimated, win,
                         "<squash-sha-unreadable>", delta,
                         risk_lines(scope_overlap(rec, delta["files"]),
                                    delta["commits"], delta["files"])))
        return 1

    if not moved:
        print("merge-guard: the window had not moved since the record was taken; "
              "nothing to file.")
        return 0

    body = issue_body(pr, rec, display_path(rec_path), base, estimated, win, squash, delta,
                      risk_lines(scope_overlap(rec, delta["files"]),
                                 delta["commits"], delta["files"]))
    out = gh(["issue", "create", "--repo", REPO, "--label", LABEL,
              "--title", issue_title(pr, squash), "--body", body])
    if out.returncode != 0:
        print(f"merge-guard: the merge landed but filing the {LABEL} issue failed "
              f"(rc={out.returncode}): {(out.stderr or out.stdout).strip()}")
        print("merge-guard: file it by hand with this body:")
        print(body)
        return 1
    url = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else "(no url printed)"
    print(f"merge-guard: filed {LABEL}: {url}")
    return 0


class FakeGit:
    """A router that answers only the spellings the guard issues, so a
    scenario names what git would say and nothing more. An unhandled call
    fails loudly instead of guessing, which is what keeps a self-test from
    passing against a command the real flow never runs."""

    def __init__(self, common, win, remote=None, merge_base=None, commits=None,
                 all_files=None):
        self.calls = []
        self.common = common
        self.win = win
        self.remote = remote
        self.merge_base = merge_base
        self.commits = commits or []      # landing order, oldest first
        self.all_files = all_files or []

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        a = args
        if a[0] == "rev-parse" and "--git-common-dir" in a:
            return Out(0, self.common + "\n")
        if a[:3] == ["rev-parse", "--verify", BASE_REF]:
            return Out(0, self.win + "\n")
        if a[0] == "remote":
            return Out(0, (self.remote or "") + "\n")
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
    """Records every call and answers the three spellings the flow uses:
    pr view (fields, and mergeCommit after the merge), pr merge, and issue
    create."""

    def __init__(self, pr, squash="e" * 40, merge_rc=0, issue_rc=0):
        self.calls = []
        self.pr = pr
        self.squash = squash
        self.merge_rc = merge_rc
        self.issue_rc = issue_rc

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        j = " ".join(args)
        if j.startswith("pr view") and "mergeCommit" in j:
            return Out(0, json.dumps({"mergeCommit": {"oid": self.squash}}))
        if j.startswith("pr view"):
            return Out(0, json.dumps(self.pr))
        if j.startswith("pr merge"):
            return Out(0, "squashed\n") if self.merge_rc == 0 \
                else Out(1, "", "merge refused by gh\n")
        if j.startswith("issue create"):
            return Out(0, "https://github.com/deblasis/wintty/issues/999\n") \
                if self.issue_rc == 0 else Out(1, "", "issue create failed\n")
        return Out(1, "", f"unexpected gh call: {j}")


def make_pr(number, head, state="OPEN", mergeable="MERGEABLE", base_ref="windows"):
    """A PR shaped the way gh returns it, with only the fields the guard
    reads; the fixtures carry the real-world equivalents."""
    return {"number": number, "title": f"synthetic pr {number}", "headRefOid": head,
            "state": state, "mergeable": mergeable, "baseRefName": base_ref, "files": []}


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
    bare pr view is not a mutation, so it does not count."""
    return any(c[0] == "pr" and c[1] == "merge" for c in fgh.calls)


def filed_calls(fgh):
    """The issue-create calls, for asserting what was (or was not) filed."""
    return [c for c in fgh.calls if c[0] == "issue" and c[1] == "create"]


def bodies_of(fgh):
    """The --body payload of every issue-create call."""
    return [c[c.index("--body") + 1] for c in filed_calls(fgh) if "--body" in c]


def self_test():
    """Replay injected sessions against the refusal matrix, the same-window
    merge, the moved-window filing against a hand-written golden body in
    the #970 shape, the estimated-base legacy record, the dry-run's
    nothing-mutated promise, and the remote-slug parsing."""
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

    tmp = tempfile.mkdtemp(prefix="merge-guard-selftest-")
    try:
        # --- refusal matrix: each path refuses and issues no merge call.
        cases = [
            ("signoff-missing", make_pr(700, head), None, False),
            ("signoff-red", make_pr(700, head), red, False),
            ("signoff-defer-blocked", make_pr(700, head), deferred, True),
            ("pr-not-open", make_pr(700, head, state="CLOSED"), green, False),
            ("pr-not-mergeable", make_pr(700, head, mergeable="CONFLICTING"), green, False),
            ("base-branch", make_pr(700, head, base_ref="main"), green, False),
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

        # wrong-repo: a checkout of some other project never reaches gh.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/other/fork.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 2 and not fgh.calls and "wrong-repo" in out,
               "refuse-wrong-repo", f"rc={rc}, gh calls={len(fgh.calls)}")

        # window-unknown: a legacy record with no base and none derivable.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, legacy)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=None, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 2 and not merged_calls(fgh) and not filed_calls(fgh)
               and "window-unknown" in out,
               "refuse-window-unknown", f"rc={rc}")

        # --- same window: merges, files nothing.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, dict(green, base=win))
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=win, commits=[], all_files=[])
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 0 and merged_calls(fgh) and not filed_calls(fgh)
               and "nothing to file" in out,
               "same-window", f"rc={rc}, merged={merged_calls(fgh)}, "
                              f"filed={len(filed_calls(fgh))}")

        # --- moved window: files the issue with the golden body.
        td = tempfile.mkdtemp(dir=tmp)
        rec_path = write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        golden = (
            "Filed by the merge guard (issue #969): this PR merged on a green signoff whose "
            "`windows` window had already moved. Nothing was re-gated; the risk is carried "
            "here instead.\n"
            "\n"
            "- PR: #700, squashed as `eeeeeeeeee` on `windows`\n"
            f"- Signed off: `{head}` (record at `{rec_path.replace(chr(92), '/')}`, "
            "all 2 legs rc=0)\n"
            f"- `windows` base at signoff time: `{base[:10]}`\n"
            f"- `windows` head at merge time: `{win[:10]}`\n"
            "\n"
            "## Delta (what merged between the record and the squash)\n"
            "\n"
            "2 commit(s) touching 2 file(s):\n"
            "\n"
            f"- `{c1[:10]}` one: first landed change (#100) "
            "[1 file(s): `windows/scripts/a.ps1`]\n"
            f"- `{c2[:10]}` two: second landed change (#101) [1 file(s): `src/lib.zig`]\n"
            "\n"
            "## Risks\n"
            "\n"
            "1. The delta itself: 2 commit(s) (`1111111111` one: first landed change (#100); "
            "`2222222222` two: second landed change (#101)) landed on `windows` after the "
            "record's base, touching 2 file(s), and none of it was exercised by the "
            "signed-off run.\n"
            "2. Same files as the record's scope: `windows/scripts/a.ps1`. The green run "
            "covered these paths in their older state.\n"
            "3. Same top-level directories as the record's scope: `windows/`. Both sides of "
            "the merge land in one place and were never run together.\n"
            "4. Same signoff legs as the record: `windows-tests`, `zig-fmt`. The record ran "
            "them green against the old branch state; the result may no longer hold.\n"
            "\n"
            "## Status\n"
            "\n"
            "Resignoff for `eeeeeeeeee`: not started. Phase 1 has no bot; when the #969 bot "
            "lands it owns the lane run and closes this issue with the evidence.\n"
        )
        report(rc == 0 and len(bodies) == 1 and bodies[0] == golden,
               "moved-window-golden-body",
               "body matches the #970 shape" if rc == 0 else f"rc={rc}, out={out}")
        if bodies and bodies[0] != golden:
            report(False, "golden-diff",
                   "\n--- got ---\n" + bodies[0] + "\n--- want ---\n" + golden)
        filed = filed_calls(fgh)
        report(bool(filed) and "--label" in filed[0] and LABEL in filed[0],
               "issue-labelled", f"filed={len(filed)}")

        # --- legacy record without a base: estimated, still files, and says so.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, legacy)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, _ = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        bodies = bodies_of(fgh)
        report(rc == 0 and bodies and "estimated at merge time" in bodies[0],
               "legacy-base-estimated", f"rc={rc}, filed={bool(bodies)}")

        # --- dry run: the full picture, zero mutations.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, dry_run=True, gh=fgh, git=fgit)
        mutated = merged_calls(fgh) or bool(filed_calls(fgh))
        report(rc == 0 and not mutated and "## Risks" in out and "not started" in out
               and "nothing merged, nothing filed" in out,
               "dry-run-mutates-nothing", f"rc={rc}, mutated={mutated}")

        # dry run over an unmoved window says nothing would be filed either.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, dict(green, base=win))
        fgh = FakeGh(make_pr(700, head))
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=win, commits=[], all_files=[])
        rc, out = run_capture(merge_flow, 700, dry_run=True, gh=fgh, git=fgit)
        report(rc == 0 and not merged_calls(fgh) and not filed_calls(fgh)
               and "file nothing" in out,
               "dry-run-same-window", f"rc={rc}")

        # --- merge refused by gh: loud failure, nothing filed.
        td = tempfile.mkdtemp(dir=tmp)
        write_record(td, head, green)
        fgh = FakeGh(make_pr(700, head), merge_rc=1)
        fgit = FakeGit(td, win, remote="https://github.com/deblasis/wintty.git",
                       merge_base=base, commits=commits, all_files=all_files)
        rc, out = run_capture(merge_flow, 700, gh=fgh, git=fgit)
        report(rc == 1 and not filed_calls(fgh) and "failed" in out,
               "merge-fails-loud", f"rc={rc}")

        # --- remote slug parsing, shared with the hook's repo predicate.
        slug_cases = [
            ("git@github.com:deblasis/wintty.git", "deblasis/wintty"),
            ("https://github.com/deblasis/wintty", "deblasis/wintty"),
            ("ssh://git@github.com/deblasis/ghostty.git", "deblasis/ghostty"),
            ("https://github.com/other/fork.git", "other/fork"),
            ("", None),
        ]
        for url, expect in slug_cases:
            got = remote_slug(url)
            report(got == expect, "remote-slug", f"{url!r} -> {got}")
        report(pr_gate.is_our_repo(remote_slug("git@github.com:deblasis/ghostty.git")),
               "slug-matches-hook-predicate", "the guard and the hook agree on the fork")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    return 1 if failed else 0


def main(argv):
    if "--self-test" in argv:
        return self_test()
    dry = "--dry-run" in argv
    positional = [a for a in argv if not a.startswith("-")]
    if len(positional) != 1 or not positional[0].isdigit():
        print(__doc__)
        return 2
    return merge_flow(int(positional[0]), dry_run=dry)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
