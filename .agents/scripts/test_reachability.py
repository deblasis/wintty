#!/usr/bin/env python3
"""Fail when a file's `test` blocks are in no test binary the build runs.

Zig collects `test` blocks from the files a test binary's own `test` and
`comptime` blocks reach, not from every file the root transitively imports.
An unreferenced `pub const` naming an import pulls nothing in. So a file can
sit in the tree full of green-looking assertions that no test step has ever
executed, which is worse than having no tests at all: it reads as coverage.
`src/build/GitVersion.zig` and `src/build/wasm_patch_growable_table.zig`
were both in exactly that state.

The oracle is exact rather than a guess about the import graph. Every test
binary the build runs is compiled and then asked, over the std.zig.Server
protocol the stock test runner speaks on `--listen=-`, for the fully
qualified name of every test it carries. A qualified name is the test's
module-relative path with `/` replaced by `.` and `.zig` dropped, so the
names map back onto files. A file with `test` blocks and no name of its own
in any binary is a finding.

Granularity is the file, not the individual test. That is the granularity the
defect has, and matching per test name would break on every rename.

Exit codes follow this repo's harness contract:
    0  every file with test blocks contributes tests to some binary
    1  the check could not run, so it never got as far as asserting
    2  a real finding: a file's test blocks are dead

Known limits, accepted deliberately. Only tracked files are scanned, so a
file that has not been added yet is invisible. And a binary registered with
`test-binaries` but hung off no run step would vouch for its files without
running them; what is enforced is the other direction, that no `addTest`
escapes registration.

Usage: python .agents/scripts/test_reachability.py
       python .agents/scripts/test_reachability.py --self-test
"""

import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading

EXIT_PASS = 0
EXIT_HARNESS = 1
EXIT_FINDING = 2

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

# `just test-full` builds with this, so these are the binaries that actually
# run here. Anything a different `-D` would have compiled instead is out of
# scope by construction, and is why the exclusions below exist.
BUILD_ARGS = ["test-binaries", "-Dapp-runtime=none"]

# Directories whose `test` blocks are knowingly not run, with the reason.
#
# Prefixes only, and every one ends in a slash so a prefix named for a
# directory cannot also swallow a sibling file. Both the reasons and the
# files each prefix took are printed on every run: an exclusion nobody is
# reminded of decays into the same silence this check exists to break, so
# there is no quiet mode and no way to add one without the output changing.
EXCLUSIONS = (
    (
        "pkg/",
        "vendored packages are separate builds. `zig build test` roots at "
        "src/main.zig and never compiles them, so no binary here can carry "
        "their tests. CI covers pkg/wuffs only, in test-pkg-linux. Making "
        "the rest reachable is a larger problem and not this check's.",
    ),
    (
        "src/apprt/gtk/",
        "the GTK apprt compiles only under -Dapp-runtime=gtk on Linux. The "
        "local ladder builds -Dapp-runtime=none, so these are absent from "
        "every binary this check can see, whatever their import graph says. "
        "Running the check on a GTK build would cover them.",
    ),
)

# Files this build cannot compile at all: another OS, another backend,
# another target. Their tests are expected to run on the host that does
# build them, so a covered entry here is normal rather than stale, and only
# a file that has stopped carrying test blocks retires the line. This is
# what keeps the check from turning red the moment it is run on a Mac.
NOT_BUILT_HERE = {
    "src/apprt/gtk.zig":
        "the GTK apprt compiles only under -Dapp-runtime=gtk",
    "src/font/face/coretext.zig":
        "src/font/face.zig selects it only for the CoreText backends, "
        "which are macOS",
    "src/font/shaper/coretext.zig":
        "the CoreText shaper is macOS only",
    "src/input/KeymapDarwin.zig":
        "src/input.zig selects it only for .macos",
    "src/os/mach.zig":
        "src/os/main.zig roots it only on Darwin",
    "src/os/macos.zig":
        "src/os/main.zig roots it only on Darwin",
    "src/os/kernel_info.zig":
        "src/os/main.zig roots it only on Linux",
    "src/renderer/metal/shaders.zig":
        "the Metal renderer is macOS only",
}

