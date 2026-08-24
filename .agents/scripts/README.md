# Quality-control scripts

Agent-host-neutral tooling for the fork's quality gates. Any agent (or
human) working in this repo uses the same contract:

- `just signoff` - run the full local test ladder (zig fmt check, zig
  tests, Windows tests) and record a pass/fail keyed to the current HEAD
  commit. A green signoff for a PR's exact head commit is required before
  that PR may be merged; local runners are the merge authority while CI is
  unavailable.
- `just pr-gate <n>` - validate a PR against the merge gate without
  merging: countable size limit, body present, no unchecked task items,
  no spaced issue references, signoff present.
- `just gates-selftest` - prove the gates still catch what they exist for
  (recorded-PR replays, matcher escapes, exemption anchoring) and that the
  nightly scripts' helpers roundtrip.
- `nightly_fuzz.ps1` / `nightly_control.ps1` / `register_nightly_fuzz.ps1` -
  the 23:00 nightly run (tests + fuzz in a dedicated worktree, deduped P1
  issues on breaks, optional hibernate-after) and its control panel.
  Register once per build machine from the main checkout.

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
config/status/logs under `.agents/nightly-logs/`, and the nightly worktree
under `.agents/worktrees/nightly`.
