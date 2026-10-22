using System.Runtime.InteropServices;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// What this process currently holds at the SCM: nothing, a single-use
/// registration serving one activation, or the process-lifetime
/// multi-use registration of the elected primary. The +defterm-status
/// probe reports this, never the intended state.
/// </summary>
public enum RegistrationKind
{
    None,
    Transient,
    Permanent,
}

/// <summary>
/// How a receiver classified itself after serving its activation.
/// </summary>
public enum ReceiverClassification
{
    /// <summary>
    /// Elevated receiver: served under the consumed transient, never
    /// primary, exits when its window closes.
    /// </summary>
    ElevatedSession,

    /// <summary>
    /// Medium receiver with no other primary: became primary and
    /// upgraded to the permanent registration.
    /// </summary>
    UpgradedToPrimary,

    /// <summary>
    /// Medium receiver with an existing primary: stays secondary, hosts
    /// in its own window under the normal last-window rule.
    /// </summary>
    Secondary,
}

/// <summary>
/// One handed-off session travelling from the COM call into placement:
/// the raw call arguments plus the completion that returns the HRESULT
/// to the waiting host. The queue drains these to the UI thread; an
/// item whose budget expires completes with the failure HRESULT and
/// that launch degrades to conhost.
/// </summary>
public sealed record HandoffPlacementRequest(
    HandoffArgs Args,
    AdoptedSessionHandles? Handles,
    Func<HandoffPlacementResult, int> Complete);

/// <summary>
/// The class-object lifecycle: register, receive, classify, revoke. All
/// machine effects go through overridable steps so a test drives the
/// whole state machine against recorders. The production embedding
/// registrations run on the dedicated MTA thread; the election-time
/// permanent registration runs on the main thread (the objects are
/// agile CCWs, so activations are not apartment-bound either way).
/// </summary>
public static partial class DefaultTermComServer
{
    /// <summary>
    /// The exit code of an embedding launch that received no activation:
    /// a distinct entry in the process taxonomy, not success and not any
    /// crash, usage, init, guard or unhandled code.
    /// </summary>
    public const int IdleEmbeddingExitCode = 5;

    private const uint ClsctxLocalServer = 4;
    private const uint RegclsSingleUse = 0;
    private const uint RegclsMultipleUse = 1;

    private static readonly object _gate = new();
    private static readonly List<uint> _cookies = new();

    /// <summary>
    /// Fires when the class object receives a handoff. Placement (Task
    /// 9's queue) subscribes; the event fires on the COM thread.
    /// </summary>
    public static event Action<HandoffPlacementRequest>? HandoffReceived;

    /// <summary>The registration this process holds right now.</summary>
    public static RegistrationKind Kind { get; private set; }

    // Test seams. Null means the production step runs; a test sets a
    // recorder to drive the state machine without touching COM.
    internal static Func<Guid, RegistrationKind, uint>? RegisterOverride;

    /// <summary>
    /// Diagnostics sink for non-fatal COM failures; the app wires it to
    /// its startup diagnostics.
    /// </summary>
    internal static Action<string>? Diagnostic;
    internal static Action<uint>? RevokeOverride;
    internal static Action? DrainOverride;
    internal static Action? ReleaseOverride;

    /// <summary>
    /// The permanent registration, attempted once after the election.
    /// Any failed condition leaves Kind at None and nothing registered.
    /// </summary>
    public static void TryRegisterPermanent(BootstrapContext ctx)
    {
        if (!DefaultTermBootstrap.ShouldRegister(ctx))
        {
            return;
        }

        if (Register(Parse(DefaultTermIdentity.TerminalClsid), RegistrationKind.Permanent) == 0)
        {
            return;
        }

        lock (_gate)
        {
            Kind = RegistrationKind.Permanent;
        }
    }

    /// <summary>
    /// The single-use registration of an SCM-launched instance, serving
    /// exactly its own activation.
    /// </summary>
    public static uint RegisterTransient(Guid clsid)
    {
        var cookie = Register(clsid, RegistrationKind.Transient);
        if (cookie == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            Kind = RegistrationKind.Transient;
        }

        return cookie;
    }

    /// <summary>
    /// Classify after serving the activation this instance was launched
    /// for. Elevated or low-integrity receivers never hold a class
    /// object afterwards; a medium receiver upgrades to the permanent
    /// registration only when EVERY registration condition holds — the
    /// same predicate as the election-time registration, re-run with the
    /// actual election outcome — so a disabled install, a nil-identity
    /// build or an unbound exe never exposes a class object it should
    /// not have. The upgrade revokes the consumed transient first,
    /// explicit against CO_E_OBJISREG.
    /// </summary>
    public static ReceiverClassification ClassifyReceiver(
        BootstrapContext ctx,
        bool otherPrimaryExists)
    {
        var elevated = ctx.IntegrityLevel() != TokenIntegrity.Medium;
        if (elevated)
        {
            RevokeAll();
            return ReceiverClassification.ElevatedSession;
        }

        if (otherPrimaryExists || !DefaultTermBootstrap.ShouldRegister(ctx))
        {
            RevokeAll();
            return ReceiverClassification.Secondary;
        }

        RevokeAll();
        if (Register(Parse(DefaultTermIdentity.TerminalClsid), RegistrationKind.Permanent) == 0)
        {
            return ReceiverClassification.UpgradedToPrimary;
        }

        lock (_gate)
        {
            Kind = RegistrationKind.Permanent;
        }

        return ReceiverClassification.UpgradedToPrimary;
    }

