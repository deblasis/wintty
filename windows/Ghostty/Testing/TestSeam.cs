using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Ghostty.Panes;
using Microsoft.UI.Dispatching;

namespace Ghostty.Testing;

/// <summary>
/// The opt-in in-process test seam: one named pipe inside Wintty whose
/// newline-delimited JSON commands drive the REAL input handlers -- the tab
/// manager ops and the vertical strip's drag engine -- on the UI thread.
/// No OS input is synthesized, nothing is focused, and the user can be using
/// the machine while a script drives the app.
///
/// What the seam grants, stated plainly, because it is no longer only "drive
/// its UI": send-text hands arbitrary bytes to a live shell, which is
/// arbitrary command execution as the user, and the read ops report tab
/// titles and working directories. Whoever can talk to this pipe can do
/// both. Everything below follows from taking that seriously.
///
/// Three gates, none of which is the pipe's name:
///
/// 1. The build. Nothing in here exists unless TESTSEAM is defined, which
///    Debug does and a shipping Release does not (windows/Directory.Build.props,
///    the same shape demo mode uses). A user's install carries zero seam bytes,
///    so the rest of this only concerns machines that build the seam in.
/// 2. The environment. WINTTY_TEST_SEAM must hold a 32-character hex session
///    token -- not "1", not "true". The token is the credential and the pipe
///    is named after it, so a process that did not launch this app cannot
///    guess the name, and cannot take the name first either (see below).
///    Rejecting the weak spellings is deliberate: an operator who sets "1"
///    gets no seam rather than a seam anybody can reach.
/// 3. The ACL. PipeOptions.CurrentUserOnly, so the pipe's DACL is one ACE for
///    the launching user. Without it .NET's default DACL grants Everyone AND
///    ANONYMOUS LOGON generic read, which is enough for any other account on
///    the box -- or an authenticated peer over SMB, since named pipes are
///    reachable as \\host\pipe\name -- to occupy the single server instance
///    and shut the seam down. Not enough to send commands (the default grants
///    no write), but a denial of service reachable from off the machine.
///
/// send-text carries a fourth gate of its own, WINTTY_TEST_SEAM_INPUT=1,
/// because "move this tab" and "run this command in my shell" should not be
/// the same permission.
///
/// What none of this defends against: a process already running as this user
/// at medium integrity. It can read the token out of this process's
/// environment block, and could equally well inject a thread. That is the
/// same-user bar every dev tool on the box clears, and it is the honest
/// boundary. A LOWER integrity process of the same user -- a sandboxed
/// browser tab -- is outside it: the pipe has no explicit label, so it sits
/// at medium and the no-write-up policy refuses its commands.
///
/// The pipe serves one client at a time; a second command connection waits
/// for the first to hang up. A second opted-in process with a different token
/// gets a different name and runs alongside. A name collision now means
/// something took the token's name first, which cannot happen by accident;
/// the server goes quiet rather than hot-spinning on a name it can never
/// take, and the driver's connect times out.
///
/// Protocol: each request is one line of JSON, {"op": "...", ...args}; each
/// response is one line, {"ok": true, ...} or {"ok": false, "error": "..."}.
/// Requests are length-capped (see MaxRequestBytes). Commands marshal to the
/// UI thread and every ack answers after the work settled, so a driver never
/// races the app.
///
/// One window is served (the first): the spike scenario is a single window.
/// Multi-window routing is hardening, not architecture.
/// </summary>
internal static class TestSeam
{
#if TESTSEAM
    private const string EnvVar = "WINTTY_TEST_SEAM";

    /// <summary>
    /// The second opt-in, for send-text alone. Everything else the seam does
    /// moves chrome or reports state; send-text runs commands as the user, so
    /// a harness that only drags tabs should not be carrying that power.
    /// </summary>
    private const string InputEnvVar = "WINTTY_TEST_SEAM_INPUT";

    /// <summary>
    /// The pipe is named after the session token, so the name is a secret
    /// rather than a well-known address. A squatter cannot pre-create a name
    /// it cannot guess, which is the half of squatting that silences the app;
    /// the client's own CurrentUserOnly and its token cover the other half.
    /// </summary>
    private const string PipeNamePrefix = "wintty-test-seam-";

    /// <summary>
    /// 128 bits, hex. The length is exact and the alphabet is closed, which
    /// is also what keeps the pipe name well-formed: no separator, no
    /// traversal, nothing a caller-supplied string could smuggle into it.
    /// </summary>
    internal const int TokenLength = 32;

    /// <summary>
    /// The ceiling on one request line.
    ///
    /// StreamReader.ReadLineAsync has no ceiling: it grows until a newline
    /// arrives, so a client that sends bytes and never a newline walks this
    /// process out of memory. The largest honest request is a seed-tabs title
    /// list, three orders of magnitude under this; the cap is far above every
    /// harness and far below anything that hurts.
    /// </summary>
    private const int MaxRequestBytes = 64 * 1024;

    private static CancellationTokenSource? _lifetime;
    private static int _started;
    private static string? _pipeName;
    private static bool _inputAllowed;

    /// <summary>
    /// Called once per window from the MainWindow constructor. The first
    /// window in a seam-enabled process wins; later windows are no-ops. The
    /// server dies with that window (the app closes with it).
    /// </summary>
    internal static void Start(MainWindow window)
    {
        // The gate reads before anything else happens, and it demands a real
        // session token: an unset, empty, "0", "1" or "true" value is off.
        var sessionToken = Environment.GetEnvironmentVariable(EnvVar);
        if (!IsSessionToken(sessionToken)) return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _pipeName = PipeNamePrefix + sessionToken;
        _inputAllowed = Environment.GetEnvironmentVariable(InputEnvVar) == "1";

        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        window.Closed += (_, _) =>
        {
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        };
        _ = Task.Run(() => ServeAsync(window, token));
    }

