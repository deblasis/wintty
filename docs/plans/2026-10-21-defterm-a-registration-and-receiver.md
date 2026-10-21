# Default Terminal Plan A: registration engine and COM receiver (fork, C#) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the in-app default-terminal capability in the fork: the registration engine (runtime-scoped, hostile-safe, ownership-checked) and the ITerminalHandoff3 COM receiver with its lifecycle, plus the settings card, the status probe, and the hermetic test program that proves all of it without ever touching a real machine's default-terminal state.

**Architecture:** One new module `windows/Ghostty.Core/DefaultTerm/` owns registration state and the COM surface; `Program.cs` gains the embedding-flag intercept and an MTA COM thread; `App.xaml.cs` gains the post-election registration hook and the shutdown revoke-drain. The engine is registry-root-injectable and CLSID-injectable so every test runs against a random HKCU test root with throwaway GUIDs. The fork build carries nil placeholder CLSIDs (feature inert) until the release-side tier injection supplies real ones, preserving the placeholder-refusal safety property in dev and oss builds.

**Tech Stack:** .NET 10 WinUI 3 frontend, CsWin32 for Win32 interop, COM interop via ComVisible classes (public types, per the machine-verified recipe), MSTest fork test conventions, WinRT-free PowerShell 5.1 only on the VM (never in tests).

**Spec:** `docs/2026-10-21-default-terminal-design.md` (same branch). The plan argues from the spec; executors read both. Machine-verified recipe facts are normative: public COM types, IDefaultTerminalMarker on the terminal object, the IClassFactory fetch pattern, own proxy copy registered under HKCU, NULL startupInfo forbidden.

## Global Constraints

- Public repo writing rules apply to all committed text: no private release ids, no review/process vocabulary, no local machine paths, no em-dashes.
- Commit messages: short declarative subject, optional `area:` prefix, signed with the configured identity.
- The fork build ships nil placeholder CLSIDs; `CanRegister` is false while either CLSID is nil, and Register/Unregister refuse (spec, identity section). Real GUIDs arrive only via tier injection.
- No test, fixture, or helper may write `HKCU\Console\%%Startup`, real CLSID keys, or the handoff Interface keys; tests use `HKCU\Software\WinttyTest\<random>` roots and throwaway GUIDs only (spec, testing section, goal 2).
- Every mechanism this plan introduces has a named test home in this plan or is listed in the VM matrix (spec testing rule: a mechanism without a home is a spec defect).
- The COM object surface recipe is fixed: public types; ITerminalHandoff3 + IDefaultTerminalMarker on the terminal object; factory answers the IClassFactory fetch; callers never receive a NULL startupInfo path without RPC-safe handling (we always pass a real struct; our receiver tolerates NULL by treating it as absent title).
- Heavy jobs (dotnet build/test of the solution) go through the `wintty-build` incoda lane with `INCODA_OWNER` set; a single small project build and `just test-debug-gated` stay lane-free. Single-project `dotnet test` runs with `/p:RestoreLockedMode=true` (repo lockfile rule).
- `/p:TierName` identity rules do not change in this plan; the CLSID constant rides the existing build-info channel only.

## Review Focus

Five input classes the spec implies but individual task tests may not exercise; each is pinned by the named test in the owning task.

1. A hostile pre-seeded `TreatAs` on our terminal CLSID key surviving enable (user believes wintty is default while sessions route elsewhere). Pinned by `RegistrationHostileResetTests.TreatAs_IsSweptByEnable` (Task 3).
2. `%%Startup` naming another edition (or a third-party terminal) while this edition disables (must not clobber). Pinned by `RegistrationOwnershipTests.NonOwningDisable_LeavesSharedGroupAndSelection` (Task 4).
3. A handoff arriving while the UI thread is blocked past the budget (must return failure, not hang the caller). Pinned by `HandoffContractTests.AdoptionExpiry_ReturnsFailureAndReleases` (Task 7).
4. An elevated `-Embedding` launch (must never hold the class object; host session and exit). Pinned by `LifecycleWiringTests.ElevationCondition_BlocksPermanentRegistration` (Task 8).
5. Running from a dev tree or materialized build (must fail the bound-path self-check and register nothing). Pinned by `LifecycleWiringTests.UnboundExe_DoesNotRegister` (Task 8).

