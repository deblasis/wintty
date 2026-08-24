---
name: syncing-the-windows-fork
description: Use when syncing this fork with upstream via `just sync` / `just sync-verify` / `just sync-publish`, when resolving the conflicts a replay raises, when a rebase finishes clean but the build then breaks, when git reports `ambiguous argument 'windows'`, when a publish is rejected, or when judging whether a replayed series is safe to publish.
---

# Syncing the Windows fork

## The shape of the flow

`windows` is never rebased and never force-pushed. Each sync lands on it as
one merge commit whose tree is taken wholesale from a verified replay, so the
branch only moves forward and branch protection can hold on it.

The replay still happens, on a separate lineage:

- **`series/vN` tags** hold the fork as a linear patch series, rebased onto
  the upstream of its day. Conflicts are resolved here, and `sync-verify`
  measures here.
- **`windows`** is what everything consumes: PRs squash-merge into it, the
  nightly tracks it, wintty-release pins point into it.

The invariant tying them together: the last snapshot merge on `windows`
carries the tree of the latest `series/vN` tag. `just sync` checks it before
touching anything, because the fold-in step relies on it.

## Quick reference

| Need | Command |
|---|---|
| Replay onto the latest upstream | `just sync` |
| Gate the result before publishing | `just sync-verify` |
| Structure only, no build ladder | `just sync-verify fast` |
| Publish | `just sync-publish` |
| List what the fork changes | `git diff --name-only upstream/main...refs/heads/windows` |
| Smoke the C boundary | `just run-win` |
| Resume after fixing a conflict | `git add <file> && git rebase --continue` |
| Abandon the replay entirely | `git rebase --abort`, then `git branch -D series-wip` |
| Fold a fix into an earlier commit | `git commit --fixup=<sha>`, then autosquash (below) |
| One-time cutover from the force-push flow | `just sync-bootstrap` |
| Self-test the whole flow on a fixture | `bash .agents/scripts/syncflow-selftest.sh` |

The autosquash step needs an env var, so it differs by shell. In sh:
`GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash <sha>^`. In pwsh, which is
this repo's default shell: `$env:GIT_SEQUENCE_EDITOR=':'; git rebase -i --autosquash <sha>^`.

## What `just sync` does

It builds the candidate on the `series-wip` branch: check out the latest
series tag, cherry-pick the PRs `windows` merged since the last snapshot (the
fold-in), rebase everything onto `upstream/main`. The fold-in applies clean by
construction - at the last snapshot both sides had the same tree - so on a
fresh replay a fold-in conflict means the invariant broke, not that you
resolved something wrong.

Re-running `just sync` is safe at every point and is also the recovery path:
a `series-wip` equal to the latest tag is rebuilt, one holding an unpublished
replay is resumed, and the fold-in is by patch-id so nothing is picked twice.
A publish that lost a race to a mid-sync PR merge is retried by exactly this.

## Overview of what can go wrong

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
the ref is still what the remote has. Nothing fetches: fetching upstream would
move the yardstick this leg exists to measure. It asks with `git ls-remote`,
which writes no ref.

Read the commit count. After a correct `just sync` it is exactly zero, because
sync fetches and then rebases onto what it fetched and nothing moves the ref in
between. Commits upstream pushed while you were replaying are not in the ref
and cannot appear here. Any non-zero number means the ref moved by a later
fetch and this is not a fresh replay waiting to be published, which is the only
state the rest of the gate is built for.

The FAIL here is the ref being stale: its tip commit is a week or more old and
nothing established that it is current, whether because it differs from the
remote, because the remote could not be reached, or because no remote is
configured to refresh it. Every later check reads that ref, so past that
distance they are comparing the fork against an upstream that has substantially
moved. The measure is the committer date of upstream's own tip commit, which
travels with the object. Fork-side dates and reflogs both looked tempting and
are both the wrong clock: a fetch that brings nothing new writes no reflog
entry, so a correct sync across a quiet weekend reads as stale, and any later
fetch makes a months-old replay read as current. It also fails when the local
ref turns out to be ahead of the remote, which means upstream rewound and the
replay is built on commits that no longer exist there. That check only fires
when the remote tip is already in the local object database, which is what a
plain rewind leaves behind; a rewind followed by new commits upstream cannot be
told from an ordinary advance without fetching, and is reported as one.

No network, and a tracking ref with no remote behind it, are both REVIEW rather
than a bare note, so the verdict names them instead of ending in an unqualified
PASSED. Verifying offline stays legal, it just verifies less, and if the ref is
also a week old it fails anyway: a ref nothing can refresh is the state where
its age matters most.

