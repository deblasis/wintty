#!/usr/bin/env bash
# Self-test for the snapshot-merge sync flow (just sync / sync-bootstrap /
# sync-verify / sync-publish). Builds a synthetic upstream + origin + two
# working clones in a temp dir and drives the real recipes from this repo's
# justfile through: bootstrap, a plain sync, a fold-in sync, a publish that
# loses a race to a mid-sync PR merge, a conflicted replay, a second clone
# joining mid-series with stale tags, a stale leftover replay that must
# be refused rather than published, and a publish carrying work no PR
# reviewed that must be named, refused, and only land with an acknowledgement
# recorded in the merge message. The fixture upstream carries a merge
# commit on purpose: the snapshot detection must not mistake upstream's own
# PR merges for the fork's snapshots.
#
# Exit 0 all green, 1 findings. No network, no writes outside the temp dir.
set -euo pipefail

JUSTFILE="$(cd "$(dirname "$0")/../.." && pwd)/justfile"
# A SHORT temp path on purpose: the fixture pushes into a bare repo, and
# git's tmp_objdir names under a deep root exceed MAX_PATH on Windows.
ROOT=$(mktemp -d "${TMP:-/tmp}/syncflow-XXXXXX")
# just is a native Windows binary under git-bash and cannot read the POSIX
# spellings; bash itself accepts the Windows ones, so convert once and use
# them everywhere.
if command -v cygpath >/dev/null 2>&1; then
  JUSTFILE=$(cygpath -m "$JUSTFILE")
  ROOT=$(cygpath -m "$ROOT")
fi
PASS=0
FAIL=0

say()  { printf '\n### %s\n' "$*"; }
ok()   { printf 'ok   - %s\n' "$*"; PASS=$((PASS+1)); }
nope() { printf 'FAIL - %s\n' "$*"; FAIL=$((FAIL+1)); }

check() { # check <desc> <cmd...>  (passes when cmd exits 0)
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$desc"; else nope "$desc"; fi
}
check_eq() { # check_eq <desc> <a> <b>
  if [ "$2" = "$3" ]; then ok "$1"; else nope "$1 ('$2' != '$3')"; fi
}
# A refusal that fails for the WRONG reason must not pass the test that
# names the right one, so every negative test asserts on the message too.
expect_fail() { # expect_fail <desc> <output-pattern> <cmd...>
  local desc="$1" pat="$2" out; shift 2
  if out=$("$@" 2>&1); then
    nope "$desc (unexpectedly succeeded)"
  elif printf '%s' "$out" | grep -q "$pat"; then
    ok "$desc"
  else
    nope "$desc (failed for the wrong reason: $(printf '%s' "$out" | tail -3 | tr '\n' ' '))"
  fi
}
quiet() { # quiet <desc> <cmd...>  (passes AND counts when cmd exits 0)
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$desc"; else nope "$desc"; fi
}

# Identity stays inside the fixture repos; nothing here may touch the
# invoking user's git config.
G="git -c user.name=fixture -c user.email=fixture@test.invalid"
J()   { ( cd "$ROOT/work" && just --justfile "$JUSTFILE" --working-directory "$ROOT/work" "$@" ); }
Jrc() { ( cd "$ROOT/rc"   && just --justfile "$JUSTFILE" --working-directory "$ROOT/rc"   "$@" ); }

cd "$ROOT"

say "setup: upstream (with a merge commit), origin, work"
git init -q -b main up
( cd up
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8\n' > a.txt
  echo lib1 > lib.txt
  $G add -A && $G commit -qm "u1: base"
  echo lib2 >> lib.txt
  $G add -A && $G commit -qm "u2: lib"
  # Upstream's own PR-merge shape: the snapshot detection walks first-parent
  # merges and must never land on one of these.
  git checkout -q -b topic
  echo topiclib > topic.txt
  $G add -A && $G commit -qm "u-t: topic work"
  git checkout -q main
  $G merge --no-ff -q -m "u-merge: upstream merges its own PR" topic
)
git init -q --bare origin.git
git init -q -b windows work
( cd work
  git remote add origin ../origin.git
  git remote add upstream ../up
  git fetch -q upstream
  git reset -q --hard upstream/main
  echo fork > fork.txt
  $G add -A && $G commit -qm "f1: fork file"
  # Big enough that range-diff pairs the hand-resolved twin later; a one-line
  # patch with drifted context falls under the default creation-factor and
  # would read as dropped, which is a limitation of tiny fixtures, not of the
  # gate.
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8\nfork1\nfork2\nfork3\nfork4\nfork5\nfork6\n' > a.txt
  $G add -A && $G commit -qm "f2: fork edit of a.txt"
  git push -q origin windows
)