# The defect proper: the file is in the build's reach and nothing analyses
# it, so its assertions have never executed on any platform.
#
# This is a debt register, not an exemption list. Every entry is printed on
# every run, and an entry whose tests start running -- or whose file loses
# its test blocks -- fails the check so the line has to go. It can only
# shrink by accident.
NOT_ROOTED = {
    "src/apprt/ipc.zig":
        "src/apprt.zig's test block lists action and structs, not ipc",
    "src/config/path.zig":
        "config/Config.zig re-exports Path with a plain const, and "
        "refAllDecls in src/config.zig does not reach through it",
    "src/input/mouse.zig":
        "src/input.zig imports it into a private const, and refAllDecls "
        "does not see private decls",
    "src/lib/allocator/wasm.zig":
        "src/lib/allocator.zig names it with a plain pub const",
    "src/os/desktop.zig":
        "absent from src/os/main.zig's test block",
    "src/os/hostname.zig":
        "absent from src/os/main.zig's test block",
    "src/os/open.zig":
        "absent from src/os/main.zig's test block",
    "src/os/passwd.zig":
        "absent from src/os/main.zig's test block",
    "src/renderer/directx12/inspector_surface.zig":
        "apprt/embedded.zig names it with a plain const",
    "src/termio/wsl_shell_integration.zig":
        "src/termio.zig's test block roots shell_integration.zig and not "
        "this one, and Exec.zig only calls into it",
}

# The stock zig test runner has no --list flag, but it speaks std.zig.Server
# over stdio when started with --listen=-.
CLIENT_EXIT = 0
CLIENT_QUERY_TEST_METADATA = 4
SERVER_ERROR_BUNDLE = 1
SERVER_TEST_METADATA = 3

QUERY_TIMEOUT_SEC = 120


class HarnessError(Exception):
    """The check could not get far enough to assert anything."""


# --- source scanning -------------------------------------------------------


