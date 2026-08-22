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
| `minutes-string.ps1` | `minutes` and `timeoutSeconds` as text coerce to `Int32` before the timeout arithmetic reads them |
| `minutes-bad.ps1` | `minutes` no arithmetic can use is refused |
| `timeout-bad.ps1` | so is a `timeoutSeconds` that is text, or a budget of zero or less, which skips the floor `minutes` gets |
| `tags-missing.ps1` | empty, null and all-blank `tags` are each refused, not just an absent key |
| `tags-padded.ps1` | a padded tag is stored trimmed, so `-Tag` can reach what `-List` shows; a numeric one is kept |
| `tags-comma.ps1` | a tag holding a comma is refused, because `-Tag` cuts on one and `-List` cannot show the difference |
| `name-comma.ps1` | the same rule on the field `-Only` and `-Skip` match |
| `name-padded.ps1` | a padded name is stored trimmed, so the collision and duplicate checks compare what selection compares |
| `missing-key.ps1` | one entry per required key, each short a different one, so shortening the list is visible |
| `empty-value.ps1` | a key that is present and empty is not a declared key |
| `script-missing.ps1` | a `script` the tier declared and never shipped is refused by integrity check 1, and so is one naming a directory |
| `script-escapes.ps1` | a `script` that climbs out of the directory is refused |
| `script-sibling.ps1` | so is one in a sibling directory whose path opens with the same characters |
| `leaf-collision.ps1` | naming a subdirectory script does not classify a same-leaf script at the top level |
| `script-relative.ps1` | a `script` written as `./name.ps1` is resolved to what the same check compares |
| `script-spellings.ps1` | so are the other ways of writing a path beside the runner, which a prefix strip left alone, and a plain name holding a wildcard character |
| `duplicate-name.ps1` | the same name twice inside one layer is refused |
| `base-collision.ps1` | a name the base set already uses is refused |
| `reserved-layer.ps1` | the layer cannot call itself `base` |
| `reserved-layer-padded.ps1` | nor `base` with padding on it, which the refusal used to compare and miss |
| `layer-blank.ps1` | a layer name that is only padding is no name, and would report the merged suite as base-only |
| `no-harnesses.ps1` | a layer with nothing to run under it is refused |
| `no-layer.ps1` | so is harnesses with no layer name, which would otherwise merge and report as base-only |
| `returns-nothing.ps1` | a manifest that emits no object at all is refused |
| `two-objects.ps1` | so is one that emits two, which member enumeration otherwise reads as a single merged layer |
| `one-object-collection.ps1` | a leading comma wraps one object in a collection, which is refused as the wrapper it is |
| `not-in-suite.ps1` | a layer can classify a runner of its own without patching the suite |
| `not-in-suite-object.ps1` | the same classification written as a custom object is read, not silently ignored |
| `not-in-suite-list.ps1` | a list of names rather than name = reason pairs is refused |
| `not-in-suite-typed-list.ps1` | so is the same list typed, which is neither an array nor a dictionary |
| `not-in-suite-harness.ps1` | a layer cannot excuse a script its own manifest names as a harness |
| `not-in-suite-harness-relative.ps1` | the same door, with the harness written as a path rather than a name |
| `not-in-suite-padded.ps1` | a padded name is stored trimmed, so it excuses the file it names |
| `not-in-suite-dotname.ps1` | so is a classified name that opens with a dot, which a character trim ate |
| `not-in-suite-path.ps1` | a name carrying a separator, or a blank one, excuses nothing and is refused |
| `not-in-suite-empty-reason.ps1` | the pairs form with an empty reason is the list form spelled the long way round |
| `not-in-suite-empty.ps1` | an empty collection is read as the list form rather than skipped |

The self-test runs these against a copy of `windows/scripts`, because the
manifest is discovered by presence: a fixture placed in the real directory
would be picked up by every other invocation from it, including a concurrent
one, and the case that asserts what an ABSENT manifest does could not exist
at all.
