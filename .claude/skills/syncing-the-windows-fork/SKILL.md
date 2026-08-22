---
name: syncing-the-windows-fork
description: Use when rebasing this fork onto upstream with `just sync` or checking one with `just sync-verify`, when resolving the conflicts a replay raises, when a rebase finishes clean but the build then breaks, when git reports `ambiguous argument 'windows'`, or when judging whether a replayed branch is safe to force-push.
---

# Syncing the Windows fork

## Quick reference

| Need | Command |
|---|---|
| Replay onto the latest upstream | `just sync` |
| Gate the result before pushing | `just sync-verify` |
| Structure only, no build ladder | `just sync-verify fast` |
| List what the fork changes | `git diff --name-only upstream/main...refs/heads/windows` |
| Smoke the C boundary | `just run-win` |
| Resume after fixing a conflict | `git add <file> && git rebase --continue` |
| Abandon the replay entirely | `git rebase --abort` |
| Fold a fix into an earlier commit | `git commit --fixup=<sha>`, then autosquash (below) |
| Publish | `git push --force-with-lease origin windows` |

The autosquash step needs an env var, so it differs by shell. In sh:
`GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash <sha>^`. In pwsh, which is
this repo's default shell: `$env:GIT_SEQUENCE_EDITOR=':'; git rebase -i --autosquash <sha>^`.

## Overview

`just sync` replays roughly 550 fork commits onto a much newer upstream. A
rebase that ends without conflicts, a file surface that still matches, and a
fully green `zig build test` are all compatible with a tree that does not
compile.

**Conflicts are textual; API compatibility is semantic. Git raises a conflict
only when both sides edited the same lines, so the breakage that matters most
is precisely the breakage that raises no conflict at all.**

A low conflict count is not reassurance. On a large upstream jump it means the
two sides edited different lines, which is the setup for every shape below.

## Shapes that never conflict

- **Fork code calls upstream code.** Upstream changes a function signature. The
  fork's call sites sit in fork-only files upstream never touched, so they
  replay clean and go stale. Seen here: a disk cache helper that gained a
  version parameter.
- **Upstream switches over a fork-extended type.** The fork adds members to an
  upstream enum; upstream later adds a file with an exhaustive `switch` over
  it. The file is upstream's, the members are the fork's, nothing overlaps.
  Seen here: kitty image formats gaining jpeg and gif.
- **C# P/Invoke drifts from the native side.** A changed struct layout, enum
  order, or signature behind `ghostty.dll` still compiles on both sides. It
  fails at the first call across the boundary, not at build time, so no build
  and no test catches it. An instant crash on a libghostty call means a stale
  or mismatched DLL; rebuild both halves.

Two mistakes come from the resolutions themselves. Dropping an import the rest
of the file still uses fails loudly. Resolving in upstream's favour and quietly
deleting fork behaviour does not fail at all: it compiles, tests pass, and the
feature is simply gone.

## What `just sync-verify` does for you

It runs the build ladder itself, `build-dll` then `test` then the C# legs on
Windows. `build-dll` is `-Dapp-runtime=none`, the cheapest leg that catches the
first two shapes; Zig analyses lazily, so `zig build test` never reaches code
no test instantiates. It also subtracts the fork's standing `zig fmt` offenders
so only new ones fail, though only under `src/`, which is narrower than the
whole-tree check upstream CI runs.

**It says what the replay landed on and how far that is from upstream.**
Every other leg measures the fork against `refs/remotes/upstream/main`, so the
gate first establishes what that ref is worth. It reports the merge base of
HEAD and the ref, the number of commits the ref has gained since, and whether
the ref is still what the remote has. Nothing fetches: fetching origin would
move the pre-rebase tip four checks compare against, and fetching upstream
would move the yardstick this leg exists to measure. It asks with
`git ls-remote`, which writes no ref.

Read the commit count. After a correct `just sync` it is zero or the handful
upstream pushed while the replay ran, because sync fetches and then rebases
onto what it fetched. A large number means the ref moved by a later fetch and
this is not a fresh replay waiting to be pushed, which is the only state the
rest of the gate is built for.

