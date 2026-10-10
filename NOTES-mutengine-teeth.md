# NOTES: the mutation engine's teeth, reproduced

Commit b3f88d9a8 ("windows: the mutation engine binds each row's lines
flat") claimed a teeth proof: a weakened engine row is caught by the
repaired engine and missed by the old one. The claim existed in no
reviewable artifact, so it is reproduced here, verbatim.

## The weakened row

The resume engine's row "the owned trees are gone", weakened in a
scratch copy so its edit is real but no scan rule pins it (it drops the
harness header's first prose line instead of the owned-tree mints):

```powershell
# was:
Break = { param($lines) @($lines | Where-Object { $_ -notmatch 'New-WinttyOwnedStateBase' }) }
# weakened:
Break = { param($lines) @($lines | Where-Object { $_ -notmatch 'Session-resumption rows for birth-coverage' }) }
```

A row whose edit changes nothing the scan pins must NOT count as red.
The repaired engine has to fail the row (its "went red" assert goes
red); the old comma-wrap engine has to keep calling it red, because its
mutant is a single unparseable line and the scan fails absence rules
over an effectively empty script.

## Commands

Two scratch copies of windows/scripts (+ the justfile) under
%TEMP%\dev\mutengine-teeth, both weakened identically; the "old" copy
additionally carries the parent commit's invocation
`$lines = @(& $row.Break (, $resumeLines))` in place of
`$lines = Invoke-RowBreak $row.Break $resumeLines`, and both stop right
after the resume engine:

```
pwsh -NoProfile -File %TEMP%\dev\mutengine-teeth\setup.ps1
pwsh -NoProfile -File %TEMP%\dev\mutengine-teeth\repaired\windows\scripts\SeamClient.Tests.ps1
pwsh -NoProfile -File %TEMP%\dev\mutengine-teeth\old\windows\scripts\SeamClient.Tests.ps1
```

## Verbatim: repaired engine (caught, exit 1)

```
ok: resume mutation went red: the always policy is gone
FAIL: resume mutation went red: the owned trees are gone
ok: resume mutation went red: the Scenario selection is gone
ok: resume mutation went red: one start does not forward
ok: resume mutation went red: Stop also gets the hook
ok: resume mutation went red: a Stop outside try/catch
ok: resume mutation went red: the hot probe is gone
ok: resume mutation went red: a start mints private over the owned tree
ok: resume mutation went red: the live cold rows are gone (header prose remains)
TEETH-STOP after the resume engine: 1 fail(s)
exit=1
```

## Verbatim: old engine, same weakened row (missed, exit 0)

```
ok: resume mutation went red: the always policy is gone
ok: resume mutation went red: the owned trees are gone
ok: resume mutation went red: the Scenario selection is gone
ok: resume mutation went red: one start does not forward
ok: resume mutation went red: Stop also gets the hook
ok: resume mutation went red: a Stop outside try/catch
ok: resume mutation went red: the hot probe is gone
ok: resume mutation went red: a start mints private over the owned tree
ok: resume mutation went red: the live cold rows are gone (header prose remains)
TEETH-STOP after the resume engine: 0 fail(s)
exit=0
```

## Why the old red was vacuous

What each engine shape writes for the weakened row, measured on the
642-line harness:

```
old shape      : 1 element(s) written by WriteAllLines
old first line : 34224 chars, starts: #requires -Version 7 <#     Session-resumption rows for birth-coverage
repaired shape : 641 lines
```

The comma-wrap binds the whole line array as ONE item, Where-Object runs
once over the file-as-object, and WriteAllLines writes the stringified
file as a single line; the leading `#` makes that line a comment, so the
scan sees an effectively empty script. Running the real scan on that
1-line mutant returns 11 failures, all of them absence echoes over the
empty script - note the owned-tree row's own Rule text ('mints no owned
state tree') IS among them, so rule-text firing alone would NOT
distinguish the shapes; it is the PARSE gate that catches the old shape
(4 parse errors on the 1-line mutant, 0 on the repaired 641-line edit):

```
weak-old-shape.ps1 declares no [scriptblock]$BeforeTeardown parameter
weak-old-shape.ps1 declares no $Scenario selection parameter (cold/hot)
weak-old-shape.ps1 starts fewer than two seam sessions: no save/restore pair to check
weak-old-shape.ps1 mints no owned state tree: the restore cannot read the save's session.json
weak-old-shape.ps1 never exports the owned tree to its launches: each session would mint a fresh tree and the restore would be fresh too
weak-old-shape.ps1 invokes no live Invoke-RestorePhase plain cold row (anchored past 'resume-cold-fallback'; header prose alone proves nothing)
weak-old-shape.ps1 invokes no live Invoke-RestorePhase fallback row (header prose alone proves nothing)
weak-old-shape.ps1 invokes no live Invoke-Scenario hot row (header prose alone proves nothing)
weak-old-shape.ps1's hot row never probes the pane-sessions op: it cannot tell a held session from a fresh boot
weak-old-shape.ps1 writes no results.json scenario ledger (no live Set-Content names it)
weak-old-shape.ps1 lost the product-finding (2) versus broken-harness (1) exit split (no live exit 2 / exit 1)
```

The same weakened row through the repaired engine's flat binding writes
a real 641-line edit, the scan returns 0 failures, and the engine's
assert fails the row by name. That is the whole claim: the old shape
cannot tell a weakened row from a live one, the repaired shape can.