**A green run is not clearance.** Exit 1 is a hard finding, exit 2 is bad
arguments, and REVIEW items exit 0 on purpose, because an upstream rename moves
the file surface legitimately and a check that cries wolf gets a flag passed to
it instead of being read. The `range-diff` listing of changed commits never
affects the exit code and caps itself, though a range-diff that fails outright,
and the dropped-commit check driven by the same output, both can fail the run.
Read both before you publish: a resolution that dropped a whole file's fork
changes surfaces in the file-surface REVIEW, and one that
dropped only part of a hunk surfaces nowhere but `range-diff`.

**It does not catch P/Invoke drift.** After a sync that touched the C
boundary, run `just run-win`, open a window and a split, and type in both.

## The pre-rebase baseline

Four checks compare the replay against the series it started from: dropped
commits, file surface, `range-diff`, and the `zig fmt` baseline. On
`series-wip` that baseline is the latest `series/vN` tag, which is durable -
nothing about publishing destroys it, so there is no backup tag to keep and no
window that closes at the push. What publishing does do is tag the replay as
the new latest, after which the four announce themselves as skipped because
there is no longer an older series to compare against; that is the expected
end state, not a loss.

Run the gate between `just sync` and `just sync-publish`, on `series-wip`.

## What `just sync-publish` does

Builds one merge commit whose parents are the current `origin/windows` and
`upstream/main` and whose tree is exactly the `series-wip` tree, tags the
replay as the next `series/vN`, and pushes both atomically. `git merge` cannot
express this - it would re-merge and could resolve differently than the replay
did - so the commit is built with `git commit-tree` and an equality check
guards the result.

The push is deliberately not forced. The two ways it fails:

- **Rejected non-fast-forward:** a PR merged into `windows` mid-sync. The tag
  is rolled back automatically; re-run `just sync` (it resumes the replay and
  folds the new commits in by patch-id), re-verify, publish again.
- **The invariant check refuses at the next `just sync`:** the last snapshot's
  tree no longer matches the latest series tag. Someone pushed to `windows`
  outside the PR flow or moved a series tag; find out which before replaying.

## Traps

**`windows` is an ambiguous ref.** A `windows/` directory exists, so
`git diff upstream/main windows` dies with "ambiguous argument". Use
`refs/heads/windows`, or end the revision list with `--`.

**Use three dots for the fork surface.**
`git diff --name-only upstream/main...refs/heads/windows` diffs from the merge
base, which is what the fork actually changes. The two-dot form shows every
upstream commit you have not merged as though the fork reverted it. Directly
after a publish both agree, which is why the difference is easy to miss and
bites later.

**Never accept `zig fmt` collateral.** Running `zig fmt` on a file you just
resolved also reformats unrelated pre-existing deviations and folds them into
that commit. Keep only your own hunk. If the reformatted region was clean
before the replay, your local zig disagrees with upstream's, which is a
toolchain problem that will corrupt every file you format.

## Landing a fix found after the replay

Before publishing, default to a new commit at the tip of `series-wip`. Fold
back with `git commit --fixup=<sha>` and
`GIT_SEQUENCE_EDITOR=: git rebase -i --autosquash <sha>^` only when that
commit must build in isolation, which here means you are about to cherry-pick
it out of the stack.

Fold-back is not free. Replaying the commits above the fixup can raise fresh
conflicts, and each must be resolved with the API vintage correct *at that
point in history*, not the current one. Getting that wrong breaks a different
commit than the one you set out to fix.

After publishing, a fix is just a normal PR into `windows`; the next sync
folds it into the series automatically.

## Cross-platform legs

**REQUIRED:** use the cross-platform-test skill for the host list and its
quirks. The commit to ship to the other hosts is the replay, i.e.
`refs/heads/series-wip`, not `windows`.

One fact it does not carry: a host with no route to GitHub needs the commit
delivered by `git bundle`, and the bundle base must be a commit that host
already has. Ask it, rather than assuming it has the upstream tip. A host fed
by an earlier bundle may have no `refs/remotes/origin/*` at all, so fall back
to whatever it has checked out:

```sh
ssh HOST 'cd ~/CODE/OSS/ghostty && (git rev-parse --verify -q refs/remotes/origin/windows || git rev-parse HEAD)'
git bundle create sync.bundle <that-sha>..refs/heads/series-wip
```