    /// <summary>
    /// Exactly 32 hex characters and nothing else. Exact rather than
    /// "at least", so there is one spelling of an armed seam and no sliding
    /// scale of weak ones; closed alphabet, so the value can be concatenated
    /// into a pipe name without any further sanitising.
    /// </summary>
    private static bool IsSessionToken(string? value)
    {
        if (value is null || value.Length != TokenLength) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// One connection at a time; a client that hangs up (or dies) frees the
    /// name for the next. Transport-level failures are not findings: the
    /// loop keeps serving.
    /// </summary>
    private static async Task ServeAsync(MainWindow window, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // CurrentUserOnly is the ACL. Without it the DACL Windows
                // hands an unsecured pipe grants Everyone and ANONYMOUS LOGON
                // generic read -- measured, not assumed:
                //   D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<user>)(A;;FR;;;WD)(A;;FR;;;AN)
                // Read is not enough to send a command, but it IS enough to
                // take the one server instance and hold it, from another
                // account on the box or from an authenticated SMB peer over
                // \\host\pipe\. With the flag the DACL is a single ACE for
                // this user.
                pipe = new NamedPipeServerStream(
                    _pipeName!, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Something already owns a name derived from this session's
                // token, which is not something that happens by accident.
                // Going quiet is still the right move -- a retry loop would
                // hot-spin on a name it can never take -- and the driver
                // hears about it as a connect that times out.
                return;
            }
            try
            {
                await pipe.WaitForConnectionAsync(token);
                await ServeConnectionAsync(pipe, window, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // The client hung up mid-conversation. Fall through to the
                // finally, then accept the next connection.
            }
            finally
            {
                try { if (pipe is { IsConnected: true }) pipe.Disconnect(); }
                catch { /* the name must free up either way */ }
                pipe.Dispose();
            }
        }
    }

    private static async Task ServeConnectionAsync(
        NamedPipeServerStream pipe, MainWindow window, CancellationToken token)
    {
        var reader = new BoundedLineReader(pipe, MaxRequestBytes);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            var (status, line) = await reader.ReadLineAsync(token);
            if (status == LineStatus.Eof) return; // client hung up
            if (status == LineStatus.TooLong)
            {
                // There is no resyncing after this: the rest of the oversized
                // line is bytes of unknown shape, and treating whatever
                // follows the cap as the next request is how a length bug
                // becomes a parsing bug. Say so once and drop the connection;
                // the accept loop takes the next client.
                await writer.WriteLineAsync(
                    Error("parse", $"request exceeds {MaxRequestBytes} bytes"));
                return;
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await ExecuteAsync(window, line);
            await writer.WriteLineAsync(response);
        }
    }

    private enum LineStatus
    {
        /// <summary>A complete line, within the cap.</summary>
        Ok,

        /// <summary>The client hung up.</summary>
        Eof,

        /// <summary>The cap was reached before a newline was.</summary>
        TooLong,
    }

    /// <summary>
    /// Reads newline-delimited UTF-8 requests off the pipe with a hard
    /// ceiling on one line.
    ///
    /// This exists because StreamReader.ReadLineAsync has no ceiling. It
    /// buffers until it sees a newline, so a client that opens the pipe and
    /// streams bytes without one grows the buffer until the app dies -- a
    /// memory exhaustion of the whole terminal, driven from outside it, with
    /// no op ever dispatched and nothing in the JSON layer able to see it
    /// coming. The cap has to sit under the reader, not over it.
    /// </summary>
    private sealed class BoundedLineReader(Stream stream, int maxBytes)
    {
        private readonly byte[] _chunk = new byte[4096];
        private readonly MemoryStream _line = new();
        private int _next;
        private int _filled;

        public async Task<(LineStatus Status, string Line)> ReadLineAsync(
            CancellationToken token)
        {
            _line.SetLength(0);
            while (true)
            {
                if (_next == _filled)
                {
                    _filled = await stream.ReadAsync(_chunk, token);
                    _next = 0;
                    // A read of zero is the hang-up. A partial line before it
                    // is a truncated request, not a request: dropping it is
                    // what keeps half a command from being executed.
                    if (_filled == 0) return (LineStatus.Eof, string.Empty);
                }

                var newline = Array.IndexOf(_chunk, (byte)'\n', _next, _filled - _next);
                var take = (newline >= 0 ? newline : _filled) - _next;
                if (_line.Length + take > maxBytes) return (LineStatus.TooLong, string.Empty);

                _line.Write(_chunk, _next, take);
                _next += take;
                if (newline < 0) continue;

                _next++; // step over the newline itself
                var bytes = _line.GetBuffer().AsSpan(0, (int)_line.Length);
                // Trailing CR, because a driver on Windows may spell the
                // terminator "\r\n" even though the protocol says "\n".
                if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
                return (LineStatus.Ok, Encoding.UTF8.GetString(bytes));
            }
        }
    }

    private static async Task<string> ExecuteAsync(MainWindow window, string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            return Error("parse", $"request is not JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("op", out var op)
                || op.ValueKind != JsonValueKind.String)
            {
                return Error("parse", "request needs a string 'op'");
            }

            var opName = op.GetString()!;
            try
            {
                return await RunOnUiThreadAsync(
                    window,
                    () => ExecuteOnUiThreadAsync(window, opName, root),
                    settle: !IsObserver(opName));
            }
            catch (Exception ex)
            {
                // A command that throws IS a finding: the response carries
                // it and the app keeps running.
                return Error(opName, ex.Message);
            }
        }
    }

    /// <summary>
    /// Ops that only look. They skip the settling layout pass, which for a
    /// command that changed nothing has nothing to settle -- and during a
    /// layout switch is ruinous: the morph ghost's Width and Height are
    /// dependent animations, so every frame already has a layout pass
    /// pending, and forcing a synchronous one per sample dragged the
    /// sampling interval from a few milliseconds to about 300 and stretched
    /// the 340ms switch past 900ms. A filmstrip that changes the motion it
    /// is filming is not evidence.
    /// </summary>
    /// <remarks>
    /// get-state is deliberately NOT here. It settles today and the drag
    /// harnesses read their assertions off it, so the pass is part of its
    /// contract; layout-frame is new and owes no one that.
    /// </remarks>
    private static bool IsObserver(string op) => op is "layout-frame";

