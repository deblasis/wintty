#!/usr/bin/env python3
"""The resignoff bot (#969 phase 2): work the debt the merge guard files.

Phase 1 (#978) made every moved-window merge carry its risk in a
`resignoff-required` issue instead of re-gating inline. That pile is
designed to sit until it is worked: a full ladder is over an hour, so this
is an operator loop, bounded to a few signoff runs per invocation, and
never a step an agent runs to finish a merge (AGENTS.md says so).

The loop, per invocation:

  1. Enumerate the open `resignoff-required` issues and parse each body
     strictly against the guard's template fields (PR number, squash sha,
     signed-off head, base, merged head, outstanding count). A body that
     does not parse is skipped with a loud note; the loop never crashes on
     one malformed filing.
  2. The issues are one ordered group by creation, oldest window to newest,
     anchored below by the last head with a green record. Work runs from
     the NEWEST window downward.
  3. For each window: a green record at the recorded squash closes the
     issue without a run (idempotent catch-up, and how a re-invocation
     after a green run picks the close up); anything else owes a run.
  4. A run claims its issue first ("resignoff started: <sha7> at <utc>,
     run <k>"), prepares a detached worktree at the recorded squash sha
     (nightly_fuzz.ps1's recipe: worktree add --detach, reset --hard,
     clean -fdx with the .zig-cache and zig-out exclusions, because
     signoff refuses a dirty tree), then takes the incoda lane exactly
     once: incoda run --queue wintty --reason "resignoff <sha7>" -- just
     signoff. The worktree hangs off this clone, so the record the run
     writes lands in the same git common dir the gate and this bot read.
     Nothing but the run holds the lane.
  5. Green: close the issue, quoting the record path and the per-leg rcs,
     then move to the next older window; when every window is green, all
     close. Red: bisect over the RECORDED squash SHAs between the last
     green anchor and the failing head - run the middle window, repeat,
     until a single window isolates the failure - then label that issue
     `signoff-bisect-culprit`, comment the failing legs and the bisect
     trail, and leave it open. Pickup is manual from there (issue #969).

Resumability: a ladder is over an hour and --max N (default 1) bounds the
runs an invocation may spend, so the bot is built to be interrupted; --max 0
is the greens-only pass, closing what the records already retire and
spending nothing. The records are the state: green and red records at
recorded squash shas reconstruct the bisect bounds exactly, so any
re-invocation continues where the last one stopped and no window is run
twice. The claim markers are an audit trail only: nothing reads them back
and they never block; the record at each sha decides every question.

One instance at a time: the bot owns one shared worktree, and two instances
resetting and cleaning -fdx under each other would read back as a green
record for a tree the other was mid-rewrite on. A lock file beside the
worktree carries pid + timestamp; a live pid refuses the invocation (exit 2,
naming the holder), a dead pid's lock is taken over with a printed note.
nightly_fuzz.ps1 guards its worktree the same way.

A lane busy with a resignoff for the same sha is left alone rather than
double-queued; the guard's status line in every filed issue asks for that
same check. The lane is never held across non-running work.

Refusals: no open issues exits 0 with nothing to do; incoda missing from
PATH and from the %LOCALAPPDATA% install exits 2 naming that install, and is
demanded only when a run is actually spendable (some window owes a run, the
budget allows spending one, and a bisect has an untested middle left); a
second live bot instance holding the worktree lock exits 2 naming the
holder; a worktree that cannot be prepared skips its issue with a note,
except for the newest window, which stops the group loudly: working older
windows past an unproven newest would spend hours against a pile whose top
state is unknown; a wrong-repo checkout or an unresolvable git common dir
exits 2, like the merge guard's own. --dry-run prints the pile, the parsed
windows, the chosen next action and the exact commands, and mutates nothing:
no worktree, no comment, no close, no label, no lane.

Deliberately not here: the reconciliation pass that would diff recently
merged PRs against filed issues (#979, phase 3; the guard's --file-only is
its filing primitive) and branch protection on `windows`. The bot trusts
the identity bullets as filed - the guard wrote them - and verifies the one
it acts on: the squash sha must resolve in this checkout, which is also
what lets a hand-filed body naming only the short form work. Re-measuring
the window belongs to the guard, not to this loop.

Exit codes: 0 nothing to do, or the invocation spent its budget cleanly and
can be re-invoked; 1 a gh/git/incoda call failed part-way (said loudly);
2 a refusal, nothing mutated.

Run with: just resignoff-bot [--max N] [--dry-run]
"""

import contextlib
import datetime
import io
import json
import os
import re
import shutil
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402
import merge_guard  # noqa: E402  (runners, record IO, repo identity: one source)
import pr_gate  # noqa: E402

REPO_ROOT = merge_guard.REPO_ROOT
REPO = merge_guard.REPO
LABEL = merge_guard.LABEL
CULPRIT_LABEL = "signoff-bisect-culprit"
SHA7 = 7
# One worktree, reused across runs and invocations, parked beside the
# nightly's: the records live in the shared common dir, so where the
# worktree hangs only decides whose disk fills up.
WORKTREE_REL = os.path.join(".agents", "worktrees", "resignoff")

MARKER_PREFIX = "resignoff started:"
# Tolerates a leading quote or indent because gh renders comment bodies
# inside markdown; the sha, timestamp and run number stay strict.
MARKER_RE = re.compile(
    r"^[>\s]*" + MARKER_PREFIX + r" ([0-9a-f]{7,40}) at (\S+), run (\d+)\s*$", re.M)

# The template fields, one regex per bullet, derived from merge_guard's
# issue_body (the #970 shape). Strict on purpose: a body these cannot find
# was not filed by the guard, and the bot does not run an hour of ladder
# against a guess of what a window means.
RE_PR_BULLET = re.compile(r"^- PR: #(\d+), squashed as `([0-9a-f]+)` on `windows`$", re.M)
RE_SIGNED_BULLET = re.compile(r"^- Signed off: `([0-9a-f]{40})`", re.M)
RE_BASE_BULLET = re.compile(r"^- `windows` base at signoff time: `([0-9a-f]+)`", re.M)
RE_WINHEAD_BULLET = re.compile(r"^- `windows` head at merge time: `([0-9a-f]+)`", re.M)
RE_SQUASH_LINE = re.compile(r"^Squash commit: `([0-9a-f]{40})`", re.M)
RE_BACKLOG_LINE = re.compile(
    r"^Outstanding `resignoff-required` issues at filing time: (\d+) \(oldest #(\d+)\)\.", re.M)


# --- runners ---------------------------------------------------------------

def git_run(args, cwd=None):
    """The real git runner. Takes git's own argument list (no leading
    "git"), the same shape merge_guard's runner and fakes speak."""
    return subprocess.run(["git"] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True, timeout=60)


def gh_run(args, cwd=None):
    """The real gh runner, same convention."""
    return subprocess.run(["gh"] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True, timeout=120)


def incoda_run(path, args, cwd=None):
    """The real lane runner. No timeout on purpose: a signoff run is over an
    hour, and the lane's own --wait is the only queue bound that should
    apply."""
    return subprocess.run([path] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True)


def incoda_status_run(path, args, cwd=None):
    """The real lane status reader (`incoda status --queue wintty`). Short
    timeout: a status read that hangs is worse than no status at all, and
    lane_busy_with treats a failed read as silent."""
    return subprocess.run([path] + args, cwd=cwd or REPO_ROOT,
                          capture_output=True, text=True, timeout=30)


def pid_alive(pid):
    """Whether a lock's pid names a live process. Windows os.kill(pid, 0)
    is not a probe: any signal value other than the CTRL events is
    TerminateProcess, so liveness goes through OpenProcess +
    GetExitCodeProcess (STILL_ACTIVE is 259). Elsewhere the classic
    signal-0 probe is safe."""
    if pid <= 0:
        return False
    if os.name == "nt":
        import ctypes
        PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
        STILL_ACTIVE = 259
        k32 = ctypes.windll.kernel32
        handle = k32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
        if not handle:
            return False
        try:
            code = ctypes.c_ulong()
            if k32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return code.value == STILL_ACTIVE
            return True
        finally:
            k32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except OSError:
        return False
    return True


LOCK_SUFFIX = ".lock"


def lock_path_for(worktree_dir):
    """The instance lock's path: beside the worktree it guards, so both move
    together when worktree_dir is relocated (the self-test relocates both
    into its temporary directory)."""
    return os.path.join(os.path.dirname(worktree_dir),
                        os.path.basename(worktree_dir) + LOCK_SUFFIX)


def acquire_instance_lock(env):
    """(ok, holder) for the one-bot-at-a-time lock beside the shared
    worktree. A lock naming a live pid refuses (the caller exits 2 naming
    the holder); a dead pid's lock, or an unreadable one, is taken over
    with a printed note. Two bots reset/clean -fdx under each other would
    read back as a green record for a tree the other was mid-rewrite on,
    which is the false-green this exists to make impossible; the nightly
    guards its worktree the same way."""
    os.makedirs(os.path.dirname(env.lock_path), exist_ok=True)
    holder = ""
    try:
        with open(env.lock_path, encoding="utf-8") as f:
            holder = f.read().strip()
    except OSError:
        holder = ""
    m = re.match(r"^(\d+) at ", holder)
    if holder and m and env.pid_alive(int(m.group(1))):
        return False, holder
    if holder:
        print(f"resignoff-bot: taking over a stale worktree lock (pid "
              f"{m.group(1) if m else 'unreadable'} is not alive): {holder}")
    try:
        with open(env.lock_path, "w", encoding="utf-8") as f:
            f.write(f"{os.getpid()} at {env.now()}\n")
    except OSError as e:
        return False, f"could not write the lock file: {e}"
    return True, ""


