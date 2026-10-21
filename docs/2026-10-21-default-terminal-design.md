# Default Terminal registration and handoff: design

Status: accepted design, pre-implementation. Everything normative in this
document was verified against primary sources (the Windows Terminal
repository's spec and sources, the shipped binaries, and two independent
third-party implementations) during design review.

## Goals

1. Every shipped edition (sponsor, pro desktop, pro enterprise, pro legacy
   desktop and enterprise) can be set as the Windows "default terminal
   application" from inside the app, and receives console session handoffs.
2. Testing never mutates the developer machine's or CI host's real
   default-terminal state. CI runs on the developer machine, so this is one
   requirement, not two.
3. An in-app settings card is the v1 surface. Appearing in the OS Settings
   dropdown is a follow-up gated on a packaging feasibility spike (sparse
   MSIX with external location); v1 does not depend on it.

Non-goals for v1: Windows 10 and pre-22621 Windows 11 (the OS ignores the
selection keys there; the card is hidden below build 22621), rebuilding
OpenConsole per flavor, honoring window position or icon hints from the
handoff, and injecting environment or shell integration into delegated
clients.

## Verified platform facts

1. Selection is two REG_SZ GUID values under `HKCU\Console\%%Startup`:
   `DelegationConsole` and `DelegationTerminal`. Per-user, no admin,
   instantly reversible.
2. `DelegationConfig::s_GetDelegationPair` classifies the pair: if either
   value is the nil GUID the pair means "let Windows decide"; if either value
   is conhost's CLSID the whole pair means "Windows Console Host" and nothing
   delegates. You cannot keep conhost as the console half; a working console
   delegate is required.
3. Flow: a console client starts, the OS launches in-box conhost, conhost
   Co-creates the console CLSID (`IConsoleHandoff::EstablishHandoff`, IID
   `{E686C757-9A35-4A1C-B3CE-0BCC8B5C69F4}`; the delegate is OpenConsole.exe),
   OpenConsole enters ConPTY mode and Co-creates the terminal CLSID with
   `CLSCTX_LOCAL_SERVER`, then calls
   `ITerminalHandoff3::EstablishPtyHandoff` (IID
   `{6F23DA90-15C5-4203-9DB0-64E73F1B1B00}`). Current conhost and OpenConsole
   call v3 only.
4. `EstablishPtyHandoff([out] HANDLE* in, [out] HANDLE* out, [in] HANDLE
   signal, [in] HANDLE reference, [in] HANDLE server, [in] HANDLE client,
   [in] const TERMINAL_STARTUP_INFO* startupInfo)`: the terminal creates the
   two pipes and returns conhost's ends; `signal` is the ConPTY signal pipe;
   `reference` keeps the session alive; `server` is the console process;
   `client` is the console client.
5. The `system_handle` IDL attributes require a registered proxy/stub
   (`OpenConsoleProxy.dll`, proxy CLSID `{3171DE52-...}`) for exactly three
   IIDs: `{E686C757...}`, `{6F23DA90...}`, and `IDefaultTerminalMarker`
   `{746E6BC0...}`. The proxy is a session-wide singleton. The ConPTY NuGet
   package ships `conpty.dll` and `OpenConsole.exe` but no proxy; the proxy
   comes from a pinned Windows Terminal release zip.
6. The console CLSID is compiled into OpenConsole and selected by build
   branding: stable branding registers `{2EACA947-...}`, preview branding
   registers `{06EC847C-...}`. The preview-branded NuGet binary carries
   `{06EC847C...}` as its object-map GUID. A fresh console CLSID pointing at
   a stock OpenConsole.exe fails activation, so the console CLSID is a
   property of the shipped binary and must be derived from it, never assumed
   from flavor conventions.
7. COM closes `[in]` system_handles when the call returns; anything kept
   past the call must be duplicated synchronously inside it, and the
   `startupInfo` pointer is call-lifetime. The receiver never closes `[in]`
   handles itself.
8. If the handoff fails, the OS degrades that launch to conhost rendering.
   A failure HRESULT from the receiver is therefore always the safe outcome.
