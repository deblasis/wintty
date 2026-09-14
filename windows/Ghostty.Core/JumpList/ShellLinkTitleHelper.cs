using System;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Populates a shell link's System.Title so jump list entries show a
/// nice display string instead of the exe filename. Allocates the
/// string via CoTaskMemAlloc and transfers ownership to the property
/// store.
///
/// Migrated from Interop/ShellInterop.SetShellLinkTitle, then moved
/// here from Ghostty.JumpList so ShellLinkTitleHelperTests in
/// Ghostty.Tests can create a real ShellLink COM object and drive
/// this method without pulling in the WinUI 3 Ghostty assembly - the
/// same reason DWriteFontEnumerator lives in this project.
///
/// IShellLinkW and IPropertyStore are two unrelated interfaces that
/// the real CLSID_ShellLink object happens to implement at once;
/// there is no interface-inheritance relationship between them in
/// the shell IDL (unlike, say, IObjectCollection : IObjectArray).
/// Reaching one from the other is therefore a genuine native
/// QueryInterface. The source-generated ("[GeneratedComInterface]")
/// COM model performs that QI when you cast an RCW that carries real
/// native COM identity to another such interface type: the RCW
/// implements IDynamicInterfaceCastable, and the cast dispatches an
/// actual QueryInterface against the underlying native object. The
/// `link` parameter here is exactly such an RCW - it comes from
/// ShellLink.CreateInstance, a real CoCreateInstance-backed coclass
/// activation - so a direct cast is enough.
///
/// This previously round-tripped through
/// Ghostty.Interop.ComCreate.GetComInterfaceForObject +
/// ComCreate.Wrap first (StrategyBasedComWrappers'
/// GetOrCreateComInterfaceForObject / GetOrCreateObjectForComInstance).
/// That pair is for the OPPOSITE direction: fabricating a
/// COM-callable wrapper (CCW) that exposes a *managed* object out to
/// native code. Handed an RCW like `link`, ComputeVtables only
/// advertises the interfaces link's own managed type declares
/// (IShellLinkW), not whatever the underlying native object would
/// separately answer to QueryInterface. The resulting CCW therefore
/// never supported IID_IPropertyStore, and casting it threw
/// InvalidCastException / E_NOINTERFACE - on every launch, since
/// SetTitle ran unconditionally as part of building every jump list
/// entry. Casting `link` itself has no such problem: it QIs the real
/// native object, which genuinely does support IPropertyStore.
/// </summary>
internal static class ShellLinkTitleHelper
{
    public static unsafe void SetTitle(IShellLinkW link, string title)
    {
        // Cross-interface QI on the real native ShellLink object; see
        // the class doc comment for why this is a plain cast and not
        // a ComCreate round-trip.
        var store = (IPropertyStore)link;

        // Allocate the backing string FIRST, before the PROPVARIANT
        // is populated. Ordering matters: if a future edit inserts
        // a throwing call between allocation and the pwszVal
        // assignment, the string would leak because PropVariantClear
        // only frees what vt + pwszVal say it owns. Stage the
        // pointer in a local so the assignment into the PROPVARIANT
        // slot is a single move.
        var pwsz = (char*)Marshal.StringToCoTaskMemUni(title);

        var pv = new PROPVARIANT();
        // PROPVARIANT union layout in CsWin32 0.3.269 (verified
        // in Windows.Win32.NativeMethods.g.cs around line 5854):
        //   PROPVARIANT.Anonymous              -> _Anonymous_e__Union_unmanaged
        //                       .Anonymous     -> _Anonymous_e__Struct_unmanaged (vt + reserved + nested union)
        //                                .vt
        //                                .Anonymous -> _Anonymous_e__Union_unmanaged (typed value slots)
        //                                         .pwszVal (PWSTR)
        // The vt field is at the *_Struct level, the pwszVal slot
        // is one nesting deeper. Assign pwszVal and vt adjacently
        // so the finally's PropVariantClear always sees a consistent
        // typed slot (VT_LPWSTR + real pointer, never VT_LPWSTR +
        // garbage).
        pv.Anonymous.Anonymous.Anonymous.pwszVal = new PWSTR(pwsz);
        pv.Anonymous.Anonymous.vt = VARENUM.VT_LPWSTR;
        try
        {
            store.SetValue(in s_pkeyTitle, in pv);
            store.Commit();
        }
        finally
        {
            // The generated CsWin32 class is DWritePInvoke in this
            // project (see Ghostty.Core/NativeMethods.json), not the
            // default PInvoke name Ghostty's own generation uses.
            DWritePInvoke.PropVariantClear(ref pv);
        }
    }

    // System.Title property key. CsWin32 0.3.269 does not expose
    // PKEY_Title as a generated constant via Win32Metadata, so the
    // GUID is hard-coded here. The fmtid is the well-known
    // FMTID_SummaryInformation; pid 2 is PIDSI_TITLE. Stored as a
    // static field rather than a property so it can be passed by
    // `in` reference to IPropertyStore.SetValue.
    private static readonly PROPERTYKEY s_pkeyTitle = new()
    {
        fmtid = new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"),
        pid = 2,
    };

    /// <summary>
    /// Test-only: reads back the title <see cref="SetTitle"/> wrote,
    /// via the same IPropertyStore facet, so ShellLinkTitleHelperTests
    /// can assert the value actually stuck rather than merely trusting
    /// that SetTitle did not throw. Not called by production code.
    /// PROPERTYKEY/PROPVARIANT stay internal to this file rather than
    /// also being driven from Ghostty.Tests directly.
    /// </summary>
    internal static unsafe string? GetTitleForTests(IShellLinkW link)
    {
        var store = (IPropertyStore)link;
        store.GetValue(in s_pkeyTitle, out var pv);
        try
        {
            return pv.Anonymous.Anonymous.Anonymous.pwszVal.ToString();
        }
        finally
        {
            DWritePInvoke.PropVariantClear(ref pv);
        }
    }
}
