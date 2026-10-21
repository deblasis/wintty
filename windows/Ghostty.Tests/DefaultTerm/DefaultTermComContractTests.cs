using System.Runtime.InteropServices;
using Ghostty.Core.DefaultTerm;
using Xunit;

namespace Ghostty.Tests.DefaultTerm;

/// <summary>
/// The COM surface the console handoff activates against, verified in
/// process. Every rule here was learned on the machine: the runtime's QI
/// refuses every custom IID for a type nested in an internal class while
/// registration still reports success, the activation machinery fetches
/// the class factory and QIs its CreateInstance result for IClassFactory,
/// and the terminal object must carry the empty marker interface or the
/// host silently refuses the handoff. These tests pin all three.
/// </summary>
public sealed class DefaultTermComContractTests
{
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid IidIDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IidTerminalHandoff3 = new("6F23DA90-15C5-4203-9DB0-64E73F1B1B00");
    private static readonly Guid IidDefaultTerminalMarker = new("746E6BC0-AB05-4E38-AB14-71E86763141F");

    [Fact]
    public void QI_AnswersAllThreeIids()
    {
        var factory = new HandoffClassFactory(() => new TerminalHandoffObject());
        var product = new TerminalHandoffObject();

        Qi.Succeeds(factory, IidIClassFactory, "factory serves IClassFactory");
        Qi.Fails(factory, IidTerminalHandoff3, "the factory itself is not the handoff object");
        Qi.Succeeds(product, IidTerminalHandoff3, "product serves ITerminalHandoff3");
        Qi.Succeeds(product, IidDefaultTerminalMarker, "product serves the marker interface");
        Qi.Fails(product, IidIDispatch, "IDispatch is not implemented (by design)");
        Qi.Succeeds(product, IidIClassFactory, "product answers the IClassFactory fetch pattern");
    }

    [Fact]
    public void FactoryFetch_PatternHolds()
    {
        // The activation machinery CoCreates with IID_IUnknown, gets the
        // factory's product via CreateInstance, and QIs THAT for
        // IClassFactory before anything else: the fetch pattern the e2e
        // run isolated. A factory returning a bare product fails every
        // activation with E_NOINTERFACE, silently.
        var factory = (IClassFactoryCom)new HandoffClassFactory(() => new TerminalHandoffObject());
        var iidU = IidIUnknown;

        var hr = factory.CreateInstance(IntPtr.Zero, ref iidU, out var unk);
        Assert.Equal(0, hr);
        Assert.NotEqual(IntPtr.Zero, unk);
        try
        {
            Qi.Succeeds(unk, IidIClassFactory, "CreateInstance(IID_IUnknown) result QIs for IClassFactory");
            Qi.Succeeds(unk, IidTerminalHandoff3, "the same object also serves the handoff interface");
        }
        finally
        {
            Marshal.Release(unk);
        }
    }

    [Fact]
    public void TypesArePublic()
    {
        // The standing lesson: COM types nested in an internal class are
        // invisible to the runtime's QI while registration reports
        // success. All handoff types must stay top-level public.
        Assert.True(typeof(HandoffClassFactory).IsVisible, "HandoffClassFactory must be public");
        Assert.True(typeof(TerminalHandoffObject).IsVisible, "TerminalHandoffObject must be public");
        Assert.True(typeof(ITerminalHandoff3).IsVisible, "ITerminalHandoff3 must be public");
        Assert.True(typeof(IDefaultTerminalMarker).IsVisible, "IDefaultTerminalMarker must be public");
    }

    [Fact]
    public void Aggregation_Refused()
    {
        var factory = (IClassFactoryCom)new HandoffClassFactory(() => new TerminalHandoffObject());
        var iidU = IidIUnknown;
        var hr = factory.CreateInstance((IntPtr)1, ref iidU, out _);
        Assert.Equal(unchecked((int)0x80040110), hr); // CLASS_E_NOAGGREGATION
    }

    private static class Qi
    {
        public static void Succeeds(object obj, Guid iid, string because)
        {
            var unk = Marshal.GetIUnknownForObject(obj);
            try
            {
                var hr = Marshal.QueryInterface(unk, ref iid, out var pv);
                Assert.True(hr == 0 && pv != IntPtr.Zero, $"{because} (hr=0x{hr:X8})");
                if (pv != IntPtr.Zero)
                {
                    Marshal.Release(pv);
                }
            }
            finally
            {
                Marshal.Release(unk);
            }
        }

        public static void Succeeds(IntPtr unk, Guid iid, string because)
        {
            var hr = Marshal.QueryInterface(unk, ref iid, out var pv);
            Xunit.Assert.True(hr == 0 && pv != IntPtr.Zero, $"{because} (hr=0x{hr:X8})");
            if (pv != IntPtr.Zero)
            {
                Marshal.Release(pv);
            }
        }

        public static void Fails(object obj, Guid iid, string because)
        {
            var unk = Marshal.GetIUnknownForObject(obj);
            try
            {
                Fails(unk, iid, because);
            }
            finally
            {
                Marshal.Release(unk);
            }
        }

        public static void Fails(IntPtr unk, Guid iid, string because)
        {
            var hr = Marshal.QueryInterface(unk, ref iid, out var pv);
            Xunit.Assert.True(hr != 0, $"{because} (hr=0x{hr:X8})");
        }
    }
}