9. COM launches the delegate at the activating client's integrity level
   (stated in the platform spec). When a class object is already registered
   by a running process, activation is served by that process at its own IL
   (COM mechanics, confirmed on the verification VM).
10. The OS Settings dropdown enumerates packaged MSIX AppExtension
    candidates only. An unpackaged app can be the default but will not
    appear in the dropdown.

## Design

### Components

| Component | Location |
|---|---|
| `ITerminalHandoff3` COM server, class factory, embedding-flag intercept, validation, in-call handle work | fork, `windows/Ghostty.Core/DefaultTerm/`, wired from `Program.cs` and `App.xaml.cs` |
| Adopted-pty surface seam (`Pty.adopt`) | fork, libghostty: surface config extension plus `src/pty.zig` adopt path and a termio backend variant |
| Registration engine (per-edition CLSIDs, runtime-scoped binding, injectable registry root, hostile-key reset) | fork, common code; moves out of the enterprise-only overlay, which becomes a compile-time-gated enterprise policy applier |
| Per-edition CLSID constants and the derived console CLSID | release-side, injected through the existing tier identity build path, reachable at runtime (same channel as the pack id) |
| Settings card | fork, on the existing settings pages |
| `OpenConsoleProxy.dll` sourcing and bundle | fork csproj beside the ConPTY NuGet |
| `+defterm-status` probe | fork, a headless subcommand printing embedded CLSIDs and live registration state, exit 0/1 |
| Uninstall cleanup | release-side MSI custom action (per-user), the one sanctioned installer exception to runtime-scoped binding |

### Identity and registration

- Terminal CLSID: one fresh stable GUID per shipped edition, pairwise
  distinct, verified by tooling per built artifact.
- Console CLSID: derived from the shipped `OpenConsole.exe` at pin time
  (object-map GUID), committed beside the pin's hashes, re-derived on every
  pin bump, asserted at enable (the binary must match the committed hash, so
  the committed CLSID is correct by construction) and by startup
  re-validation.
- Proxy CLSID `{3171DE52...}` bound to the shipped proxy DLL.

Enable writes, in order: the terminal CLSID key (`LocalServer32` = fully
expanded, quoted absolute path of this edition's `Wintty.exe`; REG_SZ, never
REG_EXPAND_SZ), the console CLSID key (quoted path of this edition's
`OpenConsole.exe`), the proxy CLSID key (`InprocServer32`, ThreadingModel
Both), the three `Interface\{IID}\ProxyStubClsid32` values, and finally the
`%%Startup` pair, console value before terminal value. Bindings first,
selection last: a mid-write crash leaves at most a fully-bound but
unselected state, never a selection naming missing bindings.

Hostile pre-seed: enable treats every pre-existing key it writes as hostile.
It enumerates each key, deletes and recreates it clean if it contains
anything other than exactly the values enable writes (a `TreatAs`, an
`AppID`, an unexpected subkey), and fails closed with a surfaced reason if a
key cannot be deleted and recreated.

Key ownership: each edition owns its terminal CLSID key outright. The
console CLSID key, the proxy CLSID key, and the three Interface keys are
shared (other deployments materialize them too). The Interface and proxy
keys are edition-invariant, so they are handled as a group with the console
key, whose value does discriminate (it names an edition's OpenConsole.exe
path). An edition writes the shared group only when it takes `%%Startup`
ownership, capturing the group's prior values first. It unwrites the group
only when, at action time, the console key still names this edition's
binary. A non-owning edition's disable touches nothing shared.

Restore correspondence, with case table and evaluation order: `%%Startup` is
rewritten first, then the group decision is evaluated against the new
selection. "Corresponds" means the selection's terminal CLSID equals the
captured prior's terminal CLSID, the terminal half only, never the console
half (the console CLSID is branding-shared across vendors and identifies no
one).

| situation at unwrite | console key names | new selection | group action |
|---|---|---|---|
| owner-disable, prior was a classic third party | this edition | the just-restored prior pair | restore the captured prior |
| owner-disable, prior absent or nil | this edition | nil | remove the group |
| shadow-unwrite (selection moved via the OS dropdown) | this edition | the new selection | restore iff it corresponds and the prior is non-nil; a nil prior always removes |
| console key names another edition | anything | anything | never touch |