def release_instance_lock(env):
    """Drop the lock only if it is still ours: a takeover in between wrote
    someone else's pid, and deleting that would un-guard a live instance."""
    try:
        with open(env.lock_path, encoding="utf-8") as f:
            holder = f.read().strip()
    except OSError:
        return
    m = re.match(r"^(\d+) at ", holder)
    if m and int(m.group(1)) == os.getpid():
        with contextlib.suppress(OSError):
            os.remove(env.lock_path)


def locate_incoda(which=None, localappdata=None):
    """The incoda executable, or None. PATH first, then the installer's
    location: the same two places the theme-matrix recipe looks, because the
    agent shell that recipe was written under had the latter and not the
    former."""
    which = which or shutil.which
    localappdata = localappdata or os.environ.get("LOCALAPPDATA")
    found = which("incoda")
    if found:
        return found
    if localappdata:
        cand = os.path.join(localappdata, "Programs", "incoda", "incoda.exe")
        if os.path.isfile(cand):
            return cand
    return None


def default_now():
    """The timestamp a claim marker carries, second precision."""
    return datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds")


def install_hint():
    """Where incoda would be installed, for the refusal message: the real
    %LOCALAPPDATA% when there is one, the literal spelling when not."""
    return os.path.join(os.environ.get("LOCALAPPDATA", "%LOCALAPPDATA%"),
                        "Programs", "incoda", "incoda.exe")


class Env:
    """The injected environment one bot invocation runs against: the gh,
    git and incoda runners, the lane status reader (real by default, so the
    lane-busy guard works in production and not only under test), the
    clock, the pid-liveness probe, the worktree path and the instance lock
    derived from it, and the git common dir that holds the records. Real
    runners by default; the self-test substitutes every one, the way
    merge_flow does."""

    def __init__(self, gh=None, git=None, incoda=None, incoda_status=None,
                 find_incoda=None, now=None, worktree_dir=None, common=None,
                 lock_path=None, pid_probe=None):
        self.gh = gh or gh_run
        self.git = git or git_run
        self.incoda = incoda
        self.incoda_status = incoda_status or incoda_status_run
        self.find_incoda = find_incoda or locate_incoda
        self.now = now or default_now
        self.worktree_dir = worktree_dir or os.path.join(REPO_ROOT, WORKTREE_REL)
        self.lock_path = lock_path or lock_path_for(self.worktree_dir)
        self.pid_alive = pid_probe or pid_alive
        self.common = common


# --- enumeration and parsing ----------------------------------------------

def enumerate_issues(gh):
    """(open issues oldest first, None) or (None, why). One gh call; the
    pile is read fresh every invocation because the close of one window is
    the next invocation's starting fact."""
    out = gh(["issue", "list", "--repo", REPO, "--label", LABEL,
              "--state", "open", "--json", "number,title,body,createdAt"])
    if out.returncode != 0:
        return None, (out.stderr or out.stdout).strip()
    try:
        issues = json.loads(out.stdout)
    except ValueError as e:
        return None, f"gh printed unparseable JSON: {e}"
    issues.sort(key=lambda i: (i.get("createdAt") or "", i.get("number") or 0))
    return issues, None


def parse_window(body, git):
    """(window, None) or (None, why) for one issue body.

    Every template bullet is required: PR number, signed-off head (full
    sha), base, merged head. The squash sha comes from the guard's full-sha
    line when present; a hand-filed body that names only the short form in
    the PR bullet is accepted only when git resolves that short form to
    exactly one commit in this checkout. Either way the sha must resolve
    locally before the window counts: the bot works at the squash, and a
    sha this checkout cannot place is not a window it can run. The remedy
    in that case is the guard's own: refile with --file-only once
    mergeCommit appears (and fetch, in case the object is merely unfetched).
    """
    if not body or not body.strip():
        return None, "empty body"
    body = body.replace("\r\n", "\n")
    m = RE_PR_BULLET.search(body)
    if not m:
        return None, "no '- PR: #N, squashed as `...` on `windows`' bullet"
    number, short = int(m.group(1)), m.group(2)
    signed = RE_SIGNED_BULLET.search(body)
    if not signed:
        return None, "no '- Signed off: `<full sha>`' bullet"
    base = RE_BASE_BULLET.search(body)
    if not base:
        return None, "no '- `windows` base at signoff time' bullet"
    winhead = RE_WINHEAD_BULLET.search(body)
    if not winhead:
        return None, "no '- `windows` head at merge time' bullet"

    remedy = ("fetch first, or refile with `--file-only` once mergeCommit appears")
    squash = None
    full = RE_SQUASH_LINE.search(body)
    if full:
        squash = full.group(1)
    elif len(short) == 40:
        squash = short
    else:
        # The one field this bot acts on has to be more than a claim: git
        # resolves the short form or the issue is skipped. --verify refuses
        # an ambiguous short sha, which is exactly the strictness wanted.
        out = git(["rev-parse", "--verify", "--quiet", f"{short}^{{commit}}"])
        got = out.stdout.strip()
        if out.returncode != 0 or not re.fullmatch(r"[0-9a-f]{40}", got):
            return None, (f"squash sha `{short}` does not resolve to one commit in this "
                          f"checkout ({remedy})")
        squash = got
    # The full-sha form gets the same local check: a body naming a sha this
    # checkout has no object for cannot have a worktree prepared against it.
    out = git(["rev-parse", "--verify", "--quiet", f"{squash}^{{commit}}"])
    if out.returncode != 0:
        return None, (f"squash sha `{merge_guard.short(squash)}` is not in this checkout "
                      f"({remedy})")

    backlog = RE_BACKLOG_LINE.search(body)
    return {
        "number": number,
        "squash": squash,
        "signed": signed.group(1),
        "base": base.group(1),
        "winhead": winhead.group(1),
        "outstanding": (int(backlog.group(1)), int(backlog.group(2))) if backlog else None,
    }, None


def parse_all(issues, git):
    """(windows newest first, skipped) over the whole pile. The group is one
    ordered chain by creation, so the newest window is worked first; a
    duplicate squash (the same window filed twice) keeps the newest filing
    rather than running one ladder per copy, and the losing numbers ride
    along on the winner's `duplicates` so the close can retire them with a
    cross-reference instead of leaving stale twins open."""
    windows, skipped = [], []
    for issue in issues:
        w, why = parse_window(issue.get("body"), git)
        if w is None:
            skipped.append((issue.get("number", "?"), (issue.get("title") or "")[:60], why))
            continue
        w["created"] = issue.get("createdAt") or ""
        w["title"] = issue.get("title") or ""
        w["duplicates"] = []
        windows.append(w)
    windows.sort(key=lambda w: (w["created"], w["number"]), reverse=True)
    seen, deduped = {}, []
    for w in windows:
        winner = seen.get(w["squash"])
        if winner is not None:
            winner["duplicates"].append(w["number"])
            skipped.append((w["number"], w["title"][:60],
                            f"duplicate of window {merge_guard.short(w['squash'])} "
                            f"(#{winner['number']}, newer filing kept)"))
            continue
        seen[w["squash"]] = w
        deduped.append(w)
    return deduped, skipped


# --- records ---------------------------------------------------------------

def load_record(common, sha):
    """The record at a squash sha, or None. merge_guard's loader: a corrupt
    file reads as absent, never as evidence."""
    return merge_guard.load_record(common, sha)[0]


def full_ladder(rec):
    """Whether a record covers every leg the gate requires: the scope says
    full, or the legs it ran are a superset of the gate's set. A run at a
    windows squash is always full (signoff finds no changed paths against
    the base), so a scoped record here means something other than the
    bot's own run wrote it, and it retires nothing."""
    scope = rec.get("scope") or {}
    if scope.get("full"):
        return True
    legs = set(scope.get("legs_run") or []) | set((rec.get("steps") or {}).keys())
    return set(gate_scope.ALL_LEGS) <= legs


def verdict(rec):
    """"green", "red", or None when the record is no evidence: absent,
    corrupt, a deferral, or a green that is not a full ladder. A deferred
    record is credit, not a run; at a squash sha it borrows exactly the
    hour this bot exists to pay. A scoped green retires nothing: the
    windows this bot closes are only ever retired by every leg running
    green, the same bar the gate's full ladder holds."""
    if rec is None or rec.get("deferred"):
        return None
    if rec.get("pass"):
        return "green" if full_ladder(rec) else None
    return "red"


# --- bodies ----------------------------------------------------------------

def marker_body(sha, now, k):
    """The claim marker, in exactly the format re-invocations and humans
    scan for. Commented before every run, so a dead invocation is always
    discoverable from the issue it died working."""
    return f"{MARKER_PREFIX} {sha[:SHA7]} at {now}, run {k}\n"


def close_body(sha, rec, fresh_run):
    """The close comment: the record path, the per-leg rcs, whether the
    record predates this invocation or was written by the run this issue
    claimed, and the settling note. merge_guard's legs_note keeps the
    wording aligned with the hand-filed #970 ("all four legs rc=0"); the
    settle line is only true of a full ladder, so a scoped record says it
    settles nothing."""
    steps = rec.get("steps") or {}
    legs = ", ".join(f"{name} rc={(steps.get(name) or {}).get('rc', '?')}"
                     for name in sorted(steps)) or "no per-leg detail recorded"
    lines = [
        f"resignoff green at {sha[:SHA7]}: this window is retired.",
        "",
        f"- record: `{merge_guard.display_path(sha)}` ({merge_guard.legs_note(rec)})",
        f"- per-leg rcs: {legs}",
        "- record was " + ("written by the run this issue claimed." if fresh_run
                           else "already on disk; no run was spent closing this window."),
    ]
    if set(steps) >= set(gate_scope.ALL_LEGS):
        lines.append("- a green full ladder auto-settles the deferral ledger: signoff.py "
                     "settles any outstanding deferrals when every leg runs green, so this "
                     "run pays that debt too.")
    else:
        lines.append("- this record ran a scoped ladder, not every leg, so it settles no "
                     "deferral debt; only a full green run does.")
    return "\n".join(lines) + "\n"


