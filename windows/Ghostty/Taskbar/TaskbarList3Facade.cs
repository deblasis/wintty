using System;
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
/// One facade per window. MainWindow constructs it with its HWND.
/// </summary>
internal sealed class TaskbarList3Facade : ITaskbarProgressSink
{
    private readonly IntPtr _hwnd;
    private readonly TaskbarInterop.ITaskbarList3 _taskbar;

    public TaskbarList3Facade(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _taskbar = (TaskbarInterop.ITaskbarList3)ComCreate.Create(
            TaskbarInterop.CLSID_TaskbarList,
            TaskbarInterop.IID_ITaskbarList3);
        _taskbar.HrInit();
    }

    public void Write(TabProgressState state)
    {
        switch (state.State)
        {
            case TabProgressState.Kind.None:
                _taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.NOPROGRESS);
                return;
            case TabProgressState.Kind.Indeterminate:
                _taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.INDETERMINATE);
                return;
            case TabProgressState.Kind.Normal:
                _taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.NORMAL);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
            case TabProgressState.Kind.Paused:
                _taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.PAUSED);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
            case TabProgressState.Kind.Error:
                _taskbar.SetProgressState(_hwnd, TaskbarInterop.TBPFLAG.ERROR);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 100 ? 100 : v;
}
