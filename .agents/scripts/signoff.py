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
amend or rebase - but the rerun is usually cheap: each leg also has a
content digest over everything it consumes (leg_cache.py), and a leg
whose digest already has a green, observed by any earlier run in any
worktree of this repo, is CARRIED into the new record instead of run.
An amended message, a rebase onto a base that only moved docs, and a
branch that carries the same src/ as the nightly's last green all carry
the Zig leg. A carried step is marked in the record and printed with the
commit its green was observed at; `--no-cache` runs every leg regardless
and records what it observes over the greens it did not trust.

Each record also carries `base`, the merge base with origin/windows at
the time the run was taken. The merge guard (#969) reads it to measure
what moved on the branch between the run and the merge, so a green record
whose window has since moved can merge anyway with the risk filed instead
of re-gating inline. Records written before this field existed have no
base; downstream readers must treat that as base-unknown and re-estimate,
never as "the window did not move".

Usage: just signoff          (scoped to what changed, greens carried)
       just signoff-full     (every leg, whatever changed)
       just signoff-fresh    (scoped, nothing carried)
       just signoff-docs     (records only when no leg is selected;
                              refuses otherwise, so it may run outside
                              the build lane)
"""

import datetime
import json
import os
import re
import subprocess
import sys
import tempfile
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402
import leg_cache  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BASE_REF = "origin/windows"

def run(cmd, **kw):
    return leg_cache.run(REPO_ROOT, *cmd, **kw)


def merge_base():
    out = run(["git", "merge-base", "HEAD", BASE_REF])
    return out.stdout.strip() if out.returncode == 0 else None


def changed_paths(base):
    out = run(["git", "diff", "--name-only", f"{base}...HEAD"])
    if out.returncode != 0:
        return None
    return sorted(p.strip().replace("\\", "/") for p in out.stdout.splitlines() if p.strip())


def _just_blob(rev):
    """The justfile's exact bytes at `rev`, or None. Direct subprocess
    rather than run(): run() decodes as text, and a lone carriage return
    must survive into the file just reads, because just's lexer treats it
    differently from a newline."""
    try:
        p = subprocess.run(["git", "-C", REPO_ROOT, "show", f"{rev}:justfile"],
                           capture_output=True, timeout=leg_cache.GIT_TIMEOUT)
    except (subprocess.TimeoutExpired, OSError):
        return None
    return p.stdout if p.returncode == 0 else None


# Keys just's JSON dump may carry that name where the file was read from,
# not what it means: `source` is the path of the file that was dumped, and
# the top-level `doc` is the comment above the default recipe. The position
# keys are not in today's dump but cost nothing to drop if a future just
# adds them.
_WHERE_KEYS = {"source", "doc"}
_POSITION_KEYS = {"line", "col", "position", "offset"}
# Dump sections keyed by author-chosen names, where a recipe or a variable
# may legitimately be called `line`: the position keys are stripped from
# the values inside these maps, never from their keys.
_NAME_KEYED = {"assignments", "aliases", "recipes", "groups", "modules", "unexports"}


def _strip_where(value, name_keyed=False):
    if isinstance(value, dict):
        out = {}
        for k, v in value.items():
            if not name_keyed and k in _POSITION_KEYS:
                continue
            out[k] = _strip_where(v, name_keyed=k in _NAME_KEYED)
        return out
    if isinstance(value, list):
        return [_strip_where(v) for v in value]
    return value


def _recipe_semantics(recipe):
    """A recipe's dump with comment-bearing fields removed: `doc` (the doc
    comment above the recipe) and body lines that are nothing but a
    comment, and only in a recipe just parses line by line on this host.
    A script recipe's body is text an interpreter reads, where a `#` line
    can be payload, so nothing is stripped there but the doc."""
    if not isinstance(recipe, dict):
        return recipe
    r = {k: v for k, v in recipe.items() if k != "doc"}
    body = r.get("body")
    if isinstance(body, list) and not r.get("shebang"):
        r["body"] = [line for line in body
                     if not (isinstance(line, list) and len(line) == 1
                             and isinstance(line[0], str)
                             and line[0].lstrip().startswith("#"))]
    return r


_BRACKET_LINE = re.compile(r"^\[([^\]]*)\]\s*(?:#.*)?$")
_PLATFORMS = frozenset(
    ("windows", "unix", "linux", "macos", "freebsd", "openbsd", "netbsd", "android",
     "dragonfly")
)


def _attr_items(content):
    """The comma-separated items of an attribute line's brackets, or None
    when the content is not a plain list (a string value, a function
    call). just accepts any spacing around the commas."""
    items = [item.strip() for item in content.split(",")]
    # just accepts a zero-argument attribute in call syntax, so [unix()]
    # gates exactly like [unix]; read both spellings as the one gate.
    items = [item[:-2] if item.endswith("()") else item for item in items]
    return items if all(items) else None


def _canonical_attr(line):
    """The canonical form of a column-0 attribute line, or None when the
    line is not one: items sorted, spacing and any trailing comment gone.
    `[private, unix]` and `[unix , private]` canonicalize the same,
    because just reads them the same. A list carrying a quoted string is
    kept as written: inside the quotes a comma or a space is an
    argument's text, not spelling."""
    m = _BRACKET_LINE.match(line)
    if m is None:
        return None
    content = m.group(1)
    if any(q in content for q in "\"'"):
        # Quote-bearing content compares verbatim: a comma or a space
        # inside a string is an argument's text, not spelling.
        return "[" + content + "]"
    items = _attr_items(content)
    if items is None:
        return None
    return "[" + ", ".join(sorted(items)) + "]"