def trail_body(culprit, failing, rec, entries, lo, excluded=()):
    """The comment an isolated culprit issue carries: the failing legs from
    the record at the culprit window, the trail of verdicts over the
    recorded squash SHAs, the excluded filings (git could not prove them
    ancestors of the failing head, or their object is missing locally), and
    the manual-pickup note the issue's design ends on. A missing lower
    anchor is said out loud: a verdict without a green record below covers
    the recorded windows only."""
    steps = (rec or {}).get("steps") or {}
    bad = [f"{name} rc={(steps.get(name) or {}).get('rc', '?')}"
           for name in sorted(steps) if (steps.get(name) or {}).get("rc") != 0]
    lines = [
        f"signoff-bisect-culprit: the #969 bot isolated this window "
        f"(`{culprit['squash'][:SHA7]}`) as the point in change history where the resignoff "
        "ladder went red.",
        "",
        f"- failing leg(s) from the record at `{merge_guard.display_path(culprit['squash'])}`: "
        + (", ".join(bad) or "no per-leg detail recorded"),
        f"- failing window: issue #{failing['number']} at `{failing['squash'][:SHA7]}`; the "
        "bisect ran over the recorded squash SHAs between the last green anchor and that head.",
        "- lower anchor: " + (f"`{lo[:SHA7]}` (green record)" if lo else
                              "none: no green record exists below the failing window in the "
                              "recorded chain, so this verdict covers the recorded windows "
                              "only."),
    ]
    if excluded:
        lines.append("- excluded from the bisect: " + "; ".join(
            f"#{n} `{sha[:SHA7]}` (not an ancestor, or object missing)"
            for n, sha in excluded))
    lines += [
        "",
        "Bisect trail (oldest first):",
    ] + [f"- `{sha[:SHA7]}` {v} ({how})" for sha, v, how in entries] + [
        "",
        "Pickup from here is manual for now (issue #969): the break belongs at or below this "
        "window, and the windows above it stay open until it is fixed and their ladders "
        "re-run.",
    ]
    return "\n".join(lines) + "\n"


def dup_body(winner):
    """The comment a duplicate filing is closed with: same window, newer
    filing carries the work, so the record path and per-leg rcs are found
    in one place."""
    return (f"Closing as a duplicate of #{winner['number']}: both filings name the same "
            f"window (`{winner['squash'][:SHA7]}`), and this is the older filing. The newer "
            "issue carries the record path and per-leg rcs; nothing is owed here.\n")


# --- lane and worktree -----------------------------------------------------

def lane_busy_with(env, incoda_path, sha):
    """The lane's holder text when a resignoff for THIS sha appears to be in
    flight, else None. A textual match on the --reason this bot uses, on
    purpose: the point is not to double-queue a run that is already running.
    Any other holder is the lane's normal business (the run then queues
    behind it, which is what the lane is for). The match can be stale: a
    finished-but-unreleased holder or an old line in the status pane reads
    the same as a live one, so the skip note surfaces the raw status and
    names the escalation (check `incoda status --queue wintty`, clear the
    stale holder or wait) instead of trusting the match silently. A failed
    status read stays silent: the lane serializes regardless."""
    if env.incoda_status is None:
        return None
    try:
        out = env.incoda_status(incoda_path, ["status", "--queue", "wintty"])
    except (OSError, subprocess.TimeoutExpired):
        return None
    if out.returncode != 0:
        return None
    text = out.stdout or ""
    if f"resignoff {sha[:SHA7]}" in text:
        return text.strip()
    return None


def prepare_worktree(git, wt, sha):
    """(ok, note) for a detached worktree at a recorded squash sha, by
    nightly_fuzz.ps1's recipe. signoff refuses a dirty tree, so the reset
    and clean are not cosmetic; the two clean exclusions are the nightly's
    own, keeping the caches that make the next hour bearable. The worktree
    hangs off this clone, so the record the run writes lands in the shared
    common dir the gate reads. A directory at the worktree path with no
    .git in it is a stray (a crashed add, a manual mkdir), not a checkout:
    it is cleared and re-added rather than failing every run until someone
    notices."""
    git(["worktree", "prune"])
    if os.path.exists(wt) and not os.path.exists(os.path.join(wt, ".git")):
        print(f"resignoff-bot: worktree path holds a stray directory with no .git; "
              f"clearing and re-adding: {wt}")
        shutil.rmtree(wt, ignore_errors=True)
        if os.path.exists(wt):
            return False, (f"worktree path holds a stray directory that could not be "
                           f"cleared: {wt}")
    if not os.path.exists(wt):
        add = git(["worktree", "add", "--detach", wt, sha])
        if add.returncode != 0:
            return False, f"worktree add failed: {(add.stderr or add.stdout).strip()}"
    co = git(["-C", wt, "checkout", "--detach", sha])
    rs = git(["-C", wt, "reset", "--hard", sha])
    cl = git(["-C", wt, "clean", "-fdx", "-e", ".zig-cache", "-e", "zig-out"])
    if co.returncode != 0 or rs.returncode != 0 or cl.returncode != 0:
        return False, (f"worktree prepare failed: checkout rc={co.returncode}, "
                       f"reset rc={rs.returncode}, clean rc={cl.returncode}")
    return True, ""


def count_markers(gh, number):
    """How many claim markers the issue already carries. The count only
    numbers the next claim, so a failed read stays at zero instead of
    failing a run that is about to cost an hour."""
    out = gh(["issue", "view", str(number), "--repo", REPO, "--comments"])
    if out.returncode != 0:
        return 0
    return len(MARKER_RE.findall(out.stdout or ""))


def run_ladder(env, incoda_path, issue_number, sha, budget_line):
    """One claimed, lane-taken signoff run at `sha`: marker comment first,
    then one incoda acquisition around `just signoff` in the worktree, and
    nothing else inside the lane. Returns True when the claim and the call
    got through; the record, not the exit code, is the verdict, so a 121
    queue timeout is reported as what it is and the missing record decides."""
    k = count_markers(env.gh, issue_number) + 1
    out = env.gh(["issue", "comment", str(issue_number), "--repo", REPO,
                  "--body", marker_body(sha, env.now(), k)])
    if out.returncode != 0:
        print(f"resignoff-bot: could not comment the claim marker on #{issue_number} "
              f"(rc={out.returncode}): {(out.stderr or out.stdout).strip()}")
        return False
    print(f"resignoff-bot: #{issue_number}: {budget_line}")
    out = env.incoda(incoda_path,
                     ["run", "--queue", "wintty", "--reason", f"resignoff {sha[:SHA7]}",
                      "--", "just", "signoff"],
                     cwd=env.worktree_dir)
    if out.returncode not in (0, 1):
        # 0 green, 1 red: anything else never reached the tests (121 is the
        # lane's queue timeout). The record check at the caller still decides.
        print(f"resignoff-bot: lane run ended rc={out.returncode} (121 is a queue timeout, "
              f"not a test failure): {(out.stderr or out.stdout).strip()[:200]}")
    return True


# --- the red path ----------------------------------------------------------

def bisect_bounds(env, failing, older):
    """(chain, lo, lo_idx, hi_idx, excluded): the bisect state the records
    hold, over the recorded squash SHAs git can place inside the failing
    window. chain is oldest first; lo/lo_idx is the newest green anchor
    below the newest red bound (-1 when there is none); hi_idx is the
    newest red bound, len(chain) when only the failing window itself is
    known red. excluded carries the filings left out of the chain, because
    a verdict that does not sit in the failing window's history proves
    nothing about it (any non-zero merge-base exit is conflated: not an
    ancestor, or the object is missing locally). Reconstructed from disk
    every time, which is why a bisect survives being interrupted: no window
    is ever run twice."""
    chain, excluded = [], []
    for c in sorted(older, key=lambda w: (w["created"], w["number"])):
        if env.git(["merge-base", "--is-ancestor", c["squash"],
                    failing["squash"]]).returncode == 0:
            chain.append(c)
        else:
            excluded.append((c["number"], c["squash"]))
            print(f"resignoff-bot: #{c['number']} ({merge_guard.short(c['squash'])}) is not "
                  f"inside the failing window's history; excluded from the bisect.")
    hi_idx = len(chain)
    for i in range(len(chain) - 1, -1, -1):
        if verdict(load_record(env.common, chain[i]["squash"])) == "red":
            hi_idx = i
            break
    lo, lo_idx = None, -1
    for i in range(hi_idx - 1, -1, -1):
        if verdict(load_record(env.common, chain[i]["squash"])) == "green":
            lo, lo_idx = chain[i]["squash"], i
            break
    return chain, lo, lo_idx, hi_idx, excluded


