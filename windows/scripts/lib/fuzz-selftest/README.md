# fuzz-suite self-test fixtures

Stand-ins for real harnesses, one per exit path the runner has to tell
apart. They ignore the exe they are handed, so `fuzz-suite.ps1 -SelfTest`
needs no build, no window and no interactive desktop.

| fixture | what it pins down |
|---|---|
| `pass.ps1` | a clean run is one attempt |
| `findings.ps1` | exit 2 is a result, never retried |
| `cannot-run.ps1` | exit 1 is retried |
| `throws.ps1` | an unhandled throw is a harness failure, not a pass |
| `flaky.ps1` | the retry re-runs rather than replaying the first verdict |
| `no-outdir.ps1` | a harness that takes no `-OutDir` is called correctly, and `-Seed` reaches it |
| `product-throw.ps1` | a thrown `PRODUCT_FAIL` leaves with 2 *and* still runs its `finally` |
| `unknown-code.ps1` | an exit code outside the convention is not a pass |
| `hangs.ps1` | a wedged harness is killed at its budget rather than hanging the run |
| `seed-unverified.ps1` | a harness that could not establish its own corpus leaves with 1 out of a catch and a classification, and still runs its `finally` |
| `seed-readback-cases.ps1` | the seed read-back rules themselves: a rising count is landing, an unreadable sample is not a miss |

Most take `-ExePath` and `-OutDir` like a real harness; `no-outdir.ps1`
deliberately takes `-Seed` and no `-OutDir`, which is the point of it.

They exist because the failure this suite is most likely to have is the one
that looks like success: a runner that reports green for a harness that
found defects, could not start, or never ran at all. That is not a
hypothetical - `vtabs-visual-qa.ps1` marked a step OK whenever the
sub-script did not throw, and a sub-script that exits 2 does not throw.

## `layers/`

Tier layer manifests, one per thing the merge in `fuzz-suite.ps1` has to
refuse or accept. They are not harnesses: each emits the object a tier
overlay would drop beside the runner as `fuzz-tier-harnesses.ps1`.

| fixture | what it pins down |
|---|---|
| `valid.ps1` | a well-formed layer merges, and the run names both counts |
| `pscustomobject.ps1` | an entry written as a custom object normalises rather than crashing |
| `minutes-string.ps1` | `minutes` as text coerces to `Int32` before the timeout arithmetic reads it |
| `minutes-bad.ps1` | `minutes` no arithmetic can use is refused |
| `tags-missing.ps1` | empty, null and all-blank `tags` are each refused, not just an absent key |
| `missing-key.ps1` | one entry per required key, each short a different one, so shortening the list is visible |
| `empty-value.ps1` | a key that is present and empty is not a declared key |
| `script-missing.ps1` | a `script` the tier declared and never shipped is refused, by integrity check 1 |
| `script-escapes.ps1` | a `script` that climbs out of the directory is refused |
| `script-sibling.ps1` | so is one in a sibling directory whose path opens with the same characters |
| `leaf-collision.ps1` | naming a subdirectory script does not classify a same-leaf script at the top level |
| `duplicate-name.ps1` | the same name twice inside one layer is refused |
| `base-collision.ps1` | a name the base set already uses is refused |
| `reserved-layer.ps1` | the layer cannot call itself `base` |
| `no-harnesses.ps1` | a layer with nothing to run under it is refused |
| `no-layer.ps1` | so is harnesses with no layer name, which would otherwise merge and report as base-only |
| `returns-nothing.ps1` | a manifest that emits no object at all is refused |
| `two-objects.ps1` | so is one that emits two, which member enumeration otherwise reads as a single merged layer |
| `not-in-suite.ps1` | a layer can classify a runner of its own without patching the suite |
| `not-in-suite-object.ps1` | the same classification written as a custom object is read, not silently ignored |
| `not-in-suite-list.ps1` | a list of names rather than name = reason pairs is refused |
| `not-in-suite-harness.ps1` | a layer cannot excuse a script its own manifest names as a harness |

The self-test runs these against a copy of `windows/scripts`, because the
manifest is discovered by presence: a fixture placed in the real directory
would be picked up by every other invocation from it, including a concurrent
one, and the case that asserts what an ABSENT manifest does could not exist
at all.