---

### Task 1: CLSID constants on the build-info channel

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermIdentity.cs`
- Modify: `windows/Ghostty.Core/Ghostty.Core.csproj` (GenerateBuildInfo target, around lines 103-197)
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTermIdentityTests.cs`

**Interfaces:**
- Consumes: the existing `GenerateBuildInfo` MSBuild target and `BuildInfo.g.cs` (AumId, Edition) in Ghostty.Core.
- Produces: `static class DefaultTermIdentity` with `string TerminalClsid` (braced GUID string), `string ConsoleClsid`, `string ProxyClsid` (constant `"{3171DE52-6EFA-4AEF-8A9F-D02BD67E7A4F}"`), `string[] HandoffIids`, `bool PlaceholdersActive` (true while TerminalClsid or ConsoleClsid is nil/unparseable), `bool CanRegister => !PlaceholdersActive`.

- [ ] **Step 1: Write the failing test**

Test names and assertions: `NilDefaults_RefuseRegistration` (default build info carries nil GUIDs; `CanRegister` is false), `WellFormedClsids_EnableRegistration` (inject two well-formed GUIDs via the test constructor or internal setter; `CanRegister` true), `ProxyAndIidConstants_AreTheVerifiedOnes` (ProxyClsid equals the constant above; HandoffIids contains exactly `{E686C757-9A35-4A1C-B3CE-0BCC8B5C69F4}`, `{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}`, `{746E6BC0-AB05-4E38-AB14-71E86763141F}`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test windows/Ghostty.Tests -p:RestoreLockedMode=true --filter DefaultTermIdentity` (lane-wrapped per global constraints)
Expected: FAIL, type not defined.

- [ ] **Step 3: Implement `DefaultTermIdentity`**

`TerminalClsid`/`ConsoleClsid` read generated build-info properties `DefaultTerminalClsid` and `DerivedConsoleClsid` whose MSBuild defaults are the nil GUID `{00000000-0000-0000-0000-000000000000}`; add the two `<PropertyGroup>` defaults and emit them in the BuildInfo generation exactly as `AumId` is emitted (follow the target's existing pattern; do not invent a new channel).

- [ ] **Step 4: Run test to verify it passes** (same command; PASS)

- [ ] **Step 5: Commit**, `DefaultTerm: identity constants on the build-info channel`

### Task 2: registration engine core (injectable root, write-set)

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermRegistryPaths.cs`
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermRegistration.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTermRegistrationTests.cs`

**Interfaces:**
- Consumes: `DefaultTermIdentity` (Task 1).
- Produces: `sealed class DefaultTermRegistryPaths` with `static DefaultTermRegistryPaths Production` and `DefaultTermRegistryPaths UnderTestRoot(string root)`; properties `StartupSubkey`, `ClsidSubkeyOf(string clsid)`, `InterfaceSubkeyOf(string iid)`. `sealed class DefaultTermRegistration` with ctor `(DefaultTermRegistryPaths paths, DefaultTermIdentity-like identity view, string winttyExePath, string openConsoleExePath, string proxyDllPath)`, members `bool IsRegistered()`, `EnableResult Enable()`, `DisableResult Disable()`, `bool OwnsSelection()`.

- [ ] **Step 1: Write the failing tests**

`Enable_WritesExactWriteSet` (after Enable on a test root: terminal CLSID LocalServer32 default = quoted absolute winttyExePath as REG_SZ; console CLSID key; proxy CLSID InprocServer32 with ThreadingModel=Both; three Interface ProxyStubClsid32 values; Startup pair console-then-terminal with the identity's GUIDs; assert every written path starts with the test root), `WriteOrder_BindingsBeforeSelection` (capture write sequence via a test hook or by deleting mid-sequence in a fault-injection mode; the Startup subkey is written last), `IsRegistered_MatchesOwnPairOnly`.

- [ ] **Step 2: Run tests to verify they fail** (filter `DefaultTermRegistration`)

- [ ] **Step 3: Implement the engine's enable path + IsRegistered**

Registry access via `Microsoft.Win32.Registry` + `RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default)` rooted at the injected root by prefixing subkey paths. Quoted-path form: `"C:\...\Wintty.exe"` with the fully expanded absolute path.

- [ ] **Step 4: Run tests to verify they pass**

- [ ] **Step 5: Commit**, `DefaultTerm: registration engine enable path`

### Task 3: hostile pre-seed reset and atomic rollback

**Files:**
- Modify: `windows/Ghostty.Core/DefaultTerm/DefaultTermRegistration.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/RegistrationHostileResetTests.cs`

**Interfaces:**
- Consumes: Task 2 engine.
- Produces: `EnableResult` gains `FailureReason` (`None`, `UndeletableKey(name)`, ...); enable enumerates each target key before writing.

- [ ] **Step 1: Write the failing tests**

`TreatAs_IsSweptByEnable` (pre-seed `TreatAs` + a hostile `LocalServer32` argument tail under the terminal CLSID key on the test root; Enable succeeds and the key afterwards contains exactly the values Enable writes), `UndeletableKey_FailsClosed` (fault-inject a delete failure; Enable returns FailureReason naming the key and writes nothing), `FailureAtStepN_RollsBack` (inject failure after each write step k; assert no partial state: either the full write-set or none).

- [ ] **Step 2-4: red, implement (enumerate + delete-tree-recreate + rollback journal), green**

- [ ] **Step 5: Commit**, `DefaultTerm: hostile pre-seed reset and rollback`

### Task 4: shared-key ownership, correspondence case table, capture store

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermCaptureStore.cs`
- Modify: `windows/Ghostty.Core/DefaultTerm/DefaultTermRegistration.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/RegistrationOwnershipTests.cs`

