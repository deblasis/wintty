using System.Runtime.InteropServices;

namespace Ghostty.Core.DefaultTerm;

/// <summary>Where an injected fault lands, for the failure-lattice test.</summary>
public enum FaultPoint
{
    None,
    SecondPipe,
    ThirdDuplicate,
    Title,
}

/// <summary>
/// The [in] parameters of one EstablishPtyHandoff call: the signal,
/// reference, server and client handles, and the startup-info block
/// pointer which may be NULL. The two pipe handles are [out]s the
/// service fills via <see cref="TerminalHandoffService.LastConsoleInRead"/>
/// and <see cref="TerminalHandoffService.LastConsoleOutWrite"/>.
/// </summary>
public sealed record HandoffArgs(
    IntPtr Signal,
    IntPtr Reference,
    IntPtr Server,
    IntPtr Client,
    IntPtr StartupInfo);

/// <summary>What the adoption of one handed-off session produced.</summary>
public enum HandoffPlacementResult
{
    /// <summary>A live surface adopted the session.</summary>
    Adopted,

    /// <summary>No surface adopted within the budget.</summary>
    Timeout,
}

/// <summary>
/// The handles an adopted session runs on, handed to the surface that
/// adopts it: the two near pipe ends (user input in, VT out) and the
/// four duplicated [in] handles, plus the peeked title. Ownership of all
/// six transfers with adoption; nothing here is closed by the service.
/// </summary>
public sealed record AdoptedSessionHandles(
    IntPtr InWrite,
    IntPtr OutRead,
    IntPtr SignalDup,
    IntPtr ReferenceDup,
    IntPtr ServerDup,
    IntPtr ClientDup,
    string? Title);

/// <summary>
/// The in-call service behind TerminalHandoffObject: validate before any
/// effect, create the two pipes with the write end overlapped-capable,
/// duplicate the [in] handles synchronously (the stub closes the
/// originals when the call returns), copy the title, adopt within a
/// bounded budget, and release exactly what was created on any failure.
/// A returned failure HRESULT makes the host degrade the launch safely,
/// which is always the safe outcome.
/// </summary>
public sealed partial class TerminalHandoffService
{
    private readonly TimeSpan _adoptionBudget;
    private readonly int _sessionCap;
    private readonly Func<int> _inFlightSessions;

    private readonly List<IntPtr> _liveHandles = new();

    public TerminalHandoffService(
        TimeSpan adoptionBudget,
        int sessionCap,
        Func<int> inFlightSessions)
    {
        _adoptionBudget = adoptionBudget;
        _sessionCap = sessionCap;
        _inFlightSessions = inFlightSessions;
    }

    /// <summary>
    /// Runs the adoption work under the caller's scheduler. Production
    /// uses the UI dispatcher with the budget; tests substitute their own.
    /// </summary>
    public Func<Func<CancellationToken, HandoffPlacementResult>, TimeSpan, HandoffPlacementResult>? Scheduler
    {
        get;
        set;
    }

    /// <summary>Injected fault point for the failure-lattice test.</summary>
    public FaultPoint Fault { get; set; }

    /// <summary>
    /// The handles of the call in flight, populated right before the
    /// adoption scheduler runs: the carrier the placement request takes
    /// to the surface.
    /// </summary>
    public AdoptedSessionHandles? LastHandles { get; private set; }

    /// <summary>
    /// The args of the call in flight on this instance. The placement
    /// scheduler runs inside HandleCall, so it is always set when the
    /// scheduler reads it; null before and between calls.
    /// </summary>
    internal HandoffArgs? CurrentArgs { get; private set; }

    /// <summary>How many pipe pairs this call created (observable per call).</summary>
    public int PipesCreated { get; private set; }

    public IntPtr LastInWriteEnd { get; private set; }

    /// <summary>
    /// The console side's input read end: the far end of the input pair
    /// that becomes the call's first [out] parameter on success.
    /// </summary>
    public IntPtr LastConsoleInRead { get; private set; }

    /// <summary>
    /// The console side's output write end: the far end of the output
    /// pair that becomes the call's second [out] parameter on success.
    /// </summary>
    public IntPtr LastConsoleOutWrite { get; private set; }

