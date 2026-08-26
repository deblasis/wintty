"""Which test legs a change actually needs.

The signoff ladder costs over an hour, almost all of it the Zig suite. A
change that touches no Zig and no C# cannot break either, so paying that
hour is a tax that eventually gets the gate bypassed rather than run. This
module maps changed paths to the legs that could possibly fail because of
them, and both sides of the contract read it: `signoff` runs the legs it
names, and `pr_gate` recomputes them from the PR's own file list and
refuses a record that ran fewer.

The safety property is failing closed on ignorance: a path no rule
recognises requires every leg. Adding a new top-level directory therefore
costs a full ladder run until someone classifies it, which is the right
direction for a guard to be wrong in.

What scoping deliberately does NOT cover: pre-existing breakage elsewhere
in the tree. A docs-only PR no longer discovers that the Zig suite was
already red on the base. That discovery belongs to the nightly run over
the whole branch, which is unconditional; per-PR signoff answers the
narrower question of whether THIS change breaks anything.
"""

LEG_FMT = "zig-fmt"
LEG_ZIG = "zig-tests"
LEG_WIN = "windows-tests"
LEG_GATES = "gates-selftest"

ALL_LEGS = (LEG_FMT, LEG_ZIG, LEG_WIN, LEG_GATES)
ZIG_LEGS = (LEG_FMT, LEG_ZIG)

# The justfile defines the legs themselves, so editing a recipe a leg runs
# through invalidates what that leg proves. The mapping is per recipe rather
# than all-or-nothing: gutting `test-win` says nothing about the Zig suite,
# and adding an unrelated recipe says nothing about either. A changed line
# outside every recipe (the shell preamble, a variable) forces everything,
# since it can reach any of them.
RECIPE_LEGS = {
    "test": (LEG_ZIG,),
    "test-lib-vt": (LEG_ZIG,),
    "test-full": (LEG_ZIG,),
    "test-pkg": (LEG_ZIG,),
    "build-dll": (LEG_ZIG, LEG_WIN),
    "test-win": (LEG_WIN,),
    "build-win": (LEG_WIN,),
    "gates-selftest": (LEG_GATES,),
    "gitversion-selftest": (LEG_GATES,),
}

# Ordered, first match wins, so a more specific prefix must precede its
# parent. An empty tuple means the path cannot affect any leg.
PREFIX_RULES = (
    ("src/", ZIG_LEGS),
    ("pkg/", ZIG_LEGS),
    ("include/", ZIG_LEGS),
    ("vendor/", ZIG_LEGS),
    ("test/", ZIG_LEGS),
    ("windows/", (LEG_WIN,)),
    (".agents/scripts/", (LEG_GATES,)),
    (".agents/", ()),
    (".claude/", ()),
    (".github/", ()),
    ("docs/", ()),
    ("images/", ()),
    ("macos/", ()),
    ("nix/", ()),
    ("flatpak/", ()),
    ("snap/", ()),
    ("po/", ()),
    ("example/", ()),
    ("tools/", ()),
    ("dist/", ()),
)

EXACT_RULES = {
    # Both of these are checked by a gates-leg script rather than by a zig
    # test, because the Zig legs cannot see a BEHAVIOR change in either.
    # zig-fmt still sees formatting and the suite still sees a compile
    # error, but nothing runs the code: src/build_config.zig imports
    # build/Config.zig only to call fromOptions(), and Zig analyses at
    # decl level, so Config.init -- which is what decides that `tip` and
    # `vX.Y.Z` are the only names a version may have -- is never reached
    # from a test root (issue 748). GitVersion.zig runs in build.zig only.
    # The selftest hardcodes that same contract, so it has to run when
    # either side of it moves, or the two drift apart in silence.
    "src/build/GitVersion.zig": ZIG_LEGS + (LEG_GATES,),
    "src/build/Config.zig": ZIG_LEGS + (LEG_GATES,),
    "build.zig": ZIG_LEGS,
    "build.zig.zon": ZIG_LEGS,
    "build.zig.zon.json": ZIG_LEGS,
    "build.zig.zon.nix": (),
    "build.zig.zon.txt": (),
    "global.json": (LEG_WIN,),
    "Directory.Build.props": (LEG_WIN,),
    ".gitignore": (),
    ".gitattributes": (),
    "AGENTS.md": (),
    "CLAUDE.md": (),
    "typos.toml": (),
    "flake.nix": (),
    "flake.lock": (),
    "default.nix": (),
    "shell.nix": (),
    "Makefile": (),
    "CMakeLists.txt": (),
    "Doxyfile": (),
    "DoxygenLayout.xml": (),
    "valgrind.supp": (),
}

