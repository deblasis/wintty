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

Most take `-ExePath` and `-OutDir` like a real harness; `no-outdir.ps1`
deliberately takes `-Seed` and no `-OutDir`, which is the point of it.

They exist because the failure this suite is most likely to have is the one
that looks like success: a runner that reports green for a harness that
found defects, could not start, or never ran at all. That is not a
hypothetical - `vtabs-visual-qa.ps1` marked a step OK whenever the
sub-script did not throw, and a sub-script that exits 2 does not throw.