**Interfaces:**
- Consumes: Task 2/3 engine.
- Produces: `sealed class DefaultTermCaptureStore` with `(string path)` ctor, `CapturedState Capture()` / `CapturedState Load()`, `void Save(CapturedState)`; `CapturedState` holds the shared group's prior values, the prior selection pair, and timestamps; corrupt or missing file reads as the nil state. Disable implements the spec's case table keyed on terminal-half correspondence.

- [ ] **Step 1: Write the failing tests**

`NonOwningDisable_LeavesSharedGroupAndSelection` (A's identity, console key names B's exe path; Disable removes only A's terminal key and store; group and Startup untouched), `OwnerDisable_RestoresCorrespondingPrior` (captured prior is a third-party pair equal to the new selection's terminal; group prior restored), `NilPrior_AlwaysRemoves`, `FiveStepCascade_EndState` (enable A, enable B, rewrite selection to third-party pair, disable A, disable B; end: group removed, selection is the third-party pair, no dangling values), `StalePrior_DeadBinaries_FallsBackToNil`, `CaptureStore_CorruptFile_IsNil` plus `CaptureStore_GoodRestorePathIsAsserted` (a restore that should happen does happen; a never-restore mutant fails this test).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: shared-key ownership and capture store`

### Task 5: re-validation, install predicate, drift self-disable

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermRevalidator.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTermRevalidatorTests.cs`

**Interfaces:**
- Consumes: Tasks 2-4.
- Produces: `sealed class DefaultTermRevalidator` with `RevalidationReport Revalidate(RevalidationInputs inputs)`; inputs carry whether the exe passes the bound-path self-check (normalized comparison: strip quotes, expand env strings), whether single-instance is on, the engine view. The report drives repair writes (only when the predicate passes), the shadow unwrite, and the self-disable decision.

- [ ] **Step 1: Write the failing tests**

`LostSelection_UnwritesLingeringGroupOnly`, `LostSelectionWithForeignConsoleKey_TouchesNothing`, `InstallMoved_InstalledExe_RewritesBindings`, `InstallMoved_DevExe_OnlyReports`, `SingleInstanceOff_SelfDisables` (full unregister per case table, surfaced reason recorded), `ReportIsSurfaced` (the notice text names the violated precondition).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: startup revalidation and drift rules`

### Task 6: COM surface (public types, marker, factory)

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermComTypes.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTermComContractTests.cs`