def bisect_red(env, failing, older, runs_used, max_runs, incoda_path):
    """Bisect over the RECORDED squash SHAs between the last green anchor
    and the failing head: run the middle window, repeat, until a single
    window isolates the failure. Returns (outcome, runs_spent, culprit,
    culprit_record, entries, lo, excluded) where outcome is "isolated"
    (culprit is the window whose issue owns the failure) or "paused"
    (budget spent or a run produced nothing; re-invoke), and entries is the
    trail for the comment, oldest first. A single-window chain isolates on
    the records alone, with no run spent."""
    chain, lo, lo_idx, hi_idx, excluded = bisect_bounds(env, failing, older)

    def remaining():
        return hi_idx - (lo_idx + 1)

    # The trail carries every verdict the records and this invocation hold,
    # ordered by position in the chain so it reads oldest to newest.
    pos_entries = []
    for i, c in enumerate(chain):
        v = verdict(load_record(env.common, c["squash"]))
        if v:
            pos_entries.append((i, c["squash"], v, "from the record on disk"))
    pos_entries.append((len(chain), failing["squash"], "red", "the failing window"))

    spent = 0
    while remaining() > 0 and runs_used + spent < max_runs:
        mid = (lo_idx + 1 + hi_idx) // 2
        target = chain[mid]
        v = verdict(load_record(env.common, target["squash"]))
        if v is None:
            run_no = runs_used + spent + 1
            ok, note = prepare_worktree(env.git, env.worktree_dir, target["squash"])
            if not ok:
                print(f"resignoff-bot: #{failing['number']} bisect skipped at "
                      f"{merge_guard.short(target['squash'])}: {note}")
                break
            if not run_ladder(env, incoda_path, failing["number"], target["squash"],
                              f"bisect run at {merge_guard.short(target['squash'])} "
                              f"(run {run_no}/{max_runs})"):
                break
            spent += 1
            v = verdict(load_record(env.common, target["squash"]))
            if v is None:
                print(f"resignoff-bot: no record was written for "
                      f"{merge_guard.short(target['squash'])}; bisect paused, re-invoke "
                      "to retry.")
                break
            pos_entries.append((mid, target["squash"], v, f"bisect run {run_no}"))
        # A sha that already has a record is free evidence: it moved a bound
        # when it was read, and it never costs a run.
        if v == "green":
            lo, lo_idx = target["squash"], mid
        else:
            hi_idx = mid

    outcome = "isolated" if remaining() == 0 else "paused"
    if outcome == "paused":
        return outcome, spent, failing, load_record(env.common, failing["squash"]), \
            [(sha, v, how) for _p, sha, v, how in sorted(pos_entries)], lo, excluded
    culprit = chain[hi_idx] if hi_idx < len(chain) else failing
    pos_entries.sort(key=lambda e: e[0])
    return outcome, spent, culprit, load_record(env.common, culprit["squash"]), \
        [(sha, v, how) for _p, sha, v, how in pos_entries], lo, excluded


# --- the loop --------------------------------------------------------------

def close_duplicates(env, w):
    """Best-effort retirement of the same-squash filings that lost to this
    window at parse time: closed with a cross-reference to the winner, so
    the pile does not keep a stale twin open next to the issue that carries
    the record. A failure to close one is a note, not a failure of the
    window's own close."""
    for dup in w.get("duplicates", ()):
        out = env.gh(["issue", "close", str(dup), "--repo", REPO,
                      "--comment", dup_body(w)])
        if out.returncode != 0:
            print(f"resignoff-bot: could not close duplicate #{dup} by hand "
                  f"(rc={out.returncode}); close it by hand: it duplicates "
                  f"#{w['number']}.")
        else:
            print(f"resignoff-bot: #{dup} closed as a duplicate of #{w['number']} "
                  f"(same window {merge_guard.short(w['squash'])}).")


def work_one(env, windows, idx, runs_used, max_runs, incoda_path):
    """One window's slice: (runs_spent, action). action is "closed",
    "isolated", "ran", "skipped", "blocked" or "failed"; "isolated" stops
    the whole group, because every window above a culprit contains the same
    break, "blocked" is a worktree that could not be prepared (bot_flow
    stops the group on it when it happens to the newest window), and
    "skipped" is the lane-busy case. runs_spent counts every signoff run
    this window consumed, including its own ladder when the red path
    follows a fresh run. The caller has already resolved the lane (bot_flow
    refuses before any mutation when a run is spendable and incoda is
    missing)."""
    w = windows[idx]
    rec = load_record(env.common, w["squash"])
    v = verdict(rec)
    if v == "green":
        out = env.gh(["issue", "close", str(w["number"]), "--repo", REPO,
                      "--comment", close_body(w["squash"], rec, fresh_run=False)])
        if out.returncode != 0:
            print(f"resignoff-bot: could not close #{w['number']} (rc={out.returncode}): "
                  f"{(out.stderr or out.stdout).strip()}")
            return 0, "failed"
        close_duplicates(env, w)
        print(f"resignoff-bot: #{w['number']} closed green at "
              f"{merge_guard.short(w['squash'])} (record already on disk; no run spent).")
        return 0, "closed"

    if v == "red":
        outcome, spent, culprit, crec, entries, lo, excluded = bisect_red(
            env, w, windows[idx + 1:], runs_used, max_runs, incoda_path)
        return finish_bisect(env, w, outcome, spent, culprit, crec, entries, lo,
                             excluded)

    # Owes a run: the record is absent, corrupt, or a deferral (credit, not
    # evidence: at a squash sha a deferral borrows exactly the hour this bot
    # exists to pay).
    busy = lane_busy_with(env, incoda_path, w["squash"])
    if busy:
        print(f"resignoff-bot: #{w['number']}: a resignoff for "
              f"{merge_guard.short(w['squash'])} appears to be already in flight on the "
              "lane; leaving the issue alone rather than double-queuing it.")
        print(f"  holder status: {busy}")
        print("  this match can be stale: check `incoda status --queue wintty`, clear the "
              "stale holder or wait, then re-run.")
        return 0, "skipped"
    ok, note = prepare_worktree(env.git, env.worktree_dir, w["squash"])
    if not ok:
        print(f"resignoff-bot: #{w['number']} skipped: {note}")
        return 0, "blocked"
    if not run_ladder(env, incoda_path, w["number"], w["squash"],
                      f"running the ladder at {merge_guard.short(w['squash'])} "
                      f"(run {runs_used + 1}/{max_runs})"):
        return 0, "failed"
    rec = load_record(env.common, w["squash"])
    v = verdict(rec)
    if v is None:
        print(f"resignoff-bot: #{w['number']}: no record was written for "
              f"{merge_guard.short(w['squash'])}; leaving the issue open, re-invoke to retry.")
        return 1, "ran"
    if v == "red":
        # The run itself went red: bisect with the run just spent counted
        # against the budget, exactly as if the record had been found red.
        # The +1 rides the whole way through finish_bisect: a bisect spend
        # reported without the failing ladder that paid for it would make
        # the summary lie about the budget.
        outcome, spent, culprit, crec, entries, lo, excluded = bisect_red(
            env, w, windows[idx + 1:], runs_used + 1, max_runs, incoda_path)
        return finish_bisect(env, w, outcome, spent + 1, culprit, crec, entries, lo,
                             excluded)
    out = env.gh(["issue", "close", str(w["number"]), "--repo", REPO,
                  "--comment", close_body(w["squash"], rec, fresh_run=True)])
    if out.returncode != 0:
        print(f"resignoff-bot: could not close #{w['number']} (rc={out.returncode}): "
              f"{(out.stderr or out.stdout).strip()}")
        return 1, "failed"
    close_duplicates(env, w)
    print(f"resignoff-bot: #{w['number']} closed green at "
          f"{merge_guard.short(w['squash'])} (record written by the run this issue "
          "claimed).")
    return 1, "closed"


def issue_labels(gh, number):
    """The labels an issue already carries, [] when the read fails. A failed
    read errs toward re-posting (a duplicate comment) rather than toward
    losing the trail: the trail is the part that cannot be reconstructed
    from the records, so it is the part never skipped on a guess."""
    out = gh(["issue", "view", str(number), "--repo", REPO, "--json", "labels"])
    if out.returncode != 0:
        return []
    try:
        return [l.get("name") for l in json.loads(out.stdout).get("labels", [])]
    except ValueError:
        return []


def finish_bisect(env, failing, outcome, spent, culprit, crec, entries, lo,
                  excluded=()):
    """The end of a red path: comment the failing legs and the trail, label
    the culprit issue (comment first: a label success with a comment
    failure would leave a labelled issue with no trail, and the trail is
    the part nothing else reconstructs), and say on the failing issue where
    the failure actually lives when that is a different issue. Idempotent:
    a culprit already carrying the label is left alone, so re-invocations
    on a settled pile do not re-comment forever. spent is this window's
    total spend (the failing ladder included), which is both the honest
    number in the paused message and the caller's budget accounting.
    Returns (spent, action); "isolated" either way once the records
    isolate, so the group still stops."""
    if outcome != "isolated":
        print(f"resignoff-bot: #{failing['number']} bisect paused with {spent} run(s) "
              "spent; the records hold the state. Resume with `just resignoff-bot`.")
        return spent, "ran"
    if CULPRIT_LABEL in issue_labels(env.gh, culprit["number"]):
        print(f"resignoff-bot: #{culprit['number']} already carries {CULPRIT_LABEL} at "
              f"{merge_guard.short(culprit['squash'])}; leaving the existing trail and "
              "stopping the group here.")
        return spent, "isolated"
    out = env.gh(["issue", "comment", str(culprit["number"]), "--repo", REPO,
                  "--body", trail_body(culprit, failing, crec, entries, lo, excluded)])
    if out.returncode != 0:
        print(f"resignoff-bot: could not comment the trail on #{culprit['number']} "
              f"(rc={out.returncode}): {(out.stderr or out.stdout).strip()}")
        return spent, "failed"
    out = env.gh(["issue", "edit", str(culprit["number"]), "--repo", REPO,
                  "--add-label", CULPRIT_LABEL])
    if out.returncode != 0:
        print(f"resignoff-bot: could not label #{culprit['number']} (rc={out.returncode}): "
              f"{(out.stderr or out.stdout).strip()}")
        return spent, "failed"
    if culprit["number"] != failing["number"]:
        env.gh(["issue", "comment", str(failing["number"]), "--repo", REPO,
                "--body", f"The failing window's ladder went red; the bisect isolated "
                          f"#{culprit['number']} ({merge_guard.short(culprit['squash'])}) as "
                          "the culprit window. This issue stays open until the fix lands "
                          "and its ladder is re-run.\n"])
    print(f"resignoff-bot: #{culprit['number']} labelled {CULPRIT_LABEL} at "
          f"{merge_guard.short(culprit['squash'])}; left open for manual pickup.")
    return spent, "isolated"