The FAIL here is the ref being stale: it differs from the remote and its tip
commit is a week or more old. Every later check reads that ref, so past that
distance they are comparing the fork against an upstream that has substantially
moved. The measure is the committer date of upstream's own tip commit, which
travels with the object. Fork-side dates and reflogs both looked tempting and
are both the wrong clock: a fetch that brings nothing new writes no reflog
entry, so a correct sync across a quiet weekend reads as stale, and any later
fetch makes a months-old replay read as current. No network, a remote that will
not answer, or a tracking ref whose remote has been removed all print SKIP or
REVIEW rather than passing quietly; verifying offline stays legal, it just
verifies less.

**A green run is not clearance.** Exit 1 is a hard finding, exit 2 is bad
arguments, and REVIEW items exit 0 on purpose, because an upstream rename moves
the file surface legitimately and a check that cries wolf gets a flag passed to
it instead of being read. The `range-diff` listing of changed commits never
affects the exit code and caps itself, though a range-diff that fails outright,
and the dropped-commit check driven by the same output, both can fail the run.
Read both before you push: a resolution that dropped a whole file's fork
changes surfaces in the file-surface REVIEW, and one that
dropped only part of a hunk surfaces nowhere but `range-diff`.

**It does not catch P/Invoke drift.** After a sync that touched the C
boundary, run `just run-win`, open a window and a split, and type in both.

## The pre-rebase tip

Four checks need the branch as it was before the replay: dropped commits, file
surface, `range-diff`, and the `zig fmt` baseline. All four read the ref this
branch tracks, which points at the old commit only until the force-push
replaces it. The first three then announce themselves as skipped; the `zig fmt`
one still runs but degrades to a REVIEW listing every standing offender,
because it can no longer tell the new ones from the old.

So run the gate before the push, and keep the old tip if you may want it:

```sh
git tag sync-backup-$(date +%F) "@{u}"
git push origin "sync-backup-$(date +%F)"
```

## Traps

**`windows` is an ambiguous ref.** A `windows/` directory exists, so
`git diff upstream/main windows` dies with "ambiguous argument". Use
`refs/heads/windows`, or end the revision list with `--`.

**Use three dots for the fork surface.**
`git diff --name-only upstream/main...refs/heads/windows` diffs from the merge
base, which is what the fork actually changes. The two-dot form shows every
upstream commit you have not merged as though the fork reverted it. Directly
after a clean rebase both agree, which is why the difference is easy to miss
and bites later.

**Never accept `zig fmt` collateral.** Running `zig fmt` on a file you just
resolved also reformats unrelated pre-existing deviations and folds them into
that commit. Keep only your own hunk. If the reformatted region was clean
before the replay, your local zig disagrees with upstream's, which is a
toolchain problem that will corrupt every file you format.

## Landing a fix found after the replay

Default to a new commit at the tip. Fold back with `git commit --fixup=<sha>`
and `GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash <sha>^` only when that
commit must build in isolation, which here means you are about to cherry-pick
it out of the stack.

Fold-back is not free. Replaying the commits above the fixup can raise fresh
conflicts, and each must be resolved with the API vintage correct *at that
point in history*, not the current one. Getting that wrong breaks a different
commit than the one you set out to fix.

## Cross-platform legs

**REQUIRED:** use the cross-platform-test skill for the host list and its
quirks.

One fact it does not carry: a host with no route to GitHub needs the commit
delivered by `git bundle`, and the bundle base must be a commit that host
already has. Ask it, rather than assuming it has the upstream tip. A host fed
by an earlier bundle may have no `refs/remotes/origin/*` at all, so fall back
to whatever it has checked out:

```sh
ssh HOST 'cd ~/CODE/OSS/ghostty && (git rev-parse --verify -q refs/remotes/origin/windows || git rev-parse HEAD)'
git bundle create sync.bundle <that-sha>..refs/heads/windows
```
