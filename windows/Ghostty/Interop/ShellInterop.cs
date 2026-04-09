using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ghostty.Interop;

/// <summary>
/// <c>[GeneratedComInterface]</c> declarations for the Windows
/// Shell APIs the jump-list code consumes, plus the
/// <c>SetCurrentProcessExplicitAppUserModelID</c> P/Invoke and
/// PROPVARIANT helpers needed to set <c>System.Title</c> on a
/// shell link.
///
/// Migrated from <c>[ComImport]</c> in the
/// dotnet-windows-reviewer-skill review of PR 170: the legacy
/// attribute relies on runtime marshalling and silently breaks
/// under <c>PublishAot</c> (CsWinRT #1927). The
/// <c>StringMarshalling = Utf16</c> setting matches the original
/// <c>UnmanagedType.LPWStr</c> behavior so the wire format is
/// unchanged.
///
/// Activation goes through <see cref="ComCreate.Create"/>, which
/// uses a shared <c>StrategyBasedComWrappers</c> to translate the
/// raw IUnknown* into a strongly-typed managed wrapper.
/// </summary>
internal static partial class ShellInterop
{
    // P/Invoke ---------------------------------------------------

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SetCurrentProcessExplicitAppUserModelID(string AppID);

    // CLSIDs -----------------------------------------------------

    public static readonly Guid CLSID_DestinationList = new("77f10cf0-3db5-4966-b520-b7c54fd35ed6");
    public static readonly Guid CLSID_EnumerableObjectCollection = new("2d3468c1-36a7-43b6-ac24-d3f02fd9607a");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-c000-000000000046");

    // Interface IIDs ---------------------------------------------

    public static readonly Guid IID_ICustomDestinationList = new("6332debf-87b5-4670-90c0-5e57b408a49e");
    public static readonly Guid IID_IObjectCollection = new("5632b1a4-e38a-400a-928a-d4cd63230295");
    public static readonly Guid IID_IObjectArray = new("92ca9dcd-5622-4bba-a805-5e9f541bd8c9");
    public static readonly Guid IID_IShellLinkW = new("000214f9-0000-0000-c000-000000000046");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

    // ICustomDestinationList ------------------------------------

    [GeneratedComInterface]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    public partial interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMaxSlots, in Guid riid, out IntPtr ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks(IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations(in Guid riid, out IntPtr ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    // IObjectArray -----------------------------------------------

    [GeneratedComInterface]
    [Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    public partial interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, in Guid riid, out IntPtr ppv);
    }

    // IObjectCollection (extends IObjectArray) -------------------

    [GeneratedComInterface]
    [Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    public partial interface IObjectCollection
    {
        // IObjectArray methods (this interface inherits from it)
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, in Guid riid, out IntPtr ppv);
        // IObjectCollection additions
        void AddObject(IntPtr punk);
        void AddFromArray(IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    // IShellLinkW ------------------------------------------------

    /// <summary>
    /// IShellLinkW vtable. Only the Set* methods we actually need
    /// are declared with full signatures; the Get* methods take
    /// <c>StringBuilder</c> output buffers, which source-generated
    /// COM does not support (SYSLIB1052). They are placeholders so
    /// the vtable index of every Set* method matches the COM ABI —
    /// each unused entry is declared as <c>void Reserved_NN()</c>
    /// and never called from managed code.
    /// </summary>
    [GeneratedComInterface]
    [Guid("000214f9-0000-0000-c000-000000000046")]
    public partial interface IShellLinkW
    {
        // 0: GetPath — unused, output StringBuilder is unsupported
        void GetPath_Reserved();
        // 1: GetIDList
        void GetIDList(out IntPtr ppidl);
        // 2: SetIDList
        void SetIDList(IntPtr pidl);
        // 3: GetDescription — unused
        void GetDescription_Reserved();
        // 4
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        // 5: GetWorkingDirectory — unused
        void GetWorkingDirectory_Reserved();
        // 6
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        // 7: GetArguments — unused
        void GetArguments_Reserved();
        // 8
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        // 9
        void GetHotkey(out ushort pwHotkey);
        // 10
        void SetHotkey(ushort wHotkey);
        // 11
        void GetShowCmd(out int piShowCmd);
        // 12
        void SetShowCmd(int iShowCmd);
        // 13: GetIconLocation — unused
        void GetIconLocation_Reserved();
        // 14
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        // 15
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        // 16
        void Resolve(IntPtr hwnd, uint fFlags);
        // 17
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    // IPropertyStore ---------------------------------------------

    [GeneratedComInterface]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    public partial interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(in PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(in PROPERTYKEY key, in PROPVARIANT pv);
        void Commit();
    }

    // PROPERTYKEY and PROPVARIANT helpers ------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;

        public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    /// <summary>System.Title: PKEY used to set the display name on a shell link for jump list entries.</summary>
    public static PROPERTYKEY PKEY_Title = new(new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), 2);

    // PROPVARIANT for VT_LPWSTR strings. Only the vt + union pointer
    // fields are used; the rest is padding. Caller is responsible for
    // allocating/freeing the string (CoTaskMemAlloc / PropVariantClear).
    [StructLayout(LayoutKind.Explicit)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    public const ushort VT_LPWSTR = 31;

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>
    /// Helper: populate a shell link's System.Title so the jump list
    /// shows a nice display string. Allocates the string via
    /// CoTaskMemAlloc and transfers ownership to the property store.
    /// </summary>
    public static void SetShellLinkTitle(IShellLinkW link, string title)
    {
        // The IShellLinkW COM object also implements IPropertyStore;
        // cross-interface QI happens through the shared ComWrappers
        // strategy. Reach the underlying IUnknown via the strategy
        // wrappers (Marshal.GetIUnknownForObject is runtime-only and
        // SYSLIB1099 against [GeneratedComInterface] types) and wrap
        // it once for the IPropertyStore facet.
        var unknown = ComCreate.GetIUnknown(link);
        try
        {
            var store = (IPropertyStore)ComCreate.Wrap(unknown);
            var pv = new PROPVARIANT
            {
                vt = VT_LPWSTR,
                pointerValue = Marshal.StringToCoTaskMemUni(title),
            };
            try
            {
                store.SetValue(in PKEY_Title, in pv);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