def strip_comments_and_literals(text):
    """Blank out comments and literals so a `test` inside one is not a block.

    Once comments, quoted strings, character literals and `\\\\` multiline
    string lines are gone, every remaining occurrence of the bare word is a
    test block: `test` is a keyword, and the one way to spell it as an
    identifier, `@"test"`, is a quoted string this has already removed.
    """
    out = []
    i = 0
    n = len(text)
    while i < n:
        c = text[i]
        # Line comments, doc comments and multiline string lines all run to
        # the end of the line and cannot nest.
        if c == "/" and text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c == "\\" and text.startswith("\\\\", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        if c in ('"', "'"):
            quote = c
            i += 1
            while i < n:
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == quote:
                    i += 1
                    break
                if text[i] == "\n":
                    # Unterminated: do not swallow the rest of the file.
                    break
                i += 1
            out.append(" ")
            continue
        out.append(c)
        i += 1
    return "".join(out)


TEST_KEYWORD = re.compile(r"(?<![A-Za-z0-9_])test(?![A-Za-z0-9_])")


def count_test_blocks(text):
    return len(TEST_KEYWORD.findall(strip_comments_and_literals(text)))


def git_zig_files(repo):
    proc = subprocess.run(
        ["git", "ls-files", "-z", "--", "*.zig"],
        cwd=repo,
        capture_output=True,
        text=True,
    )
    if proc.returncode != 0:
        raise HarnessError(f"git ls-files failed: {proc.stderr.strip()}")
    return sorted(p.replace("\\", "/") for p in proc.stdout.split("\0") if p)


def is_excluded(path):
    for prefix, _ in EXCLUSIONS:
        if path.startswith(prefix):
            return prefix
    return None


def scan_for_test_blocks(repo, paths):
    """Returns (included, excluded) as {path: test block count}."""
    included, excluded = {}, {}
    for path in paths:
        full = os.path.join(repo, path)
        try:
            with open(full, encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError as e:
            raise HarnessError(f"could not read {path}: {e}")
        count = count_test_blocks(text)
        if not count:
            continue
        if is_excluded(path):
            excluded[path] = count
        else:
            included[path] = count
    return included, excluded


# --- talking to the test binaries ------------------------------------------


def drain_in_background(stream, sink):
    """Keep reading a pipe so the writer can never block on a full buffer.

    Leaving stderr unread would deadlock the moment a binary printed more
    than a pipe buffer's worth, which is a hang rather than a verdict.
    """
    def pump():
        try:
            sink.append(stream.read() or b"")
        except OSError:
            pass

    thread = threading.Thread(target=pump, daemon=True)
    thread.start()
    return thread


def collected(sink):
    """The tail of whatever the background drain has picked up."""
    return b"".join(sink).decode("utf-8", "replace").strip()[-500:]


def read_exact(stream, count):
    buf = stream.read(count)
    if buf is None or len(buf) != count:
        raise HarnessError(f"unexpected EOF after {len(buf or b'')} of {count} bytes")
    return buf


def query_test_names(exe):
    """Every fully qualified test name a compiled test binary carries."""
    try:
        proc = subprocess.Popen(
            [exe, "--listen=-"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
    except OSError as e:
        raise HarnessError(f"could not start {exe}: {e}")
    # A binary that answers nothing would leave the read blocked forever, and
    # a gate that hangs is worse than one that fails: killing it turns that
    # into the EOF the reader already handles.
    watchdog = threading.Timer(QUERY_TIMEOUT_SEC, proc.kill)
    watchdog.daemon = True
    watchdog.start()
    stderr_sink = []
    stderr_pump = drain_in_background(proc.stderr, stderr_sink)
    try:
        proc.stdin.write(struct.pack("<II", CLIENT_QUERY_TEST_METADATA, 0))
        proc.stdin.flush()
        while True:
            tag, length = struct.unpack("<II", read_exact(proc.stdout, 8))
            body = read_exact(proc.stdout, length) if length else b""
            if tag == SERVER_ERROR_BUNDLE:
                raise HarnessError(f"{os.path.basename(exe)} returned an error bundle")
            # The server greets with zig_version before anything is asked of it.
            if tag != SERVER_TEST_METADATA:
                continue
            # string_bytes_len comes first, whatever the order the doc comment
            # on std.zig.Server.Message.TestMetadata lists the fields in.
            string_bytes_len, tests_len = struct.unpack("<II", body[:8])
            strings_at = 8 + 8 * tests_len
            strings = body[strings_at : strings_at + string_bytes_len]
            names = []
            for i in range(tests_len):
                (offset,) = struct.unpack("<I", body[8 + 4 * i : 12 + 4 * i])
                end = strings.index(b"\0", offset)
                names.append(strings[offset:end].decode("utf-8", "replace"))
            return names
    except HarnessError as e:
        # A binary that dies on startup shows up as an EOF and nothing else,
        # so its own last words are the only diagnostic there is.
        proc.kill()
        stderr_pump.join(timeout=5)
        raise HarnessError(f"{e}; stderr: {collected(stderr_sink) or '(empty)'}")
    except (OSError, struct.error, ValueError) as e:
        raise HarnessError(f"could not read test metadata from {exe}: {e}")
    finally:
        watchdog.cancel()
        try:
            proc.stdin.write(struct.pack("<II", CLIENT_EXIT, 0))
            proc.stdin.flush()
            proc.stdin.close()
        except OSError:
            pass
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()


def build_test_binaries(repo, prefix):
    """Compile every registered test binary.

    Returns [(exe path, module root directory)]. The roots come from the
    manifest build.zig writes beside the binaries: a qualified test name is
    relative to its module root, so without it `src/main.zig` and
    `src/terminal/main.zig` both answer to `main`.
    """
    if not shutil.which("zig"):
        raise HarnessError("zig is not on PATH")
    cmd = ["zig", "build"] + BUILD_ARGS + ["-p", prefix.replace("\\", "/")]
    print("building: " + " ".join(cmd), flush=True)
    proc = subprocess.run(cmd, cwd=repo)
    if proc.returncode != 0:
        raise HarnessError("`zig build test-binaries` failed; nothing to measure")

    out_dir = os.path.join(prefix, "test-binaries")
    manifest = os.path.join(out_dir, "roots.tsv")
    try:
        with open(manifest, encoding="utf-8") as f:
            lines = [ln for ln in f.read().splitlines() if ln.strip()]
    except OSError as e:
        raise HarnessError(f"could not read {manifest}: {e}")

    found = []
    for line in lines:
        name, _, root_source = line.partition("\t")
        if not root_source:
            raise HarnessError(f"malformed line in roots.tsv: {line!r}")
        exe = os.path.join(out_dir, name + (".exe" if os.name == "nt" else ""))
        if not os.path.isfile(exe):
            raise HarnessError(f"roots.tsv names {name} but {exe} is missing")
        root_dir = os.path.dirname(root_source.replace("\\", "/"))
        if not os.path.isdir(os.path.join(repo, root_dir)):
            raise HarnessError(f"{name} is rooted at {root_source}, which is not in the tree")
        found.append((exe, root_dir))
    if not found:
        raise HarnessError(f"no test binaries recorded in {manifest}")
    # `addTest` names default to "test", so two unnamed ones would install
    # over each other: two manifest lines, one file, and a whole module
    # measured twice while another goes unmeasured.
    if len({exe for exe, _ in found}) != len(found):
        raise HarnessError("two entries in roots.tsv install to the same file")
    return found


ADD_TEST_CALL = re.compile(r"(?<![A-Za-z0-9_])addTest\s*\(")


def registered_test_count(repo):
    """How many test binaries build.zig defines.

    A new `addTest` that is hooked to a run step but not to `test-binaries`
    would leave this check silently blind to a whole binary, which is the
    exact shape of the bug it exists to catch. Comparing the two counts turns
    that into a loud harness failure instead.
    """
    path = os.path.join(repo, "build.zig")
    try:
        with open(path, encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        raise HarnessError(f"could not read build.zig: {e}")
    return len(ADD_TEST_CALL.findall(strip_comments_and_literals(text)))


# --- matching names back onto files ----------------------------------------


def qualified_name(path, root_dir):
    """What `path` would be called inside a module rooted at `root_dir`."""
    if root_dir:
        if not path.startswith(root_dir + "/"):
            return None
        path = path[len(root_dir) + 1 :]
    return path[: -len(".zig")].replace("/", ".")


TEST_MARKER = re.compile(r"\.(?:test|decltest)\.")
ANON_TEST = re.compile(r"(.+)\.test_\d+\Z")


def owners_in_name(name):
    """Qualified file names a test name could belong to.

    `<file>.test.<name>` for a named test, `<file>.decltest.<decl>` for a decl
    test, `<file>.test_<n>` for an anonymous one. Every split point is offered
    rather than one, because both a directory called `test` and a test whose
    own name contains `.test.` would otherwise pick the wrong one. Which of
    them is real is settled by the caller, against the files that exist.
    """
    out = {name[: m.start()] for m in TEST_MARKER.finditer(name)}
    anon = ANON_TEST.match(name)
    if anon:
        out.add(anon.group(1))
    return out


def evaluate(files_with_tests, all_files, binaries):
    """Split the scanned files into covered and dead.

    `binaries` is [(module root directory, [qualified test names])]. Owners
    are resolved per binary rather than pooled, because two modules rooted at
    different depths can give different files the same qualified name and
    pooling would let one binary's name vouch for a file the other holds.

    A single name can still fit more than one candidate file: `foo/test.zig`
    with an anonymous test and `foo.zig` with a test called `test_0` are both
    spelled `foo.test.test_0`. Candidates that name no real file are dropped;
    if two survive, the check says so and refuses to guess.
    """
    covered, dead, conflicts = [], [], []
    hits = set()
    for root_dir, names in binaries:
        real = {}
        for path in all_files:
            qualified = qualified_name(path, root_dir)
            if qualified is not None:
                real[qualified] = path
        for name in names:
            claimed = [real[o] for o in owners_in_name(name) if o in real]
            if len(claimed) > 1:
                conflicts.append((name, claimed[0], claimed[1]))
            elif claimed:
                hits.add(claimed[0])

    for path in sorted(files_with_tests):
        (covered if path in hits else dead).append(path)
    return covered, dead, conflicts


# --- the check itself ------------------------------------------------------


def check(repo):
    paths = git_zig_files(repo)
    included, excluded = scan_for_test_blocks(repo, paths)

    for prefix, reason in EXCLUSIONS:
        took = sorted(p for p in excluded if p.startswith(prefix))
        print(f"excluded: {prefix} ({len(took)} files with test blocks)")
        print(f"          {reason}")
        for path in took:
            print(f"          {path}: {excluded[path]} test block(s)")
    print(
        f"scanned {len(paths)} tracked .zig files: {len(included)} carry test "
        f"blocks, {len(excluded)} more are excluded above"
    )
    if not included:
        raise HarnessError("no files with test blocks were found; the scanner is broken")

    prefix_dir = tempfile.mkdtemp(prefix="test-reachability-")
    try:
        binaries = build_test_binaries(repo, prefix_dir)
        registered = registered_test_count(repo)
        if registered != len(binaries):
            raise HarnessError(
                f"build.zig defines {registered} test binaries but "
                f"test-binaries produced {len(binaries)}; every addTest must "
                "be registered with installTestBinary or this check is blind "
                "to one of them"
            )
        measured, total = [], 0
        for exe, root_dir in binaries:
            names = query_test_names(exe)
            print(f"  {os.path.basename(exe)}: {len(names)} tests rooted at {root_dir}/")
            if not names:
                raise HarnessError(
                    f"{os.path.basename(exe)} carries no tests at all; a "
                    "binary that measures nothing cannot vouch for anything"
                )
            measured.append((root_dir, names))
            total += len(names)
    finally:
        shutil.rmtree(prefix_dir, ignore_errors=True)

    covered, dead, conflicts = evaluate(included, paths, measured)
    if conflicts:
        raise HarnessError(
            "a qualified test name fits two source files, so the check "
            "cannot say which one it came from: "
            + "; ".join(f"{n} -> {a} or {b}" for n, a, b in conflicts[:3])
        )
    print(f"{total} tests across {len(binaries)} binaries cover {len(covered)} files")

    covered_set = set(covered)
    stale = []
    for path in sorted(NOT_ROOTED):
        if path in covered_set:
            stale.append((path, "its tests now run"))
    for path in sorted(set(NOT_BUILT_HERE) | set(NOT_ROOTED)):
        if path not in included and path not in excluded:
            stale.append((path, "it no longer has test blocks the check scans"))
    new = [p for p in dead if p not in NOT_ROOTED and p not in NOT_BUILT_HERE]

    print(f"{len(NOT_BUILT_HERE)} files cannot be built here:")
    for path, reason in sorted(NOT_BUILT_HERE.items()):
        print(f"  {path}: {reason}")
    print(f"{len(NOT_ROOTED)} files are in reach but nothing roots them:")
    for path, reason in sorted(NOT_ROOTED.items()):
        print(f"  {path}: {reason}")

    if new:
        print("")
        print("FAIL: test blocks that no test binary carries")
        for path in new:
            print(f"  {path}: {included[path]} test block(s), never run")
        print("")
        print(
            "Root the file from a test binary the build runs, or register it "
            "in NOT_ROOTED or NOT_BUILT_HERE with the reason."
        )

    if stale:
        print("")
        print("FAIL: registered entries whose reason has expired")
        for path, why in stale:
            print(f"  {path}: {why}; drop the entry")

    if new or stale:
        return EXIT_FINDING

    print("test reachability: pass")
    return EXIT_PASS


# --- self-test -------------------------------------------------------------


SELF_TEST_SOURCE = """\
//! test in a doc comment
const std = @import("std");
// test in a line comment
const s = "test in a string";
const m =
    \\\\test in a multiline string
;
const c = '"'; // an apostrophe-free char literal
const @"test" = 1;

test "a named one" {}
test {}
"""


def self_test():
    """Exercise the scanner, the matcher and the verdict without a build.

    The end-to-end proof -- drop a file nothing imports, watch it exit 2 --
    needs a full compile and belongs to the real run. What is checked here is
    everything between the compiler and the exit code, which is where the
    quiet failure modes live.
    """
    failures = []

    def expect(label, got, want):
        if got != want:
            failures.append(f"{label}: got {got!r}, want {want!r}")
        else:
            print(f"ok   {label}")

    expect("scanner ignores comments and literals", count_test_blocks(SELF_TEST_SOURCE), 2)
    expect("scanner on an empty file", count_test_blocks(""), 0)
    expect(
        "scanner counts a decl test",
        count_test_blocks("test someDecl {}\n"),
        1,
    )
    expect(
        "a file is named relative to its module root",
        qualified_name("src/build/GitVersion.zig", "src/build"),
        "GitVersion",
    )
    expect(
        "and differently under a shallower root",
        qualified_name("src/build/GitVersion.zig", "src"),
        "build.GitVersion",
    )
    expect(
        "a file outside the root belongs to no name",
        qualified_name("src/main.zig", "src/build"),
        None,
    )

    files = ["src/build/GitVersion.zig", "src/build/orphan.zig"]
    covered, dead, conflicts = evaluate(
        dict.fromkeys(files, 1),
        files,
        [("src/build", ["GitVersion.test.detect", "GitVersion.test_0"])],
    )
    expect("a rooted file is covered", covered, ["src/build/GitVersion.zig"])
    expect("an unrooted file is a finding", dead, ["src/build/orphan.zig"])
    expect("with nothing in dispute", conflicts, [])

    # The bug the module root exists to prevent: without it both files answer
    # to `main`, and src/main.zig would be vouched for by src/terminal's tests.
    files = ["src/main.zig", "src/terminal/main.zig"]
    covered, dead, _ = evaluate(
        dict.fromkeys(files, 1), files, [("src", ["terminal.main.test.something"])]
    )
    expect("a same-basename file elsewhere does not vouch", covered, ["src/terminal/main.zig"])
    expect("so the root file is still a finding", dead, ["src/main.zig"])

    files = ["src/terminal/Screen.zig"]
    covered, dead, _ = evaluate(
        dict.fromkeys(files, 1),
        files,
        [("src", ["terminal.Screen.test.a name with .test. inside"])],
    )
    expect("a test name containing .test. still matches", covered, files)
    expect("and produces no finding", dead, [])

    files = ["src/build/test.zig"]
    covered, dead, _ = evaluate(
        dict.fromkeys(files, 1), files, [("src", ["build.test.test_0"])]
    )
    expect("a file called test.zig matches its own anonymous test", covered, files)
    expect("with no finding", dead, [])

    # `foo/test.zig`'s anonymous test and `foo.zig`'s test called `test_0`
    # are spelled identically. Refusing beats crediting the wrong one.
    files = ["src/foo.zig", "src/foo/test.zig"]
    _, _, conflicts = evaluate(
        dict.fromkeys(files, 1), files, [("src", ["foo.test.test_0"])]
    )
    expect("a name that fits two files is refused", len(conflicts), 1)

    expect("pkg/ is excluded", is_excluded("pkg/wuffs/src/gif.zig"), "pkg/")
    expect("src/ is not", is_excluded("src/terminal/Screen.zig"), None)
    expect(
        "a sibling file is not swallowed by a directory exclusion",
        is_excluded("src/apprt/gtk.zig"),
        None,
    )
    expect(
        "every exclusion prefix names a directory",
        [p for p, _ in EXCLUSIONS if not p.endswith("/")],
        [],
    )
    expect(
        "no file is registered as both unbuildable and unrooted",
        sorted(set(NOT_BUILT_HERE) & set(NOT_ROOTED)),
        [],
    )

    if failures:
        for f in failures:
            print(f"FAIL {f}")
        print("SELF-TEST FAILED")
        return EXIT_HARNESS
    print("SELF-TEST PASSED")
    return EXIT_PASS


def main(argv):
    if "--self-test" in argv:
        return self_test()
    if argv:
        print(__doc__)
        return EXIT_HARNESS
    try:
        return check(REPO_ROOT)
    except HarnessError as e:
        print(f"harness: {e}", file=sys.stderr)
        return EXIT_HARNESS


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
