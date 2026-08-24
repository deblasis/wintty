#!/usr/bin/env bash
# Self-test for the snapshot-merge sync flow (just sync / sync-bootstrap /
# sync-verify / sync-publish). Builds a synthetic upstream + origin + work
# clone in a temp dir and drives the real recipes from this repo's justfile
# through: bootstrap, a no-op sync, a fold-in sync, a publish that loses a
# race to a mid-sync PR merge, and a conflicted replay.
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

# Identity stays inside the fixture repos; nothing here may touch the
# invoking user's git config.
G="git -c user.name=fixture -c user.email=fixture@test.invalid"
J() { ( cd "$ROOT/work" && just --justfile "$JUSTFILE" --working-directory "$ROOT/work" "$@" ); }

cd "$ROOT"

say "setup: upstream, origin, work"
git init -q -b main up
( cd up
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8\n' > a.txt
  echo lib1 > lib.txt
  $G add -A && $G commit -qm "u1: base"
  echo lib2 >> lib.txt
  $G add -A && $G commit -qm "u2: lib"
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
if J sync >/dev/null 2>&1; then nope "sync refused without series tag"; else ok "sync refused without series tag"; fi

say "test 1: bootstrap"
J sync-bootstrap
check "series/v0 exists locally" git -C work rev-parse --verify refs/tags/series/v0
check "series/v0 pushed to origin" git -C origin.git rev-parse --verify refs/tags/series/v0
if J sync-bootstrap >/dev/null 2>&1; then nope "second bootstrap refused"; else ok "second bootstrap refused"; fi

say "test 2: no-op sync + publish"
J sync
check_eq "series-wip on windows tip" "$(git -C work rev-parse series-wip)" "$(git -C work rev-parse 'refs/tags/series/v0^{commit}')"
J sync-verify fast || nope "sync-verify fast (noop) exited nonzero"
J sync-publish
M1=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "snapshot M1 is a merge of old tip + upstream" \
  "$(git -C work rev-parse "$M1^1" "$M1^2" | tr '\n' ' ')" \
  "$(git -C work rev-parse 'refs/tags/series/v0^{commit}' refs/remotes/upstream/main | tr '\n' ' ')"
check_eq "M1 tree == series tree" "$(git -C work rev-parse "$M1^{tree}")" "$(git -C work rev-parse 'series-wip^{tree}')"
check "series/v1 pushed" git -C origin.git rev-parse --verify refs/tags/series/v1
check_eq "local windows fast-forwarded" "$(git -C work rev-parse refs/heads/windows)" "$M1"

say "test 3: upstream moves + a PR lands, then sync folds it"
( cd up && echo lib3 >> lib.txt && $G add -A && $G commit -qm "u3: more lib" )
( cd work
  git checkout -q windows
  echo pr > pr.txt
  $G add -A && $G commit -qm "p1: a PR squash commit"
  git push -q origin windows
)
J sync
check "p1 folded into series (pr.txt present)" git -C work cat-file -e 'series-wip:pr.txt'
check "series rebased onto u3" git -C work merge-base --is-ancestor refs/remotes/upstream/main series-wip
J sync-verify fast || nope "sync-verify fast (fold) exited nonzero"
J sync-publish
M2=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "M2 first parent is pre-sync windows tip (p1)" "$(git -C work log -1 --format=%s "$M2^1")" "p1: a PR squash commit"
check_eq "M2 tree == series tree" "$(git -C work rev-parse "$M2^{tree}")" "$(git -C work rev-parse 'series-wip^{tree}')"
check "series/v2 pushed" git -C origin.git rev-parse --verify refs/tags/series/v2

say "test 4: publish loses a race, resume folds the late PR"
( cd up && echo lib4 >> lib.txt && $G add -A && $G commit -qm "u4: even more lib" )
J sync
git clone -q "$ROOT/origin.git" "$ROOT/rc"
( cd rc
  git checkout -q windows
  echo late > late.txt
  $G add -A && $G commit -qm "p2: raced PR"
  git push -q origin windows
)
if J sync-publish >/dev/null 2>&1; then nope "raced publish rejected"; else ok "raced publish rejected"; fi
if git -C work rev-parse --verify -q refs/tags/series/v3 >/dev/null; then nope "series/v3 rolled back after rejection"; else ok "series/v3 rolled back after rejection"; fi
J sync
check "p2 folded on resume" git -C work cat-file -e 'series-wip:late.txt'
check_eq "no duplicate p1 after resume (one pr.txt commit in series)" \
  "$(git -C work log --oneline refs/remotes/upstream/main..series-wip -- pr.txt | wc -l | tr -d ' ')" "1"
J sync-publish
check "series/v3 pushed" git -C origin.git rev-parse --verify refs/tags/series/v3

say "test 5: conflicting upstream change, resolve, publish"
( cd up && printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8-upstream\n' > a.txt && $G add -A && $G commit -qm "u5: conflicts with f2" )
if J sync >/dev/null 2>&1; then nope "sync stopped on conflict"; else ok "sync stopped on conflict"; fi
( cd work
  printf 'base1\nbase2\nbase3\nbase4\nbase5\nbase6\nbase7\nbase8-upstream\nfork1\nfork2\nfork3\nfork4\nfork5\nfork6\n' > a.txt
  $G add a.txt
  GIT_EDITOR=true $G rebase --continue
)
J sync-verify fast || nope "sync-verify fast (conflict) exited nonzero"
J sync-publish
M4=$(git -C origin.git rev-parse refs/heads/windows)
check_eq "resolved content published" "$(git -C work show "$M4:a.txt" | sed -n '8p;9p' | tr '\n' '|')" "base8-upstream|fork1|"

say "guards"
( cd work && git checkout -q -b random "$M4" )
if J sync >/dev/null 2>&1; then nope "sync refused on a random branch"; else ok "sync refused on a random branch"; fi
if J sync-publish >/dev/null 2>&1; then nope "publish refused off series-wip"; else ok "publish refused off series-wip"; fi
( cd work && git checkout -q series-wip && echo dirt >> a.txt )
if J sync >/dev/null 2>&1; then nope "sync refused on dirty tree"; else ok "sync refused on dirty tree"; fi
( cd work && git checkout -q -- a.txt )

printf '\n=== %d passed, %d failed ===\n' "$PASS" "$FAIL"
if [ "$FAIL" -eq 0 ]; then
    rm -rf "$ROOT"
    exit 0
fi
echo "fixture kept for inspection: $ROOT"
exit 1