def _gates_platform(line):
    """True when an attribute line's list names a platform, which hides
    the recipe that follows it from every host it does not match.

    Quote-bearing content counts as gating too: the naive comma split
    cannot find the platform name inside such a line, and a line that
    turns out not to gate names a recipe the dump can see, so treating it
    as gating only adds coverage."""
    m = _BRACKET_LINE.match(line)
    if m is None:
        return False
    content = m.group(1)
    if any(q in content for q in "\"'"):
        return True
    items = _attr_items(content)
    return items is not None and any(i in _PLATFORMS for i in items)


def _normalized_region(lines):
    """A region's lines reduced only by what cannot change meaning: the
    carriage return of a CRLF ending, trailing blank lines, and the
    spelling, order and trailing comment of attribute lines, whose quoted
    content stays as written. Everything else counts, comments included:
    in a definition this host cannot parse, nothing can tell a comment
    from an interpreter's payload."""
    out = []
    for raw in lines:
        line = raw[:-1] if raw.endswith("\r") else raw
        canonical = _canonical_attr(line)
        if canonical is not None:
            out.append(canonical)
            continue
        out.append(line)
    while out and not out[-1]:
        out.pop()
    return "\n".join(out)


def _variant_regions(text):
    """(regions, suspicious) for a justfile. regions maps a recipe name to
    its platform-gated regions: from a column-0 attribute line whose list
    names a platform, through any stacked attribute lines, the recipe
    header and the body that follow. suspicious is True when a platform
    attribute is followed by a header line this scanner cannot read, or an
    attribute line appears that this scanner cannot read at all: the
    region is real (the host dump cannot see it) but its name or its gate
    is unknown, so the caller must force every leg rather than guess.

    The dump shows each recipe in the definition valid on the host that
    dumped it, so these regions are the one part of the file it cannot
    see. The scan is lexical and deliberately dumb; the normalization is
    as blind as it can be while keeping every real edit."""
    regions = {}
    suspicious = False
    lines = text.split("\n")
    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("[") and _canonical_attr(line) is None:
            # An attribute line this scanner cannot read may carry a
            # platform gate it cannot see; distrust it rather than skip.
            suspicious = True
        if not line or not _gates_platform(line):
            i += 1
            continue
        start = i
        while start > 0 and lines[start - 1].startswith("["):
            start -= 1
        i += 1
        header = None
        while i < len(lines):
            line = lines[i]
            if line.startswith("["):
                i += 1
                continue
            if line and not line[0].isspace():
                m = gate_scope.RECIPE_HEADER.match(line)
                header = m.group(1) if m else None
                if header is None:
                    suspicious = True
                i += 1
            break
        if header is None:
            continue
        while i < len(lines) and (not lines[i] or lines[i][0].isspace()):
            i += 1
        regions.setdefault(header, []).append(_normalized_region(lines[start:i]))
    return regions, suspicious


def _just_semantics(path):
    """just's own reading of the justfile at `path`, or None when just
    cannot load it. `--dump` parses the file without executing any of it,
    so a justfile that just refuses to load, or a comment that changes
    what a recipe is (a shebang no longer first, a continuation split),
    is a difference this sees and a line reader cannot."""
    try:
        out = run(["just", f"--justfile={path}", "--dump", "--dump-format", "json"])
    except Exception:  # noqa: BLE001 - no just on PATH, or it could not spawn
        return None
    if out.returncode != 0:
        return None
    try:
        return json.loads(out.stdout)
    except ValueError:
        return None


def _compared_semantics(sem):
    """A dump reduced to what a change in meaning can touch: where-keys and
    position keys gone, comment fields gone, per recipe."""
    sem = {k: _strip_where(v, name_keyed=k in _NAME_KEYED)
           for k, v in sem.items() if k not in _WHERE_KEYS}
    recipes = sem.get("recipes")
    if isinstance(recipes, dict):
        sem["recipes"] = {name: _recipe_semantics(r) for name, r in recipes.items()}
    return sem


