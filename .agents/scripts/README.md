# Quality-control scripts

Agent-host-neutral tooling for the fork's quality gates. Any agent (or
human) working in this repo uses the same contract:

- `just signoff` - run the test legs this branch's changes actually need
  and record a pass/fail keyed to the current HEAD commit. A green signoff
  covering a PR's exact head commit is required before that PR may be
  merged; local runners are the merge authority while CI is unavailable.
  Which legs run comes from the changed paths (`gate_scope.py`): a change
  touching no Zig and no C# does not pay for the Zig suite, and a path no
  rule classifies pays for everything. `just signoff-plan` shows the
  decision without running anything; `just signoff-full` runs every leg.
  The gate recomputes the requirement from the PR's own files, so a cheap
  record cannot stand in for an expensive one.
- `just leg-cache plan` - the content digests behind signoff's carrying
  (`leg_cache.py`). Each leg has a digest over everything it consumes:
  the paths `gate_scope.py` routes to it, the justfile recipes it runs
  through, its command, the toolchain that runs it, and the environment
  variables its suites read. A green observed for a digest, by any signoff
  or nightly run in any worktree of this repo, is carried by later
  signoffs whose digest matches. Digests come from HEAD tree objects
  only; a dirty tree carries and records nothing; a root entry no rule
  classifies goes into every digest; an unresolved input forces a run.
  Records live under `<git-common-dir>/pr-signoff/leg-cache/<machine>/`
  and name the commit the green was observed at and whether a run
  observed it (which must hand over the toolchain snapshot it ran under,
  `just leg-cache snapshot FILE` taken before the legs) or a human
  asserted it (`just leg-cache record LEG --from-sha HEAD`, on their
  word). Every carried step in a signoff record says so; `just
  signoff-fresh` carries nothing and records what it observes instead. The fixed version and seed the Zig test steps use are the same
  idea one level down; the justfile's `TEST_VERSION` and `TEST_SEED`
  comments say why.
- `just signoff-defer "<motivation>"` - merge without running the legs, on
  the record. For batching a run of small PRs behind one later ladder. The
  motivation is stored in a ledger, at most a few deferrals may be
  outstanding and none may go stale, and only a green `signoff-full` (or a
  green nightly) settles them - on a branch that contains `origin/windows`,
  since that is where the deferred merges live, and with no leg carried on
  a human's word rather than a run's. `just signoff-debt` lists what is currently
  merged on credit; the session-start doctor reports it too.