**Interfaces:**
- Consumes: nothing (leaf).
- Produces: `public interface ITerminalHandoff3` (ComVisible, Guid `6F23DA90-15C5-4203-9DB0-64E73F1B1B00`, IUnknown-based, `int EstablishPtyHandoff(out IntPtr inPipe, out IntPtr outPipe, IntPtr signal, IntPtr reference, IntPtr server, IntPtr client, IntPtr startupInfo)` PreserveSig); `public interface IDefaultTerminalMarker` (Guid `746E6BC0-AB05-4E38-AB14-71E86763141F`, empty); `public sealed class TerminalHandoffObject : ITerminalHandoff3, IDefaultTerminalMarker`; `public sealed class HandoffClassFactory : IClassFactory` implementing the fetch pattern the activation machinery uses (CreateInstance with IID_IUnknown returns an object whose QI answers IClassFactory and the two handoff interfaces).

- [ ] **Step 1: Write the failing tests**

`QI_AnswersAllThreeIids` (Marshal.GetIUnknownForObject on the factory and product; QueryInterface succeeds for IClassFactory, ITerminalHandoff3, IDefaultTerminalMarker, fails for IDispatch), `FactoryFetch_PatternHolds` (CreateInstance(outer=null, IID_IUnknown) then QI for IClassFactory on the result), `TypesArePublic` (reflection: `IsVisible` true for all three; this is the machine-verified lesson as a standing test).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: COM surface with marker interface`

### Task 7: the in-call handoff contract

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/TerminalHandoffService.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/HandoffContractTests.cs`

**Interfaces:**
- Consumes: Task 6 types.
- Produces: `sealed class TerminalHandoffService` with `int HandleCall(HandoffArgs args)` where `HandoffArgs` carries the raw pointers and a `Func<Func<CancellationToken, HandoffPlacementResult>, TimeSpan, HandoffPlacementResult>` adoption scheduler injected for tests (production uses the UI dispatcher with the 5 s budget); returns HRESULT.

- [ ] **Step 1: Write the failing tests**

`NullHandles_Refused` (any of signal/reference/server/client zero: returns failure HRESULT, no pipes created), `NonProcessClientHandle_Refused` (a pipe handle where client belongs: GetProcessId fails: refused), `OverCap_Refused` (cap 64 via injectable counter), `PipesCreated_WithOverlappedWriteEnd` (the input pipe's our-side write end is a named pipe opened with FILE_FLAG_OVERLAPPED; assert via GetNamedPipeInfo or the handle's file type + an overlapped write succeeding), `StartupInfoTitle_CopiedSynchronously` (title marshaled before return; icon path ignored), `InCallDuplication_Synchronous` (duplicates exist before HandleCall returns; originals untouched), `FailureLattice_ReleasesExactlyWhatWasCreated` (inject failure at pipe2/dup3/title; assert handle accounting via a test handle-tracker), `AdoptionExpiry_ReturnsFailureAndReleases` (scheduler stub exceeds the budget: failure HRESULT, all created handles released), `NullStartupInfo_TreatedAsAbsentTitle` (does not throw; spec's RPC note is the caller's problem, we tolerate).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: in-call handoff contract`

### Task 8: class-object lifecycle, embedding intercept, wiring

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermComServer.cs`
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermBootstrap.cs`
- Modify: `windows/Ghostty/Program.cs` (embedding-flag intercept before InitGhostty; dedicated MTA thread), `windows/Ghostty/App.xaml.cs` (post-election hook, shutdown revoke-drain)
- Test: `windows/Ghostty.Tests/DefaultTerm/LifecycleWiringTests.cs`

**Interfaces:**
- Consumes: Tasks 1, 6, 7; the single-instance election result and the last-window-close exit path.
- Produces: `static class DefaultTermComServer` with `RegistrationKind Kind { get; }` (None/Transient/Permanent; the module flag `+defterm-status` reports), `void TryRegisterPermanent(BootstrapContext ctx)`, `uint RegisterTransient(Guid clsid)`, `void RevokeAll()`, `event Action<HandoffPlacementRequest> HandoffReceived`; `static class DefaultTermBootstrap` with `bool ShouldRegister(BootstrapContext)` implementing conditions (a)-(d) incl. the integrity-level check (GetTokenInformation TokenIntegrityLevel via CsWin32) and the normalized bound-path self-check, and `bool IsEmbeddingLaunch(string[] args)` matching both `-Embedding` and `/Embedding`.

- [ ] **Step 1: Write the failing tests**

`ElevationCondition_BlocksPermanentRegistration` (ShouldRegister false when the integrity-level stub reports high), `UnboundExe_DoesNotRegister` (self-check stub fails: no registration, Kind stays None), `DisabledEdition_DoesNotRegister`, `EmbeddingSpellings_BothRecognized`, `Classify_MediumNoPrimary_UpgradesToPermanent` (state machine: transient consumed, then Kind == Permanent), `Classify_Elevated_NeverPermanent` (Kind returns to None after the session; no path sets Permanent), `Shutdown_RevokeDrainsThenReleases` (ordering recorded via the test scheduler), `IdleEmbedding_ExitsCleanly` (the exit-code taxonomy entry is distinct).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: class-object lifecycle and startup wiring`

