#!/usr/bin/env python3
"""Content-keyed greens for the signoff legs.

`just signoff` scopes its legs by which paths a branch changed, and that
is the right question for a gate. It is the wrong question for cost: the
Zig leg is half an hour, and it is paid again for an amended message, a
rebase onto a base that only moved docs, and every one of the worktrees
on this machine that carry the same src/ under a different branch name.
None of those change what the leg would prove.

This module gives each leg a digest over everything it consumes and a
store of greens keyed by that digest, so a leg whose digest already has a
green is carried instead of run. The digest is derived from the same
tables gate_scope.py uses to scope a signoff (a path that selects a leg
is an input of that leg), which keeps the two from drifting: a rule added
there widens the digest here. What a path rule cannot say - which
justfile recipes a leg runs through, which command signoff runs for it,
which toolchain compiles it, which environment variables its suites read
- is added per leg in leg_inputs().

Fail-closed, the same way gate_scope is: a root entry no rule classifies
goes into EVERY leg's digest, an input that cannot be resolved makes the
leg RUN, and the literal "unresolved" can never equal a hash. Digests are
computed from HEAD tree objects, never the working tree, and a dirty tree
carries nothing and records nothing: a green on files no commit names
must satisfy no future plan. A green is recorded only after re-checking
that HEAD, the tree and the environment are still what the digest named,
because a leg runs for tens of minutes and a relane or a commit mid-run
means it ran against inputs no digest describes. Nothing in the record
path may abort the signoff that just went green: every failure there
degrades to "not recorded" with its reason.

Records live under the git common dir beside the signoff records
(pr-signoff/leg-cache/<machine>/<leg>/<digest>.json), so every worktree
of this repo shares them and a green observed in one carries in all: the
nightly run at origin/windows seeds the zig leg for every branch whose
Zig inputs equal that tip's. Each record names the commit the green was observed
at and how it was established ("observed" by a run, which must hand over
the environment snapshot it ran under, or "asserted" by a human, on their
word); a carried leg is always printed with both.

Usage: python .agents/scripts/leg_cache.py plan [LEG ...]
       python .agents/scripts/leg_cache.py check
       python .agents/scripts/leg_cache.py snapshot FILE
       python .agents/scripts/leg_cache.py record LEG ... --from-sha HEAD
           [--origin observed|asserted] [--seconds N] [--env-snapshot FILE]
           (or --all in place of the leg names)
       python .agents/scripts/leg_cache.py gc
       python .agents/scripts/leg_cache.py --self-test
"""

import datetime
import getpass
import hashlib
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gate_scope  # noqa: E402

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STORE_DIRNAME = "leg-cache"
UNRESOLVED = "unresolved"
# Retention is sized for the traffic the lane can produce: a green is
# recorded only after its leg ran, the Zig leg is half an hour and the
# lane serialises it, so a busy day yields a few dozen greens per leg at
# most, and the one every branch wants - the nightly's, at the current
# origin/windows - is re-recorded each night. 60 keeps a week of churn
# without ever evicting it; 45 days bounds a store that saw no gc.
KEEP_PER_LEG = 60
MAX_AGE_DAYS = 45
# A .tmp is written and renamed within milliseconds; one older than this
# was left by a process that died between the two.
STALE_TMP_SECONDS = 3600
# The probes answer in a second on an idle box; on one running ten
# builds under an AV filter, tens of seconds. Past the timeout the probe
# is unresolved and the leg runs, which is the safe direction. git gets
# longer: it runs on the record path after an hour-long ladder, where a
# stall must not cost the record.
PROBE_TIMEOUT = 60
GIT_TIMEOUT = 300
TESTS_CSPROJ = "windows/Ghostty.Tests/Ghostty.Tests.csproj"

# Which environment probes each leg's digest carries. Over-approximating
# is cheap (a probe is a second) and under-approximating carries a green
# compiled by a toolchain the digest never named. The libghostty DLL is
# deliberately not here: the C# build refuses to start without it, but
# no test loads it (the parity tests read sources and the header), so its
# bytes and its presence say nothing about what a green proved, and a
# fresh worktree without one may carry the leg.
ENV_FOR_LEG = {
    gate_scope.LEG_FMT: ("zig",),
    gate_scope.LEG_ZIG: ("zig", "python"),
    gate_scope.LEG_WIN: ("dotnet",),
    gate_scope.LEG_GATES: ("python", "pwsh"),
    gate_scope.LEG_RELEASE_GATE: ("dotnet", "pwsh"),
}

# Environment variables the suites read that change how much they prove:
# a profile that sets the fuzz iterations to one records weak greens for
# the whole machine unless the value is in the digest. WINTTY_TEST_SEED is
# deliberately NOT here: std.testing.random_seed is read by no test under
# src/ or pkg/, so the seed changes only what Zig re-runs, never what the
# tests compute, and a green the nightly observed under a random seed is
# evidence for the fixed-seed digest every branch carries. The self-test
# holds this table equal to what the test projects actually read.
ENV_VARS_FOR_LEG = {
    gate_scope.LEG_WIN: ("GHOSTTY_FUZZ_ITERATIONS", "WINTTY_MOTION_FUZZ_SEED"),
}
ALL_ENV_VARS = sorted({v for vs in ENV_VARS_FOR_LEG.values() for v in vs})


def _now():
    return datetime.datetime.now(datetime.timezone.utc)


def run(root, *args, timeout=GIT_TIMEOUT):
    """A CompletedProcess for `args` run in `root`, decoded as UTF-8 with
    surrogate escapes rather than the console code page: the justfile
    holds non-ASCII rulers whose bytes the default codec either mangles
    or refuses, and a digest must not depend on which. A timeout comes
    back as exit 124 with empty output, which every caller treats as the
    input being unresolvable."""
    try:
        return subprocess.run(list(args), cwd=root, capture_output=True,
                              encoding="utf-8", errors="surrogateescape",
                              timeout=timeout)
    except subprocess.TimeoutExpired:
        return subprocess.CompletedProcess(list(args), 124, "", "timeout")


def git(root, *args):
    return run(root, "git", *args)


def head_sha(root):
    out = git(root, "rev-parse", "HEAD")
    sha = out.stdout.strip()
    return sha if out.returncode == 0 and re.fullmatch(r"[0-9a-f]{40}", sha) else None


def dirty_lines(root):
    out = git(root, "status", "--porcelain")
    if out.returncode != 0:
        return ["git status failed"]
    return [ln for ln in out.stdout.splitlines() if ln.strip()]


def machine_identity():
    return f"{socket.gethostname()}/{getpass.getuser()}"


def common_dir(root):
    """The absolute git common dir, or None if it cannot be trusted (a
    dying session can kill the git child and leave empty output)."""
    out = git(root, "rev-parse", "--git-common-dir")
    common = out.stdout.strip()
    if out.returncode != 0 or not common:
        return None
    if not os.path.isabs(common):
        common = os.path.join(root, common)
    return common if os.path.isdir(common) else None


def store_root(root):
    """<git-common-dir>/pr-signoff/leg-cache/<machine>, or None."""
    common = common_dir(root)
    if common is None:
        return None
    machine = re.sub(r"[^A-Za-z0-9._-]+", "_", machine_identity().replace("/", "-"))
    return os.path.join(common, "pr-signoff", STORE_DIRNAME, machine)


# --- justfile ----------------------------------------------------------------

_NAME = re.compile(r"[a-zA-Z_][\w-]*")


