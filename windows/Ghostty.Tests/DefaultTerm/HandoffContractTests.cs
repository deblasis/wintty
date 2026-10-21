using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The in-call contract of EstablishPtyHandoff, run against real handles
/// and real pipes with every fault point injectable. What this pins: the
/// validation gate fires before any allocation, the two pipe pairs exist
/// with overlapped-capable ends, the [in] handles are duplicated
/// synchronously before return, a failure at any intermediate releases
/// exactly what was created, and adoption beyond the budget returns the
/// failure HRESULT the host degrades safely on.
/// </summary>
public sealed class HandoffContractTests
{
    private const int EFail = unchecked((int)0x80004005);
    private const int CapForTest = 3;

    [Fact]
    public void NullHandles_Refused()
    {
        using var proc = CurrentProcessHandle();

        // Each field isolated: every other handle stays valid, so a
        // refusal names the nulled field and nothing else. The four [in]
        // handles of the platform contract; the pipes are [out]s.
        RefusedNoPipes(new HandoffArgs(IntPtr.Zero, proc, proc, proc, IntPtr.Zero));
        RefusedNoPipes(new HandoffArgs(proc, IntPtr.Zero, proc, proc, IntPtr.Zero));
        RefusedNoPipes(new HandoffArgs(proc, proc, IntPtr.Zero, proc, IntPtr.Zero));
        RefusedNoPipes(new HandoffArgs(proc, proc, proc, IntPtr.Zero, IntPtr.Zero));
    }