    /// <summary>
    /// The one marshal: every command, whatever it touches, runs on the
    /// window's UI thread and the response awaits the work.
    /// </summary>
    private static Task<string> RunOnUiThreadAsync(
        MainWindow window, Func<Task<string>> action, bool settle = true)
    {
        var done = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var result = await action();
                    // Settled means settled: layout, not just the manager.
                    if (settle) window.TestSeamSettleLayout();
                    done.SetResult(result);
                }
                catch (Exception ex) { done.SetResult(Error("ui", ex.Message)); }
            }))
        {
            done.SetResult(Error("ui", "dispatcher unavailable"));
        }
        return done.Task;
    }

    private static async Task<string> ExecuteOnUiThreadAsync(
        MainWindow window, string op, JsonElement args)
    {
        var manager = window.TabManager;
        switch (op)
        {
            case "get-state":
                return OkWithState(window, manager, op);

            case "seed-tabs":
            {
                var count = ArgInt(args, "count", -1);
                if (count < 1 || count > 32)
                    return Error(op, "count must be 1..32");
                var titles = ArgStrings(args, "titles");

                // Deterministic start: seed means a clean slate, so leftover
                // groups and the pinned prefix from a previous scenario go
                // first -- through the manager's own dissolvers. Then close
                // down to one tab (closing the last would close the window),
                // retitle it, and grow the rest. Real tabs through the
                // manager: NewTab runs the pane factory and spawns real
                // shells, exactly the state a human builds.
                while (manager.Groups.Count > 0)
                    manager.DissolveGroup(manager.Groups[0]);
                foreach (var tab in manager.Tabs.ToArray())
                {
                    if (tab.IsPinned) manager.SetPinned(tab, false);
                }
                while (manager.Tabs.Count > 1)
                {
                    manager.CloseTab(manager.Tabs[^1]);
                    // One teardown per dispatcher pass: a synchronous
                    // four-surface close is denser than any human produces,
                    // and native surface teardown racing the next churn is
                    // exactly the interleaving a seam should not invent.
                    if (manager.Tabs.Count > 1)
                        await WaitForLowPriorityAsync(window.DispatcherQueue);
                }
                for (int i = 0; i < count; i++)
                {
                    var tab = i == 0 ? manager.Tabs[0] : manager.NewTab();
                    tab.UserOverrideTitle = i < titles.Count ? titles[i] : $"tab-{i + 1}";
                }
                return OkWithState(window, manager, op);
            }

            case "pin":
            case "unpin":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                // "via":"router" sends it the way the context menu does --
                // through the router command, which announces the change
                // -- otherwise straight through the manager op the drag
                // engine commits through.
                var viaRouter = ArgString(args, "via") == "router";
                if (viaRouter) window.TestSeamRouter.RequestPin(tab, op == "pin");
                else manager.SetPinned(tab, op == "pin");
                return OkWithState(window, manager, op);
            }

            case "group":
            {
                var indices = ArgInts(args, "indices");
                if (indices.Count == 0) return Error(op, "group needs indices");
                var members = new List<TabModel>();
                foreach (var index in indices)
                {
                    var tab = TabAt(manager, index);
                    if (tab is null) return Error(op, $"no tab at index {index}");
                    members.Add(tab);
                }
                // The manager's own ops: one fresh group, then the joins.
                // Refusals (pinned, unowned) come back from the manager as
                // a null group, never as broken state.
                var group = manager.CreateGroup(members[0]);
                if (group is null)
                    return Error(op, "the manager refused the group (pinned or unowned)");
                group.Title = $"group-{manager.Groups.Count}";
                for (int i = 1; i < members.Count; i++)
                    manager.JoinGroup(members[i], group);
                return OkWithState(window, manager, op);
            }

            case "collapse":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                if (tab.Group is null) return Error(op, $"tab {index} is not in a group");
                var collapsed = ArgBool(args, "collapsed", true);
                // "via":"router" sends it the way the header's chevron does:
                // the router's collapse command stages through the strip
                // (focus re-home, drag stand-down) before the manager flips
                // the bit. The default is the bare manager op.
                if (ArgString(args, "via") == "router")
                    window.TestSeamRouter.RequestCollapseGroup(tab.Group, collapsed);
                else
                    manager.CollapseGroup(tab.Group, collapsed);
                return OkWithState(window, manager, op);
            }

            case "layout-frame":
                // One filmstrip frame's worth of truth, and the reason the
                // op exists at all: manager state is identical across a
                // switch that flashes and one that does not, so a frame has
                // to carry what the strips were HOLDING.
                return LayoutFrameJson(window, manager);

            case "toggle-layout":
            {
                // The keyboard path's own dispatch: the router event the
                // chord raises, so the seam cannot drift from the real
                // action.
                window.TestSeamRouter.RequestToggleTabLayout();

                // "await":false answers the moment the switch is under way,
                // leaving the driver free to film it. The pipe serves one
                // command at a time, so a blocking toggle would hold the
                // only channel a sampler could use for the whole flight --
                // the transition would be unobservable through the seam
                // that started it.
                if (!ArgBool(args, "await", true))
                    return OkWithState(window, manager, op);

                // The ack waits out the morph. ToggleTabLayout no-ops while
                // LayoutCoordinator is mid-switch, so a driver that did not
                // wait would silently skip every other toggle.
                var deadline = Environment.TickCount64 + 10_000;
                while (window.TestSeamLayoutSwitching
                       && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(15);
                }
                return window.TestSeamLayoutSwitching
                    ? Error(op, "layout switch did not settle within 10s")
                    : OkWithState(window, manager, op);
            }

            case "toggle-sidebar":
            {
                // The pane-pinned toggle's own dispatch. The ack waits out
                // the width tween by watching the strip's pane width stop
                // moving, so a screenshot taken after this answer shows the
                // settled width.
                var before = window.TestSeamVerticalStrip?.TestSeamPaneWidth ?? -1;
                window.TestSeamRouter.RequestToggleSidebarCollapse();
                var deadline = Environment.TickCount64 + 5_000;
                double stable = before;
                var stableSince = Environment.TickCount64;
                while (Environment.TickCount64 < deadline)
                {
                    await Task.Delay(30);
                    var now = window.TestSeamVerticalStrip?.TestSeamPaneWidth ?? -1;
                    if (now != stable)
                    {
                        stable = now;
                        stableSince = Environment.TickCount64;
                    }
                    else if (Environment.TickCount64 - stableSince > 250)
                    {
                        break;
                    }
                }
                window.TestSeamSettleLayout();
                return OkWithState(window, manager, op);
            }

            case "cycle":
            {
                // The Ctrl+Tab chord's own dispatch, so the switcher popup
                // a driver measures is the one the chord raises. The popup
                // auto-dismisses on a 1.2s timer the moment it opens, so
                // the ack deliberately does not wait for anything beyond
                // the layout settle: a driver that slept here would be
                // photographing an empty window.
                window.TestSeamRouter.RequestMruCycle(ArgBool(args, "forward", true));
                return OkWithState(window, manager, op);
            }

            case "select":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                // The manager's own activation: the same op every click and
                // jump chord funnels into, so the selection sync runs.
                manager.Activate(tab);
                // The window's focus restore rides the dispatcher, so the
                // ack waits that turn out -- otherwise the state below
                // reports the active leaf from before the switch settled.
                await WaitForLowPriorityAsync(window.DispatcherQueue);
                return OkWithState(window, manager, op);
            }

            case "close":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                if (manager.Tabs.Count <= 1)
                    return Error(op, "the last tab's close is a window close, which is a "
                        + "different teardown and is not staged here");
                // A multi-pane tab's close asks the user first, through a dialog
                // this assembly can raise but a harness cannot answer. Refused
                // rather than forced, so the op never means something a click
                // does not.
                if (tab.PaneHost.PaneCount > 1)
                    return Error(op, "close refuses a multi-pane tab: a click there "
                        + "raises the confirmation dialog, and answering it is not "
                        + "something this seam stages");
                // The manager's own close -- what the close button reaches once
                // TabCloseConfirmation's single-pane path has skipped the prompt.
                manager.CloseTab(tab);
                // Both strips re-place the selection on the dispatcher, so the
                // ack waits that turn out. Read before it, the state still
                // describes the slot the fill was painted on.
                await WaitForLowPriorityAsync(window.DispatcherQueue);
                return OkWithState(window, manager, op);
            }

            case "drag":
            case "drag-paced":
            case "drag-zone":
            case "drag-header":
            case "drag-join":
            {
                var strip = window.TestSeamVerticalStrip;
                if (strip is null)
                    return Error(op, "the vertical strip is not the active host");
                var outcome = op switch
                {
                    // The paced walk films: fine steps on a wall clock so a
                    // capture harness has frames between the moves.
                    "drag-paced" => await strip.TestSeamDragPacedAsync(
                        ArgInt(args, "from", -1), ArgInt(args, "to", -1),
                        ArgInt(args, "tickMs", 45)),
                    // Both halves of the release-classified pin contract.
                    "drag-zone" => await strip.TestSeamDragZoneAsync(
                        ArgInt(args, "from", -1), ArgString(args, "release") == "in"),
                    // The drop on a group header; the product's own drop
                    // grammar decides what the landing means.
                    "drag-header" => await strip.TestSeamDragToHeaderAsync(
                        ArgInt(args, "from", -1), ArgString(args, "group") ?? ""),
                    // Both outcomes of the hold-with-a-ring join: hold
                    // fills the ring and the release groups the pair,
                    // hold=false is the quick release that still sorts.
                    // The dwell's clock is the seam's for the length of
                    // the gesture, so neither outcome is a race.
                    "drag-join" => await strip.TestSeamDragJoinAsync(
                        ArgInt(args, "from", -1), ArgInt(args, "to", -1),
                        ArgBool(args, "hold", true)),
                    _ => await strip.TestSeamDragAsync(
                        ArgInt(args, "from", -1), ArgInt(args, "to", -1)),
                };
                return DragJson(op, outcome);
            }

            case "split":
            {
                // The chord's own dispatch, so a seam split cannot drift
                // from the real action. The new leaf becomes the active
                // one, exactly as it does for a user.
                var horizontal = ArgString(args, "orientation") == "horizontal";
                window.TestSeamRouter.Invoke(horizontal
                    ? Ghostty.Core.Input.PaneAction.SplitHorizontal
                    : Ghostty.Core.Input.PaneAction.SplitVertical);
                // Split defers its focus and the border reposition by a
                // dispatcher turn (the new leaf has no measured size yet),
                // so the ack owes the driver that turn.
                await WaitForLowPriorityAsync(window.DispatcherQueue);
                return OkWithState(window, manager, op);
            }

            case "focus-pane":
            {
                var index = ArgInt(args, "index", -1);
                if (!window.TestSeamActivePaneHost.TestSeamFocusLeaf(index))
                    return Error(op, $"the active tab has no leaf at index {index}");
                // Focus lands through GotFocus, and the border follows on
                // the next layout pass.
                await WaitForLowPriorityAsync(window.DispatcherQueue);
                return OkWithState(window, manager, op);
            }

            case "element-rects":
            {
                // The strip's arranged geometry, so a chrome oracle asserts
                // rects instead of sampling pixels through Mica.
                var strip = window.TestSeamVerticalStrip;
                if (strip is null)
                    return Error(op, "the vertical strip is not the active host");
                return Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteBoolean("ok", true);
                    json.WriteString("op", op);
                    strip.TestSeamWriteElementRects(json);
                    WriteState(json, window, manager);
                    json.WriteEndObject();
                });
            }

            case "switcher-cells":
            {
                // The cycle popup's card, slot by slot: what each one
                // renders, which group field it sits in and which end of
                // that field it carries, whether it is the selection, and
                // the rects to sample. A UIA client sees only the tile
                // titles -- the field, the header band and the preview body
                // are bare panels with no automation peer -- so an oracle
                // for the group grammar or for "which tile is lit" has to
                // come through here. Read while the popup is up: it
                // dismisses itself on a 1.2s timer.
                //
                // ONE refusal, and it is the popup's absence. The predecessor
                // op refused a second time when the client-to-screen
                // conversion failed, because it had a single rect to report
                // and no way to report half of it. This one has a rect per
                // slot per surface, so a conversion that fails writes a null
                // into that slot and the rest of the card still arrives: a
                // harness reading a null there knows the popup was up and
                // that one rect could not be placed, which is the same
                // distinction the second refusal carried and is now carried
                // per-rect instead of for the whole reply.
                if (!window.TestSeamSwitcherOpen)
                    return Error(op, "no switcher: the cycle popup is not up");
                return Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteBoolean("ok", true);
                    json.WriteString("op", op);
                    window.TestSeamWriteSwitcherCells(json);
                    WriteState(json, window, manager);
                    json.WriteEndObject();
                });
            }

            case "send-text":
            {
                // The one op that is not "drive the UI". Bytes handed to a
                // live shell are commands run as the user, so this is a
                // different power from every other op here and carries its
                // own opt-in. A harness that drags tabs does not set it, and
                // therefore cannot be turned into a shell by whatever reaches
                // the pipe.
                if (!_inputAllowed)
                    return Error(op, $"send-text is off; set {InputEnvVar}=1 to arm it");
                var text = ArgString(args, "text");
                if (string.IsNullOrEmpty(text)) return Error(op, "send-text needs text");
                var index = ArgInt(args, "index", manager.IndexOf(manager.ActiveTab));
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                // The shell's own input path: one ghostty_surface_text on the
                // pane the tab is focused on. "\n" is spelled out by the
                // driver as "\r" -- a submitted line is the caller's decision,
                // not this op's.
                if (!tab.PaneHost.ActiveLeaf.Terminal().TestSeamSendText(text))
                    return Error(op, $"tab {index} has no live surface");
                return OkWithState(window, manager, op);
            }

            case "tab-labels":
            {
                // What the shell reported and what the strip drew, per tab.
                // The cwd side proves the OSC 7 / OSC 9;9 round trip reached
                // the app; the rendered side proves the label followed.
                var strip = window.TestSeamVerticalStrip;
                return Json(json =>
                {
                    json.WriteStartObject();
                    json.WriteBoolean("ok", true);
                    json.WriteString("op", op);
                    json.WriteStartArray("labels");
                    for (int i = 0; i < manager.Tabs.Count; i++)
                    {
                        var tab = manager.Tabs[i];
                        json.WriteStartObject();
                        json.WriteNumber("index", i);
                        json.WriteString("title", tab.EffectiveTitle);
                        if (tab.PaneHost.ActiveLeaf.LastCwd is { } cwd)
                            json.WriteString("cwd", cwd);
                        if (tab.ShellReportedTitle is { } shell)
                            json.WriteString("shellTitle", shell);
                        json.WriteString("iconKey", IconKey(tab.TabIcon.Icon));
                        if (strip?.TestSeamRenderedRow(tab) is { } row)
                        {
                            json.WriteString("rendered", row.Title);
                            json.WriteString("renderedIcon", RenderedIcon(row.Icon));
                        }
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                });
            }

            case "probe":
                // Reads only: where focus sits and how many KeyDown events
                // the window content has seen. The one op that answers
                // "did the framework deliver that key?" for a press this
                // seam did not itself make.
                return OkWithFocus(window, manager, op, window.TestSeamFocusLocation, null);

            case "focus":
            {
                // Real focus, set the way a click sets it. "frame" lands on
                // the first focusable element of the active tab host (a tab
                // row); "pane" on the active leaf's terminal.
                var target = ArgString(args, "target");
                var moved = target switch
                {
                    "frame" => window.TestSeamFocusFrame(),
                    "pane" => window.TestSeamFocusPane(),
                    _ => false,
                };
                return moved
                    ? OkWithFocus(window, manager, op, window.TestSeamFocusLocation, null)
                    : Error(op, $"focus is '{window.TestSeamFocusLocation}', not '{target}'");
            }

            case "chord":
            {
                var key = ArgInt(args, "key", -1);
                if (key is < 0 or > 0xFF)
                    return Error(op, "key must be a virtual-key code 0..255");
                var mods = Windows.System.VirtualKeyModifiers.None;
                if (ArgBool(args, "ctrl", false))
                    mods |= Windows.System.VirtualKeyModifiers.Control;
                if (ArgBool(args, "shift", false))
                    mods |= Windows.System.VirtualKeyModifiers.Shift;
                if (ArgBool(args, "alt", false))
                    mods |= Windows.System.VirtualKeyModifiers.Menu;
                if (ArgBool(args, "win", false))
                    mods |= Windows.System.VirtualKeyModifiers.Windows;

                // Read focus BEFORE dispatching: the answer must name where
                // the router made its decision, and several actions re-home
                // focus into a pane on their way (a layout switch, a new
                // tab), which would otherwise erase the very thing the
                // scenario is asserting about.
                var focus = window.TestSeamFocusLocation;

                // The window's real routing function -- focus gate, residual
                // table, libghostty match, dispatch -- one call below the
                // framework. Modifiers are passed because no key is actually
                // held; everything else is the shipped path.
                var dispatched = window.TestSeamFrameChord(key, mods);

                // A libghostty-matched chord lands as an apprt action the
                // host re-posts to this thread, so the tab op it performs
                // runs a tick later than the call above returns.
                await WaitForLowPriorityAsync(window.DispatcherQueue);

                // A layout toggle animates, so the ack waits it out and the
                // state this answers with is the settled one.
                var deadline = Environment.TickCount64 + 10_000;
                while (window.TestSeamLayoutSwitching
                       && Environment.TickCount64 < deadline)
                {
                    await Task.Delay(15);
                }
                return OkWithFocus(window, manager, op, focus, dispatched);
            }

            case "tab-color":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                var name = ArgString(args, "color") ?? "None";
                if (ParseTabColor(name) is not { } color)
                    return Error(op, $"unknown colour '{name}'");
                // The context menu's own assignment (TabContextMenuBuilder's
                // colour picker is `tab.Color = color`), so the seam drives
                // the INPC chain the product drives and cannot repaint
                // anything the menu would leave alone.
                tab.Color = color;
                return OkWithState(window, manager, op);
            }

            case "header-rect":
            {
                var index = ArgInt(args, "index", -1);
                var tab = TabAt(manager, index);
                if (tab is null) return Error(op, $"no tab at index {index}");
                var host = window.TestSeamTabHost;
                if (host is null)
                    return Error(op, "the horizontal strip is not the active host");
                var part = ArgString(args, "part") ?? "row";
                var local = host.TestSeamHeaderPartRect(tab, part);
                if (local is not { } dip)
                    return Error(op, $"no '{part}' rect for tab {index}");
                var screen = window.TestSeamToScreenPixels(dip, host);
                if (screen is not { } px)
                    return Error(op, $"could not place the '{part}' rect on screen");
                return RectJson(op, part, px, host.TestSeamTagForegroundRgb(tab));
            }

            default:
                return Error(op, $"unknown op '{op}'");
        }
    }

    // ---- responses ---------------------------------------------------

    /// <summary>
    /// The one drag response: every gesture command answers with where the
    /// row landed and the manager order it left, plus the gesture-clock
    /// timestamps when the walker recorded them (the paced drag's commit
    /// and release, for a filming driver to align frames against).
    /// </summary>
    private static string DragJson(string op, TestSeamDragOutcome outcome)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", outcome.Ok);
            json.WriteString("op", op);
            if (outcome.Error is { } error) json.WriteString("error", error);
            json.WriteNumber("landed", outcome.Landed);
            json.WriteBoolean("pinned", outcome.Pinned);
            // The join gesture's two answers: what the ring had reached
            // by the release, and the group the release actually landed
            // the row in -- read off the manager, so a driver asserts the
            // commit rather than the promise.
            if (outcome.Armed is { } armed) json.WriteBoolean("armed", armed);
            if (outcome.Group is { } group) json.WriteString("group", group);
            if (outcome.CommitMs >= 0) json.WriteNumber("commitMs", outcome.CommitMs);
            if (outcome.ReleaseMs >= 0) json.WriteNumber("releaseMs", outcome.ReleaseMs);
            json.WriteStartArray("order");
            foreach (var title in outcome.Order)
                json.WriteStringValue(title);
            json.WriteEndArray();
            json.WriteEndObject();
        });

    /// <summary>
    /// The focus/chord response: where the router read focus, whether the
    /// chord was dispatched (absent when the op only moved focus), and the
    /// state it left behind.
    /// </summary>
    private static string OkWithFocus(
        MainWindow window, TabManager manager, string op, string focus, bool? dispatched)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("op", op);
            json.WriteString("focus", focus);
            json.WriteNumber("routedKeyDowns", window.TestSeamRoutedKeyDowns);
            if (dispatched is { } settled) json.WriteBoolean("dispatched", settled);
            WriteState(json, window, manager);
            json.WriteEndObject();
        });

    /// <summary>
    /// Where one header part sits, in the physical screen pixels a capture
    /// is taken in, plus the foreground the ink pass assigned that tab --
    /// the claim, next to the coordinates that let a harness check it was
    /// honoured. No tag, no "fg": an absent expectation is not #000000.
    /// </summary>
    private static string RectJson(
        string op, string part, (int X, int Y, int W, int H) rect, uint? fg)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("op", op);
            json.WriteString("part", part);
            json.WriteNumber("x", rect.X);
            json.WriteNumber("y", rect.Y);
            json.WriteNumber("w", rect.W);
            json.WriteNumber("h", rect.H);
            if (fg is { } rgb) json.WriteString("fg", $"#{rgb:X6}");
            json.WriteEndObject();
        });

    /// <summary>
    /// One frame of the layout-switch filmstrip: the manager state every
    /// op reports, plus the rendered inventory of BOTH hosts and what the
    /// morph layer is carrying.
    ///
    /// Both hosts always, never just the live one: a switch is exactly the
    /// stretch where both are on screen at once, and the defects worth
    /// catching (a collapsed run rendering its members, a row flying
    /// outside its strip, a selected tab that is briefly nowhere) live in
    /// that overlap.
    /// </summary>
    private static string LayoutFrameJson(MainWindow window, TabManager manager)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("op", "layout-frame");
            // The app's own clock, so frames order by something the driver
            // did not have to guess from its own send time.
            json.WriteNumber("appMs", Environment.TickCount64);
            WriteState(json, window, manager);

            json.WriteStartObject("render");
            json.WriteNumber("morphLayer", window.TestSeamMorphLayerCount);
            var root = window.TestSeamRoot;
            var (horizontal, vertical) = window.TestSeamHosts;
            WriteHost(json, "horizontal", root, horizontal);
            WriteHost(json, "vertical", root, vertical);

            WriteChrome(json, "captionFill", root, window.TestSeamCaptionFill);

            // The join. Measured in the same space as the rows above, which is
            // the whole point: the active row's span and its cover's span are
            // two numbers that must be one, and a driver comparing them has to
            // get both without converting between coordinate systems of its
            // own invention.
            var (hSeam, vSeam) = window.TestSeamCovers;
            WriteChrome(json, "seamHorizontal", root, hSeam);
            WriteChrome(json, "seamVertical", root, vSeam);
            json.WriteEndObject();

            json.WriteEndObject();
        });

    /// <summary>
    /// One chrome rectangle, measured exactly the way a strip row is, so
    /// what a film shows can be lined up against where the chrome
    /// actually was without either being described in its own private
    /// units.
    /// </summary>
    private static void WriteChrome(
        Utf8JsonWriter json, string name,
        Microsoft.UI.Xaml.FrameworkElement? root, Microsoft.UI.Xaml.FrameworkElement element)
    {
        var measured = root is null
            ? default
            : Ghostty.Testing.TestSeamStripRowMeasure.Row(
                root, element, "chrome", name, null, false);
        json.WriteStartObject(name);
        json.WriteBoolean("shown", measured.Shown);
        json.WriteNumber("alpha", Math.Round(measured.Alpha, 4));
        json.WriteNumber("x", Math.Round(measured.Bounds.X, 1));
        json.WriteNumber("y", Math.Round(measured.Bounds.Y, 1));
        json.WriteNumber("w", Math.Round(measured.Bounds.Width, 1));
        json.WriteNumber("h", Math.Round(measured.Bounds.Height, 1));
        json.WriteEndObject();
    }

    private static void WriteHost(
        Utf8JsonWriter json, string name,
        Microsoft.UI.Xaml.FrameworkElement? root, Ghostty.Tabs.ITabHost host)
    {
        json.WriteStartObject(name);
        var element = host.HostElement;
        json.WriteBoolean(
            "visible",
            element.Visibility == Microsoft.UI.Xaml.Visibility.Visible);
        json.WriteNumber("opacity", element.Opacity);
        // The host's own rect, so "is this row inside its strip?" is
        // answerable from the frame alone rather than from a lane width
        // the driver would have to reconstruct.
        var lane = root is null
            ? default
            : Ghostty.Testing.TestSeamStripRowMeasure.Row(
                root, element, "host", name, null, false);
        json.WriteNumber("hx", Math.Round(lane.Bounds.X, 1));
        json.WriteNumber("hy", Math.Round(lane.Bounds.Y, 1));
        json.WriteNumber("hw", Math.Round(lane.Bounds.Width, 1));
        json.WriteNumber("hh", Math.Round(lane.Bounds.Height, 1));
        json.WriteStartArray("rows");
        // No root means no window content, which only happens mid-teardown.
        // An empty inventory is the honest answer, not a throw.
        if (root is not null)
        {
            foreach (var row in host.TestSeamRows(root))
            {
                json.WriteStartObject();
                json.WriteString("kind", row.Kind);
                json.WriteString("label", row.Label);
                if (row.Group is { } group) json.WriteString("group", group);
                json.WriteBoolean("active", row.Active);
                json.WriteBoolean("shown", row.Shown);
                json.WriteNumber("alpha", Math.Round(row.Alpha, 4));
                json.WriteNumber("x", Math.Round(row.Bounds.X, 1));
                json.WriteNumber("y", Math.Round(row.Bounds.Y, 1));
                json.WriteNumber("w", Math.Round(row.Bounds.Width, 1));
                json.WriteNumber("h", Math.Round(row.Bounds.Height, 1));
                json.WriteEndObject();
            }
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static string OkWithState(MainWindow window, TabManager manager, string op)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", true);
            json.WriteString("op", op);
            WriteState(json, window, manager);
            json.WriteEndObject();
        });

    /// <summary>The one response builder: AOT-safe, reflection-free.</summary>
    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using var json = new Utf8JsonWriter(stream);
        write(json);
        // The writer buffers internally; nothing reaches the stream until
        // this flush. The dispose-time flush is too late for a read.
        json.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Error(string op, string message)
        => Json(json =>
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", false);
            json.WriteString("op", op);
            json.WriteString("error", message);
            json.WriteEndObject();
        });

    /// <summary>
    /// The assert surface: manager truth -- order, pin flags, groups and
    /// their collapse bits -- plus the two layout bits the driver needs to
    /// know where the window stands.
    /// </summary>
    private static void WriteState(
        Utf8JsonWriter json, MainWindow window, TabManager manager)
    {
        json.WriteStartObject("state");
        json.WriteBoolean("vertical", window.TestSeamVerticalTabs);
        json.WriteBoolean("switching", window.TestSeamLayoutSwitching);
        json.WriteNumber("active", manager.IndexOf(manager.ActiveTab));
        json.WriteNumber("paneWidth",
            window.TestSeamVerticalStrip?.TestSeamPaneWidth ?? 0);
        json.WriteStartArray("tabs");
        for (int i = 0; i < manager.Tabs.Count; i++)
        {
            var tab = manager.Tabs[i];
            json.WriteStartObject();
            json.WriteNumber("index", i);
            json.WriteString("title", tab.EffectiveTitle);
            json.WriteBoolean("pinned", tab.IsPinned);
            // Per-tab pane memory: the leaf each tab would come back to.
            if (tab.PaneHost is Ghostty.Panes.PaneHost host)
            {
                json.WriteNumber("leaves", host.TestSeamLeafCount);
                json.WriteNumber("activeLeaf", host.TestSeamActiveLeafIndex);
            }
            if (tab.Color != TabColor.None)
                json.WriteString("color", TabColorPalette.LocalizedName(tab.Color));
            if (tab.Group is { } group)
            {
                json.WriteString("group", group.Title);
                json.WriteBoolean("collapsedGroup", group.IsCollapsed);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("groups");
        foreach (var group in manager.Groups)
        {
            json.WriteStartObject();
            json.WriteString("title", group.Title);
            json.WriteBoolean("collapsed", group.IsCollapsed);
            json.WriteStartArray("members");
            foreach (var member in manager.MembersOf(group))
                json.WriteStringValue(member.EffectiveTitle);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        WritePanes(json, window);
        json.WriteEndObject();
    }

    /// <summary>
    /// The active tab's pane geometry: where the leaves and the active-pane
    /// stroke are RENDERED, in window-root DIPs, plus the scale and the
    /// stroke's colour a capture harness needs to find it in a screenshot.
    /// The rects come off the drawing path, so "the border moved" is an
    /// assertable claim rather than a restatement of the active-leaf field.
    /// </summary>
    private static void WritePanes(Utf8JsonWriter json, MainWindow window)
    {
        var host = window.TestSeamActivePaneHost;
        var focused = window.TestSeamFocusedElement;
        json.WriteStartObject("panes");
        json.WriteNumber("activeLeaf", host.TestSeamActiveLeafIndex);
        // The remembered leaf and the leaf you can type into are two
        // different facts; the seam reports both so a driver can catch the
        // window remembering one and focusing neither.
        json.WriteNumber("focusedLeaf", host.TestSeamFocusedLeafIndex(focused));
        json.WriteString("focusedElement", focused?.GetType().Name ?? "");
        json.WriteNumber("scale", window.TestSeamRasterizationScale);
        json.WriteNumber("borderArgb", host.TestSeamActiveBorderArgb);
        WriteRect(json, "border", host.TestSeamActiveBorderRect);
        json.WriteStartArray("leaves");
        foreach (var rect in host.TestSeamLeafRects)
        {
            json.WriteStartObject();
            WriteRectBody(json, rect);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private static void WriteRect(
        Utf8JsonWriter json, string name, Windows.Foundation.Rect rect)
    {
        json.WriteStartObject(name);
        WriteRectBody(json, rect);
        json.WriteEndObject();
    }

    private static void WriteRectBody(Utf8JsonWriter json, Windows.Foundation.Rect rect)
    {
        json.WriteNumber("x", rect.X);
        json.WriteNumber("y", rect.Y);
        json.WriteNumber("w", rect.Width);
        json.WriteNumber("h", rect.Height);
    }

    // ---- argument readers --------------------------------------------

    private static string? ArgString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ArgInt(JsonElement args, string name, int fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static bool ArgBool(JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static List<string> ArgStrings(JsonElement args, string name)
    {
        var result = new List<string>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    result.Add(item.GetString()!);
        }
        return result;
    }

    private static List<int> ArgInts(JsonElement args, string name)
    {
        var result = new List<int>();
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number)
                    result.Add(item.GetInt32());
        }
        return result;
    }

    /// <summary>
    /// The palette's own name table rather than Enum.TryParse: the seam
    /// speaks the vocabulary the colour menu shows a user, and it stays
    /// reflection-free for the AOT build.
    /// </summary>
    private static TabColor? ParseTabColor(string name)
    {
        foreach (var row in TabColorPalette.PaletteRows)
            foreach (var candidate in row)
                if (string.Equals(TabColorPalette.LocalizedName(candidate), name,
                        StringComparison.OrdinalIgnoreCase))
                    return candidate;
        return null;
    }

    private static TabModel? TabAt(TabManager manager, int index)
        => index >= 0 && index < manager.Tabs.Count ? manager.Tabs[index] : null;

    /// <summary>
    /// The spec a tab's icon VM currently names, flattened to one token so a
    /// driver can assert two shells differ without knowing the record shapes.
    /// </summary>
    private static string IconKey(Ghostty.Core.Profiles.IconSpec spec) => spec switch
    {
        Ghostty.Core.Profiles.IconSpec.BundledKey b => "bundled:" + b.Key,
        Ghostty.Core.Profiles.IconSpec.BrandKey b => "brand:" + b.Key,
        Ghostty.Core.Profiles.IconSpec.Mdl2Token m => "mdl2:" + m.CodePoint,
        Ghostty.Core.Profiles.IconSpec.Path p => "path:" + p.FilePath,
        Ghostty.Core.Profiles.IconSpec.AutoForExe e => "exe:" + e.ExePath,
        Ghostty.Core.Profiles.IconSpec.AutoForWslDistro w => "wsl:" + w.DistroName,
        _ => "unknown",
    };

    /// <summary>
    /// What the nav item is actually wearing. "image" only when the element
    /// carries a decoded source: an ImageIcon whose bytes never resolved
    /// renders empty, and a driver asserting "the icon is present" has to be
    /// able to tell those apart.
    /// </summary>
    private static string RenderedIcon(Microsoft.UI.Xaml.Controls.IconElement? icon) => icon switch
    {
        Microsoft.UI.Xaml.Controls.ImageIcon { Source: not null } => "image",
        Microsoft.UI.Xaml.Controls.ImageIcon => "image-empty",
        Microsoft.UI.Xaml.Controls.FontIcon f => "glyph:" + f.Glyph,
        null => "none",
        _ => icon.GetType().Name,
    };
#endif

    // ---- outside the build gate ---------------------------------------
    //
    // The strip's own drag walkers call this, and they are ordinary internal
    // methods on VerticalTabStrip rather than seam code. Guarding it with the
    // rest would take the strip down with it in a build that has no seam, so
    // this one handoff stays. It opens nothing: a dispatcher hop no caller
    // outside this process can reach.

    /// <summary>
    /// A handoff one priority below the strip's Normal-priority drag tick:
    /// when the awaited task completes, everything the last synthetic move
    /// scheduled -- crossings included -- has already run. This is what
    /// makes a seam drag deterministic without sleeps.
    /// </summary>
    internal static Task WaitForLowPriorityAsync(DispatcherQueue queue)
    {
        var done = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(DispatcherQueuePriority.Low, () => done.SetResult()))
            done.SetException(new InvalidOperationException("dispatcher unavailable"));
        return done.Task;
    }
}

/// <summary>
/// What one seam drag reports back: whether the engine landed the row where
/// the driver aimed, and the manager order it left behind.
/// </summary>
internal sealed class TestSeamDragOutcome
{
    public bool Ok = true;
    public string? Error;
    public int Landed = -1;
    public bool Pinned;
    // The join gesture's own two fields, absent from the response for
    // every other drag op: nullable rather than defaulted, so a driver
    // cannot read "not armed, no group" off a gesture that was never
    // asked the question.
    public bool? Armed;
    public string? Group;
    public long CommitMs = -1;
    public long ReleaseMs = -1;
    public List<string> Order = new();

    public TestSeamDragOutcome Fail(string reason)
    {
        Ok = false;
        Error = reason;
        return this;
    }
}