def parse_justfile(text):
    """(recipes, preamble): recipe name -> (text, deps), and the column-0
    lines that belong to no recipe (settings, assignments). Comments and
    blank lines are dropped on both sides, so a reworded comment moves no
    digest; a shebang is not a comment, it picks the interpreter, so it
    stays. Attribute lines ([windows], [private]) travel with the recipe
    they decorate because they change where and whether it runs, and a
    name defined twice under different attributes keeps both bodies."""
    recipes = {}
    preamble = []
    pending_attrs = []
    current = None
    for raw in text.splitlines():
        stripped = raw.strip()
        if not stripped or (stripped.startswith("#") and not stripped.startswith("#!")):
            continue
        if raw[0].isspace():
            if current is not None:
                recipes[current][0].append(stripped)
            continue
        if stripped.startswith("[") and stripped.endswith("]"):
            pending_attrs.append(stripped)
            current = None
            continue
        m = gate_scope.RECIPE_HEADER.match(raw)
        if m:
            name = m.group(1)
            deps = []
            # A trailing comment on the header is not a dependency list.
            dep_text = re.split(r"(?:^|\s)#", m.group(3), maxsplit=1)[0]
            for tok in re.findall(r"\(([^)]*)\)|(\S+)", dep_text):
                inner = tok[0] if tok[0] else tok[1]
                dm = _NAME.match(inner)
                if dm:
                    deps.append(dm.group(0))
            if name in recipes:
                recipes[name][0].extend([*pending_attrs, stripped])
                recipes[name][1].extend(deps)
            else:
                recipes[name] = ([*pending_attrs, stripped], deps)
            pending_attrs = []
            current = name
            continue
        preamble.append(stripped)
        pending_attrs = []
        current = None
    return {k: ("\n".join(v[0]), v[1]) for k, v in recipes.items()}, "\n".join(preamble)


def leg_recipes(leg, recipes):
    """The recipe names a leg runs through, closed over dependencies.
    Unknown names are returned separately so the caller can fail closed."""
    wanted = [r for r, legs in gate_scope.RECIPE_LEGS.items() if leg in legs]
    seen, missing, queue = [], [], list(wanted)
    while queue:
        name = queue.pop(0)
        if name in seen or name in missing:
            continue
        if name not in recipes:
            missing.append(name)
            continue
        seen.append(name)
        queue.extend(recipes[name][1])
    return sorted(seen), sorted(missing)


# --- inputs --------------------------------------------------------------------

def leg_paths(leg):
    """The tree and blob paths a leg reads according to gate_scope's
    prefix and exact tables. Root entries matched by a suffix rule are
    handled by root_entry_legs, since a suffix names no path."""
    paths = []
    for prefix, legs in gate_scope.PREFIX_RULES:
        if leg in legs:
            paths.append(prefix.rstrip("/"))
    for prefix, _suffix, legs in gate_scope.PREFIX_SUFFIX_RULES:
        if leg in legs:
            paths.append(prefix.rstrip("/"))
    for path, legs in gate_scope.EXACT_RULES.items():
        if leg in legs:
            paths.append(path)
    return sorted(set(paths))


def root_entries(root):
    """(name, kind) for every entry of the HEAD root tree, or None."""
    out = git(root, "ls-tree", "HEAD")
    if out.returncode != 0:
        return None
    entries = []
    for line in out.stdout.splitlines():
        meta, tab, name = line.partition("\t")
        parts = meta.split()
        if tab and len(parts) >= 3:
            entries.append((name, parts[1]))
    return entries


def root_entry_legs(entries):
    """name -> legs for every root entry, as gate_scope routes it. None
    means no rule recognises the entry; gate_scope makes such a path cost
    every leg, and here it goes into every digest, which is the same rule
    in the other direction. A classified entry goes into the digest of
    each leg it routes to, whichever kind of rule matched it, so a root
    file a suffix rule sends to the Zig legs is in their digest even
    though no prefix or exact table names it."""
    out = {}
    for name, kind in entries:
        if name == "justfile":
            # No rule classifies the justfile, and treating it as
            # unclassified would put its whole blob into every digest;
            # its recipes are hashed one by one instead.
            continue
        probe = name + "/" if kind == "tree" else name
        out[name] = gate_scope.legs_for_path(probe)
    return out


def head_hashes(root, paths):
    """Blob/tree hashes at HEAD for `paths`, in one `git cat-file
    --batch-check` call. A path absent at HEAD maps to None: absence is a
    state, not an error. A git that fails or times out maps every path to
    UNRESOLVED, which leg_inputs turns into a problem: a stalled git must
    never hash as "every input absent", a digest another stalled plan
    could match."""
    paths = list(dict.fromkeys(paths))
    if not paths:
        return {}
    try:
        proc = subprocess.run(
            ["git", "cat-file", "--batch-check"], cwd=root, capture_output=True,
            encoding="utf-8", errors="surrogateescape", timeout=PROBE_TIMEOUT,
            input="".join(f"HEAD:{p}\n" for p in paths))
    except subprocess.TimeoutExpired:
        return {p: UNRESOLVED for p in paths}
    if proc.returncode != 0:
        return {p: UNRESOLVED for p in paths}
    lines = proc.stdout.splitlines()
    if len(lines) != len(paths):
        return {p: UNRESOLVED for p in paths}
    out = {}
    for path, line in zip(paths, lines):
        parts = line.split()
        if len(parts) >= 3 and parts[1] in ("blob", "tree"):
            out[path] = parts[0]
        elif line.endswith(" missing"):
            out[path] = None
        else:
            out[path] = UNRESOLVED
    return out


_FILE_DIGESTS = {}


def _sha256_file(path):
    """Content hash of a file, remembered per (path, size, mtime) for the
    life of the process: the zig binary is 170 MB and is probed at plan
    time and again at every record, and it does not change in between."""
    try:
        st = os.stat(path)
        key = (path, st.st_size, st.st_mtime_ns)
        if key in _FILE_DIGESTS:
            return _FILE_DIGESTS[key]
        h = hashlib.sha256()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
    except OSError:
        return None
    _FILE_DIGESTS[key] = h.hexdigest()
    return _FILE_DIGESTS[key]


def _probe(root, cmd):
    """stdout of `cmd` run in `root`, or None when it will not answer.

    The command is passed by its bare name, not the path which() found:
    the zig on PATH is the lane shim, which takes its lane from its own
    argv[0], and which() returns `zig.EXE` on Windows, a lane that does
    not exist. which() is only consulted for presence."""
    if not shutil.which(cmd[0]):
        return None
    try:
        p = run(root, *cmd, timeout=PROBE_TIMEOUT)
    except OSError:
        return None
    return p.stdout.strip() if p.returncode == 0 and p.stdout.strip() else None


def probe_env(root, names):
    """name -> identity string for the requested probes; UNRESOLVED when a
    probe will not answer. The zig identity is the version AND a content
    hash of the binary the shim served: the version alone survives a
    relane, and a green compiled by a different compiler must not carry."""
    out = {}
    for name in sorted(set(names)):
        if name == "zig":
            text = _probe(root, ["zig", "env"])
            exe = re.search(r'\.zig_exe\s*=\s*"((?:[^"\\]|\\.)*)"', text or "")
            ver = re.search(r'\.version\s*=\s*"([^"]*)"', text or "")
            digest = _sha256_file(exe.group(1).replace("\\\\", "\\")) if exe else None
            out[name] = (f"{ver.group(1)} sha256:{digest[:12]}"
                         if ver and digest else UNRESOLVED)
        elif name == "dotnet":
            out[name] = _probe(root, ["dotnet", "--version"]) or UNRESOLVED
        elif name == "pwsh":
            out[name] = _probe(root, ["pwsh", "-NoProfile", "-Command",
                                      "$PSVersionTable.PSVersion.ToString()"]) or UNRESOLVED
        elif name == "python":
            v = sys.version_info
            out[name] = f"{v.major}.{v.minor}.{v.micro}"
        else:
            out[name] = UNRESOLVED
    return out