A key is removed only when its current value is ours. A colliding third
party's registration is the captured prior, restored when its selection
corresponds, otherwise not ours to touch. The full five-step cascade
(A enabled, B enabled, third party re-selected, disable A, disable B) is an
asserted test whose end state is: group removed, packaged resolution clean,
no dangling default.

Capture store: `%LOCALAPPDATA%\<pack id>\defterm-state.json`, written at
enable before `%%Startup` is touched, read by disable and by the uninstall
action (which reads it before removing files; upgrades never delete
per-user state). Crash before capture means the prior is nil. A restored
prior applies only if the pair being written names binaries that still exist
and passes the case table; otherwise the nil pair is written.

Startup re-validation: an enabled installation verifies at every startup
that `%%Startup` still names it (if not, it leaves everything alone except a
shared group whose console key still names it: that lingering shadow is
unwritten), that each binding still resolves to its binaries (the rewrite
branch runs only when this exe passes the installed-path self-check; a
non-installed exe only reports), and that the enable preconditions still
hold, including single-instance mode. A bumped OpenConsole whose compiled-in
CLSID changed is detected here and surfaced.

Coexistence: the console CLSID's collision partner is whichever other
terminal ships the same branding line (Windows Terminal Preview at the
current pin, not stable). While this edition owns the selection, its
OpenConsole serves the console half for any selection of that CLSID; the
wire contracts are IID-stable NDR, which bounds the cross-vendor skew. The
verification VM leg installs the actual collision partner, names the flavor
it installed, and refuses to run against the wrong one.

### Handoff receiver

Class-object policy, two kinds:

- Permanent registration: the instance that won the single-instance
  election as primary, has the feature enabled, passes the bound-path
  self-check (this exe's fully-expanded path equals the normalized
  `LocalServer32` value; quotes stripped, environment strings expanded), and
  is not elevated (medium integrity, own token) registers
  `REGCLS_MULTIPLEUSE` and holds it for its process lifetime. While the
  feature is disabled, nothing is exposed. Dev builds and materialized build
  trees fail the self-check unless `WINTTY_DEFTERM_UNBOUND=1` is set
  explicitly, which no recipe, smoke, or CI run sets.
- Transient registration: an instance the SCM launched for an activation may
  register `REGCLS_SINGLEUSE` solely to serve that activation (requires
  enabled plus the self-check; does not require the election, and is allowed
  for elevated instances). A transient registration never outlives its own
  activation.

Lifecycle, register, receive, classify, revoke: registration happens
immediately after the election and before WinUI initialization (the handoff
path is COM/MTA and needs no UI; `Main` is `[STAThread]`, so a dedicated MTA
thread hosts it). Handoffs arriving before the UI is ready are held in an
in-process, COM-thread-owned queue inside the DefaultTerm module (not the
App launch-deferral queue), bounded, drained to the UI thread when the first
surface host is ready; expiry returns the failure HRESULT, that launch
degrades to conhost, and the instance continues starting. Classification:
elevated receivers serve their activation under the transient registration
(consumed by it; they host their session with no class object in place, so
no medium-IL activation can reach them while their window is open), never
become primary, load no plugins, do no config watching, no probing, no
network, no update checks, and exit when their window closes; medium
receivers with no other primary become primary and upgrade to the permanent
registration (defensively revoking the consumed transient first, explicit
against `CO_E_OBJISREG`); medium receivers with an existing primary stay
secondary and host in their own window, which closes under the normal
last-window rule. `+defterm-status` reports the actual registration state
via a module-level flag, never the intended state.

