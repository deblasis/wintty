using System.Runtime.InteropServices;

namespace Ghostty.Core.DefaultTerm;

/// <summary>
/// The console handoff contract, v3: the terminal creates the two pipes
/// and returns the console side's ends, receives the signal, reference,
/// server, and client handles, and a pointer to the startup info. All
/// HRESULTs are preserved: the caller (the delegated console host)
/// treats any failure as "degrade this launch to the classic console",
/// which is the safe outcome. The GUID and shape are the platform's,
/// verified on the machine.
/// </summary>
[ComVisible(true)]
[Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITerminalHandoff3
{
    [PreserveSig]
    int EstablishPtyHandoff(
        out IntPtr inPipe,
        out IntPtr outPipe,
        IntPtr signal,
        IntPtr reference,
        IntPtr server,
        IntPtr client,
        IntPtr startupInfo);
}

/// <summary>
/// The empty marker interface the platform checks before trusting a
/// terminal candidate: an object without it is silently refused. Carried
/// by the terminal object; it has no members by design.
/// </summary>
[ComVisible(true)]
[Guid("746E6BC0-AB05-4E38-AB14-71E86763141F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDefaultTerminalMarker
{
}

/// <summary>
/// The classic COM class factory interface, declared locally so the CCW
/// can serve it for self-registration via CoRegisterClassObject. The
/// GUID is the platform's IClassFactory.
/// </summary>
[ComVisible(true)]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactoryCom
{
    [PreserveSig]
    int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr ppv);

    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}

/// <summary>
/// The terminal handoff object served to the console host. Implements
/// the marker interface (the platform refuses the handoff without it)
/// and also answers IClassFactory: the activation machinery fetches the
/// factory, calls CreateInstance for IID_IUnknown, and QIs that result
/// for IClassFactory before anything else, so the product itself must
/// survive the fetch pattern. Every type in this file must stay
/// top-level public: the runtime's QI refuses every custom IID for a
/// type nested in an internal class, while registration still reports
/// success.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class TerminalHandoffObject :
    ITerminalHandoff3,
    IDefaultTerminalMarker,
    IClassFactoryCom
{
    private readonly TerminalHandoffService? _service;

    /// <summary>
    /// The bare product the class factory hands out by default: no
    /// service, so every call fails closed, which the host degrades
    /// safely.
    /// </summary>
    public TerminalHandoffObject()
    {
    }

    /// <summary>
    /// The production product: the call contract runs on the in-call
    /// service, whose placement scheduler routes the session into the
    /// queue.
    /// </summary>
    public TerminalHandoffObject(TerminalHandoffService service)
    {
        _service = service;
    }

    int ITerminalHandoff3.EstablishPtyHandoff(
        out IntPtr inPipe,
        out IntPtr outPipe,
        IntPtr signal,
        IntPtr reference,
        IntPtr server,
        IntPtr client,
        IntPtr startupInfo)
    {
        if (_service is null)
        {
            inPipe = IntPtr.Zero;
            outPipe = IntPtr.Zero;
            return unchecked((int)0x80004005); // E_FAIL, fail closed
        }

        var hr = _service.HandleCall(new HandoffArgs(
            signal,
            reference,
            server,
            client,
            startupInfo));
        if (hr == 0)
        {
            // The console side's far ends: the caller owns them from
            // here, exactly as the [out] contract says.
            inPipe = _service.LastConsoleInRead;
            outPipe = _service.LastConsoleOutWrite;
        }
        else
        {
            inPipe = IntPtr.Zero;
            outPipe = IntPtr.Zero;
        }

        return hr;
    }

    int IClassFactoryCom.CreateInstance(IntPtr outer, ref Guid iid, out IntPtr ppv) =>
        HandoffClassFactory.CreateAndQuery(() => new TerminalHandoffObject(), outer, ref iid, out ppv);

    int IClassFactoryCom.LockServer(bool fLock) => 0;
}

/// <summary>
/// The class factory registered for the terminal CLSID while the feature
/// is enabled. CreateInstance for IID_IUnknown returns the product
/// object (which itself answers the IClassFactory QI of the fetch
/// pattern); any other IID is resolved on the product.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class HandoffClassFactory : IClassFactoryCom
{
    private readonly Func<TerminalHandoffObject> _productFactory;

    public HandoffClassFactory(Func<TerminalHandoffObject> productFactory)
    {
        _productFactory = productFactory;
    }

    int IClassFactoryCom.CreateInstance(IntPtr outer, ref Guid iid, out IntPtr ppv) =>
        CreateAndQuery(_productFactory, outer, ref iid, out ppv);

    int IClassFactoryCom.LockServer(bool fLock) => 0;

    internal static int CreateAndQuery(
        Func<TerminalHandoffObject> productFactory,
        IntPtr outer,
        ref Guid iid,
        out IntPtr ppv)
    {
        ppv = IntPtr.Zero;
        if (outer != IntPtr.Zero)
        {
            return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        }

        var unknown = Marshal.GetIUnknownForObject(productFactory());
        if (iid == new Guid("00000000-0000-0000-C000-000000000046"))
        {
            // Transfer the reference we hold; the caller releases it.
            ppv = unknown;
            return 0;
        }

        try
        {
            var hr = Marshal.QueryInterface(unknown, ref iid, out ppv);
            if (hr != 0)
            {
                return hr;
            }

            // QueryInterface produced its own reference for the caller;
            // ours is released below with the unknown wrapper.
            return 0;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