### Task 9: session placement queue

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermPlacementQueue.cs`
- Test: `windows/Ghostty.Tests/DefaultTerm/PlacementQueueTests.cs`

**Interfaces:**
- Consumes: Task 8's HandoffReceived event; the existing surface-creation API for a new tab in a chosen window.
- Produces: `sealed class DefaultTermPlacementQueue` (bounded, COM-thread-owned, drains to the UI thread when the first surface host is ready; most-recent-window rule: last-activated, then any, then new window).

- [ ] **Step 1: Write the failing tests**

`PreWindowHandoff_QueuesAndOpensWithFirstWindow`, `BudgetExpiry_DegradesThatLaunchOnly` (queued item past the budget returns the failure HRESULT to its waiting caller; later items unaffected), `MostRecentWindowRule_FallsBackInOrder`, `SecondaryReceiver_HostsInOwnWindow_NoForwarding` (no single-instance pipe traffic in any placement test; asserted via a pipe-traffic spy).

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: session placement`

### Task 10: settings card

**Files:**
- Create: `windows/Ghostty/Settings/Pages/DefaultTerminalCard.xaml` + `.xaml.cs`
- Modify: the settings page hosting the card (follow the existing AdvancedPage pattern)
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTerminalCardTests.cs` (view-model level)

**Interfaces:**
- Consumes: Tasks 2-5 engine + revalidator; `DefaultTermIdentity.CanRegister`.
- Produces: a card view model with `State` (`IsDefault` / `NotDefault` / `ManagedByPolicy` / `UnavailableBelowFloor`), `CanToggle`, `ToggleCommand`, surfaced failure reasons; OS-floor check (build >= 22621) hides the card; single-instance-off refuses enable with the reason naming the setting.

- [ ] **Step 1: Write the failing tests** (`BelowFloor_Hidden`, `EngineFailure_ReasonSurfaced`, `SingleInstanceOff_EnableRefusedWithReason`, `StateReflectsIsRegistered`)
- [ ] **Step 2-4: red, implement, green**
- [ ] **Step 5: Commit**, `DefaultTerm: settings card`

### Task 11: status probe

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DefaultTermStatusProbe.cs`
- Modify: the CLI action dispatch alongside the `+list-themes` pattern
- Test: `windows/Ghostty.Tests/DefaultTerm/DefaultTermStatusProbeTests.cs`

**Interfaces:**
- Consumes: `DefaultTermIdentity`, `DefaultTermRegistration`, `DefaultTermComServer.Kind`.
- Produces: headless `+defterm-status`: prints embedded terminal CLSID, console CLSID, proxy presence + hash match, live registration state, actual registration kind; exit 0 healthy / 1 not; machine-readable final line.