def run_spendable(env, windows, max_runs):
    """Whether this invocation could actually spend a signoff run: the
    budget allows one, and some window owes one - unproven windows always
    do, a red window only while its bisect still has an untested middle
    between the reconstructed bounds. This is what gates the incoda
    refusal, so a pile the records can already finish (greens to close, or
    a red already isolated) never demands the lane, and --max 0 is the
    greens-only pass that needs no lane at all."""
    if max_runs <= 0:
        return False
    for idx, w in enumerate(windows):
        v = verdict(load_record(env.common, w["squash"]))
        if v is None:
            return True
        if v == "red":
            _chain, _lo, lo_idx, hi_idx, _excluded = bisect_bounds(
                env, w, windows[idx + 1:])
            if hi_idx - (lo_idx + 1) > 0:
                return True
    return False


def bot_flow(max_runs=1, dry_run=False, env=None):
    """The whole invocation. Returns the process exit code (see the module
    docstring). Every runner is injectable through env, so the self-test
    replays whole sessions without touching GitHub, git or the lane."""
    env = env or Env()
    common = merge_guard.resolve_common_dir(env.git)
    if not common:
        print("resignoff-bot: could not resolve the git common dir; the records live there, "
              "and refusing to act in a half-dead session.")
        return 2
    env.common = common
    slug = merge_guard.checkout_slug(env.git)
    if not slug or not pr_gate.is_our_repo(slug):
        print(f"resignoff-bot: wrong-repo: this checkout's origin is {slug or 'unresolvable'}; "
              f"the bot only works {REPO} windows. Clone {REPO} (the records live in this "
              "clone's git dir) and re-run.")
        return 2

    issues, err = enumerate_issues(env.gh)
    if err:
        print(f"resignoff-bot: could not list {LABEL} issues: {err}")
        return 1
    if not issues:
        print(f"resignoff-bot: nothing to do: no open {LABEL} issues.")
        return 0

    # Objects first: a recorded squash sha must exist locally for the
    # worktree add to have anything to detach onto. A failed fetch is only a
    # note, the nightly's stance: the last-known refs may still be enough.
    if not merge_guard.fetch_windows(env.git):
        print(f"resignoff-bot: WARNING: git fetch origin {merge_guard.BASE_BRANCH} failed; "
              "recorded squash shas newer than the last fetch will fail to check out.")

    windows, skipped = parse_all(issues, env.git)
    for number, title, why in skipped:
        print(f"resignoff-bot: SKIPPED #{number} ({title}): {why}")
    if not windows:
        print(f"resignoff-bot: nothing workable: {len(skipped)} issue(s) filed, none parsed.")
        return 0

    if dry_run:
        return dry_run_flow(env, windows, max_runs)

    runs_used = 0
    # The lane is demanded only when a run is actually spendable: some
    # window owes one (unproven, or red with an untested bisect middle
    # left) and the budget allows spending it. An all-green pile closes on
    # the records alone with no incoda on the machine, a red pile whose
    # records already isolate needs none either, and --max 0 is the
    # greens-only pass that spends nothing by definition. Refusing here,
    # before any close or comment, keeps exit 2 meaning "nothing was
    # mutated".
    incoda_path = None
    if run_spendable(env, windows, max_runs):
        incoda_path = env.find_incoda()
        if not incoda_path:
            print(f"resignoff-bot: refusing (exit 2): the pile owes signoff runs but incoda "
                  f"is on neither PATH nor {install_hint()}. A run is an hour of lane time "
                  "and must not be started outside the lane; install incoda or put it on "
                  "PATH and re-run.")
            return 2

    # One instance at a time on the shared worktree (see acquire_instance_lock).
    ok, holder = acquire_instance_lock(env)
    if not ok:
        print(f"resignoff-bot: refusing (exit 2): another resignoff bot instance appears "
              f"to hold the worktree lock: {holder}. Check that pid and "
              "`incoda status --queue wintty` before touching anything; a dead pid's "
              "lock is taken over automatically, so a live one is being named here.")
        return 2
    try:
        for idx in range(len(windows)):
            # Free closes (already-green records) cost no budget, so the
            # budget check only gates windows that would actually run; a
            # spent budget must not stop green windows behind it from
            # closing.
            free = verdict(load_record(env.common, windows[idx]["squash"])) == "green"
            if not free and runs_used >= max_runs:
                print(f"resignoff-bot: run budget reached ({runs_used} of {max_runs} "
                      f"run(s) spent); #{windows[idx]['number']} and any older windows "
                      "stay open for the next invocation. Resume with `just "
                      "resignoff-bot`.")
                break
            spent, action = work_one(env, windows, idx, runs_used, max_runs, incoda_path)
            runs_used += spent
            if action == "isolated":
                break
            if action == "blocked" and idx == 0:
                # The newest window cannot be prepared: working older
                # windows would spend hours against a pile whose top state
                # is unknown, so stop loudly instead of walking down.
                print("resignoff-bot: stopping: the NEWEST window's worktree could not "
                      "be prepared, and working older windows past an unproven newest "
                      "would report progress the pile cannot stand behind. Check "
                      "`git worktree list` and the path above, then re-run.")
                return 1
            if action == "failed":
                return 1
    finally:
        release_instance_lock(env)
    if runs_used == 0:
        print(f"resignoff-bot: done: {len(windows)} window(s) considered, no run spent.")
    else:
        print(f"resignoff-bot: done: {runs_used} signoff run(s) spent this invocation "
              f"(--max {max_runs}).")
    return 0


def describe_run_commands(w, prefix):
    """The exact command sequence one ladder run would take, printed by the
    dry run: the claim, the worktree recipe, the lane acquisition."""
    return [
        f"{prefix}would: gh issue comment {w['number']} --repo {REPO} --body "
        f"\"{marker_body(w['squash'], '<utc>', 1).strip()}\"",
        f"{prefix}would: git worktree add --detach <worktree> {w['squash']}",
        f"{prefix}would: git -C <worktree> reset --hard {w['squash']} && "
        f"git -C <worktree> clean -fdx -e .zig-cache -e zig-out",
        f"{prefix}would: incoda run --queue wintty --reason "
        f"\"resignoff {w['squash'][:SHA7]}\" -- just signoff   (cwd <worktree>)",
    ]


def dry_run_flow(env, windows, max_runs):
    """The same enumeration, ordering and decision the live loop would make,
    printed with the exact commands, and nothing executed: no worktree, no
    comment, no close, no label, no lane, and no incoda requirement (a
    missing install is printed as the refusal it would be, not raised)."""
    incoda_path = env.find_incoda()
    print(f"resignoff-bot: dry run: {len(windows)} workable window(s), newest first "
          f"(--max {max_runs}).")
    budget = max_runs
    for idx, w in enumerate(windows):
        rec = load_record(env.common, w["squash"])
        v = verdict(rec)
        out = (f"(outstanding at filing: {w['outstanding'][0]}, oldest #{w['outstanding'][1]})"
               if w["outstanding"] else "(no backlog line)")
        print(f"resignoff-bot: #{w['number']} {w['title']}")
        print(f"resignoff-bot:   squash {merge_guard.short(w['squash'])}, signed off "
              f"{merge_guard.short(w['signed'])}, base {merge_guard.short(w['base'])}, "
              f"windows head at merge {merge_guard.short(w['winhead'])} {out}")
        if v == "green":
            print("resignoff-bot:   action: CLOSE (record already green; no run, no lane)")
            print(f"resignoff-bot:   would: gh issue close {w['number']} --repo {REPO} "
                  "--comment <record path + per-leg rcs + settle note>")
            continue
        if v == "red":
            chain, lo, lo_idx, hi_idx, excluded = bisect_bounds(env, w, windows[idx + 1:])
            for n, sha in excluded:
                print(f"resignoff-bot:   excluded from the bisect: #{n} "
                      f"`{sha[:SHA7]}` (not an ancestor, or object missing)")
            if hi_idx - (lo_idx + 1) == 0:
                culprit = chain[hi_idx] if hi_idx < len(chain) else w
                labelled = CULPRIT_LABEL in issue_labels(env.gh, culprit["number"])
                print("resignoff-bot:   action: LABEL + TRAIL (the records isolate the "
                      f"culprit at {merge_guard.short(culprit['squash'])} with no run"
                      + ("; already labelled and trailed" if labelled else "") + ")")
                if not labelled:
                    print(f"resignoff-bot:   would: gh issue comment {culprit['number']} "
                          f"--repo {REPO} --body <failing legs + bisect trail>")
                    print(f"resignoff-bot:   would: gh issue edit {culprit['number']} "
                          f"--repo {REPO} --add-label {CULPRIT_LABEL}")
            else:
                mid = (lo_idx + 1 + hi_idx) // 2
                print("resignoff-bot:   action: BISECT (red record at this window; bounds "
                      f"reconstructed from the records, lower anchor "
                      f"{merge_guard.short(lo) if lo else 'none'})")
                for line in describe_run_commands(chain[mid], "resignoff-bot:   "):
                    print(line)
            continue
        if budget <= 0:
            print("resignoff-bot:   action: RUN, but the budget is spent; this window waits "
                  "for the next invocation.")
            continue
        if incoda_path is None:
            print(f"resignoff-bot:   action: RUN, but this would refuse (exit 2): incoda is "
                  f"on neither PATH nor {install_hint()}.")
            continue
        print("resignoff-bot:   action: RUN the ladder at the recorded squash")
        for line in describe_run_commands(w, "resignoff-bot:   "):
            print(line)
        budget -= 1
    print("resignoff-bot: dry run: nothing commented, nothing closed, nothing labelled, "
          "no worktree, no lane.")
    return 0


# --- self-test -------------------------------------------------------------