    public string? LastTitle { get; private set; }

    public string? LastIconPath { get; private set; }

    public IntPtr LastClientDuplicate { get; private set; }

    public int LiveHandleCount => _liveHandles.Count;

    public int PreexistingHandleCount { get; private set; }

    public int HandleCall(HandoffArgs args)
    {
        PreexistingHandleCount = _liveHandles.Count;
        PipesCreated = 0;
        LastInWriteEnd = IntPtr.Zero;
        LastConsoleInRead = IntPtr.Zero;
        LastConsoleOutWrite = IntPtr.Zero;
        LastHandles = null;
        LastTitle = null;
        LastIconPath = null;
        LastClientDuplicate = IntPtr.Zero;

        CurrentArgs = args;

        // Validation before any effect.
        if (args.Signal == IntPtr.Zero
            || args.Reference == IntPtr.Zero
            || args.Server == IntPtr.Zero
            || args.Client == IntPtr.Zero)
        {
            return unchecked((int)0x80070057); // E_INVALIDARG
        }

        if (GetProcessId(args.Client) == 0)
        {
            return unchecked((int)0x80070057); // E_INVALIDARG
        }

        if (_inFlightSessions() >= _sessionCap)
        {
            return unchecked((int)0x8007000E); // E_OUTOFMEMORY as the cap signal
        }

        try
        {
            // The two pipes: conhost reads user input from our input
            // pipe's far end and writes VT to our output pipe's far end.
            // Our write side must be overlapped-capable for the IOCP
            // loop, so the input pipe is a named pipe pair.
            var (consoleInRead, ourInWrite) = CreateOverlappedPipePair();
            _liveHandles.Add(consoleInRead);
            _liveHandles.Add(ourInWrite);
            PipesCreated++;

            if (Fault == FaultPoint.SecondPipe)
            {
                throw new InjectedFaultException(FaultPoint.SecondPipe);
            }

            var (ourOutRead, consoleOutWrite) = CreateOverlappedPipePair();
            _liveHandles.Add(ourOutRead);
            _liveHandles.Add(consoleOutWrite);
            PipesCreated++;
            LastInWriteEnd = ourInWrite;
            LastConsoleInRead = consoleInRead;
            LastConsoleOutWrite = consoleOutWrite;

            // Synchronous in-call duplication of the [in] handles: the
            // stub closes the originals when this call returns, so
            // anything kept past the call must be duplicated now.
            var signalDup = Duplicate(args.Signal);
            _liveHandles.Add(signalDup);
            var referenceDup = Duplicate(args.Reference);
            _liveHandles.Add(referenceDup);
            var serverDup = Duplicate(args.Server);
            _liveHandles.Add(serverDup);

            if (Fault == FaultPoint.ThirdDuplicate)
            {
                throw new InjectedFaultException(FaultPoint.ThirdDuplicate);
            }

            var clientDup = Duplicate(args.Client);
            _liveHandles.Add(clientDup);
            LastClientDuplicate = clientDup;

            // The startup info pointer is call-lifetime: peek the title
            // now, never later.
            if (args.StartupInfo != IntPtr.Zero)
            {
                if (Fault == FaultPoint.Title)
                {
                    throw new InjectedFaultException(FaultPoint.Title);
                }

                var titlePtr = Marshal.ReadIntPtr(args.StartupInfo);
                var iconPtr = Marshal.ReadIntPtr(args.StartupInfo, IntPtr.Size);
                LastTitle = titlePtr == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(titlePtr);
                // The icon path is deliberately ignored: loading a
                // caller-chosen resource path is not something this side
                // ever does.
                _ = iconPtr;
            }

            // Adoption with a bounded budget. The far ends go to the
            // console side as the [out] parameters of the COM call; the
            // near ends and the duplicated [in] handles travel to the
            // surface through LastHandles: the request's carrier.
            LastHandles = new AdoptedSessionHandles(
                InWrite: ourInWrite,
                OutRead: ourOutRead,
                SignalDup: signalDup,
                ReferenceDup: referenceDup,
                ServerDup: serverDup,
                ClientDup: clientDup,
                Title: LastTitle);
            var scheduler = Scheduler ?? DefaultAdopt;
            var placement = scheduler(
                _ => HandoffPlacementResult.Adopted,
                _adoptionBudget);
            if (placement != HandoffPlacementResult.Adopted)
            {
                ReleaseThisCall();
                return unchecked((int)0x80004005); // E_FAIL
            }

            // Adopted: ownership transfers rather than closes. The far
            // ends leave with the caller through the [out] parameters,
            // the near ends and the duplicated [in] handles with the
            // surface; nothing of this call stays in our ledger.
            _liveHandles.Remove(consoleInRead);
            _liveHandles.Remove(consoleOutWrite);
            _liveHandles.Remove(ourInWrite);
            _liveHandles.Remove(ourOutRead);
            _liveHandles.Remove(signalDup);
            _liveHandles.Remove(referenceDup);
            _liveHandles.Remove(serverDup);
            _liveHandles.Remove(clientDup);
            return 0;
        }
        catch (InjectedFaultException)
        {
            ReleaseThisCall();
            return unchecked((int)0x80004005);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            ReleaseThisCall();
            return unchecked((int)0x80004005);
        }
    }

