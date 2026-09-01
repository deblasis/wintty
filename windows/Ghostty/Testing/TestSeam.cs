using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Tabs;
using Microsoft.UI.Dispatching;

namespace Ghostty.Testing;

/// <summary>
/// The opt-in in-process test seam: one named pipe inside Wintty whose
/// newline-delimited JSON commands drive the REAL input handlers -- the tab
/// manager ops and the vertical strip's drag engine -- on the UI thread.
/// No OS input is synthesized, nothing is focused, and the user can be using
/// the machine while a script drives the app.
///
/// The gate is the whole surface: without WINTTY_TEST_SEAM=1 in the app's
/// environment this class never creates a pipe and costs one env-var read
/// per window. With it, the pipe is session-local and serves one client at
/// a time; a second command connection waits for the first to hang up. A
/// second opted-in process does not spin: the fixed name belongs to
/// whoever took it first, and the loser goes quiet (one seam per machine).
///
/// Trust model, spike-honest: the pipe carries no ACL of its own, so any
/// process running as the same local user can connect to an opted-in
/// instance and drive its UI -- the same-user bar every dev tool on the
/// box already clears. A user-scoped ACL (or a per-start nonce in the
/// pipe name) is the hardening step for when the seam ships beyond this
/// opt-in.
///
/// Protocol: each request is one line of JSON, {"op": "...", ...args}; each
/// response is one line, {"ok": true, ...} or {"ok": false, "error": "..."}.
/// Commands marshal to the UI thread and every ack answers after the work
/// settled, so a driver never races the app.
///
/// One window is served (the first): the spike scenario is a single window.
/// Multi-window routing is hardening, not architecture.
/// </summary>
internal static class TestSeam
{
    private const string EnvVar = "WINTTY_TEST_SEAM";

    /// <summary>The pipe the driver connects to, when the seam is on.</summary>
    internal const string PipeName = "wintty-test-seam";

    private static CancellationTokenSource? _lifetime;
    private static int _started;

    /// <summary>
    /// Called once per window from the MainWindow constructor. The first
    /// window in a seam-enabled process wins; later windows are no-ops. The
    /// server dies with that window (the app closes with it).
    /// </summary>
    internal static void Start(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1") return;
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        window.Closed += (_, _) =>
        {
            try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        };
        _ = Task.Run(() => ServeAsync(window, token));
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
                pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The name belongs to another opted-in instance. One seam
                // per machine is the honest semantics, so this process goes
                // quiet instead of hot-spinning on a name it can never take.
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
        var reader = new StreamReader(pipe, Encoding.UTF8);
        var writer = new StreamWriter(pipe, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n",
        };
        while (!token.IsCancellationRequested && pipe.IsConnected)
        {
            var line = await reader.ReadLineAsync(token);
            if (line is null) return; // client hung up
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = await ExecuteAsync(window, line);
            await writer.WriteLineAsync(response);
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
                    window, () => ExecuteOnUiThreadAsync(window, opName, root));
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
    /// The one marshal: every command, whatever it touches, runs on the
    /// window's UI thread and the response awaits the work.
    /// </summary>
    private static Task<string> RunOnUiThreadAsync(
        MainWindow window, Func<Task<string>> action)
    {
        var done = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var result = await action();
                    // Settled means settled: layout, not just the manager.
                    window.TestSeamSettleLayout();
                    done.SetResult(result);
                }
                catch (Exception ex) { done.SetResult(Error("ui", ex.Message)); }
            }))
        {
            done.SetResult(Error("ui", "dispatcher unavailable"));
        }
        return done.Task;
    }

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

            case "toggle-layout":
            {
                // The keyboard path's own dispatch: the router event the
                // chord raises, so the seam cannot drift from the real
                // action.
                window.TestSeamRouter.RequestToggleTabLayout();

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

            case "drag":
            case "drag-paced":
            case "drag-zone":
            case "drag-header":
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

    private static TabModel? TabAt(TabManager manager, int index)
        => index >= 0 && index < manager.Tabs.Count ? manager.Tabs[index] : null;
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