# Indirection for the environment probes so the self-test can run its
# synthetic repo without spawning zig, dotnet and pwsh; every probe goes
# through here.
PROBE = probe_env


def env_names(legs):
    return sorted({n for leg in legs for n in ENV_FOR_LEG.get(leg, ())})


def env_var_values():
    return {v: os.environ.get(v, "unset") for v in ALL_ENV_VARS}


def csproj_embeds(text):
    """The repo-relative paths a test project embeds from outside its own
    tree: `..\\..\\<path>` includes, literal ones only. They are inputs of
    the Windows leg by construction, so a new embed is in the digest the
    day it lands, whether or not gate_scope's table has caught up."""
    out = []
    for inc in re.findall(r'Include="([^"]+)"', text):
        if not inc.startswith("..\\..\\") or any(c in inc for c in "*/$;"):
            continue
        rel = inc[6:].replace("\\", "/")
        if not rel.startswith(("windows/", "zig-out/")):
            out.append(rel)
    return sorted(set(out))


class Context:
    """Everything a plan reads once and every leg's digest draws from.
    `legs` limits the environment probes to the legs being planned; the
    tree reads are one batch whatever the legs. `env` and `env_vars`
    replace the live environment with a snapshot taken before a run."""

    def __init__(self, root, legs=None, env=None, env_vars=None):
        self.root = root
        self.legs = list(legs or gate_scope.ALL_LEGS)
        self.problems = []
        self.head = head_sha(root)
        if self.head is None:
            self.problems.append("HEAD unresolvable")
        self.dirty = dirty_lines(root)
        self.store = store_root(root)
        entries = root_entries(root)
        self.entries = entries or []
        if entries is None:
            self.problems.append("HEAD root tree unreadable")
        self.root_legs = root_entry_legs(self.entries)
        jf = git(root, "show", "HEAD:justfile")
        if jf.returncode != 0:
            self.problems.append("justfile not readable at HEAD")
            self.recipes, self.preamble = {}, ""
        else:
            self.recipes, self.preamble = parse_justfile(jf.stdout)
        csproj = git(root, "show", f"HEAD:{TESTS_CSPROJ}")
        self.embeds = csproj_embeds(csproj.stdout) if csproj.returncode == 0 else []
        wanted = {"justfile"} | set(self.root_legs) | set(self.embeds)
        for leg in gate_scope.ALL_LEGS:
            wanted.update(leg_paths(leg))
        self.hashes = head_hashes(root, sorted(wanted))
        self.env = dict(env) if env is not None else PROBE(root, env_names(self.legs))
        self.env_vars = dict(env_vars) if env_vars is not None else env_var_values()


def leg_inputs(leg, ctx):
    """The flat mapping a leg's digest is taken over, or (None, problems).
    Keys are named so a moved digest can say which input moved."""
    problems = list(ctx.problems)
    inputs = {"leg": leg, "machine": machine_identity()}

    def tree_input(key, path):
        value = ctx.hashes.get(path)
        if value == UNRESOLVED:
            problems.append(f"{path} unresolved at HEAD (git failed or timed out)")
        inputs[key] = value or "absent"

    for path in leg_paths(leg):
        tree_input(f"path/{path}", path)
    if leg == gate_scope.LEG_WIN:
        for path in ctx.embeds:
            tree_input(f"path/{path}", path)
    for name, legs in sorted(ctx.root_legs.items()):
        if legs is None:
            tree_input(f"unclassified/{name}", name)
        elif leg in legs:
            tree_input(f"path/{name}", name)
    names, missing = leg_recipes(leg, ctx.recipes)
    if missing:
        problems.append(f"justfile recipe(s) not found: {', '.join(missing)}")
    for name in names:
        inputs[f"recipe/{name}"] = hashlib.sha256(
            ctx.recipes[name][0].encode("utf-8", "surrogateescape")).hexdigest()[:16]
    inputs["justfile/preamble"] = hashlib.sha256(
        ctx.preamble.encode("utf-8", "surrogateescape")).hexdigest()[:16]
    if leg == gate_scope.LEG_GATES:
        # The gate scripts' self-tests read the whole justfile (they hold
        # its recipes and settings to their own expectations), so for this
        # leg every recipe is an input, not only the ones it runs through;
        # comments stay out, as for every other leg.
        inputs["justfile/all-recipes"] = hashlib.sha256("\n".join(
            f"{name}\n{ctx.recipes[name][0]}" for name in sorted(ctx.recipes)
        ).encode("utf-8", "surrogateescape")).hexdigest()[:16]
    inputs["command"] = " ".join(gate_scope.LEG_COMMANDS[leg])
    for name in ENV_FOR_LEG.get(leg, ()):
        value = ctx.env.get(name, UNRESOLVED)
        inputs[f"env/{name}"] = value
        if value == UNRESOLVED:
            problems.append(f"env/{name} unresolved")
    for var in ENV_VARS_FOR_LEG.get(leg, ()):
        inputs[f"var/{var}"] = ctx.env_vars.get(var, "unset")
    return (None, problems) if problems else (inputs, [])


def digest_of(inputs):
    payload = json.dumps(inputs, sort_keys=True, separators=(",", ":"))
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


# --- store ---------------------------------------------------------------------

_RECORD_ERRORS = (OSError, ValueError, TypeError, AttributeError)


def record_path(store, leg, digest):
    return os.path.join(store, leg, f"{digest}.json")


def _load_record(path):
    """The parsed record at `path`, or None for one that cannot be read or
    is not a record: a miss, never a carried green."""
    try:
        with open(path, encoding="utf-8") as f:
            rec = json.load(f)
        return rec if isinstance(rec, dict) else None
    except _RECORD_ERRORS:
        return None


def read_record(store, leg, digest):
    rec = _load_record(record_path(store, leg, digest))
    if rec is None or rec.get("verdict") != "green" or rec.get("digest") != digest:
        return None
    return rec


def _replace_with_retry(tmp, dest, attempts=5, pause=0.05):
    """os.replace, retried: on Windows a rename over a file another
    process has open for reading is refused, and a plan that cannot carry
    a leg opens each of that leg's records to say which input moved."""
    for i in range(attempts):
        try:
            os.replace(tmp, dest)
            return
        except PermissionError:
            if i == attempts - 1:
                raise
            time.sleep(pause)


def write_record(store, leg, digest, inputs, head, command, seconds, origin,
                 worktree=""):
    """Atomic (temp + rename in the same dir) so a crash cannot leave a
    half-written file that reads as green."""
    payload = {
        "leg": leg,
        "digest": digest,
        "verdict": "green",
        "origin": origin,
        "head": head,
        "recorded_at": _now().isoformat(timespec="seconds"),
        "command": command,
        "seconds": seconds,
        "worktree": worktree,
        "inputs": inputs,
    }
    os.makedirs(os.path.join(store, leg), exist_ok=True)
    dest = record_path(store, leg, digest)
    tmp = f"{dest}.{os.getpid()}.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)
    try:
        _replace_with_retry(tmp, dest)
    except OSError:
        try:
            os.remove(tmp)
        except OSError:
            pass
        raise
    return dest


