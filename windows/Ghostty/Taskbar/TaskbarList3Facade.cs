using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Ghostty.Interop;

namespace Ghostty.Taskbar;

/// <summary>
/// Real implementation of <see cref="ITaskbarProgressSink"/>.
/// CoCreates an <see cref="TaskbarInterop.ITaskbarList3"/>, calls
/// HrInit once, and forwards sink writes to SetProgressValue and
/// SetProgressState against the window's HWND.
///
/// One facade per window. MainWindow constructs it with its HWND
/// and disposes it on Closed so the RCW is released deterministically
/// rather than waiting on GC.
/// </summary>
internal sealed class TaskbarList3Facade : ITaskbarProgressSink, IDisposable
{
    private const ulong ProgressTotal = 100UL;

    private readonly IntPtr _hwnd;
    private TaskbarInterop.ITaskbarList3? _taskbar;

    public TaskbarList3Facade(IntPtr hwnd)
    {
        _hwnd = hwnd;
        var clsid = TaskbarInterop.CLSID_TaskbarList;
        var iid = TaskbarInterop.IID_ITaskbarList3;
        ShellInterop.CoCreateInstance(
            ref clsid,
            IntPtr.Zero,
            ShellInterop.CLSCTX_INPROC_SERVER,
            ref iid,
            out var obj);
        _taskbar = (TaskbarInterop.ITaskbarList3)obj;
        _taskbar.HrInit();
    }

    public void Write(TabProgressState state)
    {
        var taskbar = _taskbar;
        if (taskbar is null) return;

        // COM RPC can fail at any time (explorer restart, desktop switch,
        // taskbar in a transient state). The taskbar indicator is a
        // nice-to-have, so swallow + log rather than take the window
        // down through the PropertyChanged chain that reaches us.
        try
        {
            switch (state.State)
            {
                case TabProgressState.Kind.None:
                    taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.NOPROGRESS);
                    return;
                case TabProgressState.Kind.Indeterminate:
                    taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.INDETERMINATE);
                    return;
                case TabProgressState.Kind.Normal:
                    taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.NORMAL);
                    taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), ProgressTotal);
                    return;
                case TabProgressState.Kind.Paused:
                    taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.PAUSED);
                    taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), ProgressTotal);
                    return;
                case TabProgressState.Kind.Error:
                    taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.ERROR);
                    taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), ProgressTotal);
                    return;
                default:
                    Debug.Fail($"Unknown TabProgressState.Kind: {state.State}");
                    return;
            }
        }
        catch (COMException ex)
        {
            // Explorer restarts, DWM transitions and remote-session
            // teardown can all kill the live ITaskbarList3 instance.
            // We don't want to take the window down because the
            // taskbar indicator stopped responding — but we DO want
            // debug builds to fail loudly so real bugs are not hidden.
            // Release builds log to the debug stream and the sink
            // becomes a no-op for the rest of the window's lifetime.
            _taskbar = null;
            Debug.WriteLine($"[TaskbarList3Facade] SetProgress* failed: 0x{ex.HResult:X8} {ex.Message}");
            Debug.Fail("ITaskbarList3 call failed", ex.ToString());
        }
    }

    public void Dispose()
    {
        var taskbar = _taskbar;
        _taskbar = null;
        if (taskbar is null) return;
        try
        {
            // Best-effort clear so the indicator does not linger after
            // the window closes.
            taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.NOPROGRESS);
        }
        catch (COMException ex)
        {
            // Tearing down; the RCW is still released below. Log so
            // a recurring teardown failure is visible in debug builds.
            Debug.WriteLine($"[TaskbarList3Facade] teardown clear failed: 0x{ex.HResult:X8} {ex.Message}");
        }
        Marshal.FinalReleaseComObject(taskbar);
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 100 ? 100 : v;
}