def justfile_legs(base):
    """Legs whose meaning a justfile edit could have changed.

    Editing the recipe a leg runs through invalidates that leg only; a
    change outside every recipe (a setting, an assignment) can reach any
    of them and forces all. Adding an unrelated recipe forces nothing.
    Comment lines and doc comments are free where just parses them as
    comments; a script's body and a platform region keep every line,
    because there a `#` line can be payload; a comment at the end of a
    code line counts. A guard that demands an hour of tests for a typo
    fix is the kind that gets switched off.

    Both versions are read by just itself (`--dump`, which parses without
    executing) and compared as JSON, comment fields stripped first. Every
    earlier miss of this function came from a private reading of the file:
    `git diff` output shaped by the reader's config, line numbers mapped
    back onto the file, comments treated as inert when just's grammar
    gives some of them meaning. just is the definition of what the file
    means, so just is the parser.

    The one part of the file the dump cannot see is a recipe defined once
    per platform, which appears in the definition valid on this host only;
    those regions are compared from the raw text, every line counting but
    the trailing blanks and the spelling of readable attribute lines (a
    list carrying a quoted string compares as written). A
    missing justfile on either side, one just cannot load, or a gated
    recipe whose attribute or header line this scanner cannot read, forces
    every leg.
    """
    every = set(gate_scope.ALL_LEGS)
    old_blob = _just_blob(base)
    new_blob = _just_blob("HEAD")
    if old_blob is None or new_blob is None:
        return every
    with tempfile.TemporaryDirectory() as td:
        # Separate subdirectories, so an import in one version cannot
        # resolve the other version's file.
        old_path = os.path.join(td, "old", "justfile")
        new_path = os.path.join(td, "new", "justfile")
        for path, blob in ((old_path, old_blob), (new_path, new_blob)):
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(blob)
        old_sem = _just_semantics(old_path)
        new_sem = _just_semantics(new_path)
    if old_sem is None or new_sem is None:
        return every
    old_sem = _compared_semantics(old_sem)
    new_sem = _compared_semantics(new_sem)
    legs = set()
    if old_sem != new_sem:
        for key in (set(old_sem) | set(new_sem)) - {"recipes"}:
            if old_sem.get(key) != new_sem.get(key):
                return every
        old_recipes = old_sem.get("recipes") or {}
        new_recipes = new_sem.get("recipes") or {}
        for name in set(old_recipes) | set(new_recipes):
            if old_recipes.get(name) != new_recipes.get(name):
                legs.update(gate_scope.RECIPE_LEGS.get(name, ()))
    old_regions, old_suspicious = _variant_regions(old_blob.decode("utf-8", "surrogateescape"))
    new_regions, new_suspicious = _variant_regions(new_blob.decode("utf-8", "surrogateescape"))
    if old_suspicious or new_suspicious:
        return every
    for name in set(old_regions) | set(new_regions):
        if old_regions.get(name) != new_regions.get(name):
            legs.update(gate_scope.RECIPE_LEGS.get(name, ()))
    return legs


def scoped_legs(base, paths):
    """(legs, justfile_legs) a path list needs against `base`. The single
    place a path list becomes legs, so every caller that scopes a list,
    whatever produced it, applies the same rules, the justfile's
    per-recipe reading included."""
    jf = sorted(justfile_legs(base)) if "justfile" in paths else []
    return gate_scope.required_legs(paths, justfile_legs=jf), jf


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
    legs, jf = scoped_legs(base, paths)
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
    common = leg_cache.common_dir(REPO_ROOT)
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
        carried = sum(1 for s in record["steps"].values() if s.get("carried"))
        if record["pass"]:
            state, desc = "success", (
                f"PASS {len(legs)} leg(s) in {total}s"
                + (f" ({carried} carried)" if carried else "")
                + f": {', '.join(legs)}")
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


def carried_step(lp):
    """The step a carried leg contributes to the record: green, with the
    provenance of the green it stands on, so a reader can tell a run from a
    carry and see which commit and which observer produced the evidence."""
    rec = lp.record or {}
    return {
        "rc": 0,
        # This run spent nothing on the leg; the original cost rides along
        # so the saving can be read, but the run's own total stays honest.
        "seconds": 0,
        "saved_seconds": rec.get("seconds", 0),
        "carried": True,
        "digest": lp.digest,
        "green_at": rec.get("head"),
        "origin": rec.get("origin"),
        "recorded_at": rec.get("recorded_at"),
    }


def run_legs(legs, plans, ctx, runner):
    """Run or carry each leg; returns (results, ok). `plans` maps leg name
    to its LegPlan (None, or a None `ctx`, disables carrying and
    recording), `runner(cmd)` returns an exit code. A green run is
    recorded for its digest through the record-time guards in leg_cache,
    which refuse when HEAD, the tree or the toolchain moved while the leg
    ran."""
    results = {}
    ok = True
    if ctx is None:
        plans = None
    for name in legs:
        cmd = list(gate_scope.LEG_COMMANDS[name])
        lp = plans.get(name) if plans else None
        if lp is not None and lp.carried:
            results[name] = carried_step(lp)
            print(f"signoff: carried {name}: {lp.why}", flush=True)
            continue
        print(f"signoff: running {name}: {' '.join(cmd)}"
              + (f" ({lp.why})" if lp is not None else ""), flush=True)
        start = time.monotonic()
        rc = runner(cmd)
        duration = round(time.monotonic() - start, 1)
        results[name] = {"rc": rc, "seconds": duration}
        if lp is not None:
            results[name]["digest"] = lp.digest
        print(f"signoff: {name} -> rc={rc} ({duration}s)")
        if rc != 0:
            ok = False
        elif lp is not None:
            refused = leg_cache.record_green(REPO_ROOT, ctx, lp, " ".join(cmd),
                                             duration, "observed")
            if refused:
                print(f"signoff: {name} green, NOT recorded for reuse: {refused}")
            else:
                print(f"signoff: {name} green recorded for reuse (digest {lp.digest[:8]})")
    return results, ok


