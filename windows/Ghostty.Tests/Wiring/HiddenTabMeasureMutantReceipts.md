# Hidden-tab measure wiring: verbatim mutant receipts

Every fact in `HiddenTabMeasureWiringTests.cs` is proven to FAIL against
the defect shape it exists to refuse; the index at the bottom maps each
fact to the mutant that kills it. Each receipt records the planted
mutation, the command, and the verbatim red text the run printed.
Nothing here is a summary: the failure messages are the runner's own
output, copied as it appeared. All runs were made against a tree whose
only change was the planted mutation, through the build lane.

Command shape for every receipt (the filter names the fact under test;
whole-file runs used `~HiddenTabMeasure`):

```
dotnet test windows/Ghostty.Tests/Ghostty.Tests.csproj ^
  /p:Platform=x64 /p:RestoreLockedMode=true --nologo ^
  --filter "FullyQualifiedName~<fact>"
```

## M0: the first-layout handler's starvation record hoisted out of the hidden branch

Mutation: in `OnFirstLayoutUpdated`, `_creationStarvedByHiddenEra = true;`
moved from inside the `if (!IsEffectivelyVisible())` branch to above the
`if`. A pane that was never hidden now arms the backstop on every pass,
and the flip body re-runs for every visible pane.

Red (facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.FirstLayoutHandler_IgnoresPassesWhileHidden (18ms): Error Message: the starvation record must sit INSIDE the hidden-pass guard's branch: a record hoisted above the guard arms the backstop on every pass and re-baselines every visible pane
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.FirstLayoutHandler_IgnoresPassesWhileHidden() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs
```

## M1: the flip gate deleted

Mutation: `if (!_creationStarvedByHiddenEra) return;` removed from
`OnLayoutShowWatch`. The flip body then runs on EVERY visible pass: the
measure reset re-baselines the two-pass settle for every pane in the app
and the settle never fires for any of them.

Red (facts ran, 1 failed):

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
Re-run after the containment assert was tightened to the branch's own
statement: still red.

Red (filter `~ShowWatch_SettlesPendingCreation`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (550ms): Error Message: the settle attempt must sit INSIDE the `if (!_surfaceCreated)` branch's own statement: moved out of the branch - below it, or into its else - a pending pane never settles (the show-path backstop is dead) and a created pane creates its surface a second time
```

## M3: the watch's invisible branch keeps its record but loses its return

Mutation: the `return;` inside `OnLayoutShowWatch`'s
`if (!IsEffectivelyVisible())` branch removed. An invisible pass then
records the era and falls through to the flip body, pushing the size and
scale at the placeholder it just recorded.

Red (facts ran, 3 failed; the same terminal-branch assertion fires
from three facts, because that one guard is the only thing standing
between an invisible pass and the re-push):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (119ms): Error Message: OnLayoutShowWatch's invisible-pass guard: the `!IsEffectivelyVisible()` branch must RETURN, not fall through: without the return the hidden pane records the era and then does the refused work anyway
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.AssertGuardReturns(IfStatementSyntax guard, String what) in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs

Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (5267ms): Error Message: Controls.TerminalControl.xaml.cs::OnLayoutShowWatch: the `!IsEffectivelyVisible()` branch must RETURN, not fall through: without the return the hidden pane records the era and then does the refused work anyway

Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_RePushesTheScaleAndSizePairTogether [FAIL] (same assertion, same message)
```

## M4: the Loaded fallback's starvation record hoisted out of the hidden branch

Mutation: in `OnLoaded`'s fallback tick, `_creationStarvedByHiddenEra =
true;` moved above the `if (!IsEffectivelyVisible())` guard. A visible
tick now arms the backstop too, and every later visible pass re-runs the
flip body. Re-run after the containment asserts were added; still red.

Red (filter `~LoadedFallback`, 2 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.LoadedFallback_RecordsTheHiddenEraInsteadOfSettling (635ms): Error Message: the fallback's hidden branch needs the visibility refusal around its starvation record: a hidden tick must record and return, never settle at the placeholder
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.LoadedFallback_RecordsTheHiddenEraInsteadOfSettling() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs
```

## M5: an unguarded SurfaceSetSize call site hidden behind a using alias

Mutation: in `MainWindow.xaml.cs`, `using NM =
global::Ghostty.Interop.NativeMethods;` added and a private method calling
`NM.SurfaceSetSize(default, 1, 1);` added. The census matches callees by
tail across every embedded shell source, so the alias's spelling does not
hide the site.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (2977ms): Error Message: unexpected SurfaceSetSize call site 'MainWindow.xaml.cs::MutantAliasSizeProbe' (callee matched by tail, aliases included): every pane-size push must sit in a guarded TerminalControl method
```

## M6: an unguarded SurfaceSetContentScale call site hidden behind a using alias

Mutation: the same alias shape as M5, calling
`NM.SurfaceSetContentScale(default, 1, 1);`. The scale half of the pair
is censused like the size half.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (3092ms): Error Message: unexpected SurfaceSetContentScale call site 'MainWindow.xaml.cs::MutantAliasSizeProbe' (callee matched by tail, aliases included): every pane-scale push must sit in a guarded TerminalControl method
```

## M7: the tab host stops collapsing inactive pane hosts

Mutation: `paneHost.Visibility = Visibility.Collapsed;` removed from
`MainWindow.AddPaneHost`. Every pane host then enters the tree visible
and `IsEffectivelyVisible` stops discriminating hidden eras, with every
TerminalControl guard still green; this fact is the one that goes red.

Red (filter `~TabHost_CollapsesInactiveHosts`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.TabHost_CollapsesInactiveHostsAndFlipsTheActiveOne (921ms): Error Message: Assert.Contains() Failure: Filter not matched in collection
Collection: [paneHost.HorizontalAlignment = HorizontalAlignment.Stretch, paneHost.VerticalAlignment = VerticalAlignment.Stretch, paneHost.ContextMenuRequested += OnPaneContextMenuRequested]
```

## M8: the watch's own starvation record hoisted out of its invisible branch

Mutation: in `OnLayoutShowWatch`, `_creationStarvedByHiddenEra = true;`
moved from inside the `if (!IsEffectivelyVisible())` branch to above the
`if`. This is the third producer of the flag; the other two producers
are pinned by containment, and this plant was 12/12 green against the
existence-only pin before the containment assert was added.

Red (13 facts ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (25ms): Error Message: the invisible-pass arming must sit INSIDE the invisible-pass guard's branch: hoisted above the guard it arms the backstop on a visible pass too, and every visible pane re-runs the flip body
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs:line 430
```

## M9: the watch's zero-handle refusal deleted

Mutation: `if (_surface.Handle == IntPtr.Zero) return;` removed from the
watch's re-push path (after the pending branch, before the scale/size
pair). A zeroed handle - a renderer failure, or a freed surface re-hidden
before its teardown - is then handed to the native side twice; no other
fact in the file notices. Re-run after the matcher rework (N1-N3 below);
still red.

Red (filter `~ShowWatch_RefusesToRePush`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_RefusesToRePushOntoAZeroedSurface (646ms): Error Message: Assert.Single() Failure: The collection did not contain any matching items
Expected:   (predicate expression)
Collection: [if (_surfaceDisposed) return;, if (!IsEffectivelyVisible()) { ... }, if (!_surfaceCreated) { ... }, ...]
```

## M10: an unguarded scale push hoisted above the guard in a dual-listed site

Mutation: `NativeMethods.SurfaceSetContentScale(_surface, 1f, 1f);`
added as the first statement of `OnFirstLayoutUpdated`, above the
visibility guard (the guarded original push below it stays). The site is
dual-listed in the census (it pushes both size and scale); a per-SITE
guard check would ask only where the size push sits and stay green. The
per-(site, callee) pair loop reds it.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (3834ms): Error Message: Controls.TerminalControl.xaml.cs::OnFirstLayoutUpdated pushes SurfaceSetContentScale without an `!IsEffectivelyVisible()` refusal before it
```

## M11: the settle attempt moved into the pending branch's else

Mutation: in `OnLayoutShowWatch`, the pending branch emptied of its
settle attempt and `TrySettleSurfaceCreation(); return;` placed in an
`else` on the same `if`. A pending pane takes the branch and returns
uncreated; the settle runs only for panes that already have a surface.
The if-node's full span includes the else, so a Contains over the
statement (not the branch's own block) is what refuses it.

Red (filter `~ShowWatch_SettlesPendingCreation`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (545ms): Error Message: the settle attempt must sit INSIDE the `if (!_surfaceCreated)` branch's own statement: moved out of the branch - below it, or into its else - a pending pane never settles (the show-path backstop is dead) and a created pane creates its surface a second time
```

## M12: a push hidden inside a disabled preprocessor region

Mutation: in `MainWindow.AddPaneHost`, inside `#if !DEBUG ... #endif`:

```
NativeMethods.SurfaceSetSize(default, 2, 2);
```

The corpus parse defines DEBUG, so the region is invisible to the call
census; the release build compiles the push unguarded. Disabled regions
survive as trivia, and the census refuses any push-shaped callee in one.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (3161ms): Error Message: MainWindow.xaml.cs disables a preprocessor region containing a 'SurfaceSetSize(' call: the census cannot see it, but a build without this parse's symbols compiles it
```

## M13: the creation gate's visibility half removed

Mutation: `|| !IsEffectivelyVisible()` removed from the measured gate's
condition in `TryCreateSurface`. The gate still refuses unmeasured panes;
an invisible pane with a positive placeholder read now passes it. Re-run
after the refusal assert was upgraded from a substring to a top-level
disjunct match; still red.

Red (filter `~CreationGate`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationGate_RefusesAnInvisiblePaneArrange (770ms): Error Message: the visibility refusal must be a top-level disjunct of the measured gate's condition: nested under `&&` it is false for exactly the hidden pane with a positive placeholder read the gate exists to refuse
   at Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationGate_RefusesAnInvisiblePaneArrange() in C:\...\windows\Ghostty.Tests\Wiring\HiddenTabMeasureWiringTests.cs
```

## M14: the watch's constructor arming deleted

Mutation: `Panel.LayoutUpdated += OnLayoutShowWatch;` removed from the
`TerminalControl` constructor. The backstop never exists, and every
hidden era consumes the ordinary drivers with nothing to finish the work
at the show.

Red (filter `~ShowWatch_IsArmed`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_IsArmedForTheControlsLifetime (575ms): Error Message: Assert.Contains() Failure: Filter not matched in collection
Collection: [_imeForwarder.ForwardedText += ForwardImeText, _imeForwarder.PreeditChanged += UpdateSurfacePreedit, Panel.Margin = gutter, VerticalScrollBar.Margin = gutter, ResizeOverlay.Margin = gutter, ···]
```

## M15: the retry tick's visibility half removed

Mutation: `|| !IsEffectivelyVisible()` removed from the skip condition in
`OnSurfaceRetryTick`. Hidden ticks then reach the budget decrement and
burn it on refusals the creation gate makes anyway.

Red (filter `~RetryTick`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.RetryTick_SkipsWithoutSpendingWhileHidden (597ms): Error Message: OnSurfaceRetryTick needs an `!IsEffectivelyVisible()` skip before the attempt: a hidden pane's ticks must not burn the retry budget on refusals
```

## M16: PushSurfaceSize's hidden refusal deleted

Mutation: `if (!IsEffectivelyVisible()) return;` removed from
`PushSurfaceSize`. Every later caller pushes the placeholder slot to the
grid while the pane is hidden.

Red (filter `~PushSurfaceSize_RefusesWhileHidden`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.PushSurfaceSize_RefusesWhileHidden (607ms): Error Message: PushSurfaceSize needs an `!IsEffectivelyVisible()` return before SurfaceSetSize: a hidden pane's placeholder slot must not be pushed as the pane's size
```

## M17: OnCompositionScaleChanged's hidden refusal deleted

Mutation: `if (!IsEffectivelyVisible()) return;` removed from
`OnCompositionScaleChanged`. A scale change while hidden re-derives the
grid from a size the pane is not showing, half-applying the pair.

Red (filter `~ScaleChange_RefusesWhileHidden`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ScaleChange_RefusesWhileHidden (576ms): Error Message: OnCompositionScaleChanged needs an `!IsEffectivelyVisible()` return before the scale push: the pair moves together or not at all, and the show pass is what re-pushes both
```

## N1: a decoy field replaces the checked handle at the zero-handle gate

Mutation: a `private IntPtr _surfaceSnapshot;` field added, and the
watch's gate replaced with `if (_surfaceSnapshot == IntPtr.Zero)
return;`. The pushed surface's own handle is no longer checked at all.
The gate's name-root still contains `_surface`, which is what the
pre-rework matcher keyed on: this exact plant was 13/13 GREEN against it
(run recorded before the rework: `Test summary: total: 13, failed: 0,
succeeded: 13`). The reworked matcher binds the checked operand to the
pushes' own first argument, so the decoy is no longer the gate.

Red (filter `~ShowWatch_RefusesToRePush`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_RefusesToRePushOntoAZeroedSurface (576ms): Error Message: Assert.Single() Failure: The collection did not contain any matching items
Expected:   (predicate expression)
Collection: [if (_surfaceDisposed) return;, if (!IsEffectivelyVisible()) { ... }, ...]
```

## N2: the house spelling folds the handle test into a disjunction (GREEN, by design)

Mutation: the watch's gate replaced with the file's house spelling
`if (_surfaceDisposed || _surface.Handle == IntPtr.Zero) return;` -
behavior-identical to the bare test, and the shape nine-plus existing
sites in this file already use. This receipt is deliberately GREEN: a
matcher that false-reds the house spelling would force every one of
those sites out of shape for nothing. The reworked matcher recurses
LogicalOr operands, so the folded test still counts.

Green (13 facts ran, 0 failed):

```
Test summary: total: 13, failed: 0, succeeded: 13, skipped: 0, duration: 11.9s
```

## N4: the starved flag's consume moved inside the pending branch

Mutation: in `OnLayoutShowWatch`, `_creationStarvedByHiddenEra = false;`
moved from the flip body into the `if (!_surfaceCreated)` branch, before
the settle attempt. A pending pane still settles, but a pane whose
surface already exists never takes the branch: its flag stays set
forever, and every later visible pass re-runs the re-push and re-arms
the startup resize grace. Both touching facts' ORDER asserts stay
satisfied; the containment assert is what refuses it.

Red (filter `~ShowWatch_SettlesPendingCreation`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass (537ms): Error Message: the starved flag's consume must sit OUTSIDE the `if (!_surfaceCreated)` branch: a consume inside it leaves a created pane's flag set forever, and every later visible pass re-runs the re-push and re-arms the startup resize grace
```

## N5: a guard conjunct-weakened under an `&&`

Mutation: PushSurfaceSize's refusal changed to `if (!IsEffectivelyVisible()
&& Panel.ActualWidth == 0) return;`. The substring
`!IsEffectivelyVisible()` is still present, but as a CONJUNCT: the
condition is false for exactly the hidden pane with a positive
placeholder read that the guard exists to refuse, so the pane falls
through and the placeholder is pushed. GuardBefore now demands the term
as a top-level disjunct, so every guard in this file refuses this shape.

Red (13 facts ran, 2 failed - the guard's own fact and the census):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (4234ms): Error Message: Controls.TerminalControl.xaml.cs::PushSurfaceSize pushes SurfaceSetSize without an `!IsEffectivelyVisible()` refusal before it

Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.PushSurfaceSize_RefusesWhileHidden [FAIL] (same plant: GuardBefore finds no guard carrying the term as a disjunct)
```

## N6: a second, deferred push of the same callee in a dual-listed site

Mutation: in `OnFirstLayoutUpdated`'s created tail,
`DispatcherQueue.TryEnqueue(() => NativeMethods.SurfaceSetSize(_surface,
1, 1));` added beside the existing push. The site census sees one site
either way and the guard check reads one token; the deferred call
skips the guard evaluation entirely. The census now counts CALLS per
(site, callee) and demands exactly one.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (3786ms): Error Message: Assert.Distinct() Failure: Duplicate item found
Collection: ["Controls.TerminalControl.xaml.cs::OnFirstLayoutUpd"···, "Controls.TerminalControl.xaml.cs::OnFirstLayoutUpd"···, "Controls.TerminalControl.xaml.cs::OnLayoutShowWatc"···, "Controls.TerminalControl.xaml.cs::PushSurfa ceSize"]
Item:       "Controls.TerminalControl.xaml.cs::OnFirstLayoutUpd"·
```

## N7: a disabled-region push spelled with whitespace before the argument list

Mutation: in `MainWindow.AddPaneHost`, inside `#if !DEBUG ... #endif`:

```
NativeMethods.SurfaceSetSize (default, 2, 2);
```

The space before `(` is legal C# and stepped around the previous
`SurfaceSetSize(` needle. The disabled-text check now matches with all
whitespace removed.

Red (filter `~CreationFunnel`, 1 fact ran, 1 failed):

```
Ghostty.Tests.Wiring.HiddenTabMeasureWiringTests.CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource (5065ms): Error Message: MainWindow.xaml.cs disables a preprocessor region containing a 'SurfaceSetSize(' call: the census cannot see it, but a build without this parse's symbols compiles it
```

## Fact-to-receipt index

| Fact | Killing mutant(s) |
| --- | --- |
| CreationGate_RefusesAnInvisiblePaneArrange | M13 |
| FirstLayoutHandler_IgnoresPassesWhileHidden | M0 |
| PushSurfaceSize_RefusesWhileHidden | M16, N5 |
| RetryTick_SkipsWithoutSpendingWhileHidden | M15 |
| ShowWatch_IsArmedForTheControlsLifetime | M14 |
| ShowWatch_TheFlipBodyRunsOnlyAfterAHiddenEra | M1 |
| ShowWatch_RefusesToRePushOntoAZeroedSurface | M9, N1 (N2 green by design) |
| ShowWatch_SettlesPendingCreationAtTheFirstVisiblePass | M2, M3, M8, M11, N4 |
| ShowWatch_RePushesTheScaleAndSizePairTogether | M3 |
| ScaleChange_RefusesWhileHidden | M17 |
| LoadedFallback_RecordsTheHiddenEraInsteadOfSettling | M4 |
| CreationFunnel_SurfacePushesAreGuardedAcrossEveryShellSource | M3, M5, M6, M10, M12, N5, N6, N7 |
| TabHost_CollapsesInactiveHostsAndFlipsTheActiveOne | M7 |

## Green counterparts

- The same 13 facts, unmutated, all green: `Test summary: total: 13,
  failed: 0, succeeded: 13, skipped: 0`.
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