- [ ] **Step 1: Write the failing tests** (`ReportsActualKind_NotIntended`, `ExitCodes`, `DeterministicLineForScripting`)
- [ ] **Step 2-4: red, implement, green**
- [ ] **Step 5: Commit**, `DefaultTerm: status probe`

### Task 12: machine-state canary and scan gate

**Files:**
- Create: `windows/Ghostty.Core/DefaultTerm/DeftermCanary.cs` (snapshot/compare library over the exact write-set key list, sourced from `DefaultTermIdentity` + `DefaultTermRegistryPaths.Production`)
- Create: `tools/defterm-canary/` runner wrapper (the suite-level entry the tier harness invokes around the whole dev-runnable suite; owns the kill-test)
- Test: `windows/Ghostty.Tests/DefaultTerm/DeftermCanaryTests.cs`

**Interfaces:**
- Consumes: Task 2 paths + Task 1 identity.
- Produces: `DeftermCanary.Snapshot()` / `Diff(a,b)`; the wrapper performs the nested kill-test (one deliberate write to one named real-root value via the library, inner compare must go red using the canary's own compare routine, restore, outer compare clean) and fails the suite otherwise; the scan-gate test asserts no test assembly references the production root or real CLSIDs outside the canary's allowlist.

- [ ] **Step 1: Write the failing tests**

`Snapshot_DetectsSingleValueChange` (red on one named value), `KillTest_SubstituteComparator_Fails` (a comparator that is not the library's own makes the wrapper fail: comparator identity pinned), `ScanGate_RealRootReferences_BannedOutsideAllowlist`, `Allowlist_ConfinedToWrapperAssembly`.

- [ ] **Step 2-4: red, implement, green**

- [ ] **Step 5: Commit**, `DefaultTerm: machine-state canary and scan gate`

### Task 13: suite integration and branch hygiene

**Files:**
- Modify: test-tier harness wiring for the canary wrapper invocation (per-tier floor: at least one registration, one contract, one seam-independent defterm test per tier suite, plus the wrapper invocation count)
- Test: the whole `Ghostty.Tests` suite

- [ ] **Step 1: Run the full fork test suite** (lane-wrapped solution run; all green; quote the count from the solution run)
- [ ] **Step 2: Run `just verify-patches` and `just verify-build` for one tier from the release repo against this branch's pin** (the overlay surgery is Plan C; this step only confirms no accidental coupling)
- [ ] **Step 3: Whole-branch review gate**: the hard multi-reviewer bar on the branch diff before any merge (the review charter is this spec; the reviewers read the spec and the plan's Review Focus)
- [ ] **Step 4: Commit any fixes; re-run the suite**

## Execution notes

- Model routing (per task): Tasks 1, 10, 11 to haiku-tier executors (mechanical, interface-pinned); Tasks 2-9, 12-13 to sonnet-tier executors (COM semantics, failure lattices, concurrency); every task's reviewer on sonnet.
- Follow-up plans: B (Zig `Pty.adopt` seam, hermetic tests; next), C (release-side: tier CLSIDs, sparse MSIX generation, proxy pins, uninstall action, overlay-to-common patch-stack surgery), D (VM matrix runner and scenarios incl. the open residuals: marker-isolated control, derived CLSID activation, PackPseudoConsole presence, pairing matrix, dropdown UI write, elevated matrix).
- The design review's follow-up list (the ~30 named items) maps into Tasks 2-13 as shown in their test lists; anything without a fork home belongs to Plans C or D by construction.
- Coexistence until Plan C: the pro overlay's existing scaffold lives at `windows/Ghostty.Core/DefaultTerminal/` (namespace `Ghostty.Core.DefaultTerminal`, class `WindowsDefaultTerminalRegistration`); Plan A's module is `windows/Ghostty.Core/DefaultTerm/` (namespace `Ghostty.Core.DefaultTerm`, class `DefaultTermRegistration`). Distinct namespaces and class names, so both compile side by side while plans A and C land; Plan C's surgery removes the overlay scaffold and re-points its policy applier at the common engine. No task in Plan A may modify the overlay files.
