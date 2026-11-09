# Hidden-tab measure wiring: verbatim mutant receipts

Every fact in `HiddenTabMeasureWiringTests.cs` was proven to FAIL against
the defect shape it exists to refuse. Each receipt below records the
planted mutation, the command, and the verbatim red text the run printed.
Nothing here is a summary: the failure messages are the runner's own
output, copied as it appeared. All runs were made against a tree whose
only change was the planted mutation, through the build lane.

Command shape for every receipt (the filter names the facts under test):

```
dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj ^
  /p:Platform=x64 /p:RestoreLockedMode=true --nologo ^
  --filter "FullyQualifiedName~HiddenTabMeasure"
```

## M0: the first-layout handler's starvation record hoisted out of the hidden branch

Mutation: in `OnFirstLayoutUpdated`, `_creationStarvedByHiddenEra = true;`
moved from inside the `if (!IsEffectivelyVisible())` branch to above the
`if`. A pane that was never hidden now arms the backstop on every pass,
and the flip body re-runs for every visible pane.

Red (12 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.FirstLayoutHandler_IgnoresPassesWhileHidden (18ms): Error Message: the starvation record must sit INSIDE the hidden-pass guard's branch: a record hoisted above the guard arms the backstop on every pass and re-baselines every visible pane
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.FirstLayoutHandler_IgnoresPassesWhileHidden() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs
```

## M1: the flip gate deleted

Mutation: `if (!_creationStarvedByHiddenEra) return;` removed from
`OnLayoutShowWatch`. The flip body then runs on EVERY visible pass: the
measure reset re-baselines the two-pass settle for every pane in the app
and the settle never fires for any of them.

Red (12 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_TheFlipBodyRunsOnlyAfterAHiddenEra (20ms): Error Message: Assert.Single() Failure: The collection did not contain any matching items
Expected:   (predicate expression)
Collection: [if (_surfaceDisposed) return;, if (!IsEffectivelyVisible())
        {
            // Any invisible pass (re)arms the backstop, including passes
            // of an already-created surface whose creation handler retired
            // long ago: a pane hidden again re-enters the class, because
            // the size/scale re-push below is owed on every show.
            _creationStarvedByHiddenEra = true;
            return;
        }, if (!_surfaceCreated)
        {
            // The one-shot re-arm: this showing pass's own arrange is the
            // measurement; a renderer refusal inside arms the bounded
            // retry, exactly as any failed attempt does.
            TrySettleSurfaceCreation();
            return;
        }, if (_surface.Handle == IntPtr.Zero) return;]
```

## M2: the settle attempt moved below the emptied pending branch

Mutation: in `OnLayoutShowWatch`, `TrySettleSurfaceCreation();` removed
from inside the `if (!_surfaceCreated)` branch and placed after its
closing brace. A pending pane then returns uncreated (the show-path
backstop is dead) and a created pane runs the attempt (a second surface).

Red (12 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass [FAIL]
      the settle attempt must sit INSIDE the `if (!_surfaceCreated)` branch: moved below the branch, a pending pane never settles (the show-path backstop is dead) and a created pane creates its surface a second time
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs:line 373
```

## M3: the watch's invisible branch keeps its record but loses its return

Mutation: the `return;` inside `OnLayoutShowWatch`'s
`if (!IsEffectivelyVisible())` branch removed. An invisible pass then
records the era and falls through to the flip body, pushing the size and
scale at the placeholder it just recorded.

Red (12 facts ran, 3 failed; the same terminal-branch assertion fires
from three facts, because that one guard is the only thing standing
between an invisible pass and the re-push):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (119ms): Error Message: OnLayoutShowWatch's invisible-pass guard: the `!IsEffectivelyVisible()` branch must RETURN, not fall through: without the return the hidden pane records the era and then does the refused work anyway
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.AssertGuardReturns(IfStatementSyntax guard, String what) in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs:line 94

Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (5267ms): Error Message: Controls.TerminalControl.xaml.cs::OnLayoutShowWatch: the `!IsEffectivelyVisible()` branch must RETURN, not fall through: without the return the hidden pane records the era and then does the refused work anyway
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.AssertGuardReturns(IfStatementSyntax guard, String what) in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs:line 94

Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_RePushesTheScaleAndSizePairTogether [FAIL] (same assertion, same message)
```

## M4: the Loaded fallback's starvation record hoisted out of the hidden branch

Mutation: in `OnLoaded`'s fallback tick, `_creationStarvedByHiddenEra =
true;` moved above the `if (!IsEffectivelyVisible())` guard. A visible
tick now arms the backstop too, and every later visible pass re-runs the
flip body.

Red (12 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.LoadedFallback_RecordsTheHiddenEraInsteadOfSettling (13ms): Error Message: the fallback's hidden branch needs the visibility refusal around its starvation record: a hidden tick must record and return, never settle at the placeholder
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.LoadedFallback_RecordsTheHiddenEraInsteadOfSettling() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs:line 489
```

## M5: an unguarded SurfaceSetSize call site hidden behind a using alias

Mutation: in `MainWindow.xaml.cs`, `using NM =
global::Ghostty.Interop.NativeMethods;` added and a private method calling
`NM.SurfaceSetSize(default, 1, 1);` added. The census matches callees by
tail across every embedded shell source, so the alias's spelling does not
hide the site.

Red (filter `FullyQualifiedName~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (2977ms): Error Message: unexpected SurfaceSetSize call site 'MainWindow.xaml.cs::MutantAliasSizeProbe' (callee matched by tail, aliases included): every pane-size push must sit in a guarded TerminalControl method
```

## M6: an unguarded SurfaceSetContentScale call site hidden behind a using alias

Mutation: the same alias shape as M5, calling
`NM.SurfaceSetContentScale(default, 1, 1);`. The scale half of the pair
is censused like the size half.

Red (filter `FullyQualifiedName~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (3092ms): Error Message: unexpected SurfaceSetContentScale call site 'MainWindow.xaml.cs::MutantAliasSizeProbe' (callee matched by tail, aliases included): every pane-scale push must sit in a guarded TerminalControl method
```

## M7: the tab host stops collapsing inactive pane hosts

Mutation: `paneHost.Visibility = Visibility.Collapsed;` removed from
`MainWindow.AddPaneHost`. Every pane host then enters the tree visible
and `IsEffectivelyVisible` stops discriminating hidden eras, with every
TerminalControl guard still green; this fact is the one that goes red.

Red (filter `FullyQualifiedName~TabHost_CollapsesInactiveHosts`, 1 fact
ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.TabHost_CollapsesInactiveHostsAndFlipsTheActiveOne (921ms): Error Message: Assert.Contains() Failure: Filter not matched in collection
Collection: [paneHost.HorizontalAlignment = HorizontalAlignment.Stretch, paneHost.VerticalAlignment = VerticalAlignment.Stretch, paneHost.ContextMenuRequested += OnPaneContextMenuRequested]
```

## Green counterparts

- The same 12 facts, unmutated, all green: `Test summary: total: 12,
  failed: 0, succeeded: 12, skipped: 0`.
- The platform premise test (`CollapsedAncestorLayoutDeliveryTests`,
  Ghostty.Tests.Windows) passes on the same host: `Passed! - Failed: 0,
  Passed: 1, Skipped: 0, Total: 1`.
- Its red counterpart was observed during development by asserting on a
  detached tree instead of an attached window: a manually
  measured-and-arranged detached root raised NO LayoutUpdated at all
  (`collapsed=0 visible=0 root=0`), which is why the premise test
  attaches its tree to an offscreen window rather than trusting a
  detached harness, and why its visible-leaf control assert exists: a
  harness that fires nothing must fail as blind, not pass as vacuous.