    /// <summary>
    /// Revoke every cookie this process holds, newest first, then clear
    /// the kind. Tolerates revoking an already-revoked cookie.
    /// </summary>
    public static void RevokeAll()
    {
        lock (_gate)
        {
            for (var i = _cookies.Count - 1; i >= 0; i--)
            {
                var cookie = _cookies[i];
                if (RevokeOverride is not null)
                {
                    RevokeOverride(cookie);
                }
                else
                {
                    _ = CoRevokeClassObject(cookie);
                }
            }

            _cookies.Clear();
            Kind = RegistrationKind.None;
        }
    }

    /// <summary>
    /// Process shutdown, in the only safe order: revoke first so no new
    /// activation can arrive, then drain the handoffs already in flight
    /// within their budgets, then release COM.
    /// </summary>
    public static void Shutdown()
    {
        _embeddingShutdown.Set();
        RevokeAll();
        (DrainOverride ?? DrainInFlight)();
        (ReleaseOverride ?? ReleaseCom)();
    }

    /// <summary>
    /// The process-wide placement queue. The COM call path parks
    /// handoffs here; the app attaches its surface host once a window
    /// can adopt, and the queue drains to it.
    /// </summary>
    public static DefaultTermPlacementQueue PlacementQueue { get; } = new(16);

    private static Thread? _embeddingThread;
    private static readonly ManualResetEventSlim _embeddingShutdown = new(false);
    private static readonly ManualResetEventSlim _activationArrived = new(false);

    /// <summary>
    /// The app side answers whether any live window exists: the
    /// embedding lifetime rules below need it.
    /// </summary>
    public static Func<bool>? AnyWindowExists;

    private static readonly TimeSpan ActivationWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WindowGrace = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Start the dedicated MTA thread hosting the embedding launch's
    /// class object: CoInitialize MTA, register the single-use class
    /// object when the transient conditions hold, hold it until
    /// <see cref="Shutdown"/>, then revoke and uninitialize. Safe to
    /// call once per process; a second call is a no-op.
    /// </summary>
    public static void StartEmbeddingServer(BootstrapContext ctx)
    {
        lock (_gate)
        {
            if (_embeddingThread is not null)
            {
                return;
            }

            _embeddingThread = new Thread(() => EmbeddingLoop(ctx))
            {
                IsBackground = true,
                Name = "DefaultTerm embedding server",
            };
            _embeddingThread.SetApartmentState(ApartmentState.MTA);
            _embeddingThread.Start();
        }
    }