SUFFIX_RULES = (
    (".zig", ZIG_LEGS),
    (".md", ()),
    (".txt", ()),
)


def normalize(path):
    """Repo-relative, forward-slashed. Note lstrip('./') would take a
    CHARACTER SET and eat the leading dot of '.gitignore', so the './'
    prefix is removed explicitly."""
    p = path.replace("\\", "/")
    while p.startswith("./"):
        p = p[2:]
    return p


def legs_for_path(path):
    """Legs a single path can affect, or None when no rule recognises it."""
    p = normalize(path)
    if p in EXACT_RULES:
        return EXACT_RULES[p]
    for prefix, legs in PREFIX_RULES:
        if p.startswith(prefix):
            return legs
    for suffix, legs in SUFFIX_RULES:
        if p.endswith(suffix):
            return legs
    return None


def required_legs(paths, justfile_legs=None):
    """The legs a change over `paths` must run.

    `justfile_legs` answers the one question a path alone cannot: which legs
    the justfile edit could have changed the meaning of. Callers that cannot
    inspect the diff must pass every leg, which is the conservative answer.
    """
    if justfile_legs is None:
        justfile_legs = ALL_LEGS
    needed = set()
    for path in paths:
        p = normalize(path)
        if p == "justfile":
            needed.update(justfile_legs)
            continue
        legs = legs_for_path(p)
        if legs is None:
            needed.update(ALL_LEGS)
        else:
            needed.update(legs)
    return sorted(needed)


# --- deferral ledger -------------------------------------------------------
#
# Merging a run of small PRs and paying for one expensive ladder afterwards is
# a reasonable trade, so deferral is supported rather than left to be smuggled
# past the gate. It is credit, not a discount: each deferral is recorded with
# its motivation in a ledger, the gate refuses to extend more credit than the
# limits below, and only a green full run settles the debt. Unsettled debt is
# reported by the doctor at session start and by the nightly run, because a
# skip nobody is reminded of is indistinguishable from a pass.

LEDGER_NAME = "deferred.json"
DEFER_MAX_OUTSTANDING = 5
DEFER_MAX_AGE_DAYS = 3
DEFER_MIN_REASON_CHARS = 25


def ledger_path(signoff_dir):
    import os
    return os.path.join(signoff_dir, LEDGER_NAME)


def load_ledger(signoff_dir):
    import json
    import os
    path = ledger_path(signoff_dir)
    try:
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
    except (OSError, ValueError):
        return []
    return data if isinstance(data, list) else []


def ledger_blockers(entries, now=None):
    """Why no further deferral may be granted. Empty list means credit is
    available."""
    import datetime
    if now is None:
        now = datetime.datetime.now(datetime.timezone.utc)
    out = []
    if len(entries) >= DEFER_MAX_OUTSTANDING:
        out.append(
            f"{len(entries)} deferred signoff(s) outstanding (limit {DEFER_MAX_OUTSTANDING})"
        )
    for e in entries:
        try:
            created = datetime.datetime.fromisoformat(e.get("created", ""))
        except ValueError:
            continue
        age = (now - created).days
        if age >= DEFER_MAX_AGE_DAYS:
            out.append(
                f"deferral for {e.get('sha', '?')[:10]} is {age} days old (limit {DEFER_MAX_AGE_DAYS})"
            )
            break
    return out


def unknown_paths(paths):
    """Paths no rule classifies; reported so a full run says why it is full."""
    out = []
    for path in paths:
        p = normalize(path)
        if p != "justfile" and legs_for_path(p) is None:
            out.append(p)
    return out