def _record_stamp(path):
    """recorded_at of a record file; the epoch for one that will not
    parse or carries no timezone, so gc sweeps it first."""
    epoch = datetime.datetime.min.replace(tzinfo=datetime.timezone.utc)
    rec = _load_record(path)
    try:
        stamp = datetime.datetime.fromisoformat(str((rec or {}).get("recorded_at", "")))
    except ValueError:
        return epoch
    return stamp if stamp.tzinfo is not None else epoch


def gc(store, keep=KEEP_PER_LEG, max_age_days=MAX_AGE_DAYS, now=None, legs=None):
    """Drop records older than max_age_days, then all but the newest
    `keep` per leg, plus temp files left by a process that died between
    write and rename. Returns the number removed. Age is on recorded_at,
    not file mtime, so a copied store keeps its history. A file another
    process holds open stays for the next pass. `legs` limits the sweep
    to the directories that just grew."""
    now = now or _now()
    removed = 0
    if not store or not os.path.isdir(store):
        return 0
    for leg in (legs or os.listdir(store)):
        leg_dir = os.path.join(store, leg)
        if not os.path.isdir(leg_dir):
            continue
        dated = []
        for name in os.listdir(leg_dir):
            path = os.path.join(leg_dir, name)
            if name.endswith(".tmp"):
                try:
                    if time.time() - os.path.getmtime(path) > STALE_TMP_SECONDS:
                        os.remove(path)
                        removed += 1
                except OSError:
                    pass
                continue
            if name.endswith(".json"):
                dated.append((_record_stamp(path), path))
        dated.sort(reverse=True)
        for i, (stamp, path) in enumerate(dated):
            if i >= keep or (now - stamp).days >= max_age_days:
                try:
                    os.remove(path)
                    removed += 1
                except OSError:
                    pass
    return removed


# --- plan ------------------------------------------------------------------------

class LegPlan:
    def __init__(self, leg, digest, inputs, carried, why, record=None):
        self.leg, self.digest, self.inputs = leg, digest, inputs
        self.carried, self.why, self.record = carried, why, record


def _newest_record(store, leg):
    """The most recently written record of a leg, by file mtime: the one
    a plan explains a miss against. One parse, not one per record, and
    an unreadable newest is a plain "no green record"."""
    try:
        with os.scandir(os.path.join(store, leg)) as it:
            files = [(e.stat().st_mtime, e.path) for e in it
                     if e.is_file() and e.name.endswith(".json")]
    except OSError:
        return None
    if not files:
        return None
    return _load_record(max(files)[1])


def _why_moved(inputs, store, leg, digest):
    """Name the inputs that differ from the NEWEST record for the leg, so
    the plan says what moved rather than only that something did."""
    newest = _newest_record(store, leg)
    if not newest:
        return "no green record"
    old = newest.get("inputs") or {}
    moved = [k for k in sorted(set(old) | set(inputs)) if old.get(k) != inputs.get(k)]
    since = f" since {str(newest.get('head'))[:8]}"
    if moved:
        return f"inputs moved{since}: {', '.join(moved)}"
    return f"no record for digest {digest[:8]}{since}"


def build_plan(root, legs=None, ctx=None):
    legs = list(legs or gate_scope.ALL_LEGS)
    ctx = ctx or Context(root, legs=legs)
    store = ctx.store
    plans = {}
    for leg in legs:
        inputs, problems = leg_inputs(leg, ctx)
        digest = digest_of(inputs) if inputs else None
        record = None
        if problems:
            carried, why = False, "; ".join(problems)
        elif ctx.dirty:
            carried, why = False, "working tree not clean"
        elif store is None:
            carried, why = False, "git common dir unresolvable"
        else:
            record = read_record(store, leg, digest)
            if record:
                carried = True
                why = (f"green at {str(record.get('head'))[:8]} "
                       f"({record.get('origin')}, {str(record.get('recorded_at', ''))[:16]}, "
                       f"{int(record.get('seconds') or 0)}s)")
            else:
                carried, why = False, _why_moved(inputs, store, leg, digest)
        plans[leg] = LegPlan(leg, digest, inputs, carried, why, record)
    return plans


def render_plan(root, plans, ctx=None):
    lines = []
    if ctx is not None:
        lines.append(f"leg cache at HEAD {(ctx.head or '?')[:8]} on {machine_identity()}")
        if ctx.dirty:
            lines.append("working tree not clean - carrying refused: "
                         + "; ".join(ctx.dirty[:3]))
    store = ctx.store if ctx is not None else store_root(root)
    lines.append(f"store: {store or 'unresolved'}")
    lines.append(f"{'leg':<16} {'digest':<8} {'action':<8} why")
    for leg, lp in plans.items():
        lines.append(f"{leg:<16} {(lp.digest or '-')[:8]:<8} "
                     f"{'carried' if lp.carried else 'RUN':<8} {lp.why}")
    n = sum(1 for lp in plans.values() if lp.carried)
    lines.append(f"{n} carried, {len(plans) - n} to run")
    return "\n".join(lines)


def still_at(root, head):
    """True when HEAD is still `head` and the tree is still clean: a green
    is evidence only about the state its digest was computed from."""
    return head is not None and head_sha(root) == head and not dirty_lines(root)


def env_drift(ctx, leg):
    """Which of the leg's environment probes moved since the plan, or []."""
    names = ENV_FOR_LEG.get(leg, ())
    if not names:
        return []
    now = PROBE(ctx.root, names)
    return [f"env/{n}" for n in names if now.get(n) != ctx.env.get(n)]


def record_green(root, ctx, lp, command, seconds, origin):
    """Write a green for `lp` after the record-time guards, or return the
    reason it was not recorded. Never raises: the caller has a green leg
    in hand and a store problem must not turn it red. A record a run
    observed is never replaced by one a person asserted for the same
    digest; the stronger provenance stays."""
    try:
        if lp.digest is None or lp.inputs is None:
            return "unresolved inputs"
        if not still_at(root, ctx.head):
            return "HEAD or tree changed while the leg was running"
        drift = env_drift(ctx, lp.leg)
        if drift:
            return "environment changed while the leg was running: " + ", ".join(drift)
        store = ctx.store
        if store is None:
            return "git common dir unresolvable"
        existing = read_record(store, lp.leg, lp.digest)
        if existing and origin == "asserted" and existing.get("origin") == "observed":
            return None
        write_record(store, lp.leg, lp.digest, lp.inputs, ctx.head, command,
                     round(seconds, 1), origin, worktree=os.path.abspath(root))
        gc(store, legs=[lp.leg])
        return None
    except _RECORD_ERRORS + (subprocess.SubprocessError,) as e:
        return f"store not writable: {e}"


# --- CLI -------------------------------------------------------------------------

def _known_legs(names):
    unknown = [leg for leg in names if leg not in gate_scope.ALL_LEGS]
    if unknown:
        print(f"leg-cache: unknown leg(s) {', '.join(unknown)}; known: "
              + ", ".join(gate_scope.ALL_LEGS))
        return False
    return True


def _no_flags(command, args):
    flags = [a for a in args if a.startswith("--")]
    if flags:
        print(f"leg-cache: {command} takes no options, got {', '.join(flags)}")
        return False
    return True


def cmd_plan(root, args):
    if not _no_flags("plan", args):
        return 2
    legs = args or None
    if legs and not _known_legs(legs):
        return 2
    ctx = Context(root, legs=legs)
    plans = build_plan(root, legs, ctx)
    print(render_plan(root, plans, ctx))
    return 0


