using System;
using System.Runtime.InteropServices;
using Ghostty.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Ghostty.JumpList;

/// <summary>
/// Populates a shell link's System.Title so jump list entries show a
/// nice display string instead of the exe filename. Allocates the
/// string via CoTaskMemAlloc and transfers ownership to the property
/// store.
///
/// Migrated from Interop/ShellInterop.SetShellLinkTitle. The shared
/// StrategyBasedComWrappers instance in ComCreate handles cross-
/// interface QueryInterface from IShellLinkW to IPropertyStore.
/// </summary>
internal static class ShellLinkTitleHelper
{
    public static unsafe void SetTitle(IShellLinkW link, string title)
    {
        // The IShellLinkW COM object also implements IPropertyStore;
        // cross-interface QI happens through the shared ComWrappers
        // strategy. Reach a raw COM interface pointer and wrap it
        // for the IPropertyStore facet. Marshal.GetIUnknownForObject
        // is runtime-only and SYSLIB1099 against [GeneratedComInterface]
        // types, which is why ComCreate.GetComInterfaceForObject exists.
        //
        // Ownership: ComCreate.GetComInterfaceForObject returns an
        // AddRef'd pointer that this method owns. The finally block
        // Releases it after the Wrap + SetValue + Commit sequence.
        // ComCreate.Wrap internally AddRefs during the RCW creation,
        // so we still MUST release this raw pointer here (Wrap does
        // not take ownership - see ComCreate.cs doc comment).
        var unknown = ComCreate.GetComInterfaceForObject(link);
        try
        {
            var store = (IPropertyStore)ComCreate.Wrap(unknown);

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
                PInvoke.PropVariantClear(ref pv);
            }
        }
        finally
        {
            // Release the raw pointer we own from
            // GetComInterfaceForObject. Wrap AddRef'd internally so
            // the RCW's lifetime is unaffected by this Release.
            Marshal.Release(unknown);
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
}