- `just pr-gate <n>` - validate a PR against the merge gate without
  merging: countable size limit, body present, no unchecked task items,
  no spaced issue references, signoff present. The hook form also denies a
  raw `gh pr merge` whose signoff window has moved (commits landed on
  `origin/windows` after the record's base) and names the fix: rebase,
  `just signoff` again (unmoved legs carry their green in seconds), then
  `just merge-checked`; a same-window merge stays allowed raw, and any
  window answer is only as fresh as the checkout's last fetch. Its honest limits: a hook
  only sees commands typed through tools it is wired into, so the GitHub
  web UI or mobile, a plain `git push` to `windows` (which has no branch
  protection), and hosts without the hook wiring are all uncovered, and
  `gh pr merge --auto` is judged at submit time.
- `just merge-checked <n>` - the merge guard (`merge_guard.py`) and the
  normal way to merge: it re-validates the record (missing, red,
  scope-mismatched and ledger-blocked records refuse), fetches and
  measures the delta from the record's base to `origin/windows`
  first-parent, and refuses a moved window with the fix named (rebase,
  `just signoff` again, merge); on an unmoved one it squash-merges, reads
  back the squash sha and verifies it against a second fetch. The owner's
  override `--carry-risk` merges on a moved window anyway and files a
  `resignoff-required` issue on the #970 template, with a structured
  delta: per-commit attribution, the risks in words (same files, same
  top-level directories, same signoff legs as the record's scope, plus the
  never-signed-off squash itself), and the resignoff status. `--dry-run`
  prints all of it and the would-be verdict, mutating nothing (it also
  accepts a MERGED pr); `--file-only <n>` files the owed issue without
  merging, for a merge that landed outside the guard.
- `just resignoff-bot [--max N] [--dry-run]` - work the pile the guard
  filed under #969 and files under `--carry-risk`: an operator loop, never a merge step, so agents
  merging a PR do not run it. Each invocation spends at most `--max`
  signoff runs (default 1; a full ladder is over an hour of lane time),
  newest window first: a window whose recorded squash already has a green
  record closes on that evidence, an unproven window takes one claim
  marker, one detached worktree at the recorded squash sha and one
  `incoda run --queue wintty-build -- just signoff`, and a red window bisects
  the recorded squash SHAs down to a single culprit issue, which gets the
  `signoff-bisect-culprit` label, the failing legs and the trail, and
  stays open. One instance at a time: a lock beside the worktree refuses a
  second live bot (a dead holder's lock is taken over). The records ARE
  the bisect state, so any re-invocation resumes where the last one
  stopped; `--max 0` is the greens-only pass, closing what the records
  already retire and spending nothing; and `--dry-run` prints the
  decisions and exact commands while mutating nothing.
- `just doctor` - verify everything the gates depend on: required tools on
  PATH, scripts where the hook wiring points, settings parseable, nightly
  task registration, and, where incoda is present, that the heavy job lanes
  are configured the way `lanes.ps1` records. A Claude Code SessionStart
  hook runs the fast subset at the start of every session, so a broken gate
  environment is loud instead of silently absent.
- `just lanes` - apply the heavy job lanes' configuration (`lanes.ps1`):
  `wintty-build` with three slots, `wintty-desktop` and `wintty-publish`
  with one, all three refusing a run without `--reason`, and the retired
  `wintty` key closed with a message naming its replacements. incoda keeps
  that state in its own directory where nothing in the repo could see it,
  so the script is the record: apply touches only a lane that drifted, and
  `-Check` (what `just doctor` runs) reports each drift and changes nothing.
- `just gates-selftest` - prove the gates still catch what they exist for
  (recorded-PR replays, matcher escapes, exemption anchoring, the merge
  guard's refusal matrix and golden issue body) and that the nightly
  scripts' helpers roundtrip. Runs `gitversion-selftest` first.
- `just release-gate-check` - prove the shipping-build gate REFUSES a leak,
  by evaluating Release rather than by reading the targets file. Three probe
  sets: the build-time refusal in both polarities and by both routes (a `-p:`
  on the derived property, and an environment variable); what a Release
  evaluation actually defines, per project, since the target reads
  `DemoEnabled` and never `DefineConstants`; and the two `#if !DEBUG` facts
  in `ShippingBuildGateTests`, each asserted by name and by a count of
  exactly one, because that class also holds facts that run in Debug.
  Windows-only, and nothing compiles: the target is invoked directly.
- `just gitversion-selftest` - prove a tag outside the release namespace
  cannot name a version. `sync-publish` tags each published snapshot
  `series/vN` and Config.init panics on a tag it does not recognise, so
  the version lookup filters with `--match v* --match tip --exclude */*`.
  `--match` does not stop at a slash, so `--exclude` is what carries the
  namespace rule. This runs real `git describe` against a throwaway repo
  over ten tag layouts, with the argument list read out of
  GitVersion.zig, and requires three broken argument lists to be caught.
- `just test-reachability` - prove no file's `test` blocks are dead. Zig
  collects test blocks from the files a test binary's own test and
  comptime blocks reach, so a file can carry assertions no test step has
  ever run, which reads as coverage. This compiles every test binary the
  build runs, asks each one over the protocol the stock test runner speaks
  on `--listen=-` which tests it carries, and matches the qualified names
  back onto files. What is knowingly out of reach is registered file by
  file with its reason, and an entry whose tests start running fails the
  check so the line has to go. All of it prints on every run. It needs a
  full test-binary build, so it runs in the Zig leg via `just test`; its
  build-free selftest runs in `gates-selftest`.
- `nightly_fuzz.ps1` / `nightly_control.ps1` / `register_nightly_fuzz.ps1` -
  the 23:00 nightly run (tests + fuzz in a dedicated worktree, deduped P1
  issues on breaks, optional hibernate-after) and its control panel.
  Register once per build machine from the main checkout. The test legs
  run under the `wintty-build` lane like any other caller of `just test`
  and `just test-win`, so a missing incoda aborts the run instead of
  landing a nightly build on top of a session's; a lane that never came
  free files an infra issue, not a red test suite.

Enforcement wiring is host-specific and lives with each host. For Claude
Code, `.claude/settings.json` hooks call `pr_gate.py --hook` and
`workspace_guard.py --check` before shell commands and `workspace_guard.py
--track` after file edits. Another agent host should wire its own
equivalent triggers to these same scripts; the scripts read the hook JSON
on stdin and print a deny decision, so any host that can spawn a process
around its tool calls can enforce the same gates.

Requirements: Python 3 on PATH as `python`; the nightly scripts are
Windows-only (PowerShell 7, scheduled tasks, GetLastInputInfo).

Runtime state (not committed): signoff records under the git common dir in
`pr-signoff/`, workspace-guard state in the per-worktree git dir, nightly
config/status/logs under `.agents/nightly-logs/`, and the nightly and
resignoff worktrees under `.agents/worktrees/`.