def _run_command(cmd):
    try:
        return subprocess.run(cmd, cwd=REPO_ROOT, shell=(os.name == "nt")).returncode
    except FileNotFoundError as e:
        print(f"signoff: could not run {' '.join(cmd)}: {e}")
        return 127


def no_legs_refusal(legs):
    """Why a --no-legs-only run must not proceed, or None when it may.

    AGENTS.md has every signoff that runs a leg wrapped in the build lane.
    A change whose scope selects no leg (a README, a docs page) runs
    nothing heavy, so making it queue for build slots behind an exclusive
    holder costs wall time and protects nothing. --no-legs-only is the
    entry point that may be run without the lane, and this check is what
    keeps it honest: the moment the plan selects any leg, it refuses
    before running or recording anything, so the lane-free form cannot
    become a way to run tests outside the lane.
    """
    if not legs:
        return None
    return (f"the plan selects {', '.join(legs)}, so this signoff runs tests and "
            "belongs in the build lane: incoda run --queue wintty-build "
            '--reason "..." -- just signoff')


def legs_without_renames(base):
    """Legs the change needs when every rename counts as a delete of its
    old path plus an add of its new one, or None if git cannot say.

    `git diff --name-only` (and GitHub's PR file list, which the merge gate
    compares against) report a rename by its new path only, so moving a
    file out of src/ into docs/ plans no leg even though src/ lost a file.
    The split list goes through the same scoping as the plan, so a renamed
    justfile is read as the justfile it was. The lane-free path refuses on
    any of it; the record itself keeps the regular path list, so it stays
    identical to what `just signoff` writes.
    """
    out = run(["git", "diff", "--no-renames", "--name-only", f"{base}...HEAD"])
    if out.returncode != 0:
        return None
    paths = sorted(p.strip().replace("\\", "/") for p in out.stdout.splitlines() if p.strip())
    return scoped_legs(base, paths)[0]


def no_legs_gate(head, legs, base):
    """Every refusal of the lane-free path, in order, or None to proceed:
    an unknown HEAD, a plan with legs, a rename whose old side needs legs,
    a split diff git cannot produce, and a HEAD that moved while the plan
    was computed."""
    if not head:
        return "could not resolve HEAD"
    refused = no_legs_refusal(legs)
    if refused:
        return refused
    both_sides = legs_without_renames(base)
    if both_sides is None:
        return "could not list the changed paths with renames split; run `just signoff` in the build lane"
    if both_sides:
        return "counting each rename's old path, " + no_legs_refusal(both_sides)
    now = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    if now != head:
        return f"HEAD moved from {head[:10]} to {now[:10]} while planning; rerun"
    return None


# Flags whose handling exits before main() is reached, plus --full, which
# selects every leg. None of them combines with the lane-free path.
NO_LEGS_CONFLICTS = ("--full", "--plan", "--debt", "--post", "--defer", "--settle")


def no_legs_conflict(argv):
    """The first argument that cannot go with --no-legs-only, or None. A
    misspelling such as `--no-legs-only=1` counts: otherwise it is ignored
    and the run turns into a plain signoff that runs its legs."""
    misspelt = next((a for a in argv if a.startswith("--no-legs-only") and a != "--no-legs-only"), None)
    if misspelt:
        return misspelt
    if "--no-legs-only" not in argv:
        return None
    return next((f for f in NO_LEGS_CONFLICTS if f in argv), None)


def main(argv):
    full = "--full" in argv
    no_cache = "--no-cache" in argv
    no_legs_only = "--no-legs-only" in argv
    head = run(["git", "rev-parse", "HEAD"]).stdout.strip()
    dirty = run(["git", "status", "--porcelain"]).stdout.strip()
    if dirty:
        print("signoff: working tree is dirty; commit first so the record vouches for the exact code under review.")
        print(dirty)
        return 1

    legs, paths, jf, reason, base = plan(full)
    print(f"signoff: {reason}")
    print(f"signoff: legs: {', '.join(legs) if legs else '(none needed)'}")

    if no_legs_only:
        refused = no_legs_gate(head, legs, base)
        if refused:
            print(f"signoff: NOT recorded: {refused}")
            return 3

    plans, ctx = None, None
    if legs:
        # The cache is an optimisation on top of the ladder; a store or
        # probe problem must never stop the legs from running. A fresh run
        # still plans, so that what it observes replaces the green it was
        # asked not to trust.
        try:
            ctx = leg_cache.Context(REPO_ROOT, legs=legs)
            plans = leg_cache.build_plan(REPO_ROOT, legs, ctx)
            if no_cache:
                for lp in plans.values():
                    if lp.carried:
                        lp.carried, lp.why = False, "fresh run requested; " + lp.why
        except Exception as e:  # noqa: BLE001 - degrade to no carrying
            print(f"signoff: leg cache unavailable, every leg runs: {e}")
            plans, ctx = None, None
    results, ok = run_legs(legs, plans, ctx, _run_command)

    common = leg_cache.common_dir(REPO_ROOT)
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
    why_not = settle_blocker(legs, results, ok, contains_base())
    if why_not is None:
        entries = gate_scope.load_ledger(outdir)
        if entries:
            settle(f"full ladder green at {head[:10]}")
    else:
        if ok and set(legs) == set(gate_scope.ALL_LEGS):
            print(f"signoff: full ladder green but the ledger is not settled: {why_not}")
        report_debt()
    return 0 if ok else 1