class FakeGh:
    """A stateful issue tracker answering only the spellings the bot
    issues, plus a mutation log and a shared event list so a test can prove
    a claim marker landed before the run it claimed."""

    def __init__(self, issues, events=None, list_rc=0):
        self.issues = {i["number"]: {"title": i.get("title", ""), "body": i.get("body", ""),
                                     "createdAt": i.get("createdAt", ""),
                                     "comments": list(i.get("comments", [])),
                                     "labels": list(i.get("labels", [])),
                                     "state": "OPEN"}
                       for i in issues}
        self.calls = []
        self.mutations = []
        self.events = events if events is not None else []
        self.list_rc = list_rc

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        j = " ".join(args)
        if j.startswith("issue list"):
            if self.list_rc != 0:
                return merge_guard.Out(1, "", "gh: could not list\n")
            data = [{"number": n, "title": i["title"], "body": i["body"],
                     "createdAt": i["createdAt"]} for n, i in self.issues.items()]
            return merge_guard.Out(0, json.dumps(data))
        if j.startswith("issue view"):
            n = int(args[2])
            if "--json" in args:
                return merge_guard.Out(0, json.dumps(
                    {"labels": [{"name": x} for x in self.issues[n]["labels"]]}))
            body = "\n\n".join(self.issues[n]["comments"])
            return merge_guard.Out(0, f"issue #{n}\n\ncomments:\n{body}\n")
        if j.startswith("issue comment"):
            n, text = int(args[2]), args[args.index("--body") + 1]
            self.issues[n]["comments"].append(text)
            self.mutations.append(("comment", n, text))
            self.events.append(f"comment:{n}")
            return merge_guard.Out(0, "commented\n")
        if j.startswith("issue close"):
            n = int(args[2])
            if "--comment" in args:
                self.issues[n]["comments"].append(args[args.index("--comment") + 1])
            self.issues[n]["state"] = "CLOSED"
            self.mutations.append(("close", n, ""))
            self.events.append(f"close:{n}")
            return merge_guard.Out(0, f"closed #{n}\n")
        if j.startswith("issue edit"):
            n, label = int(args[2]), args[args.index("--add-label") + 1]
            self.issues[n]["labels"].append(label)
            self.mutations.append(("label", n, label))
            self.events.append(f"label:{n}")
            return merge_guard.Out(0, "edited\n")
        return merge_guard.Out(1, "", f"unexpected gh call: {j}")

    def by_kind(self, kind):
        """The mutation log filtered to one kind, for assertions."""
        return [m for m in self.mutations if m[0] == kind]

    def open_numbers(self):
        """Which issues the fake still holds open, for the left-open claims."""
        return sorted(n for n, i in self.issues.items() if i["state"] == "OPEN")


class FakeGit:
    """A router for the spellings the bot issues: common dir, origin remote,
    fetch, short-sha resolution, ancestry, and the worktree recipe (answered
    quietly, with an injectable add failure so the skip path is reachable).
    Full 40-hex shas resolve to themselves unless resolve_all is False,
    which is how the missing-object case is reached."""

    def __init__(self, common, remote="https://github.com/deblasis/wintty.git",
                 resolve=None, ancestors=(), fail_worktree_add=False,
                 resolve_all=True):
        self.common = common
        self.remote = remote
        self.resolve = resolve or {}
        self.ancestors = set(ancestors)
        self.fail_worktree_add = fail_worktree_add
        self.resolve_all = resolve_all
        self.calls = []

    def __call__(self, args, cwd=None):
        self.calls.append(list(args))
        a = list(args)
        if a[0] == "rev-parse" and "--git-common-dir" in a:
            return merge_guard.Out(0, self.common + "\n")
        if a[0] == "remote":
            return merge_guard.Out(0, self.remote + "\n")
        if a[0] == "fetch":
            return merge_guard.Out(0, "")
        if a[0] == "rev-parse" and a[-1].endswith("^{commit}"):
            asked = a[-1].split("^")[0]
            full = self.resolve.get(asked)
            if full is None and self.resolve_all and re.fullmatch(r"[0-9a-f]{40}", asked):
                full = asked
            return merge_guard.Out(0, full + "\n") if full \
                else merge_guard.Out(1, "", "fatal: not found\n")
        if a[:2] == ["merge-base", "--is-ancestor"]:
            return merge_guard.Out(0, "") if (a[2], a[3]) in self.ancestors \
                else merge_guard.Out(1, "")
        if a[:2] == ["worktree", "prune"]:
            return merge_guard.Out(0, "")
        if a[:2] == ["worktree", "add"]:
            return merge_guard.Out(1, "", "fatal: worktree add failed\n") \
                if self.fail_worktree_add else merge_guard.Out(0, "")
        if a[0] == "-C":
            return merge_guard.Out(0, "")
        return merge_guard.Out(1, "", f"unexpected git call: {a}")

    def touched_worktree(self):
        """Whether any worktree-mutating call was made, for the dry-run's
        zero-mutation proof."""
        return any(c[0] == "worktree" or c[0] == "-C" for c in self.calls)


class FakeIncoda:
    """The lane and the run it takes. A run parses its own --reason, finds
    the sha it was for and writes that sha's record into the real records
    dir, exactly what `just signoff` inside the worktree would do; the
    verdict table (full sha -> "green"/"red"/None) is the fake ladder, and
    None means the run never produced a record."""

    def __init__(self, common, shas, verdicts=None, events=None):
        self.common = common
        self.by_short = {sha[:SHA7]: sha for sha in shas}
        self.verdicts = verdicts or {}
        self.events = events if events is not None else []
        self.calls = []

    def __call__(self, path, args, cwd=None):
        self.calls.append((list(args), cwd))
        self.events.append("incoda")
        sha = self.by_short.get(args[args.index("--reason") + 1].split()[-1])
        v = self.verdicts.get(sha, "green")
        if v:
            merge_guard.write_record(self.common, sha, make_record(sha, v == "green"))
            return merge_guard.Out(0 if v == "green" else 1, "")
        return merge_guard.Out(121, "", "queue wait elapsed\n")


def make_record(sha, ok):
    """A record shaped the way signoff.py writes one at a windows squash: a
    full ladder (which is what a run at a squash always is, since the
    changed-path set against itself is empty), red on one leg when not ok."""
    steps = {"gates-selftest": {"rc": 0}, "release-gate": {"rc": 0},
             "windows-tests": {"rc": 0}, "zig-fmt": {"rc": 0},
             "zig-tests": {"rc": 0 if ok else 1}}
    return {"sha": sha, "base": sha, "created": "2026-09-04T00:00:00+00:00",
            "steps": steps, "pass": ok,
            "scope": {"legs_run": sorted(steps), "paths": None, "justfile_legs": [],
                      "reason": "no changes against the base; nothing to scope",
                      "full": True}}


def run_capture(fn, *a, **kw):
    """A flow's exit code and captured stdout, so assertions read the
    agent-facing output and not the return code alone."""
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        rc = fn(*a, **kw)
    return rc, buf.getvalue()


GUARD_BODY = """Filed by the merge guard (issue #969): this PR merged on a green signoff whose `windows` window had already moved. Nothing was re-gated; the risk is carried here instead.

- PR: #{pr}, squashed as `eeeeeeeee` on `windows`
- Signed off: `{signed}` (record at `{path}`, all two legs rc=0)
- `windows` base at signoff time: `bbbbbbbbb`
- `windows` head at merge time: `ccccccccc`

## Risks

1. The delta itself.

## Status

Resignoff for `eeeeeeeee`: not started at filing time. Check
`incoda status --queue wintty` first; the #969 bot owns this (`just resignoff-bot`, owner-run): agents do not queue runs for it.
Squash commit: `{squash}` (full sha)
Outstanding `resignoff-required` issues at filing time: 1 (oldest #690).
"""

HAND_BODY = """Filed by hand until the merge guard exists.

- PR: #958, squashed as `900e44bb4` on `windows`
- Signed off: `dff953aac5c50c3f9286ac2035d0ec88ad7817d6` (record at
`.git/pr-signoff/dff953aac5c50c3f9286ac2035d0ec88ad7817d6.json`, all four legs rc=0)
- `windows` base at signoff time: `19110d76d`
- `windows` head at merge time: `791cf0344`

## Status

A full signoff for the merged head was already queued on the lane when the policy
changed. If it comes back green, this issue closes with that evidence.
"""