say "guard: sync before bootstrap refuses"
expect_fail "sync refused without series tag" "no series/v\* tag" J sync

say "test 1: bootstrap"
J sync-bootstrap
check "series/v0 exists locally" git -C work rev-parse --verify refs/tags/series/v0
check "series/v0 pushed to origin" git -C origin.git rev-parse --verify refs/tags/series/v0
expect_fail "second bootstrap refused" "already exist" J sync-bootstrap

say "test 2: first sync (upstream moved, nothing to fold) + publish"
( cd up && echo lib3 >> lib.txt && $G add -A && $G commit -qm "u3: first sync delta" )
J sync
check "series rebased onto upstream" git -C work merge-base --is-ancestor refs/remotes/upstream/main series-wip
check_eq "series-wip stamped with its generation" "$(git -C work config --get branch.series-wip.seriesbase)" "series/v0"
quiet "sync-verify fast (first sync)" J sync-verify fast
if out=$(J sync-publish 2>&1) && ! printf '%s' "$out" | grep -q "REFUSING\|unreviewed"; then
  ok "a clean publish is quiet about unreviewed work"
else
  nope "a clean publish is quiet about unreviewed work"
fi
M1=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "snapshot M1 is a merge of old tip + upstream" \
  "$(git -C work rev-parse "$M1^1" "$M1^2" | tr '\n' ' ')" \
  "$(git -C work rev-parse 'refs/tags/series/v0^{commit}' refs/remotes/upstream/main | tr '\n' ' ')"
check_eq "M1 tree == series tree" "$(git -C work rev-parse "$M1^{tree}")" "$(git -C work rev-parse 'series-wip^{tree}')"
check "series/v1 pushed" git -C origin.git rev-parse --verify refs/tags/series/v1
check_eq "local windows fast-forwarded" "$(git -C work rev-parse refs/heads/windows)" "$M1"

say "test 3: upstream moves + a PR lands, then sync folds it"
( cd up && echo lib4 >> lib.txt && $G add -A && $G commit -qm "u4: more lib" )
( cd work
  git checkout -q windows
  echo pr > pr.txt
  $G add -A && $G commit -qm "p1: a PR squash commit"
  git push -q origin windows
)
J sync
check "p1 folded into series (pr.txt present)" git -C work cat-file -e 'series-wip:pr.txt'
check "series rebased onto u4" git -C work merge-base --is-ancestor refs/remotes/upstream/main series-wip
quiet "sync-verify fast (fold)" J sync-verify fast
J sync-publish
M2=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "M2 first parent is pre-sync windows tip (p1)" "$(git -C work log -1 --format=%s "$M2^1")" "p1: a PR squash commit"
check_eq "M2 tree == series tree" "$(git -C work rev-parse "$M2^{tree}")" "$(git -C work rev-parse 'series-wip^{tree}')"
check "series/v2 pushed" git -C origin.git rev-parse --verify refs/tags/series/v2