    private static HandoffPlacementResult DefaultAdopt(
        Func<CancellationToken, HandoffPlacementResult> work,
        TimeSpan budget) =>
        work(CancellationToken.None);

    private static (IntPtr ConsoleSide, IntPtr OurSide) CreateOverlappedPipePair()
    {
        // Anonymous pipes cannot be overlapped, so the pair is a named
        // pipe opened with FILE_FLAG_OVERLAPPED both ways. The pipe name
        // is unguessable enough for the session's lifetime.
        var name = @"\\.\pipe\wintty-defterm-"
            + Guid.NewGuid().ToString("N");
        var server = CreateNamedPipe(
            name,
            PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance,
            PipeTypeByte | PipeRejectRemoteClients,
            1,
            4096,
            4096,
            0,
            IntPtr.Zero);
        if (server == new IntPtr(-1))
        {
            throw new InvalidOperationException("CreateNamedPipe failed: " + Marshal.GetLastWin32Error());
        }

        var client = CreateFile(
            name,
            GenericRead | GenericWrite,
            0,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);
        if (client == new IntPtr(-1))
        {
            CloseHandle(server);
            throw new InvalidOperationException("CreateFile on pipe failed: " + Marshal.GetLastWin32Error());
        }

        // The server end supports both directions; hand out the two ends
        // as the pair. The console side gets the client end, our side
        // keeps the server end.
        return (client, server);
    }

    private static IntPtr Duplicate(IntPtr source)
    {
        var self = GetCurrentProcess();
        if (!DuplicateHandle(self, source, self, out var copy, 0, false, 2 /* DUPLICATE_SAME_ACCESS */))
        {
            throw new InvalidOperationException("DuplicateHandle failed: " + Marshal.GetLastWin32Error());
        }

        return copy;
    }

    private void ReleaseThisCall()
    {
        var start = PreexistingHandleCount;
        for (var i = _liveHandles.Count - 1; i >= start; i--)
        {
            CloseHandle(_liveHandles[i]);
            _liveHandles.RemoveAt(i);
        }

        LastInWriteEnd = IntPtr.Zero;
        LastClientDuplicate = IntPtr.Zero;
    }

    private sealed class InjectedFaultException : Exception
    {
        public InjectedFaultException(FaultPoint point)
            : base($"injected fault at {point}")
        {
        }
    }

    private const uint PipeAccessDuplex = 0x3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagFirstPipeInstance = 0x80000;
    private const uint PipeTypeByte = 0;
    private const uint PipeRejectRemoteClients = 8;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    // LibraryImport (not DllImport): Ghostty.Core disables runtime
    // marshalling, and a DllImport with SetLastError throws
    // MarshalDirectiveException at the first call. Same note as
    // NtProcessInterop.cs.
    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outBuffer,
        uint inBuffer,
        uint defaultTimeout,
        IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFile(
        string name,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetProcessId(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr sourceHandle,
        IntPtr targetProcess,
        out IntPtr targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
