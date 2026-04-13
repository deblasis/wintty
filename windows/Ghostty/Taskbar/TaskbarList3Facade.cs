using System;
using Ghostty.Core.Tabs;
using Ghostty.Core.Taskbar;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Ghostty.Taskbar;

/// <summary>
/// Real implementation of <see cref="ITaskbarProgressSink"/>.
/// CoCreates an <see cref="ITaskbarList3"/>, calls HrInit once, and
/// forwards sink writes to SetProgressValue and SetProgressState
/// against the window's HWND.
///
/// One facade per window. MainWindow constructs it with its HWND.
/// </summary>
internal sealed class TaskbarList3Facade : ITaskbarProgressSink
{
    private readonly HWND _hwnd;
    private readonly ITaskbarList3 _taskbar;

    public TaskbarList3Facade(IntPtr hwnd)
    {
        _hwnd = new HWND(hwnd);
        // CsWin32 emits a CoCreateInstance-backed factory on
        // CoCreateable coclasses (PR 1502). The TaskbarList class
        // ctor is marked obsolete and routes through CreateInstance<T>.
        _taskbar = TaskbarList.CreateInstance<ITaskbarList3>();
        _taskbar.HrInit();
    }

    public void Write(TabProgressState state)
    {
        switch (state.State)
        {
            case TabProgressState.Kind.None:
                _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_NOPROGRESS);
                return;
            case TabProgressState.Kind.Indeterminate:
                _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_INDETERMINATE);
                return;
            case TabProgressState.Kind.Normal:
                _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_NORMAL);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
            case TabProgressState.Kind.Paused:
                _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_PAUSED);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
            case TabProgressState.Kind.Error:
                _taskbar.SetProgressState(_hwnd, TBPFLAG.TBPF_ERROR);
                _taskbar.SetProgressValue(_hwnd, (ulong)Clamp(state.Percent), 100UL);
                return;
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 100 ? 100 : v;
}