def cmd_check(root, args):
    if args:
        print("leg-cache: check takes no arguments")
        return 2
    ctx = Context(root)
    plans = build_plan(root, None, ctx)
    print(render_plan(root, plans, ctx))
    missing = [leg for leg, lp in plans.items() if not lp.carried]
    if missing:
        print(f"check: legs without a green for the current inputs: {', '.join(missing)}")
        return 1
    print("check: every leg has a green for the current inputs")
    return 0


def cmd_snapshot(root, args):
    """Write the environment identity to a file, for a `record` that comes
    after a long run: the digest must name the toolchain the legs ran
    under, and only a snapshot taken before them can say what that was."""
    if len(args) != 1 or args[0].startswith("--"):
        print("leg-cache: snapshot takes exactly one file path")
        return 2
    env = PROBE(root, env_names(gate_scope.ALL_LEGS))
    with open(args[0], "w", encoding="utf-8") as f:
        json.dump({"head": head_sha(root), "env": env, "vars": env_var_values(),
                   "taken_at": _now().isoformat(timespec="seconds")}, f, indent=2)
    print(f"leg-cache: environment snapshot written to {args[0]}")
    return 0


def cmd_record(root, args):
    """Assert a green somebody saw at exactly HEAD. --from-sha must resolve
    to HEAD so a green can never be stamped onto a different commit, and a
    dirty tree is refused for the same reason a plan on one carries
    nothing. With --env-snapshot the digest is computed over the
    environment as it was before the run and the record is refused when
    the environment has moved since; --origin observed requires one,
    because a run that watched the leg pass is exactly the caller that
    can have taken it, and without it "observed" is only a word."""
    origin = "asserted"
    from_sha = None
    snapshot = None
    seconds = 0.0
    legs = []
    it = iter(args)
    for a in it:
        if a == "--from-sha":
            from_sha = next(it, None)
        elif a == "--origin":
            origin = next(it, "asserted")
        elif a == "--seconds":
            seconds = float(next(it, "0") or 0)
        elif a == "--env-snapshot":
            snapshot = next(it, None)
        elif a == "--all":
            legs = list(gate_scope.ALL_LEGS)
        elif a.startswith("--"):
            print(f"leg-cache: unknown option {a}")
            return 2
        else:
            legs.append(a)
    if origin not in ("asserted", "observed"):
        print("leg-cache: --origin must be asserted or observed")
        return 2
    if not legs or not _known_legs(legs):
        if not legs:
            print("leg-cache: record needs leg names or --all")
        return 2
    if not from_sha:
        print("leg-cache: record needs --from-sha <HEAD>: a record asserts a green "
              "you saw at exactly this commit; naming it keeps that checkable")
        return 2
    if origin == "observed" and not snapshot:
        print("leg-cache: --origin observed needs --env-snapshot FILE taken before the run")
        return 2
    env, env_vars, snapshot_head = None, None, None
    if snapshot:
        try:
            with open(snapshot, encoding="utf-8") as f:
                data = json.load(f)
            env, env_vars, snapshot_head = data["env"], data.get("vars", {}), data.get("head")
        except _RECORD_ERRORS + (KeyError,) as e:
            print(f"leg-cache: record refused: snapshot {snapshot} unreadable ({e})")
            return 1
    ctx = Context(root, legs=legs, env=env, env_vars=env_vars)
    if snapshot and snapshot_head != ctx.head:
        print(f"leg-cache: record refused: snapshot was taken at "
              f"{str(snapshot_head)[:8]}, HEAD is {(ctx.head or '?')[:8]}")
        return 1
    if ctx.dirty:
        print("leg-cache: record refused: working tree not clean ("
              + "; ".join(ctx.dirty[:3]) + ")")
        return 1
    resolved = git(root, "rev-parse", "--verify", f"{from_sha}^{{commit}}")
    if resolved.returncode != 0 or resolved.stdout.strip() != ctx.head:
        print(f"leg-cache: record refused: --from-sha {from_sha} does not resolve "
              f"to HEAD {(ctx.head or '?')[:8]}")
        return 1
    plans = build_plan(root, legs, ctx)
    for leg in legs:
        lp = plans[leg]
        if lp.inputs is None:
            print(f"leg-cache: record refused for {leg}: {lp.why}")
            return 1
        if lp.carried and origin == "asserted" and (lp.record or {}).get("origin") == "observed":
            print(f"leg-cache: {leg} already recorded as observed at "
                  f"{str(lp.record.get('head'))[:8]}; keeping that record")
            continue
        reason = record_green(root, ctx, lp, " ".join(gate_scope.LEG_COMMANDS[leg]),
                              seconds, origin)
        if reason:
            print(f"leg-cache: record refused for {leg}: {reason}")
            return 1
        print(f"leg-cache: recorded {leg} green at {ctx.head[:8]} (digest {lp.digest[:8]}, {origin})")
    return 0


def cmd_gc(root, args):
    if args:
        print("leg-cache: gc takes no arguments")
        return 2
    store = store_root(root)
    removed = gc(store)
    print(f"leg-cache: removed {removed} record(s) under {store}")
    return 0


# --- self-test -------------------------------------------------------------------