def contains_base():
    """True when origin/windows is an ancestor of HEAD, which is where the
    merges the ledger vouches for live."""
    return run(["git", "merge-base", "--is-ancestor", BASE_REF, "HEAD"]).returncode == 0


def settle_blocker(legs, results, ok, has_base):
    """Why this run may not settle the deferral ledger, or None when it
    may. Deferred merges landed on origin/windows, so only a green over
    every leg on a branch that contains them is evidence about them; a
    branch behind the base can carry the Zig leg from the nightly and go
    green in seconds without containing the code that was deferred. A
    carried leg counts, since its digest names the same inputs, unless
    the green it stands on was asserted by a person rather than observed
    by a run."""
    if not ok:
        return "the ladder is red"
    if set(legs) != set(gate_scope.ALL_LEGS):
        return "not every leg ran"
    if not has_base:
        return f"HEAD does not contain {BASE_REF}"
    asserted = sorted(n for n, s in results.items()
                      if s.get("carried") and s.get("origin") == "asserted")
    if asserted:
        return f"carried on asserted greens: {', '.join(asserted)}"
    return None


def self_test():
    failed = False

    def report(ok, label, detail=""):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}{': ' + detail if detail else ''}")

    # justfile_legs: just's own dump of both versions decides. The seam is
    # faked (_just_blob / _just_semantics), so these cases pin the decision
    # rules; the real oracle is exercised against real git and real just
    # outside the self-test. A None on either side is a justfile just
    # cannot load, and that selects every leg.
    every = set(gate_scope.ALL_LEGS)
    zig = set(gate_scope.RECIPE_LEGS["test"])
    win = set(gate_scope.RECIPE_LEGS["test-win"])

    def sem(recipes=None, **top):
        base = {"source": "somewhere", "settings": {}, "assignments": {},
                "recipes": recipes if recipes is not None else {}}
        base.update(top)
        return base

    def with_justfile_reading(old_blob, new_blob, old_sem, new_sem):
        saved_blob, saved_sem = globals()["_just_blob"], globals()["_just_semantics"]

        def fake_blob(rev):
            return new_blob if rev.startswith("HEAD") else old_blob

        def fake_sem(path):
            return {"old": old_sem, "new": new_sem}["old" if "old" in path else "new"]
        try:
            globals()["_just_blob"], globals()["_just_semantics"] = fake_blob, fake_sem
            return justfile_legs("b" * 40)
        finally:
            globals()["_just_blob"], globals()["_just_semantics"] = saved_blob, saved_sem

    same = sem({"test": {"body": ["a"]}, "hello": {}})
    oracle_cases = [
        ("old-blob-missing", None, b"x", None, None, every),
        ("new-blob-missing", b"x", None, None, None, every),
        ("unparsable-old", b"x", b"y", None, sem(), every),
        ("unparsable-new", b"x", b"y", sem(), None, every),
        ("equal", b"x", b"y", same, sem({"test": {"body": ["a"]}, "hello": {}}), set()),
        ("source-path-stripped", b"x", b"y", sem({"test": {"body": ["a"]}}, source="tmp1"),
         sem({"test": {"body": ["a"]}}, source="tmp2"), set()),
        ("settings-change", b"x", b"y", sem(), sem(settings={"x": 1}), every),
        ("assignment-change", b"x", b"y", sem(), sem(assignments={"a": 1}), every),
        ("default-recipe-change", b"x", b"y", sem(), sem(first="other"), every),
        ("recipe-body", b"x", b"y", sem({"test": {"body": ["a"]}, "hello": {}}), sem({"test": {"body": ["b"]}, "hello": {}}), zig),
        ("doc-comment-change", b"x", b"y", sem({"test": {"doc": "a"}}), sem({"test": {"doc": "b"}}), set()),
        ("body-comment-change", b"x", b"y", sem({"test": {"body": [["# note"], ["zig build test"]]}}), sem({"test": {"body": [["zig build test"]]}}), set()),
        ("shebang-flip", b"x", b"y", sem({"test": {"shebang": True, "body": [["#!/bin/sh"]]}}), sem({"test": {"shebang": False, "body": [["#!/bin/sh"]]}}), zig),
        ("script-comment-line-counts", b"x", b"y", sem({"test": {"shebang": True, "body": [["#!/bin/sh"], ["# payload"]], "doc": "d"}}), sem({"test": {"shebang": True, "body": [["#!/bin/sh"], ["# payload 2"]], "doc": "d"}}), zig),
        ("script-interpreter-line-counts", b"x", b"y", sem({"test": {"shebang": True, "body": [["#!/bin/sh"], ["echo a"]]}}), sem({"test": {"shebang": True, "body": [["#!/usr/bin/env python3"], ["echo a"]]}}), zig),
        ("assignment-named-line", b"x", b"y", sem(assignments={"line": "1"}), sem(assignments={"line": "2"}), every),
        ("recipe-added", b"x", b"y", sem({"hello": {}}), sem({"hello": {}, "test-win": {}}), win),
        ("recipe-removed", b"x", b"y", sem({"test": {}, "hello": {}}), sem({"hello": {}}), zig),
        ("unrelated-recipe-only", b"x", b"y", sem({"hello": {"body": ["a"]}}), sem({"hello": {"body": ["b"]}}), set()),
        ("position-keys-stripped", b"x", b"y", sem({"test": {"body": ["a"], "line": 1}}), sem({"test": {"body": ["a"], "line": 9}}), set()),
    ]
    for label, ob, nb, os_, ns_, expect in oracle_cases:
        got = with_justfile_reading(ob, nb, os_, ns_)
        report(got == expect, f"justfile-oracle-{label}", str(sorted(got)))

    # Platform regions are compared from the raw text, because the host's
    # dump shows only this host's definition of a recipe defined once per
    # platform. Same dumps, different blobs.
    same_sem = sem({"test": {"body": ["a"]}})
    variant_blobs = [
        ("variant-body-edited", b"no regions", b"[unix]\ntest-win:\n    echo a\n", win),
        ("variant-added", b"no regions", b"[linux]\ntest:\n    echo a\n", zig),
        ("variant-unmapped", b"no regions", b"[unix]\nhello:\n    echo a\n", set()),
        ("variant-comment-counts", b"[unix]\ntest-win:\n    echo a\n", b"[unix]\ntest-win:\n    # note\n    echo a\n", win),
        ("variant-attr-spelling-free", b"[unix] # note\ntest-win:\n    echo a\n", b"[ unix ]\ntest-win:\n    echo a\n", set()),
        ("variant-attr-flip-counts", b"[unix]\ntest-win:\n    echo a\n", b"[linux]\ntest-win:\n    echo a\n", win),
        ("variant-comma-attr-edited", b"[private, unix]\ntest-win:\n    echo a\n", b"[private, unix]\ntest-win:\n    echo b\n", win),
        ("variant-comma-attr-order-free", b"[private, unix]\ntest-win:\n    echo a\n", b"[unix , private]\ntest-win:\n    echo a\n", set()),
        ("variant-attr-above-removed", b"[private]\n[unix]\ntest-win:\n    echo a\n", b"[unix]\ntest-win:\n    echo a\n", win),
        ("variant-stacked-attrs-edited", b"[linux]\n[unix]\ntest-win:\n    echo a\n", b"[linux]\n[unix]\ntest-win:\n    echo b\n", win),
        ("variant-unreadable-header-suspicious", b'[unix]\nrec x="a\\"b":\n    echo a\n', b'[unix]\nrec x="a\\"b":\n    echo b\n', every),
        ("variant-inner-blank-counts", b"[unix]\ntest-win:\n    echo a\n\n    echo b\n", b"[unix]\ntest-win:\n    echo a\n    echo b\n", win),
        ("variant-quoted-content-verbatim", b'[script("a,b"), unix]\ntest-win:\n    echo a\n', b'[script("a, b"), unix]\ntest-win:\n    echo a\n', win),
        ("variant-unreadable-attr-suspicious", b'[unix]\ntest-win:\n    echo a\n', b'[doc("a]b"), unix]\ntest-win:\n    echo b\n', every),
        ("variant-mixed-quoted-gate-edited", b'[script("a"), unix]\ntest-win:\n    echo a\n', b'[script("a"), unix]\ntest-win:\n    echo b\n', win),
        ("variant-single-quoted-verbatim", b"[doc('a,b'), unix]\ntest-win:\n    echo a\n", b"[doc('a, b'), unix]\ntest-win:\n    echo a\n", win),
        ("variant-quoted-empty-fragment-edited", b'[script("a,,b"), unix]\ntest-win:\n    echo a\n', b'[script("a,,b"), unix]\ntest-win:\n    echo b\n', win),
        ("variant-single-quoted-empty-fragment-edited", b"[script('a,,b'), unix]\ntest-win:\n    echo a\n", b"[script('a,,b'), unix]\ntest-win:\n    echo b\n", win),
        ("variant-freebsd-edited", b"[freebsd]\ntest-win:\n    echo a\n", b"[freebsd]\ntest-win:\n    echo b\n", win),
        ("variant-android-comma-edited", b"[private, android]\ntest-win:\n    echo a\n", b"[private, android]\ntest-win:\n    echo b\n", win),
        ("variant-dragonfly-edited", b"[dragonfly]\ntest-win:\n    echo a\n", b"[dragonfly]\ntest-win:\n    echo b\n", win),
        ("variant-call-form-gate-edited", b"[unix()]\ntest-win:\n    echo a\n", b"[unix()]\ntest-win:\n    echo b\n", win),
        ("variant-call-form-spelling-free", b"[unix()]\ntest-win:\n    echo a\n", b"[unix]\ntest-win:\n    echo a\n", set()),
        ("variant-attr-spelled-edited", b"[unix]\t# note\ntest-win:\n    echo a\n", b"[unix]\t# note\ntest-win:\n    echo b\n", win),
        ("variant-macos-arm", b"no regions", b"[macos]\ntest:\n    echo a\n", zig),
        ("variant-crlf-free", b"[unix]\ntest-win:\n    echo a\n", b"[unix]\r\ntest-win:\r\n    echo a\r\n", set()),
        ("variant-shebang-counts", b"[unix]\ntest-win:\n    echo a\n", b"[unix]\ntest-win:\n    #!/bin/sh\n    echo a\n", win),
        ("variant-indent-noise-ignored", b"[unix]\ntest-win:\n    echo a\n\n", b"[unix]\ntest-win:\n    echo a\n", set()),
    ]
    for label, ob, nb, expect in variant_blobs:
        got = with_justfile_reading(ob, nb, same_sem, sem({"test": {"body": ["a"]}}))
        report(got == expect, f"justfile-variant-{label}", str(sorted(got)))
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

    # run_legs: a carried leg is a green step that names its evidence and
    # runs nothing; a leg the cache cannot carry runs; a red run stays red
    # and is never recorded. The runner and the cache are both fakes here,
    # so this proves the glue, not the legs.
    calls = []

    def fake_runner(cmd):
        calls.append(cmd[-1])
        return 1 if cmd[-1] == "test-win" else 0

    green = leg_cache.LegPlan("zig-tests", "d" * 64, {"leg": "zig-tests"}, True,
                              "green at abcdef12", {"head": "abcdef12" + "0" * 32,
                                                    "seconds": 2400, "origin": "observed",
                                                    "recorded_at": "2026-09-05T00:00:00+00:00"})
    miss = leg_cache.LegPlan("windows-tests", "e" * 64, {"leg": "windows-tests"}, False,
                             "no green record")
    # A stand-in context: the only leg that goes green here has no plan,
    # so nothing reaches the record path and the store is never touched.
    import types
    fake_ctx = types.SimpleNamespace(root=REPO_ROOT, head="0" * 40, env={})
    results, ok = run_legs(["zig-tests", "windows-tests", "gates-selftest"],
                           {"zig-tests": green, "windows-tests": miss}, fake_ctx, fake_runner)
    report(calls == ["test-win", "gates-selftest"], "run-legs-carries-and-runs", str(calls))
    report(results["zig-tests"].get("carried") is True and results["zig-tests"]["rc"] == 0
           and results["zig-tests"]["green_at"].startswith("abcdef12")
           and results["zig-tests"]["seconds"] == 0
           and results["zig-tests"]["saved_seconds"] == 2400,
           "run-legs-carried-step-shape", json.dumps(results["zig-tests"]))
    # The deferral ledger settles only on a full green that contains the
    # base and stands on observed greens.
    every = list(gate_scope.ALL_LEGS)
    green_all = {leg: {"rc": 0} for leg in every}
    report(settle_blocker(every, green_all, True, True) is None, "settle-full-green")
    report("red" in settle_blocker(every, green_all, False, True), "settle-refuses-red")
    report("not every" in settle_blocker(every[:2], green_all, True, True), "settle-refuses-scoped")
    report("does not contain" in settle_blocker(every, green_all, True, False), "settle-refuses-behind-base")
    with_asserted = dict(green_all, **{"zig-tests": {"rc": 0, "carried": True, "origin": "asserted"}})
    report("asserted" in settle_blocker(every, with_asserted, True, True), "settle-refuses-asserted-carry")
    with_observed = dict(green_all, **{"zig-tests": {"rc": 0, "carried": True, "origin": "observed"}})
    report(settle_blocker(every, with_observed, True, True) is None, "settle-accepts-observed-carry")
    report(results["windows-tests"]["rc"] == 1 and not ok
           and results["windows-tests"].get("digest") == "e" * 64,
           "run-legs-red-stays-red", json.dumps(results["windows-tests"]))
    report("carried" not in results["gates-selftest"], "run-legs-uncached-leg-plain")
    results, ok = run_legs(["zig-tests"], None, None, fake_runner)
    report(ok and calls[-1] == "test" and "carried" not in results["zig-tests"],
           "run-legs-no-cache-runs", str(calls))
    results, ok = run_legs(["zig-tests"], {"zig-tests": green}, None, fake_runner)
    report(ok and calls[-1] == "test" and "carried" not in results["zig-tests"],
           "run-legs-plans-without-context-run", str(calls))

    # no_legs_refusal: the lane-free entry point proceeds only on an empty
    # leg list. Any selected leg, a single one or the whole ladder (which
    # is also what a failed scope computation falls back to), refuses and
    # names the lane form to use instead.
    report(no_legs_refusal([]) is None, "no-legs-proceeds", "an empty plan may record")
    one = no_legs_refusal(["gates-selftest"])
    report(one is not None and "gates-selftest" in one and "wintty-build" in one,
           "no-legs-refuses-one-leg", str(one))
    report(no_legs_refusal(sorted(gate_scope.ALL_LEGS)) is not None,
           "no-legs-refuses-full-ladder", "the fail-closed every-leg plan refuses")

    # main() --no-legs-only: the refusal must come before the leg cache, the
    # legs and the record. plan, git, the runner, the status post and the
    # record dir are stubbed, so this exercises main's ordering, not git.
    import tempfile
    import types as _types
    mod = sys.modules[__name__]
    stubbed = ("plan", "run", "run_legs", "post_github_status", "report_debt")
    saved = {n: getattr(mod, n) for n in stubbed}
    saved_common, saved_ctx = leg_cache.common_dir, leg_cache.Context

    def drive(legs, split_paths, heads, argv=("--no-legs-only",), split_rc=0):
        state = {"legs_calls": 0, "ctx": 0, "heads": list(heads)}
        with tempfile.TemporaryDirectory() as td:
            def fake_run(cmd, **kw):
                if cmd[:2] == ["git", "rev-parse"]:
                    h = state["heads"].pop(0) if len(state["heads"]) > 1 else state["heads"][0]
                    return _types.SimpleNamespace(returncode=0, stdout=h + "\n")
                if cmd[:2] == ["git", "diff"] and "--no-renames" in cmd:
                    return _types.SimpleNamespace(returncode=split_rc, stdout="\n".join(split_paths))
                # A justfile that is gone at HEAD (renamed or deleted) has
                # no blob there.
                if cmd[:2] == ["git", "show"] and cmd[2].startswith("HEAD:"):
                    return _types.SimpleNamespace(returncode=128, stdout="")
                return _types.SimpleNamespace(returncode=0, stdout="")

            def fake_run_legs(ls, plans, ctx, runner):
                state["legs_calls"] += 1
                return {}, True

            def fake_ctx(*a, **k):
                state["ctx"] += 1
                raise RuntimeError("leg cache must not be built")

            mod.plan = lambda full=False: (list(legs), ["README.md"], [], "stub plan", "b" * 40)
            mod.run = fake_run
            mod.run_legs = fake_run_legs
            mod.post_github_status = lambda record: None
            mod.report_debt = lambda: None
            leg_cache.common_dir = lambda root: td
            leg_cache.Context = fake_ctx
            try:
                rc = main(list(argv))
            finally:
                for n, v in saved.items():
                    setattr(mod, n, v)
                leg_cache.common_dir, leg_cache.Context = saved_common, saved_ctx
            records = os.listdir(os.path.join(td, "pr-signoff")) if os.path.isdir(os.path.join(td, "pr-signoff")) else []
        return rc, state, records

    head = "f" * 40
    rc, st, recs = drive(["gates-selftest"], ["README.md"], [head])
    report(rc == 3 and st["legs_calls"] == 0 and st["ctx"] == 0 and not recs,
           "no-legs-main-refuses-before-legs-and-record",
           f"rc={rc} legs_calls={st['legs_calls']} ctx={st['ctx']} records={recs}")
    rc, st, recs = drive([], ["README.md"], [head])
    report(rc == 0 and st["legs_calls"] == 1 and recs == [f"{head}.json"],
           "no-legs-main-records-empty-plan", f"rc={rc} records={recs}")
    rc, st, recs = drive([], ["src/App.zig", "docs/moved.zig"], [head])
    report(rc == 3 and st["legs_calls"] == 0 and not recs,
           "no-legs-main-refuses-rename-out-of-src", f"rc={rc} records={recs}")
    rc, st, recs = drive([], ["README.md"], [head, "e" * 40])
    report(rc == 3 and st["legs_calls"] == 0 and not recs,
           "no-legs-main-refuses-moved-head", f"rc={rc} records={recs}")
    rc, st, recs = drive([], ["justfile", "justfile-notes.md"], [head])
    report(rc == 3 and st["legs_calls"] == 0 and not recs,
           "no-legs-main-refuses-renamed-justfile", f"rc={rc} records={recs}")
    rc, st, recs = drive([], ["README.md"], [head], split_rc=128)
    report(rc == 3 and st["legs_calls"] == 0 and not recs,
           "no-legs-main-refuses-failed-split-diff", f"rc={rc} records={recs}")
    rc, st, recs = drive([], ["README.md"], [""])
    report(rc == 3 and st["legs_calls"] == 0 and not recs,
           "no-legs-main-refuses-unknown-head", f"rc={rc} records={recs}")
    report(no_legs_conflict(["--no-legs-only", "--defer", "why"]) == "--defer"
           and no_legs_conflict(["--no-legs-only", "--full"]) == "--full"
           and no_legs_conflict(["--no-legs-only=1"]) == "--no-legs-only=1"
           and no_legs_conflict(["--no-legs-only"]) is None
           and no_legs_conflict(["--defer", "why"]) is None,
           "no-legs-flag-conflicts", "dispatch flags, --full and misspellings refuse the lane-free path")

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
    conflict = no_legs_conflict(argv)
    if conflict:
        print(f"signoff: {conflict} is not accepted with or in place of --no-legs-only")
        sys.exit(2)
    if "--self-test" in argv:
        sys.exit(self_test())
    if "--plan" in argv:
        legs, paths, jf, reason, base = plan("--full" in argv)
        print(f"reason: {reason}")
        print(f"legs:   {', '.join(legs) if legs else '(none needed)'}")
        print(f"paths:  {len(paths) if paths is not None else 'unknown'}")
        print(f"base:   {base or 'unknown'}")
        if legs and "--no-cache" not in argv:
            try:
                ctx = leg_cache.Context(REPO_ROOT, legs=legs)
                print(leg_cache.render_plan(REPO_ROOT, leg_cache.build_plan(REPO_ROOT, legs, ctx), ctx))
            except Exception as e:  # noqa: BLE001 - the plan is still the answer
                print(f"leg cache unavailable, every leg would run: {e}")
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