Elevation position: the warm case (running unelevated app, elevated client)
hosts the session, matching the current Windows Terminal receiver, which
performs no integrity checks; the OS threat model already treats same-user
medium-IL code as able to tamper with any medium-IL process. The cold case
runs the launched app at the client's IL, so an elevated client gets an
elevated app hosting its session at matching integrity. The mixed-IL
funneling class cannot persist beyond a single activation through any launch
shape; the one theoretical window (the gap between the SCM launching an
elevated process and its SINGLEUSE registration being published) is contrary
to COM's documented single-use handout semantics, bounded to one activation
inside the hardened receiver, and probed on the VM. An elevated session
badge or separate elevated instance is a recorded follow-up.

In-call contract of `EstablishPtyHandoff`, in order:

1. Validate before any effect: all four `[in]` handles non-null; pipe and
   file handles confirmed by `GetFileType`; process handles validated by
   `GetProcessId` succeeding (GetFileType cannot classify them);
   `startupInfo` is an untrusted display hint (strings copied
   synchronously; icon path, position, pixel sizes, fill attribute, and
   show-window are ignored in v1; loading a caller-chosen resource path is
   an NTLM-leak vector and none of it is needed); a per-process concurrent
   adopted-session cap (default 64); over-cap or invalid returns failure
   and conhost degrades.
2. Do the work synchronously: create the two pipes with the write end an
   overlapped-capable named pipe (`FILE_FLAG_OVERLAPPED`; anonymous pipe
   ends break the IOCP write path silently while reads still work);
   duplicate signal, reference, server, client into self; peek the title.
   Handles transfer ownership: after a successful call libghostty owns and
   closes our pipe ends and duplicates; the receiver never closes `[in]`
   handles.
3. Failure lattice: for each intermediate, the receiver closes exactly what
   it created or duplicated so far and returns the failure HRESULT. No path
   both returns failure and keeps a handle; no path closes a stub-owned
   handle. A title-peek fault is non-fatal (empty title).
4. Adopt with a latency budget: the surface-adoption wait inside the call
   has a bounded timeout (5 s; the UI thread may be blocked by a modal);
   expiry returns failure and releases everything per the lattice.
5. Placement: a new tab in the most recent window (last-activated, then any
   window, then a new window).

No cross-process handle forwarding: the primary serves handoffs directly,
and a cold receiver with no primary becomes the primary. Delegated handles
never cross the single-instance named pipe, which keeps that wire format
untouched (its documented extension rule forbids new payload kinds, because
during an upgrade the primary is the older build and drops unknown shapes).
In the rare activation race (SCM launched a receiver because no class object
existed for a moment), the receiver serves its activation transiently, finds
a primary, and hosts in its own window as a secondary; the "tab in most
recent window" guarantee cannot hold inside that one window, which is
documented and asserted on the VM. No forwarding of handles exists as an
alternative.

### Core seam

