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
/// whole state machine against recorders; the production steps call
/// CoRegisterClassObject on the dedicated MTA thread and nothing else.
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

        Register(Parse(DefaultTermIdentity.TerminalClsid), RegistrationKind.Permanent);
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
        lock (_gate)
        {
            Kind = RegistrationKind.Transient;
        }

        return cookie;
    }

    /// <summary>
    /// Classify after serving the activation this instance was launched
    /// for. Elevated receivers never hold a class object afterwards;
    /// medium receivers either upgrade to the permanent registration
    /// (revoking the consumed transient first, explicit against
    /// CO_E_OBJISREG) or stay secondary.
    /// </summary>
    public static ReceiverClassification ClassifyReceiver(
        bool elevated,
        bool otherPrimaryExists)
    {
        if (elevated)
        {
            RevokeAll();
            return ReceiverClassification.ElevatedSession;
        }

        if (otherPrimaryExists)
        {
            RevokeAll();
            return ReceiverClassification.Secondary;
        }

        RevokeAll();
        Register(Parse(DefaultTermIdentity.TerminalClsid), RegistrationKind.Permanent);
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

            _embeddingShutdown.Wait();
            RevokeAll();
        }
        finally
        {
            CoUninitialize();
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
    internal static void RaiseHandoffReceived(HandoffPlacementRequest request) =>
        HandoffReceived?.Invoke(request);

    internal static void Reset()
    {
        lock (_gate)
        {
            _cookies.Clear();
            Kind = RegistrationKind.None;
            _embeddingThread = null;
        }

        _embeddingShutdown.Reset();

        RegisterOverride = null;
        RevokeOverride = null;
        DrainOverride = null;
        ReleaseOverride = null;
        HandoffReceived = null;
    }

    private static uint Register(Guid clsid, RegistrationKind kind)
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
                throw new InvalidOperationException(
                    $"CoRegisterClassObject failed for {clsid}: 0x{hr:X8}");
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