def self_test():
    """Replay injected sessions against the whole state machine: strict
    parsing (guard-template body, hand-filed #970-style body, missing full
    and short shas, the rejects, duplicates), newest-first ordering, the
    idempotent green close and its comment shape, claim markers before every
    run, the bisect over a synthetic sha chain including the resume across
    invocations, the isolation with its label and trail, excluded filings in
    the trail, the --max bound on both the disk-red and ran-then-red shapes,
    the budget not blocking free closes, --max 0 as the greens-only pass,
    dry-run's zero-mutation promise, duplicate filings closing with the
    winner, the stray-worktree re-add, the instance lock's refuse and
    takeover, and every refusal (nothing to do, incoda missing and not
    demanded, newest-window worktree failure stopping the group, lane
    already busy)."""
    import tempfile

    failed = False

    def report(ok, label, detail=""):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}{': ' + detail if detail else ''}")

    signed = "d" * 40
    squash_g = "e" * 40
    squash_h = "9" * 40
    # One synthetic window chain, s1 oldest to s4 newest; the bisect tests
    # pre-write the failing record at s4 and let the fake ladder answer s3.
    chain = [f"{i}" * 40 for i in range(1, 5)]
    s1, s2, s3, s4 = chain
    inside = [(chain[a], chain[b]) for a in range(4) for b in range(a + 1, 4)]

    tmp = tempfile.mkdtemp(prefix="resignoff-bot-selftest-")
    try:
        # --- parsing: the guard template, the hand-filed shape, the rejects.
        fgit = FakeGit(tmp, resolve={"900e44bb4": squash_h})
        w, why = parse_window(GUARD_BODY.format(pr=700, signed=signed, squash=squash_g,
                                                path=merge_guard.display_path(signed)), fgit)
        report(w is not None and w["number"] == 700 and w["squash"] == squash_g
               and w["signed"] == signed and w["base"] == "bbbbbbbbb"
               and w["winhead"] == "ccccccccc" and w["outstanding"] == (1, 690),
               "parse-guard-body", why or f"squash={w['squash'][:7] if w else None}")
        w, why = parse_window(HAND_BODY, fgit)
        report(w is not None and w["number"] == 958 and w["squash"] == squash_h
               and w["outstanding"] is None,
               "parse-hand-filed-body", why or "short sha resolved by git")
        w, why = parse_window(HAND_BODY, FakeGit(tmp))
        report(w is None and "does not resolve" in why and "--file-only" in why,
               "parse-unresolvable-short", why)
        # The full-sha form gets the same local check: an object this
        # checkout lacks is a window the bot cannot run.
        w, why = parse_window(GUARD_BODY.format(pr=700, signed=signed, squash=squash_g,
                                                path=merge_guard.display_path(signed)),
                              FakeGit(tmp, resolve_all=False))
        report(w is None and "not in this checkout" in why and "--file-only" in why,
               "parse-full-sha-missing", why)
        for label, body in (("empty", ""),
                            ("no-pr-bullet", "- Signed off: `" + "d" * 40 + "`\n"),
                            ("no-signed-bullet",
                             "- PR: #700, squashed as `eeeeeeeee` on `windows`\n")):
            w, why = parse_window(body, fgit)
            report(w is None and bool(why), "parse-" + label, why)

        # --- ordering: newest window first; a duplicate squash keeps the newest.
        issues = [{"number": 701, "title": "older", "createdAt": "2026-09-01T00:00:00Z",
                   "body": GUARD_BODY.format(pr=701, signed=signed, squash=s2,
                                             path=merge_guard.display_path(signed))},
                  {"number": 703, "title": "newest", "createdAt": "2026-09-03T00:00:00Z",
                   "body": GUARD_BODY.format(pr=703, signed=signed, squash=s4,
                                             path=merge_guard.display_path(signed))},
                  {"number": 702, "title": "middle", "createdAt": "2026-09-02T00:00:00Z",
                   "body": GUARD_BODY.format(pr=702, signed=signed, squash=s3,
                                             path=merge_guard.display_path(signed))},
                  {"number": 704, "title": "dup", "createdAt": "2026-09-04T00:00:00Z",
                   "body": GUARD_BODY.format(pr=704, signed=signed, squash=s4,
                                             path=merge_guard.display_path(signed))}]
        windows, skipped = parse_all(issues, fgit)
        report([x["number"] for x in windows] == [704, 702, 701] and len(skipped) == 1
               and windows[0]["squash"] == s4 and skipped[0][0] == 703
               and windows[0]["duplicates"] == [703],
               "ordering-newest-first",
               f"order={[x['number'] for x in windows]}, "
               f"dups={windows[0]['duplicates']}, skipped={skipped}")

        # --- nothing to do: the empty pile is exit 0 and nothing else.
        fgh = FakeGh([])
        rc, out = run_capture(bot_flow, 1, False,
                              Env(gh=fgh, git=FakeGit(tmp), common=tmp))
        report(rc == 0 and "nothing to do" in out and len(fgh.calls) == 1,
               "refuse-nothing-to-do", f"rc={rc}, gh calls={len(fgh.calls)}")

        def issue_for(sha, n, day):
            """One filed window on the synthetic chain."""
            return {"number": n, "title": f"window {sha[:7]}",
                    "createdAt": f"2026-09-0{day}T00:00:00Z",
                    "body": GUARD_BODY.format(pr=n, signed=signed, squash=sha,
                                              path=merge_guard.display_path(signed))}

        def wire(pile, common=None, **kw):
            """One wired environment over its own records dir: the gh and
            incoda fakes share an event list so marker-before-run is
            provable, find_incoda is pinned so the test never depends on the
            host's install, the lane status reads empty (no holder) unless a
            test overrides it, and the worktree and instance lock live
            inside the temporary directory so no test touches this clone's
            own .agents/worktrees."""
            td = common or tempfile.mkdtemp(dir=tmp)
            events = []
            gh = FakeGh(pile, events=events)
            git = FakeGit(td, resolve={"900e44bb4": squash_h}, **kw)
            incoda = FakeIncoda(td, chain, events=events)
            return td, Env(gh=gh, git=git, incoda=incoda, common=td,
                           find_incoda=lambda: "C:\\fake\\incoda.exe",
                           incoda_status=lambda path, args: merge_guard.Out(0, ""),
                           worktree_dir=os.path.join(td, "wt", "resignoff")), \
                gh, git, incoda

        # --- idempotent close: a green record closes with no run and no claim.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s4, make_record(s4, True))
        rc, out = run_capture(bot_flow, 1, False, env)
        body = gh.issues[703]["comments"][0] if gh.issues[703]["comments"] else ""
        report(rc == 0 and len(gh.by_kind("close")) == 1 and not incoda.calls
               and not gh.by_kind("comment")
               and merge_guard.display_path(s4) in body and "rc=0" in body
               and "auto-settles" in body and "all five legs rc=0" in body
               and "already on disk; no run was spent" in body,
               "idempotent-green-close", f"rc={rc}, closes={len(gh.by_kind('close'))}, "
                                         f"runs={len(incoda.calls)}")

        # The same, where a dead invocation left a claim marker behind: the
        # record decides, and the marker is not re-commented.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s4, make_record(s4, True))
        gh.issues[703]["comments"].append(
            marker_body(s4, "2026-09-03T09:00:00+00:00", 1))
        rc, _ = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(gh.by_kind("close")) == 1
               and len(gh.by_kind("comment")) == 0 and not incoda.calls,
               "resume-green-after-marker", f"runs={len(incoda.calls)}, "
                                            f"comments={len(gh.by_kind('comment'))}")

        # --- claim marker before every run, in the exact format.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        env.now = lambda: "2026-09-04T08:00:00+00:00"
        rc, _ = run_capture(bot_flow, 1, False, env)
        comments = gh.by_kind("comment")
        marker = comments[0][2].strip() if comments else ""
        report(marker == f"resignoff started: {s4[:SHA7]} at 2026-09-04T08:00:00+00:00, run 1"
               and "incoda" in gh.events
               and gh.events.index("comment:703") < gh.events.index("incoda"),
               "claim-marker-before-run", f"marker={marker!r}, events={gh.events}")

        # --- bisect: a red newest window over a four-sha chain isolates the
        # culprit window in two runs, labels its issue and leaves it open.
        td, env, gh, git, incoda = wire([issue_for(s, 701 + i, i + 1)
                                         for i, s in enumerate(chain)],
                                        ancestors=inside)
        incoda.verdicts = {s3: "red"}
        merge_guard.write_record(td, s4, make_record(s4, False))
        rc, out = run_capture(bot_flow, 5, False, env)
        trail = gh.issues[703]["comments"][-1] if gh.issues[703]["comments"] else ""
        report(rc == 0 and len(incoda.calls) == 2
               and gh.by_kind("label") == [("label", 703, CULPRIT_LABEL)]
               and 703 in gh.open_numbers()
               and "zig-tests rc=1" in trail and f"`{s3[:SHA7]}` red" in trail
               and f"`{s2[:SHA7]}` green" in trail
               and f"`{s4[:SHA7]}` red (the failing window)" in trail
               and "- lower anchor: `2222222`" in trail,
               "bisect-isolates-culprit",
               f"rc={rc}, runs={len(incoda.calls)}, labels={gh.by_kind('label')}")

        # --- the same bisect, --max 1: the first invocation runs one middle
        # window and pauses; the second resumes from the records and isolates.
        td, env, gh, git, incoda = wire([issue_for(s, 701 + i, i + 1)
                                         for i, s in enumerate(chain)],
                                        ancestors=inside)
        incoda.verdicts = {s3: "red"}
        merge_guard.write_record(td, s4, make_record(s4, False))
        rc, out = run_capture(bot_flow, 1, False, env)
        paused = (rc == 0 and len(incoda.calls) == 1 and not gh.by_kind("label")
                  and "paused" in out)
        rc2, _ = run_capture(bot_flow, 2, False, env)
        report(paused and rc2 == 0 and len(incoda.calls) == 2
               and gh.by_kind("label") == [("label", 703, CULPRIT_LABEL)],
               "bisect-resumes-across-invocations",
               f"first invocation runs=1, total runs={len(incoda.calls)}, "
               f"labels={gh.by_kind('label')}")

        # --- --max bounds the runs: two unproven windows, one run spent, the
        # older window untouched and still open.
        td, env, gh, git, incoda = wire([issue_for(s3, 702, 2), issue_for(s4, 703, 3)])
        rc, out = run_capture(bot_flow, 1, False, env)
        first_reason = incoda.calls[0][0][incoda.calls[0][0].index("--reason") + 1] \
            if incoda.calls else ""
        report(rc == 0 and len(incoda.calls) == 1
               and first_reason == f"resignoff {s4[:SHA7]}"
               and len(gh.by_kind("close")) == 1 and 702 in gh.open_numbers()
               and "budget reached" in out,
               "max-bounds-runs", f"runs={len(incoda.calls)}, "
                                  f"closes={len(gh.by_kind('close'))}, open={gh.open_numbers()}")

        # --- dry run: the full picture, zero mutations anywhere.
        td = tempfile.mkdtemp(dir=tmp)
        merge_guard.write_record(td, s2, make_record(s2, True))
        merge_guard.write_record(td, s3, make_record(s3, False))
        events = []
        gh = FakeGh([issue_for(s1, 701, 1), issue_for(s2, 702, 2), issue_for(s3, 703, 3),
                     issue_for(s4, 704, 4)], events=events)
        git = FakeGit(td, ancestors=inside)
        env = Env(gh=gh, git=git, incoda=FakeIncoda(td, chain, events=events), common=td,
                  find_incoda=lambda: "C:\\fake\\incoda.exe")
        rc, out = run_capture(bot_flow, 1, True, env)
        report(rc == 0 and not gh.mutations and not env.incoda.calls
               and not git.touched_worktree()
               and "would: gh issue close 702" in out and "would: incoda run" in out
               and "action: LABEL + TRAIL" in out and "action: RUN the ladder" in out
               and "dry run: nothing commented" in out,
               "dry-run-mutates-nothing",
               f"rc={rc}, mutations={len(gh.mutations)}, wt calls={git.touched_worktree()}, "
               f"runs={len(env.incoda.calls)}")

        # --- refusals: incoda missing names the install and mutates nothing.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        env.find_incoda = lambda: None
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 2 and not gh.mutations and not git.touched_worktree()
               and "Programs" in out and "incoda" in out,
               "refuse-incoda-missing", f"rc={rc}, mutations={len(gh.mutations)}")

        # A worktree that cannot be created on the NEWEST window stops the
        # group loudly (rc 1): working older windows past an unproven newest
        # would spend hours against a pile whose top state is unknown.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)], fail_worktree_add=True)
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 1 and not incoda.calls and not gh.mutations
               and "worktree add failed" in out and "stopping" in out
               and 703 in gh.open_numbers(),
               "refuse-worktree-failure-newest-stops", f"rc={rc}, runs={len(incoda.calls)}")

        # A non-newest window whose worktree fails is skipped with a note and
        # the group goes on.
        td, env, gh, git, incoda = wire([issue_for(s3, 702, 2), issue_for(s4, 703, 3)],
                                        fail_worktree_add=True)
        merge_guard.write_record(td, s4, make_record(s4, True))
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and not incoda.calls and len(gh.by_kind("close")) == 1
               and "skipped" in out,
               "worktree-failure-older-skips", f"rc={rc}, closes={len(gh.by_kind('close'))}")

        # --- the lane-busy check: a holder whose reason names this resignoff
        # is left alone instead of double-queued.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])

        class Status:
            """A lane status reader whose output names one holder."""
            def __call__(self, path, args):
                return merge_guard.Out(0, f"holder: resignoff {s4[:SHA7]} just signoff")

        env.incoda_status = Status()
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and not incoda.calls and not gh.by_kind("comment")
               and "in flight" in out, "lane-busy-left-alone",
               f"runs={len(incoda.calls)}")

        # --- verdict(): a deferral is credit, not evidence, at a squash sha,
        # and a scoped green retires nothing: only a full ladder does.
        deferred = dict(make_record(s1, True), deferred=True, reason="batching")
        scoped = make_record(s1, True)
        scoped["scope"]["full"] = False
        scoped["scope"]["legs_run"] = ["zig-fmt"]
        scoped["steps"] = {"zig-fmt": {"rc": 0}}
        covered = make_record(s1, True)
        covered["scope"]["full"] = False
        report(verdict(deferred) is None and verdict(make_record(s1, True)) == "green"
               and verdict(None) is None and verdict(scoped) is None
               and verdict(covered) == "green",
               "verdict-deferred-and-scoped-not-evidence", "")

        # --- BLOCKER regression: the newest window's OWN ladder goes red
        # under --max 1. Exactly one incoda call, the summary says 1, and the
        # bisect that follows (an empty chain here) does not free-ride.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        incoda.verdicts = {s4: "red"}
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(incoda.calls) == 1 and "1 signoff run(s) spent" in out
               and gh.by_kind("label") == [("label", 703, CULPRIT_LABEL)]
               and 703 in gh.open_numbers(),
               "red-own-ladder-bounded", f"rc={rc}, runs={len(incoda.calls)}, "
                                         f"labels={gh.by_kind('label')}")

        # The same shape resumed: the second invocation isolates on the
        # records alone, and the already-labelled culprit is left alone
        # (no re-label, no duplicate trail).
        labels_before = len(gh.by_kind("label"))
        comments_before = len(gh.by_kind("comment"))
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(incoda.calls) == 1
               and len(gh.by_kind("label")) == labels_before
               and len(gh.by_kind("comment")) == comments_before
               and 703 in gh.open_numbers(),
               "red-own-ladder-resume-idempotent",
               f"rc={rc}, runs={len(incoda.calls)}, labels={len(gh.by_kind('label'))}, "
               f"comments={len(gh.by_kind('comment'))}")

        # --- the instance lock: a live holder refuses (exit 2, lock intact),
        # a dead holder's lock is taken over and released on the way out.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        env.pid_alive = lambda p: True
        os.makedirs(os.path.dirname(env.lock_path), exist_ok=True)
        with open(env.lock_path, "w", encoding="utf-8") as f:
            f.write(f"{os.getpid()} at 2026-09-04T00:00:00+00:00\n")
        rc, out = run_capture(bot_flow, 1, False, env)
        with open(env.lock_path, encoding="utf-8") as f:
            kept = f.read().strip()
        report(rc == 2 and not incoda.calls and not gh.mutations
               and "worktree lock" in out and kept.startswith(f"{os.getpid()} at "),
               "refuse-instance-lock", f"rc={rc}, runs={len(incoda.calls)}, lock={kept!r}")

        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        env.pid_alive = lambda p: False
        os.makedirs(os.path.dirname(env.lock_path), exist_ok=True)
        with open(env.lock_path, "w", encoding="utf-8") as f:
            f.write("999999 at 2026-09-03T00:00:00+00:00\n")
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(incoda.calls) == 1 and "taking over" in out
               and not os.path.exists(env.lock_path),
               "lock-takeover-then-release", f"rc={rc}, runs={len(incoda.calls)}")

        # --- duplicate filings: closing a winner retires the same-squash
        # losers with a cross-reference to where the record lives.
        td, env, gh, git, incoda = wire([issue_for(s4, 702, 2), issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s4, make_record(s4, True))
        rc, out = run_capture(bot_flow, 1, False, env)
        dup_comment = gh.issues[702]["comments"][-1] if gh.issues[702]["comments"] else ""
        report(rc == 0 and len(gh.by_kind("close")) == 2 and not incoda.calls
               and "#703" in dup_comment and "duplicate" in dup_comment,
               "duplicate-filings-closed", f"rc={rc}, closes={len(gh.by_kind('close'))}, "
                                           f"dup comment tail={dup_comment[:60]!r}")

        # --- a stray directory at the worktree path (no .git) is cleared and
        # re-added, not failed on forever.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        os.makedirs(env.worktree_dir, exist_ok=True)
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(incoda.calls) == 1 and "stray" in out
               and not os.path.exists(env.worktree_dir),
               "worktree-stray-readd", f"rc={rc}, runs={len(incoda.calls)}")

        # --- the lane is not demanded when the records can finish the pile:
        # a greens-only pile closes with an incoda that cannot exist.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s4, make_record(s4, True))

        def boom():
            """A lane demand the test must never reach."""
            raise AssertionError("incoda demanded for a pile that owes no run")

        env.find_incoda = boom
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(gh.by_kind("close")) == 1 and not incoda.calls,
               "greens-only-needs-no-lane", f"rc={rc}, closes={len(gh.by_kind('close'))}")

        # --- --max 0 is the greens-only pass: nothing spendable, no lane
        # demand, owed windows stay open.
        td, env, gh, git, incoda = wire([issue_for(s4, 703, 3)])
        env.find_incoda = boom
        rc, out = run_capture(bot_flow, 0, False, env)
        report(rc == 0 and not incoda.calls and not gh.mutations
               and 703 in gh.open_numbers() and "budget reached" in out,
               "max-zero-greens-only", f"rc={rc}, mutations={len(gh.mutations)}")

        # --- a spent budget does not stop already-green windows behind it
        # from closing: closes are free, only runs are budgeted.
        td, env, gh, git, incoda = wire([issue_for(s3, 702, 2), issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s3, make_record(s3, True))
        rc, out = run_capture(bot_flow, 1, False, env)
        report(rc == 0 and len(incoda.calls) == 1 and len(gh.by_kind("close")) == 2,
               "budget-spent-greens-still-close",
               f"rc={rc}, runs={len(incoda.calls)}, closes={len(gh.by_kind('close'))}")

        # --- an excluded filing is named in the trail, not only the console:
        # a verdict outside the failing window's history proves nothing.
        td, env, gh, git, incoda = wire([issue_for(s2, 702, 2), issue_for(s4, 703, 3)])
        merge_guard.write_record(td, s4, make_record(s4, False))
        rc, out = run_capture(bot_flow, 5, False, env)
        trail = gh.issues[703]["comments"][-1] if gh.issues[703]["comments"] else ""
        report(rc == 0 and not incoda.calls
               and f"excluded from the bisect: #702 `{s2[:SHA7]}` "
                   "(not an ancestor, or object missing)" in trail
               and gh.by_kind("label") == [("label", 703, CULPRIT_LABEL)],
               "excluded-filing-in-trail", f"rc={rc}, runs={len(incoda.calls)}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    return 1 if failed else 0


def main(argv):
    """--self-test replays injected sessions; otherwise --max and --dry-run
    over the live pile. Anything unrecognised prints this docstring and
    refuses, like the guard's argument handling."""
    if "--self-test" in argv:
        return self_test()
    max_runs = 1
    dry_run = "--dry-run" in argv
    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--max":
            if i + 1 >= len(argv) or not argv[i + 1].isdigit():
                print(__doc__)
                return 2
            max_runs = int(argv[i + 1])
            i += 2
            continue
        if a.startswith("--max="):
            v = a.split("=", 1)[1]
            if not v.isdigit():
                print(__doc__)
                return 2
            max_runs = int(v)
            i += 1
            continue
        if a == "--dry-run":
            i += 1
            continue
        print(__doc__)
        return 2
    return bot_flow(max_runs=max_runs, dry_run=dry_run)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