def self_test():
    import tempfile
    failed = False

    def report(ok, label, detail=""):
        nonlocal failed
        if not ok:
            failed = True
        print(f"{'ok ' if ok else 'FAIL'} {label}{': ' + detail if detail else ''}")

    env_state = {"zig": "0.16.0 sha256:aaaaaaaaaaaa", "dotnet": "10.0.303",
                 "pwsh": "7.6.5", "python": "3.11.9"}

    def fake_probe(_root, names):
        return {n: env_state.get(n, UNRESOLVED) for n in names}

    # No zig, dotnet or pwsh is spawned by the synthetic repository.
    global PROBE
    saved_probe, PROBE = PROBE, fake_probe

    justfile = "\n".join([
        "# preamble comment",
        'set windows-shell := ["pwsh.exe"]',
        'TEST_VERSION := "0.0.0-test"',
        "",
        "test: test-configure test-lib-vt test-full test-pkg test-reachability",
        "",
        "test-configure:",
        "    zig build --list-steps",
        "",
        "test-lib-vt:",
        "    zig build test-lib-vt \"-Dversion-string={{TEST_VERSION}}\"",
        "",
        "test-full:",
        "    zig build test -Dapp-runtime=none",
        "",
        "test-pkg:",
        "    cd pkg/wuffs && zig build test",
        "",
        "test-reachability:",
        "    python .agents/scripts/test_reachability.py",
        "",
        "build-dll:",
        "    zig build -Dapp-runtime=none",
        "",
        "[windows]",
        "test-win:",
        "    dotnet build windows/Ghostty.sln /p:Platform=x64",
        "    dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj",
        "",
        "build-win:",
        "    dotnet build windows/Ghostty.sln",
        "",
        "gitversion-selftest:",
        "    #!/usr/bin/env bash",
        "    pwsh -File .agents/scripts/gitversion_selftest.ps1",
        "",
        "gates-selftest: gitversion-selftest",
        "    python .agents/scripts/pr_gate.py --self-test",
        "",
        "[windows]",
        "release-gate-check:",
        "    pwsh -File .agents/scripts/release_gate_check.ps1",
        "",
        "unrelated args=\"\": (test-win args)",
        "    echo {{args}}",
    ]) + "\n"

    def sh(root, *args):
        p = git(root, *args)
        assert p.returncode == 0, f"git {' '.join(args)}: {p.stderr}"
        return p.stdout.strip()

    def write(root, rel, text):
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)

    def commit(root, msg):
        sh(root, "add", "-A")
        sh(root, "commit", "-q", "-m", msg)

    def digests(root):
        ctx = Context(root)
        return {leg: lp.digest for leg, lp in build_plan(root, None, ctx).items()}, ctx

    def moved(before, after):
        return sorted(leg for leg in before if before[leg] != after[leg])

    ALL = sorted(gate_scope.ALL_LEGS)
    FMT, ZIG, WIN, GATES, REL = (gate_scope.LEG_FMT, gate_scope.LEG_ZIG, gate_scope.LEG_WIN,
                                 gate_scope.LEG_GATES, gate_scope.LEG_RELEASE_GATE)

    with tempfile.TemporaryDirectory() as td:
        root = os.path.join(td, "repo")
        os.makedirs(root)
        sh(root, "init", "-q")
        sh(root, "config", "user.email", "t@example.com")
        sh(root, "config", "user.name", "t")
        sh(root, "config", "core.autocrlf", "false")
        write(root, "justfile", justfile)
        write(root, "src/main.zig", "pub fn main() void {}\n")
        write(root, "src/build/GitVersion.zig", "// gv\n")
        write(root, "src/main_c.zig", "// exports\n")
        write(root, "pkg/wuffs/build.zig", "// wuffs\n")
        write(root, "include/ghostty.h", "#define X 1\n")
        write(root, "windows/Ghostty/App.cs", "class App {}\n")
        write(root, "windows/Ghostty.Tests/Demo/ShippingBuildGateTests.cs", "class T {}\n")
        write(root, TESTS_CSPROJ.replace("/", os.sep),
              '<Project>\n  <EmbeddedResource Include="..\\..\\src\\extra.zig" />\n'
              '  <Compile Include="..\\Ghostty.Core\\X.cs" />\n</Project>\n')
        write(root, "src/extra.zig", "// embedded by the test project\n")
        write(root, "dist/windows/IconGen.Tests/A.cs", "class A {}\n")
        write(root, "images/icons/icon_16.png", "png\n")
        write(root, "images/shot.png", "png\n")
        write(root, ".agents/scripts/pr_gate.py", "print('gate')\n")
        write(root, ".agents/scripts/test_reachability.py", "print('reach')\n")
        write(root, ".agents/scripts/release_gate_check.ps1", "exit 0\n")
        write(root, "docs/notes.md", "notes\n")
        write(root, "README.md", "readme\n")
        write(root, "CODEOWNERS", "* @owner\n")
        write(root, "build.zig", "// build\n")
        write(root, "helper.zig", "// a root-level zig file a suffix rule routes\n")
        write(root, "global.json", "{}\n")
        commit(root, "init")

        # 1. Determinism and the fail-closed derivation.
        d0, ctx0 = digests(root)
        d0b, _ = digests(root)
        report(d0 == d0b, "digest-deterministic")
        report(all(d0[leg] for leg in ALL), "every-leg-resolves",
               "; ".join(f"{leg}: {lp.why}" for leg, lp in build_plan(root, None, ctx0).items()
                         if lp.digest is None))
        report(ctx0.root_legs.get("CODEOWNERS") is None and ctx0.root_legs.get("README.md") == (),
               "root-entries-routed", str({k: v for k, v in ctx0.root_legs.items() if k in ("CODEOWNERS", "README.md", "helper.zig")}))
        inputs_zig, _ = leg_inputs(ZIG, ctx0)
        report("recipe/test-full" in inputs_zig and "recipe/test-configure" in inputs_zig
               and "recipe/unrelated" not in inputs_zig and "recipe/build-dll" in inputs_zig
               and "path/helper.zig" in inputs_zig and "command" in inputs_zig,
               "zig-leg-inputs", ", ".join(k for k in inputs_zig if k.startswith(("recipe/", "path/helper", "command"))))
        inputs_gates, _ = leg_inputs(GATES, ctx0)
        report("recipe/gitversion-selftest" in inputs_gates and "justfile/all-recipes" in inputs_gates,
               "gates-leg-inputs")
        inputs_win, _ = leg_inputs(WIN, ctx0)
        report("path/helper.zig" not in inputs_win and "var/GHOSTTY_FUZZ_ITERATIONS" in inputs_win
               and "path/src/extra.zig" in inputs_win and ctx0.embeds == ["src/extra.zig"],
               "win-leg-inputs", str(ctx0.embeds))
        recipes_hc, _ = parse_justfile("x: a b # not a dep\n    echo\n")
        report(recipes_hc["x"][1] == ["a", "b"], "header-comment-is-not-a-dependency",
               str(recipes_hc["x"][1]))

        # 2. Mutation sensitivity: which legs move for which edit.
        cases = [
            ("src/main.zig", "pub fn main() void { return; }\n", sorted([FMT, ZIG])),
            ("src/main_c.zig", "// exports 2\n", sorted([FMT, ZIG, WIN])),
            ("src/extra.zig", "// embedded, edited\n", sorted([FMT, ZIG, WIN])),
            (TESTS_CSPROJ, '<Project>\n  <EmbeddedResource Include="..\\..\\src\\extra.zig" />\n'
                           '</Project>\n', sorted([GATES, REL, WIN])),
            ("helper.zig", "// moved\n", sorted([FMT, ZIG])),
            ("windows/Ghostty/App.cs", "class App { int x; }\n", sorted([WIN, REL])),
            ("include/ghostty.h", "#define X 2\n", sorted([FMT, ZIG, WIN])),
            ("dist/windows/IconGen.Tests/A.cs", "class A { }\n", [WIN]),
            ("images/icons/icon_16.png", "png2\n", [WIN]),
            ("images/shot.png", "png2\n", []),
            (".agents/scripts/pr_gate.py", "print('gate2')\n", [GATES]),
            (".agents/scripts/test_reachability.py", "print('r2')\n", sorted([GATES, ZIG])),
            ("src/build/GitVersion.zig", "// gv2\n", sorted([FMT, ZIG, GATES])),
            ("docs/notes.md", "other notes\n", []),
            ("README.md", "readme 2\n", []),
            ("CODEOWNERS", "* @someone\n", ALL),
        ]
        prev = d0
        for rel, text, expect in cases:
            write(root, rel, text)
            commit(root, f"edit {rel}")
            cur, _ = digests(root)
            report(moved(prev, cur) == expect, f"moves[{rel}]", f"{moved(prev, cur)}")
            prev = cur

        # 3. Justfile edits: a recipe body moves its legs only; a comment
        #    moves nothing; a shebang is not a comment; the preamble moves
        #    everything; any recipe edit at all moves the gates leg, whose
        #    self-tests read every recipe.
        def edit_justfile(old, new, label, expect):
            nonlocal prev
            text = open(os.path.join(root, "justfile"), encoding="utf-8").read()
            assert old in text, label
            write(root, "justfile", text.replace(old, new))
            commit(root, label)
            cur, _ = digests(root)
            report(moved(prev, cur) == expect, f"moves[justfile {label}]", f"{moved(prev, cur)}")
            prev = cur

        edit_justfile("    dotnet build windows/Ghostty.sln /p:Platform=x64",
                      "    dotnet build windows/Ghostty.sln /p:Platform=x64 /p:X=1",
                      "test-win body", sorted([GATES, WIN]))
        edit_justfile("# preamble comment", "# reworded comment", "comment", [])
        edit_justfile("    echo {{args}}", "    echo changed {{args}}", "unrelated recipe", [GATES])
        edit_justfile("    #!/usr/bin/env bash", "    #!/usr/bin/env pwsh", "shebang", [GATES])
        edit_justfile('set windows-shell := ["pwsh.exe"]', 'set windows-shell := ["pwsh.exe", "-NoLogo"]',
                      "preamble", ALL)
        edit_justfile("    zig build test -Dapp-runtime=none", "    zig build test -Dapp-runtime=none -Dx",
                      "test-full body", sorted([GATES, ZIG]))
        recipes, _pre = parse_justfile("[unix]\nx:\n    echo a\n[windows]\nx:\n    echo b\n")
        report("echo a" in recipes["x"][0] and "echo b" in recipes["x"][0], "duplicate-recipe-keeps-both")

        # 4. Environment identity and the variables the suites read.
        env_state["zig"] = "0.16.0 sha256:bbbbbbbbbbbb"
        cur, _ = digests(root)
        report(moved(prev, cur) == sorted([FMT, ZIG]), "moves[env/zig relane]", f"{moved(prev, cur)}")
        prev = cur
        saved_var = os.environ.get("GHOSTTY_FUZZ_ITERATIONS")
        os.environ["GHOSTTY_FUZZ_ITERATIONS"] = "1" if saved_var != "1" else "2"
        try:
            cur, _ = digests(root)
            report(moved(prev, cur) == [WIN], "moves[var/GHOSTTY_FUZZ_ITERATIONS]", f"{moved(prev, cur)}")
        finally:
            if saved_var is None:
                del os.environ["GHOSTTY_FUZZ_ITERATIONS"]
            else:
                os.environ["GHOSTTY_FUZZ_ITERATIONS"] = saved_var
        env_state["dotnet"] = UNRESOLVED
        plans = build_plan(root, None, Context(root))
        report(not plans[WIN].carried and "env/dotnet unresolved" in plans[WIN].why
               and plans[WIN].digest is None, "unresolved-env-forces-run", plans[WIN].why)
        env_state["dotnet"] = "10.0.303"

        # 5. Record, carry, and the carries that are the whole point: an
        #    unrelated commit, an amended message, and a rebase-equivalent.
        ctx = Context(root)
        plans = build_plan(root, None, ctx)
        report(not any(lp.carried for lp in plans.values()), "nothing-carried-before-record")
        reason = record_green(root, ctx, plans[ZIG], "just test", 2400.0, "observed")
        report(reason is None, "record-observed", reason or "")
        plans = build_plan(root, None, Context(root))
        report(plans[ZIG].carried and not plans[WIN].carried, "carried-after-record",
               f"{plans[ZIG].why} / {plans[WIN].why}")
        write(root, "docs/notes.md", "docs only\n")
        commit(root, "docs only")
        plans = build_plan(root, None, Context(root))
        report(plans[ZIG].carried, "carried-across-docs-only-commit", plans[ZIG].why)
        sh(root, "commit", "-q", "--amend", "-m", "docs only, reworded")
        plans = build_plan(root, None, Context(root))
        report(plans[ZIG].carried, "carried-across-amend", plans[ZIG].why)
        write(root, "src/main.zig", "pub fn main() void { return; } // moved\n")
        commit(root, "zig edit")
        plans = build_plan(root, None, Context(root))
        report(not plans[ZIG].carried and "path/src" in plans[ZIG].why,
               "zig-edit-names-the-moved-input", plans[ZIG].why)

        # 6. Dirty tree: nothing carries, nothing records.
        ctx = Context(root)
        plans = build_plan(root, None, ctx)
        record_green(root, ctx, plans[ZIG], "just test", 1.0, "observed")
        write(root, "src/main.zig", "// dirty\n")
        plans = build_plan(root, None, Context(root))
        report(not plans[ZIG].carried and plans[ZIG].why == "working tree not clean",
               "dirty-tree-carries-nothing", plans[ZIG].why)
        reason = record_green(root, ctx, build_plan(root, None, ctx)[ZIG],
                              "just test", 1.0, "observed")
        report(reason is not None and "changed" in reason, "dirty-tree-records-nothing", reason or "")
        sh(root, "checkout", "--", "src/main.zig")

        # 7. Record-time guards: HEAD moved, env drifted, store unwritable.
        ctx = Context(root)
        lp = build_plan(root, None, ctx)[WIN]
        write(root, "docs/notes.md", "moved head\n")
        commit(root, "head moves during the leg")
        reason = record_green(root, ctx, lp, "just test-win", 1.0, "observed")
        report(reason is not None and "HEAD" in reason, "record-refused-head-moved", reason or "")
        ctx = Context(root)
        lp = build_plan(root, None, ctx)[WIN]
        env_state["dotnet"] = "10.0.999"
        reason = record_green(root, ctx, lp, "just test-win", 1.0, "observed")
        report(reason is not None and "env/dotnet" in reason, "record-refused-env-drift", reason or "")
        env_state["dotnet"] = "10.0.303"
        store = store_root(root)
        blocker = os.path.join(store, WIN)
        os.makedirs(os.path.dirname(blocker), exist_ok=True)
        if os.path.isdir(blocker):
            shutil.rmtree(blocker)
        with open(blocker, "w", encoding="utf-8") as f:
            f.write("a file where the leg's directory should be")
        reason = record_green(root, ctx, lp, "just test-win", 1.0, "observed")
        report(reason is not None and "store not writable" in reason,
               "record-degrades-not-raises", reason or "")
        os.remove(blocker)

        # 8. The CLI: record refuses the wrong sha and a dirty tree, records
        #    the right one as asserted, and a snapshot catches env drift.
        try:
            head = sh(root, "rev-parse", "HEAD")
            report(cmd_record(root, [ZIG, "--from-sha", "HEAD~1"]) == 1, "cli-record-wrong-sha")
            write(root, "src/main.zig", "// dirty again\n")
            report(cmd_record(root, [ZIG, "--from-sha", head]) == 1, "cli-record-dirty")
            sh(root, "checkout", "--", "src/main.zig")
            report(cmd_record(root, ["nope", "--from-sha", head]) == 2, "cli-record-unknown-leg")
            report(cmd_plan(root, ["--bogus"]) == 2, "cli-plan-rejects-flags")
            report(cmd_record(root, [GATES, "--from-sha", head, "--seconds", "20"]) == 0,
                   "cli-record-asserted")
            rec = build_plan(root, None, Context(root))[GATES].record
            report(bool(rec) and rec.get("origin") == "asserted" and rec.get("seconds") == 20.0,
                   "cli-record-shape", json.dumps({k: rec.get(k) for k in ("origin", "seconds")}) if rec else "no record")
            snap = os.path.join(td, "env.json")
            report(cmd_snapshot(root, [snap]) == 0 and os.path.isfile(snap), "cli-snapshot")
            report(cmd_record(root, [ZIG, "--from-sha", head, "--origin", "observed"]) == 2,
                   "cli-record-observed-needs-snapshot")
            report(cmd_record(root, [ZIG, "--from-sha", head, "--env-snapshot",
                                     os.path.join(td, "absent.json")]) == 1,
                   "cli-record-unreadable-snapshot-refused")
            env_state["zig"] = "0.16.0 sha256:cccccccccccc"
            report(cmd_record(root, [ZIG, "--from-sha", head, "--origin", "observed",
                                     "--env-snapshot", snap]) == 1,
                   "cli-record-snapshot-refuses-drift")
            env_state["zig"] = "0.16.0 sha256:bbbbbbbbbbbb"
            report(cmd_record(root, [ZIG, "--from-sha", head, "--origin", "observed",
                                     "--env-snapshot", snap]) == 0,
                   "cli-record-snapshot-accepts-same-env")
            rec = build_plan(root, None, Context(root))[ZIG].record
            report(bool(rec) and rec.get("origin") == "observed" and rec.get("command") == "just test",
                   "cli-record-observed-shape", json.dumps({k: rec.get(k) for k in ("origin", "command")}) if rec else "no record")
            write(root, "docs/notes.md", "after the snapshot\n")
            commit(root, "head moves after the snapshot")
            report(cmd_record(root, [ZIG, "--from-sha", "HEAD", "--env-snapshot", snap]) == 1,
                   "cli-record-snapshot-refuses-other-head")
            # An asserted record never displaces an observed one; and a
            # snapshot carries the suite's variables, so a record made under
            # a different value than the run's is refused by its digest.
            head = sh(root, "rev-parse", "HEAD")
            snap2 = os.path.join(td, "env2.json")
            os.environ["GHOSTTY_FUZZ_ITERATIONS"] = "3"
            try:
                cmd_snapshot(root, [snap2])
            finally:
                del os.environ["GHOSTTY_FUZZ_ITERATIONS"]
            report(cmd_record(root, [WIN, "--from-sha", head, "--origin", "observed",
                                     "--env-snapshot", snap2]) == 0, "cli-record-with-snapshot-vars")
            plan_now = build_plan(root, [WIN], Context(root, legs=[WIN]))[WIN]
            report(not plan_now.carried and "var/GHOSTTY_FUZZ_ITERATIONS" in plan_now.why,
                   "snapshot-vars-enter-the-digest", plan_now.why)
            os.environ["GHOSTTY_FUZZ_ITERATIONS"] = "3"
            try:
                report(cmd_record(root, [WIN, "--from-sha", head]) == 0, "cli-record-asserted-over-observed")
                rec = build_plan(root, [WIN], Context(root, legs=[WIN]))[WIN].record
                report(bool(rec) and rec.get("origin") == "observed", "observed-record-kept",
                       str((rec or {}).get("origin")))
            finally:
                del os.environ["GHOSTTY_FUZZ_ITERATIONS"]
        finally:
            pass

        # 9. Store hygiene: unreadable records and stray temp files are
        #    misses, gc keeps the newest and sweeps the rest.
        d = build_plan(root, None, Context(root))[WIN].digest
        os.makedirs(os.path.join(store, WIN), exist_ok=True)
        with open(record_path(store, WIN, d), "w", encoding="utf-8") as f:
            f.write("[]")
        report(read_record(store, WIN, d) is None, "unreadable-record-is-a-miss")
        stray = os.path.join(store, WIN, "x.json.1.tmp")
        with open(stray, "w", encoding="utf-8") as f:
            f.write("{")
        os.utime(stray, (time.time() - 2 * STALE_TMP_SECONDS,) * 2)
        report("no green record" in _why_moved({}, store, WIN, "0" * 64)
               or "inputs moved" in _why_moved({}, store, WIN, "0" * 64),
               "why-moved-survives-stray-files")
        old = _now() - datetime.timedelta(days=MAX_AGE_DAYS + 1)
        for i in range(5):
            write_record(store, FMT, f"{i:064x}", {"leg": FMT}, "a" * 40, "x", 1.0, "asserted")
        with open(record_path(store, FMT, f"{0:064x}"), "r+", encoding="utf-8") as f:
            rec = json.load(f)
            rec["recorded_at"] = old.isoformat(timespec="seconds")
            f.seek(0)
            json.dump(rec, f)
            f.truncate()
        removed = gc(store, keep=3)
        left = sorted(os.listdir(os.path.join(store, FMT)))
        report(removed == 4 and f"{0:064x}.json" not in left and len(left) == 3
               and not os.path.exists(record_path(store, WIN, d)) and not os.path.exists(stray),
               "gc-drops-old-excess-unreadable-and-stray", f"removed={removed} left={left}")

    # 10. The real repo: every leg has recipes and paths, the justfile
    #     parses to what `just` sees, and gate_scope routes every file the
    #     C# test project embeds from outside windows/ to the Windows leg,
    #     so the EXACT_RULES list cannot fall behind the csproj.
    with open(os.path.join(REPO_ROOT, "justfile"), encoding="utf-8") as f:
        recipes, preamble = parse_justfile(f.read())
    for leg in gate_scope.ALL_LEGS:
        names, missing = leg_recipes(leg, recipes)
        # zig-fmt is the one leg signoff runs directly rather than through
        # a recipe, so it legitimately closes over none.
        report((bool(names) or leg == gate_scope.LEG_FMT) and not missing and bool(leg_paths(leg)),
               f"real-justfile[{leg}]", f"recipes={names} missing={missing}")
    report("TEST_VERSION" in preamble and "set windows-shell" in preamble, "real-justfile-preamble")
    csproj = os.path.join(REPO_ROOT, "windows", "Ghostty.Tests", "Ghostty.Tests.csproj")
    try:
        with open(csproj, encoding="utf-8") as f:
            includes = [v for v in re.findall(r'Include="([^"]+)"', f.read()) if ".." in v]
    except OSError:
        includes = None
    unrouted = []
    for inc in includes or []:
        # A single `..\` stays inside windows/, which the windows/ rule
        # routes whole. Two levels leave it, and only a literal path can
        # be routed: a glob, a forward slash or a property expansion is
        # refused by name so a new spelling cannot slip past the check as
        # "not an include".
        if not inc.startswith("..\\..\\"):
            continue
        if any(c in inc for c in "*/$;"):
            unrouted.append(inc)
            continue
        rel = inc[6:].replace("\\", "/")
        if rel.startswith(("windows/", "zig-out/")):
            continue
        legs = gate_scope.legs_for_path(rel)
        if legs is None or gate_scope.LEG_WIN not in legs:
            unrouted.append(rel)
    report(includes is not None and includes and not unrouted,
           "csproj-out-of-tree-includes-route-to-windows-tests",
           f"unrouted={unrouted}" if includes else "csproj unreadable")
    report(gate_scope.LEG_GATES in (gate_scope.legs_for_path(TESTS_CSPROJ) or ()),
           "csproj-edit-runs-this-check")
    # The variable table is held to what the test projects read, and the
    # seed's exclusion to no test reading std.testing.random_seed; either
    # drifting would let a weak green carry machine-wide.
    reads = git(REPO_ROOT, "grep", "-h", "-o", "-E", r'GetEnvironmentVariable\("[A-Z_]+"\)',
                "--", "windows/Ghostty.Tests", "windows/Ghostty.Tests.Windows", "dist/windows")
    seen = sorted(set(re.findall(r'"([A-Z_]+)"', reads.stdout))) if reads.returncode == 0 else None
    report(seen == sorted(ENV_VARS_FOR_LEG[gate_scope.LEG_WIN]),
           "env-vars-table-matches-the-test-projects", str(seen))
    seed_users = git(REPO_ROOT, "grep", "-l", "random_seed", "--", "src", "pkg")
    report(seed_users.returncode == 1 and not seed_users.stdout.strip(),
           "no-test-reads-the-random-seed", seed_users.stdout.strip()[:200])

    PROBE = saved_probe
    print("SELF-TEST " + ("FAILED" if failed else "PASSED"))
    return 1 if failed else 0


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if not argv:
        print(__doc__)
        return 2
    cmd, rest = argv[0], argv[1:]
    handlers = {"plan": cmd_plan, "check": cmd_check, "snapshot": cmd_snapshot,
                "record": cmd_record, "gc": cmd_gc}
    if cmd not in handlers:
        print(f"leg-cache: unknown command {cmd}; one of {', '.join(handlers)}")
        return 2
    return handlers[cmd](REPO_ROOT, rest)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