    [Fact]
    public void NonProcessClientHandle_Refused()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService();
        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, pipe.Write, IntPtr.Zero));

        Assert.NotEqual(0, hr);
        Assert.Equal(0, service.PipesCreated);
    }

    [Fact]
    public void OverCap_Refused()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService(inFlight: CapForTest);
        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.NotEqual(0, hr);
        Assert.Equal(0, service.PipesCreated);
    }

    [Fact]
    public void PipesCreated_WithOverlappedWriteEnd()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService();
        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.Equal(0, hr);
        // The input pair and the output pair: two named-pipe pairs.
        Assert.Equal(2, service.PipesCreated);

        // The input pipe's our-side write end is a named pipe opened for
        // overlapped IO: the IOCP write path silently wedges otherwise.
        Assert.True(service.LastInWriteEnd != IntPtr.Zero, "our write end was not captured");
        Assert.True(GetNamedPipeInfo(service.LastInWriteEnd, out var flags, out _, out _, out _),
            "GetNamedPipeInfo failed on the write end");
        // GetNamedPipeInfo reports pipe mode flags only; it never shows
        // FILE_FLAG_OVERLAPPED. The capability itself is proven by an
        // overlapped write, which a synchronous-only handle rejects with
        // ERROR_INVALID_PARAMETER.
        Assert.NotEqual(0u, flags & 0x1); // PIPE_SERVER_END (0x1 in GetNamedPipeInfo's output): we keep the server end
        Assert.True(WriteOverlappedSucceeds(service.LastInWriteEnd),
            "the write end rejected an overlapped write");
        // The far ends are what the call hands back as its [out] pipe
        // parameters: they must exist on success.
        Assert.True(service.LastConsoleInRead != IntPtr.Zero, "no console input end");
        Assert.True(service.LastConsoleOutWrite != IntPtr.Zero, "no console output end");
    }

    [Fact]
    public void StartupInfoTitle_CopiedSynchronously()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var title = "some console title";
        var info = StartupInfoWithTitle(title);
        var service = NewService();
        try
        {
            var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, info.Ptr));
            Assert.Equal(0, hr);
        }
        finally
        {
            info.Dispose();
        }

        Assert.Equal(title, service.LastTitle);
    }

    [Fact]
    public void NullStartupInfo_TreatedAsAbsentTitle()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService();
        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.Equal(0, hr);
        Assert.Null(service.LastTitle);
    }

    [Fact]
    public void AdoptedSession_TravelsWithItsHandles()
    {
        // The placement request is the ONLY carrier of an adopted
        // session's handles: without LastHandles populated before the
        // scheduler runs, the surface receives nothing it can run on
        // and the six handles leak.
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();
        AdoptedSessionHandles? seen = null;

        var service = NewService();
        service.Scheduler = (_, _) =>
        {
            seen = service.LastHandles;
            return HandoffPlacementResult.Adopted;
        };

        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.Equal(0, hr);
        Assert.NotNull(seen);
        Assert.NotEqual(IntPtr.Zero, seen!.InWrite);
        Assert.NotEqual(IntPtr.Zero, seen.OutRead);
        Assert.NotEqual(IntPtr.Zero, seen.SignalDup);
        Assert.NotEqual(IntPtr.Zero, seen.ReferenceDup);
        Assert.NotEqual(IntPtr.Zero, seen.ServerDup);
        Assert.NotEqual(IntPtr.Zero, seen.ClientDup);
    }

    [Fact]
    public void InCallDuplication_Synchronous()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService();
        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.Equal(0, hr);
        // The duplicates were made during the call and are still valid
        // after return: the stub closes the originals when the call
        // returns, so our copies must be independently usable.
        Assert.True(service.LastClientDuplicate != IntPtr.Zero, "client was not duplicated");
        Assert.NotEqual(0, GetProcessId(service.LastClientDuplicate));
    }

    [Fact]
    public void FailureLattice_ReleasesExactlyWhatWasCreated()
    {
        using var proc = CurrentProcessHandle();
        var info = StartupInfoWithTitle("lattice title");
        try
        {
            LatticeBody(info.Ptr, proc);
        }
        finally
        {
            info.Dispose();
        }
    }

    private static void LatticeBody(IntPtr startupInfo, NoCloseHandle proc)
    {
        var args = new HandoffArgs(proc, proc, proc, proc, startupInfo);

        // Inject a failure at each intermediate in turn; every run must
        // leave zero live handles from this call.
        foreach (var point in new[] { FaultPoint.SecondPipe, FaultPoint.ThirdDuplicate, FaultPoint.Title })
        {
            var service = NewService();
            service.Fault = point;
            var hr = service.HandleCall(args);
            Assert.NotEqual(0, hr);
            Assert.True(
                service.LiveHandleCount - service.PreexistingHandleCount == 0,
                $"failure at {point} leaked handles");
        }
    }

    [Fact]
    public void AdoptionExpiry_ReturnsFailureAndReleases()
    {
        using var pipe = new RealPipe();
        using var proc = CurrentProcessHandle();

        var service = NewService();
        service.Scheduler = (_, _) => HandoffPlacementResult.Timeout;

        var hr = service.HandleCall(new HandoffArgs(proc, proc, proc, proc, IntPtr.Zero));

        Assert.Equal(EFail, hr);
        Assert.True(
            service.LiveHandleCount - service.PreexistingHandleCount == 0,
            "adoption expiry leaked handles");
    }

    private static void RefusedNoPipes(HandoffArgs args)
    {
        var service = NewService();
        var hr = service.HandleCall(args);
        Assert.NotEqual(0, hr);
        Assert.Equal(0, service.PipesCreated);
    }

    private static TerminalHandoffService NewService(int inFlight = 0)
    {
        var service = new TerminalHandoffService(
            adoptionBudget: TimeSpan.FromSeconds(5),
            sessionCap: CapForTest,
            inFlightSessions: () => inFlight);
        service.Scheduler ??= (work, budget) => work(CancellationToken.None);
        return service;
    }

    private static NoCloseHandle CurrentProcessHandle() => new((IntPtr)Process.GetCurrentProcess().Handle);

    /// <summary>
    /// A real OS handle wrapper that never closes: the current-process
    /// pseudo handle must not be closed, and the originals of every [in]
    /// handle belong to the stub, never to us.
    /// </summary>
    private sealed class NoCloseHandle : IDisposable
    {
        public NoCloseHandle(IntPtr handle) => Value = handle;

        public IntPtr Value { get; }

        public static implicit operator IntPtr(NoCloseHandle h) => h.Value;

        public void Dispose()
        {
        }
    }

    private sealed class RealPipe : IDisposable
    {
        public RealPipe()
        {
            if (!CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            {
                throw new InvalidOperationException("CreatePipe failed");
            }

            Read = read;
            Write = write;
        }

        public IntPtr Read { get; }

        public IntPtr Write { get; }

        public void Dispose()
        {
            CloseHandle(Read);
            CloseHandle(Write);
        }
    }

    private sealed class StartupInfoBlock : IDisposable
    {
        public StartupInfoBlock(IntPtr ptr, IntPtr titleBstr, IntPtr iconBstr)
        {
            Ptr = ptr;
            _titleBstr = titleBstr;
            _iconBstr = iconBstr;
        }

        public IntPtr Ptr { get; }

        private readonly IntPtr _titleBstr;
        private readonly IntPtr _iconBstr;

        public void Dispose()
        {
            Marshal.FreeHGlobal(Ptr);
            Marshal.FreeBSTR(_titleBstr);
            Marshal.FreeBSTR(_iconBstr);
        }
    }

    private static StartupInfoBlock StartupInfoWithTitle(string title)
    {
        var titleBstr = Marshal.StringToBSTR(title);
        var iconBstr = Marshal.StringToBSTR(@"C:\caller\icon.ico");
        var block = Marshal.AllocHGlobal(IntPtr.Size * 4);
        Marshal.WriteIntPtr(block, 0, titleBstr);
        Marshal.WriteIntPtr(block, IntPtr.Size, iconBstr);
        return new StartupInfoBlock(block, titleBstr, iconBstr);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr sa, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeInfo(IntPtr pipe, out uint flags, out uint outBuffer, out uint inBuffer, out uint maxInstances);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr securityAttributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(IntPtr file, byte[] buffer, uint bytesToWrite, out uint bytesWritten, ref Overlapped overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr EventHandle;
    }

    /// <summary>
    /// One overlapped write of a single byte: TRUE or ERROR_IO_PENDING
    /// means the handle accepts overlapped IO, ERROR_INVALID_PARAMETER
    /// means it does not.
    /// </summary>
    private static bool WriteOverlappedSucceeds(IntPtr handle)
    {
        var evt = CreateEvent(IntPtr.Zero, true, false, null);
        if (evt == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateEvent failed: " + Marshal.GetLastWin32Error());
        }

        try
        {
            var overlapped = new Overlapped { EventHandle = evt };
            if (WriteFile(handle, new byte[1], 1, out _, ref overlapped))
            {
                return true;
            }

            return Marshal.GetLastWin32Error() == 997; // ERROR_IO_PENDING
        }
        finally
        {
            CloseHandle(evt);
        }
    }
}
