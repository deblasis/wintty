using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Sets a taskbar icon overlay on the main window for
/// UpdateAvailable / RestartPending / Error states. Clears on
/// Idle / NoUpdatesFound / progress states (the pill itself
/// shows progress; the taskbar stays quiet mid-flight).
///
/// Must be constructed and <see cref="Attach"/>-ed on the UI
/// (STA) thread; <see cref="UpdateService.StateChanged"/> may
/// arrive on a background thread in D.2, so the handler
/// marshals to the provided <see cref="DispatcherQueue"/>.
/// </summary>
internal sealed class TaskbarOverlayProvider : IDisposable
{
    private readonly UpdateService _service;
    private readonly DispatcherQueue _dispatcher;
    private ITaskbarList3? _taskbar;
    private HWND _hwnd;

    public TaskbarOverlayProvider(UpdateService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
    }

    public void Attach(Window window)
    {
        _hwnd = new HWND(WindowNative.GetWindowHandle(window));
        try
        {
            // CsWin32 emits a CoCreateInstance-backed factory on
            // CoCreateable coclasses. Use the same pattern as TaskbarList3Facade.
            _taskbar = Windows.Win32.UI.Shell.TaskbarList.CreateInstance<ITaskbarList3>();
            _taskbar.HrInit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] taskbar init failed: {ex.Message}");
            _taskbar = null;
            return;
        }
        _service.StateChanged += OnStateChanged;
        Apply(_service.Current.State);
    }

    private void OnStateChanged(object? sender, Ghostty.Core.Sponsor.Update.UpdateStateSnapshot snap)
    {
        // StateChanged may fire on the driver's thread (D.2 timer path).
        // ITaskbarList3 is STA COM; marshal to the UI thread where we CoCreated it.
        _dispatcher.TryEnqueue(() => Apply(snap.State));
    }

    private void Apply(Ghostty.Core.Sponsor.Update.UpdateState state)
    {
        if (_taskbar is null || _hwnd == default) return;

        try
        {
            var (iconPath, desc) = state switch
            {
                Ghostty.Core.Sponsor.Update.UpdateState.UpdateAvailable => ("Assets/UpdateOverlay_Available.ico", "Update available"),
                Ghostty.Core.Sponsor.Update.UpdateState.RestartPending  => ("Assets/UpdateOverlay_Restart.ico", "Restart to finish updating"),
                Ghostty.Core.Sponsor.Update.UpdateState.Error            => ("Assets/UpdateOverlay_Error.ico", "Update error"),
                _ => (null, null),
            };

            if (iconPath is null)
            {
                _taskbar.SetOverlayIcon(_hwnd, default(HICON), null);
            }
            else
            {
                var full = System.IO.Path.Combine(AppContext.BaseDirectory, "Sponsor", "Update", iconPath);
                // LoadImage with IMAGE_ICON, 16x16, LR_LOADFROMFILE. Native handle
                // leak is acceptable at process scope; Windows cleans up on exit.
                HANDLE hRaw = Windows.Win32.PInvoke.LoadImage(
                    default(HINSTANCE), full,
                    GDI_IMAGE_TYPE.IMAGE_ICON,
                    16, 16,
                    IMAGE_FLAGS.LR_LOADFROMFILE);
                if (hRaw.IsNull)
                {
                    // Missing/unreadable .ico: keep whatever overlay is currently
                    // showing rather than accidentally clearing it with a null HICON.
                    Debug.WriteLine($"[sponsor/update] LoadImage failed for {full}");
                    return;
                }
                var hIcon = new HICON((IntPtr)hRaw);
                // desc is non-null here; ! suppresses nullable warning so the
                // compiler resolves to the CsWin32 string extension overload.
                _taskbar.SetOverlayIcon(_hwnd, hIcon, desc!);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[sponsor/update] taskbar overlay failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        if (_taskbar is not null && _hwnd != default)
        {
            try
            {
                _taskbar.SetOverlayIcon(_hwnd, default(HICON), null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[sponsor/update] clear overlay on dispose: {ex.Message}");
            }
        }
    }
}