`Pty.adopt(server, reference, signal, in, out)`: resolve
`ConptyPackPseudoConsole` from the bundled `conpty.dll` (extending the
existing resolver's trio; the export is not in kernel32), pack the handoff
handles into an HPCON, and reuse the existing HPCON setSize plumbing for
resize and the existing close path for teardown (reset the pseudoconsole,
which delivers CTRL_CLOSE_EVENT to attached clients, then close pipe ends).
Hand-written ConDrv signal packets are used only if the pinned conpty.dll
lacks the export, with written justification.

The termio backend gains an `adopted` variant reusing the Windows reader
thread (blocking reads plus CancelIoEx; pipe provenance is irrelevant), the
Windows process-wait thread (waiting on the client handle), and the
child-exit push. Teardown triggers: client-handle wait or read-EOF on either
pipe, whichever first (covering console death and FreeConsole, which
surfaces as EOF with exit code STILL_ACTIVE while the client lives). User
close honors `confirm-close-surface`; TerminateProcess on the client is
never used by us.

Surface facts: the startup-info title seeds an initial, OSC-overridable
title (a config-static title would suppress later TITLE and OSC 0/2
changes); pwd stays unset until OSC 7; initial size converts
`dwXCountChars`/`dwYCountChars` to pixels with the target window's font
metrics at its DPI (the default config at the primary monitor's DPI for a
cold first window), falling back to the default size when both are zero; the
client's environment is its parent's, with nothing injected.

### UX and policy

A settings card shows the state (is or is not default, managed by policy,
or unavailable below the OS floor) and an enable/disable toggle, hidden
below build 22621. Enabling requires single-instance mode; the setting is a
dev-only escape hatch, and if it is turned off while enabled the next
startup self-disables with a surfaced notice rather than leaving an enabled
registration whose routing model is unsatisfiable. The enterprise policy
that forces this registration stays enterprise-gated by a compile-time
variant gate; other builds contain no policy applier, and a per-tier test
proves a machine-wide policy does nothing there. The dev-tree predicate for
the self-check is: the exe's fully-expanded path is under `%LOCALAPPDATA%\<this
edition's pack id>\` and equals the registered `LocalServer32` value.

Enable-time checks refuse with a reason before any write: OS floor,
OpenConsole.exe present, proxy present and identity-verified, single-instance
on, no undeletable hostile key. Enable is atomic in effect: a failure at any
write step rolls back to the captured prior state.

### Supply chain

At pin time, SHA-256 of `OpenConsole.exe` (NuGet) and `OpenConsoleProxy.dll`
(release zip) are committed beside the version pins and ship as build-time
constants on the build-info channel. Packaging verifies Authenticode
signatures (Microsoft signer) for both. Enable re-verifies the on-disk
hashes and signatures before writing any key. The proxy loads into in-box
conhost at the client's integrity level, which makes it the strongest single
artifact in the chain; a one-time VM check cannot survive later re-sourcing,
so identity is enforced, not assumed.

## Testing

Dev-runnable classes run in the per-tier CI; nothing they do may mutate the
machine's real default-terminal state.

- Unit, hermetic: the registration engine against an injected registry root
  (`...\WinttyTest\<random>`) with throwaway CLSIDs; a two-arm nil test
  (injected nil GUID refuses, real GUID succeeds); an injected-prefix oracle
  (every written path starts with the random root); root teardown on
  dispose. Failure-path suite: enable atomicity (failure injected at each
  write step, no partial state, reason surfaced), enable over pre-seeded
  hostile keys, ownership-checked disable and uninstall end states, upgrade
  re-validation, capture-store lifecycle (crash before capture, prior naming
  dead binaries, corrupt store treated as nil and never blocking disable,
  and the good restore path asserted so a never-restore mutant fails), the
  edition-interleave guarantee including the five-step cascade, the drift
  self-disable arm, and the four-step variant with a stale
  registration-but-live-binaries prior.
- Machine-state canary: a suite-level runner wraps the whole dev-runnable
  suite, snapshotting `%%Startup`, the real CLSID keys, and the three
  Interface keys at entry and comparing at exit, failing on any delta. Its
  key-set comes from the same committed constants. A scan gate bans
  real-root or real-CLSID references in dev-runnable tests (constructor
  defaults banned; the allowlist names the canary itself and, in the
  wrapper's own assembly only, the kill-test's single named value).
  Kill-test: the canary's own compare routine must go red on one deliberate
  write to one named real-root value, which the wrapper then restores, with
  the outer compare ending clean; a substitute comparator that passes while
  the real compare stays unexercised is itself a failure.
- Contract, in-process COM: factory and `EstablishPtyHandoff` over the COM
  wrappers with real pipes; catches managed vtable wiring, validation
  refusals, pipe creation, synchronous in-call duplication, startup-info
  copying. Stated boundary: IDL and proxy conformance, stub-side handle
  closing, and SCM launch semantics are VM-only, and the mutant table
  carries mutants only the VM leg kills (vtable-order swap; dropped
  synchronous duplication).
- Hermetic adopted-pty seam tests: in-test pipes, a fake client handle, and
  a signal pipe; VT bytes reach the surface grid; resize produces a correct
  packed-HPCON resize (golden fixture); closing the client handle triggers
  exit cleanup; read-EOF tears down; the user-close path honors
  `confirm-close-surface` and the reset-then-close order; no child process
  is ever spawned on this path.
- VM end-to-end (isolated VM only, build 22621 or later, the harness refuses
  on older builds): a checked-in runner script emits a results file naming
  each scenario and verdict, copied back and attached to the change;
  required before merge, before release, and on every ConPTY or proxy pin
  bump. Scenarios, all input-synthesis-free (console clients are launched
  with CreateProcess; no synthesized input anywhere; the app side is
  asserted through the in-app test seam extended for delegated surfaces):
  register, launch cmd, session lands as a tab (surface exists, grid
  contains the client's marker bytes, key input reaches the client's stdin,
  resize reaches the client, exit closes the surface); delegated-tab close
  with the client-side CTRL_CLOSE_EVENT asserted; disable restores conhost;
  the elevated matrix (warm, cold, cold-then-medium with an IL oracle,
  manually elevated primary registers nothing); coexistence with the actual
  collision-partner flavor, which the leg names and refuses to substitute;
  two editions plus the registration race plus the activation-race arm; the
  plain cold arm with a chained warm launch (second console lands as a tab
  in the cold instance's window with no new process); budget-expiry arms;
  the startup shadow-unwrite arm; rewrite-versus-report arms;
  uninstall-while-enabled reset (the action reads the capture store before
  removing files); hostile pre-seed; dead-registration degrade. The
  real-root, real-CLSID path is exercised here and only here, with the real
  keys enumerated at their real paths (all three CLSID keys, the
  `LocalServer32` value equal to the installed exe path, the three
  Interface keys).
- Per-tier non-vacuity: the `+defterm-status` probe against each tier's
  built artifact asserts the embedded terminal CLSID is non-nil, equals the
  tier's committed GUID, and is pairwise distinct across shipped tiers; a
  tooling test asserts the committed table itself. Per-tier floor: each
  shipped tier's suite carries at least one registration-engine, one
  contract, and one seam test, plus the canary wrapper invocation count.
- Mutant-kill scope: registry layer, handle duplication, adopted-pty seam,
  CLSID build-time injection, validation refusals, restore correspondence,
  the elevation check (a build with the integrity check dropped must fail
  the cold-then-medium IL oracle), and the canary kill-test. The VM
  uninstall and upgrade arms assert the capture-store lattice (absent,
  stale, corrupt) and that upgrades preserve the store.
- Pin-bump SOP: every ConPTY pin bump re-runs the protocol-smoke matrix plus
  a delegated-session probe (image-protocol marker through a handoff-created
  session) plus the pairing check, whose first step is re-deriving the
  compiled-in console CLSID from the new binary and updating the committed
  constant.
- Repo gates: wiring tests per the fork's wiring-test pattern,
  `verify-patches` and `verify-build`, parity manifests re-baselined after
  the overlay-to-common move.

## Open verification items (VM, during implementation)

Activation confirms the derived console CLSID; `ConptyPackPseudoConsole` is
present in the pinned conpty.dll; the proxy and OpenConsole pairing matrix
(mixed NuGet plus zip versus a matched zip pair, with the matched-pair
fallback); classic-versus-packaged COM precedence in both directions with
the actual collision partner; the resize golden bytes for the seam fixture;
the elevated matrix outcomes.

## Accepted residual risks

- Warm-case elevated hosting: a medium-IL process drives an elevated
  shell's stdin through the hosted pty. This matches the current Windows
  Terminal receiver; an elevated-session badge or separate elevated
  instance is the recorded follow-up.
- Same-IL baseline: same-user code can rewrite HKCU, inject into a
  medium-IL process, or keylog. Inherited from the OS model; every new
  trust edge this design creates is validated or eliminated above.
- The OS loads a user-hijackable proxy-stub CLSID for `IConsoleHandoff`;
  this design's share of that weakness (what it writes, its proxy's
  integrity) is addressed by the ownership rules and the supply-chain
  enforcement.
- Cross-vendor version skew while enabled, bounded by IID-stable contracts,
  the pairing checks, and the coexistence leg.
- Dropdown absence for unpackaged apps, pending the packaging spike.
- Inside the one activation-race window, a delegated session opens in its
  own window rather than the most recent window; forced by the
  no-forwarding rule, documented and VM-asserted.