    private static void EmbeddingLoop(BootstrapContext ctx)
    {
        const uint CoinitMultithreaded = 0x0;
        _ = CoInitializeEx(IntPtr.Zero, CoinitMultithreaded);
        try
        {
            if (DefaultTermBootstrap.ShouldRegisterTransient(ctx))
            {
                RegisterTransient(Parse(DefaultTermIdentity.TerminalClsid));
            }

            // A windowless receiver must not outlive its purpose: the
            // SCM launched this process BECAUSE an activation was
            // pending, so it arrives within milliseconds when it comes
            // at all. None within the wait: idle embedding, exit with
            // the taxonomy code. One arrived: give a window a grace
            // period to appear (an adopted session opens one); if the
            // launch degraded and nothing parked remains, exit cleanly
            // instead of holding a registered class object forever.
            if (_activationArrived.Wait(ActivationWait))
            {
                WaitForWindowOrSettle();
            }
            // The oracle must affirmatively report "no window": a null
            // or unwired oracle fails toward staying alive, never toward
            // killing a possibly-windowed process.
            else if (HandoffCount == 0
                     && AnyWindowExists?.Invoke() == false
                     && !_embeddingShutdown.IsSet)
            {
                Environment.Exit(IdleEmbeddingExitCode);
            }

            _embeddingShutdown.Wait();
            RevokeAll();
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static void WaitForWindowOrSettle()
    {
        var deadline = Environment.TickCount64 + WindowGrace.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            // Shutdown in progress: never exit from here, the main path
            // owns the process end from this point.
            if (_embeddingShutdown.IsSet)
            {
                return;
            }

            if (AnyWindowExists?.Invoke() == true)
            {
                return;
            }

            if (PlacementQueue.Pending == 0)
            {
                // The activation was served (or degraded) and nothing is
                // parked: with no window, this receiver has nothing left
                // to do.
                Environment.Exit(0);
            }

            Thread.Sleep(500);
        }
    }

    /// <summary>
    /// An embedding launch that received no activation: revoke and hand
    /// back the idle taxonomy code.
    /// </summary>
    public static int ExitIdleEmbedding()
    {
        RevokeAll();
        return IdleEmbeddingExitCode;
    }

    /// <summary>
    /// Raise <see cref="HandoffReceived"/>; the COM call path uses this
    /// so the event has exactly one raise site.
    /// </summary>
    /// <summary>
    /// How many handoffs this process has received. An embedding launch
    /// with zero exits with the idle taxonomy code.
    /// </summary>
    public static int HandoffCount { get; private set; }

    internal static void RaiseHandoffReceived(HandoffPlacementRequest request)
    {
        HandoffCount++;
        _activationArrived.Set();
        HandoffReceived?.Invoke(request);
    }

    internal static void Reset()
    {
        lock (_gate)
        {
            _cookies.Clear();
            Kind = RegistrationKind.None;
            _embeddingThread = null;
        }

        HandoffCount = 0;
        _embeddingShutdown.Reset();

        RegisterOverride = null;
        Diagnostic = null;
        RevokeOverride = null;
        DrainOverride = null;
        ReleaseOverride = null;
        HandoffReceived = null;
    }

    private static uint Register(Guid clsid, RegistrationKind kind)
    {
        try
        {
            return RegisterStep(clsid, kind);
        }
        catch (Exception e)
        {
            // Non-fatal by design: whatever failed, nothing is
            // registered, the app keeps starting, and the host degrades
            // launches safely.
            Diagnostic?.Invoke($"class-object registration failed for {clsid}: {e.Message}");
            return 0;
        }
    }

    private static uint RegisterStep(Guid clsid, RegistrationKind kind)
    {
        if (RegisterOverride is not null)
        {
            var testCookie = RegisterOverride(clsid, kind);
            lock (_gate)
            {
                _cookies.Add(testCookie);
            }

            return testCookie;
        }

        // The creator is self-referencing on purpose: products created
        // through it carry the same creator, so the factory-fetch
        // pattern continued on a PRODUCT also yields serviced objects.
        Func<TerminalHandoffObject>? creator = null;
        creator = () => new TerminalHandoffObject(CreateHandoffService(), creator);
        var factory = new HandoffClassFactory(creator);
        var unknown = Marshal.GetIUnknownForObject(factory);
        try
        {
            var hr = CoRegisterClassObject(
                clsid,
                unknown,
                ClsctxLocalServer,
                kind == RegistrationKind.Permanent ? RegclsMultipleUse : RegclsSingleUse,
                out var cookie);
            if (hr != 0)
            {
                // Non-fatal by design: an enabled install that fails one
                // COM call still starts; nothing is registered, the
                // probe reports the actual state, and the host degrades
                // launches to conhost safely.
                Diagnostic?.Invoke(
                    $"CoRegisterClassObject failed for {clsid}: 0x{hr:X8}");
                return 0;
            }

            lock (_gate)
            {
                _cookies.Add(cookie);
            }

            return cookie;
        }
        finally
        {
            _ = Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// One service per activated product object: the adoption budget of
    /// the design, the session cap fed by the queue's parking count,
    /// and the placement scheduler routed through the queue. The
    /// handoff-received event fires for observability right before the
    /// session parks.
    /// </summary>
    private static TerminalHandoffService CreateHandoffService()
    {
        var service = new TerminalHandoffService(
            TimeSpan.FromSeconds(5),
            64,
            () => PlacementQueue.Pending);

        service.Scheduler = (work, budget) =>
        {
            var request = new HandoffPlacementRequest(
                service.CurrentArgs ?? new HandoffArgs(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero),
                service.LastHandles,
                r => r == HandoffPlacementResult.Adopted ? 0 : unchecked((int)0x80004005));
            RaiseHandoffReceived(request);
            return PlacementQueue.ParkAndWait(request, budget);
        };

        return service;
    }

    private static void DrainInFlight()
    {
        // Settle what parked: expired items complete with their failure
        // result, parked-and-adoptable ones drain to the host if there
        // is one. After this the queue is empty.
        PlacementQueue.Drain();
    }

    private static void ReleaseCom()
    {
        // CoUninitialize pairs with the MTA thread's CoInitialize in
        // StartEmbeddingServer; the thread teardown performs it.
    }

    private static Guid Parse(string clsid) => Guid.Parse(clsid.Trim('{', '}'));

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(
        in Guid rclsid,
        IntPtr pUnk,
        uint dwClsContext,
        uint dwFlags,
        out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint dwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
