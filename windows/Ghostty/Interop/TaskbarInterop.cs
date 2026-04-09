using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Ghostty.Interop;

/// <summary>
/// <c>[GeneratedComInterface]</c> declaration for
/// <c>ITaskbarList3</c>. Sibling of <see cref="ShellInterop"/>;
/// kept narrow to the methods the progress facade actually
/// invokes (HrInit, SetProgressValue, SetProgressState).
///
/// Migrated from <c>[ComImport]</c> in the
/// dotnet-windows-reviewer-skill review of PR 170 — see
/// <see cref="ShellInterop"/> for the rationale.
/// </summary>
internal static partial class TaskbarInterop
{
    public static readonly Guid CLSID_TaskbarList = new("56fdf344-fd6d-11d0-958a-006097c9a090");
    public static readonly Guid IID_ITaskbarList3 = new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

    [GeneratedComInterface]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    public partial interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3 — only SetProgressValue / SetProgressState used
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
    }

    [Flags]
    public enum TBPFLAG
    {
        NOPROGRESS    = 0,
        INDETERMINATE = 0x1,
        NORMAL        = 0x2,
        ERROR         = 0x4,
        PAUSED        = 0x8,
    }
}