say "test 4: publish loses a race, resume folds the late PR"
( cd up && echo lib5 >> lib.txt && $G add -A && $G commit -qm "u5: even more lib" )
J sync
git clone -q "$ROOT/origin.git" "$ROOT/rc"
( cd rc
  git checkout -q windows
  echo late > late.txt
  $G add -A && $G commit -qm "p2: raced PR"
  git push -q origin windows
)
expect_fail "raced publish refused before the push, naming the PR" "p2: raced PR" J sync-publish
if git -C work rev-parse --verify -q refs/tags/series/v3 >/dev/null; then nope "series/v3 rolled back after rejection"; else ok "series/v3 rolled back after rejection"; fi
J sync
check "p2 folded on resume" git -C work cat-file -e 'series-wip:late.txt'
check_eq "no duplicate p1 after resume (one pr.txt commit in series)" \
  "$(git -C work log --oneline refs/remotes/upstream/main..series-wip -- pr.txt | wc -l | tr -d ' ')" "1"
J sync-publish
check "series/v3 pushed" git -C origin.git rev-parse --verify refs/tags/series/v3

say "test 5: conflicting upstream change, resolve, publish"
( cd up && printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8-upstream\n' > a.txt && $G add -A && $G commit -qm "u6: conflicts with f2" )
expect_fail "sync stopped on conflict" "CONFLICT\|could not apply" J sync
check "rebase left in progress for the operator" test -d "$(git -C "$ROOT/work" rev-parse --absolute-git-dir)/rebase-merge"
( cd work
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8-upstream\nfork1\nfork2\nfork3\nfork4\nfork5\nfork6\n' > a.txt
  $G add a.txt
  GIT_EDITOR=true $G rebase --continue
)
quiet "sync-verify fast (after conflict)" J sync-verify fast
J sync-publish
M4=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "resolved content published" "$(git -C work show "$M4:a.txt" | sed -n '8p;9p' | tr '\n' '|')" "base8-upstream|fork1|"

say "test 6: a second clone with stale tags joins and publishes"
# rc was cloned before v3/v4 existed, so its series tags are stale by two
# generations - exactly the state a plain fetch cannot repair.
( cd up && echo lib6 >> lib.txt && $G add -A && $G commit -qm "u7: second-clone era" )
( cd rc && git remote add upstream ../up )
Jrc sync
check_eq "rc fetched the newest series tag" \
  "$(git -C rc tag --list 'series/v*' | sed 's|^series/v||' | sort -n | tail -1)" "4"
check "rc series carries the raced PR" git -C rc cat-file -e 'series-wip:late.txt'
quiet "sync-verify fast (second clone)" Jrc sync-verify fast
Jrc sync-publish
check "series/v5 pushed by the second clone" git -C origin.git rev-parse --verify refs/tags/series/v5

say "test 7: a stale leftover replay is refused, not published"
( cd up && echo lib7 >> lib.txt && $G add -A && $G commit -qm "u8: stale-wip era" )
J sync
check_eq "work wip stamped v5" "$(git -C work config --get branch.series-wip.seriesbase)" "series/v5"
( cd rc
  git checkout -q windows
  git pull -q --ff-only origin windows
  echo pr3 > pr3.txt
  $G add -A && $G commit -qm "p3: PR landed elsewhere"
  git push -q origin windows
)
Jrc sync
Jrc sync-publish
check "series/v6 pushed with the elsewhere PR" git -C origin.git rev-parse --verify refs/tags/series/v6
expect_fail "stale work replay refused on sync" "REFUSING: series-wip was built from" J sync
expect_fail "stale work replay refused on publish" "REFUSING: series-wip was built from" J sync-publish
( cd work && git checkout -q windows && git branch -q -D series-wip )
J sync
check "rebuilt replay carries the elsewhere PR" git -C work cat-file -e 'series-wip:pr3.txt'
if out=$(J sync-publish 2>&1) && printf '%s' "$out" | grep -q "nothing to publish"; then
  ok "publish no-ops when windows already carries the tree"
else
  nope "publish no-ops when windows already carries the tree"
fi

say "test 8: unreviewed work on the wip is named, refused, and needs an ack"
( cd up && echo lib8 >> lib.txt && $G add -A && $G commit -qm "u9: unreviewed era" )
J sync
( cd work
  git checkout -q series-wip
  echo direct > direct.txt
  $G add -A && $G commit -qm "w1: direct work, never through a PR"
)
expect_fail "unreviewed publish refused and the commit named" "w1: direct work" J sync-publish
if git -C work rev-parse --verify -q refs/tags/series/v7 >/dev/null; then
  nope "series/v7 not minted by a refused publish"
else
  ok "series/v7 not minted by a refused publish"
fi
J sync-publish yes
check "series/v7 pushed on the acknowledged publish" git -C origin.git rev-parse --verify refs/tags/series/v7
check "acknowledged work reached windows" git -C work cat-file -e 'refs/remotes/origin/windows:direct.txt'
M8=$(git -C origin.git rev-parse refs/heads/windows)
if git -C work log -1 --format=%B "$M8" | grep -q "w1: direct work"; then
  ok "the acknowledged commit is recorded in the snapshot merge message"
else
  nope "the acknowledged commit is recorded in the snapshot merge message"
fi
if git -C work log -1 --format=%B "$M8" | grep -q "acknowledged at publish"; then
  ok "the merge message marks the acknowledgement itself"
else
  nope "the merge message marks the acknowledgement itself"
fi

say "test 9: a hand-resolved fold-in still publishes (pairing, not patch-id)"
( cd up && echo lib9 >> lib.txt && $G add -A && $G commit -qm "u10: resolved-fold era" )
J sync
( cd work
  git checkout -q windows
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8\nfork1\nfork2\nfork3\nfork4\nfork5\nfork6\np4-line\n' > a.txt
  $G add -A && $G commit -qm "p4: PR that conflicts with the stack's f2"
  git push -q origin windows
)
# The fold-in of p4 onto the standing replay conflicts (the recovery path
# SKILL.md documents); resolve by hand and continue the pick. The resolved
# carry has a different patch-id than p4 - patch-id containment would refuse
# this publish forever; range-diff pairing must let it through.
( cd work
  git checkout -q series-wip
  git cherry-pick refs/remotes/origin/windows >/dev/null 2>&1 || true
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8\nfork1\nfork2\nfork3\nfork4\nfork5\nfork6\np4-line\n' > a.txt
  $G add a.txt
  GIT_EDITOR=true $G cherry-pick --continue >/dev/null 2>&1 || true
)
if out=$(J sync-publish 2>&1) && ! printf '%s' "$out" | grep -q "REFUSING"; then
  ok "a hand-resolved fold-in publishes"
else
  nope "a hand-resolved fold-in publishes ($(printf '%s' "$out" | tail -3 | tr '\n' ' '))"
fi
check "the resolved fold's content reached windows" \
  git -C work cat-file -e 'refs/remotes/origin/windows:a.txt'
if git -C work show 'refs/remotes/origin/windows:a.txt' | grep -q "p4-line"; then
  ok "the resolved fold's change is in the published tree"
else
  nope "the resolved fold's change is in the published tree"
fi

say "test 10: new work cannot hide behind an old fork commit's subject"
( cd up && echo lib10 >> lib.txt && $G add -A && $G commit -qm "u11: sneaky era" )
J sync
( cd work
  git checkout -q series-wip
  echo sneaky > sneaky.txt
  $G add -A && $G commit -qm "f1: fork file"
)
expect_fail "a reused deep-history subject is still refused" "f1: fork file" J sync-publish
( cd work && git checkout -q windows && git branch -q -D series-wip )
# The guards below need a standing wip; rebuild one without the sneaky work.
J sync

say "guards"
( cd work && git checkout -q -b random "$M4" )
expect_fail "sync refused on a random branch" "WARNING" J sync
expect_fail "publish refused off series-wip" "REFUSING: publish runs from" J sync-publish
( cd work && git checkout -q series-wip && echo dirt >> a.txt )
expect_fail "sync refused on dirty tree" "not clean" J sync
( cd work && git checkout -q -- a.txt )

printf '\n=== %d passed, %d failed ===\n' "$PASS" "$FAIL"
if [ "$FAIL" -eq 0 ]; then
    rm -rf "$ROOT"
    exit 0
fi
echo "fixture kept for inspection: $ROOT"
exit 1
