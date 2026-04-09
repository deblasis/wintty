using System;
using System.Runtime.InteropServices;

namespace Ghostty.Interop;

/// <summary>
/// ComImport declarations and P/Invoke for the Windows Shell APIs
/// this project needs. Kept in one file so the ABI surface is easy
/// to audit.
///
/// Interfaces: ICustomDestinationList (jump list), IObjectCollection
/// (category content), IObjectArray (read-only views), IShellLinkW
/// (entries), IPropertyStore (link titles), plus the PROPERTYKEY /
/// PROPVARIANT helpers needed to set System.Title on a shell link.
///
/// P/Invoke: SetCurrentProcessExplicitAppUserModelID from shell32.
///
/// TODO(aot): these declarations use classic [ComImport] + [DllImport]
/// which pull in runtime marshalling and are incompatible with
/// [assembly: DisableRuntimeMarshalling] and NativeAOT. When the rest
/// of the Windows shell flips to AOT, migrate this file to
/// [GeneratedComInterface]/[LibraryImport] — or, preferably, replace
/// it entirely with CsWin32-generated bindings (ICustomDestinationList,
/// IShellLinkW, IPropertyStore, PROPVARIANT, CoCreateInstance and
/// SetCurrentProcessExplicitAppUserModelID are all in Win32 metadata).
/// </summary>
internal static partial class ShellInterop
{
    // P/Invoke ---------------------------------------------------

    // SetCurrentProcessExplicitAppUserModelID is a plain void(LPCWSTR)
    // export with no runtime-marshalling surface worth speaking of, so
    // it's already AOT-friendly via LibraryImport.
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    public static partial void SetCurrentProcessExplicitAppUserModelID(string AppID);

    [DllImport("ole32.dll", PreserveSig = false)]
    public static extern void CoCreateInstance(
        [In] ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    public const uint CLSCTX_INPROC_SERVER = 0x1;

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

    [ComImport]
    [Guid("6332debf-87b5-4670-90c0-5e57b408a49e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ICustomDestinationList
    {
        void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void BeginList(out uint pcMaxSlots, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void AppendCategory([MarshalAs(UnmanagedType.LPWStr)] string pszCategory, [MarshalAs(UnmanagedType.Interface)] IObjectArray poa);
        void AppendKnownCategory(int category);
        void AddUserTasks([MarshalAs(UnmanagedType.Interface)] IObjectArray poa);
        void CommitList();
        void GetRemovedDestinations([In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        void DeleteList([MarshalAs(UnmanagedType.LPWStr)] string pszAppID);
        void AbortList();
    }

    // IObjectCollection (extends IObjectArray) -------------------

    [ComImport]
    [Guid("5632b1a4-e38a-400a-928a-d4cd63230295")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectCollection
    {
        // IObjectArray methods (this interface inherits from it)
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        // IObjectCollection additions
        void AddObject([MarshalAs(UnmanagedType.IUnknown)] object punk);
        void AddFromArray([MarshalAs(UnmanagedType.Interface)] IObjectArray poaSource);
        void RemoveObjectAt(uint uiIndex);
        void Clear();
    }

    [ComImport]
    [Guid("92ca9dcd-5622-4bba-a805-5e9f541bd8c9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IObjectArray
    {
        void GetCount(out uint pcObjects);
        void GetAt(uint uiIndex, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    }

    // IShellLinkW -------------------------------------------------

    [ComImport]
    [Guid("000214f9-0000-0000-c000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    // IPropertyStore ---------------------------------------------

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue([In] ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue([In] ref PROPERTYKEY key, [In] ref PROPVARIANT pv);
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
    public static readonly PROPERTYKEY PKEY_Title = new(new Guid("f29f85e0-4ff9-1068-ab91-08002b27b3d9"), 2);

    // PROPVARIANT for VT_LPWSTR strings. Only the vt + union pointer
    // fields are used; the rest is padding. Caller is responsible for
    // allocating/freeing the string (CoTaskMemAlloc / PropVariantClear).
    //
    // Size is pinned explicitly: the native PROPVARIANT is 16 bytes on
    // x86 and 24 bytes on x64 (8-byte header + 16-byte union — the
    // largest union member being BLOB { ULONG cbSize; BYTE* pBlobData; }
    // or CA* caXxx, both of which pad to 16 on x64). Without Size, the
    // marshaller sizes the struct at max(offset+size)=16 and
    // IPropertyStore::SetValue — which does a full-sized PropVariantCopy
    // internally — would read 8 bytes past our managed storage on x64.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    public const ushort VT_LPWSTR = 31;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    /// <summary>
    /// Helper: populate a shell link's System.Title so the jump list
    /// shows a nice display string. Allocates the string via
    /// CoTaskMemAlloc and transfers ownership to the property store.
    /// </summary>
    public static void SetShellLinkTitle(IShellLinkW link, string title)
    {
        var store = (IPropertyStore)link;
        var pv = new PROPVARIANT
        {
            vt = VT_LPWSTR,
            pointerValue = Marshal.StringToCoTaskMemUni(title),
        };
        try
        {
            store.SetValue(ref PKEY_Title, ref pv);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref pv);
        }
    }
}
