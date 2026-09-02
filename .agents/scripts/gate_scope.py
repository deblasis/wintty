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
# Windows-only, and deliberately not folded into gates-selftest: that leg has
# no [windows] attribute and runs where a Release build of a WinUI project
# cannot.
LEG_RELEASE_GATE = "release-gate"

ALL_LEGS = (LEG_FMT, LEG_ZIG, LEG_WIN, LEG_GATES, LEG_RELEASE_GATE)
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
    "test-reachability": (LEG_ZIG,),
    "build-dll": (LEG_ZIG, LEG_WIN),
    "test-win": (LEG_WIN,),
    "build-win": (LEG_WIN,),
    "gates-selftest": (LEG_GATES,),
    "gitversion-selftest": (LEG_GATES,),
    "release-gate-check": (LEG_RELEASE_GATE,),
}

# Checked BEFORE the prefix rules, because these need both ends of the path.
# A bare ".csproj" suffix rule would never fire for anything under windows/:
# the windows/ prefix matches first and suffixes are only consulted when no
# prefix did. And a project file is a real route into a leaking build -- a
# DefineConstants;DEMO written straight into one never sets DemoEnabled, so
# the build-time <Error> cannot see it and only the compiled result can.
PREFIX_SUFFIX_RULES = (
    ("windows/", ".csproj", (LEG_WIN, LEG_RELEASE_GATE)),
    ("windows/", ".props", (LEG_WIN, LEG_RELEASE_GATE)),
    ("windows/", ".targets", (LEG_WIN, LEG_RELEASE_GATE)),
)

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
    # The check itself. Not a gates-selftest member: that leg runs anywhere,
    # and this one needs a Windows toolchain.
    ".agents/scripts/release_gate_check.ps1": (LEG_RELEASE_GATE,),
    # The compiled-result half of the shipping gate. It is #if !DEBUG, so
    # editing it proves nothing until a Release run executes it.
    "windows/Ghostty.Tests/Demo/ShippingBuildGateTests.cs": (LEG_WIN, LEG_RELEASE_GATE),
    # The gates leg runs this script's selftest, which needs no build.
    # Editing it must also run the check itself, and that needs the Zig
    # toolchain, so it rides the Zig leg through `just test`.
    ".agents/scripts/test_reachability.py": (LEG_GATES, LEG_ZIG),
    # Both of these carry a gates-leg check on top of the Zig legs, because
    # the contract they share -- that `tip` and `vX.Y.Z` are the only names a
    # version may have -- is split across them and only real `git describe`
    # against real tag layouts can say whether it still holds. GitVersion.zig
    # owns the filter, which the selftest reads out of the source rather than
    # copying; Config.init owns which names it then accepts, which the
    # selftest's expectation table hardcodes. Either side moving without the
    # other is how the two drift apart in silence.
    #
    # No test root reaches Config.init: src/build_config.zig imports
    # build/Config.zig only to call fromOptions(). GitVersion.zig is reached
    # through src/build/test.zig.
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
    for prefix, suffix, legs in PREFIX_SUFFIX_RULES:
        if p.startswith(prefix) and p.endswith(suffix):
            return legs
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
# Batch-sized on 2026-08-29 for the tab-reorder ladder, whose rungs defer by
# owner protocol and settle in one full ladder at the end of the stack; the
# old 5/3 defaults refused credit before a stack that size could finish. The
# first raise to 10 was sized before PR 6 split into six rungs -- its 10th
# deferral filled the cap with 6b, PR 7, and the two tail rungs still owed.
# 16 covered that remainder but assumed PR 7 was one rung; its survey split
# it into three, and with both tails counted the batch prices out at five
# more deferrals, which 16 refuses to merge at its edge. 20 covers the true
# remainder with margin (owner decision 2026-08-29).
# 28 is the fourth raise (owner standing authorization, 2026-08-31): #850
# landed the drag-harness rung and took the ledger to 24, and the two
# remaining tails price at 25 and 26 -- at the cap, so the second tail's
# merge would be refused exactly at its edge with the settle still owed.
# 28 leaves that margin. The age window moves with it so the arc's own
# deferrals stay inside the window until the ladder settles and they are
# paid down together.
# 30 is the fifth raise (owner standing authorization, 2026-08-31): the
# keybind tail landed and took the ledger to 27, and the arc's second tail
# prices at 28 -- AT the cap, so its own merge would be refused with the
# settle still owed. 30 leaves the settle margin plus room for one fix PR
# before the ladder pays the ledger down. The age window moves with it so
# the arc's own deferrals stay inside the window until the ladder settles
# and they are paid down together.
DEFER_MAX_OUTSTANDING = 30
DEFER_MAX_AGE_DAYS = 30
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
