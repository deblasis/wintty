using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Ghostty;

/// <summary>
/// Application entry point. Keeps a strong reference to the main window so
/// it is not collected while the message loop is running.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    static App()
    {
        // libghostty.dll lives in a `native/` subdirectory next to this
        // assembly so its filename (ghostty.dll) does not collide with our
        // own managed Ghostty.dll on case-insensitive filesystems. The
        // DllImport entries use "ghostty" so we resolve that name here.
        NativeLibrary.SetDllImportResolver(
            typeof(Interop.NativeMethods).Assembly,
            (name, assembly, path) =>
            {
                if (!string.Equals(name, "ghostty", StringComparison.OrdinalIgnoreCase))
                    return IntPtr.Zero;
                // Prefer AppContext.BaseDirectory: assembly.Location is empty
                // under single-file publish and Native AOT, which is where
                // this shell will eventually run.
                var baseDir = AppContext.BaseDirectory;
                if (string.IsNullOrEmpty(baseDir))
                    baseDir = Path.GetDirectoryName(assembly.Location) ?? string.Empty;
                var candidate = Path.Combine(baseDir, "native", "ghostty.dll");
                return NativeLibrary.Load(candidate);
            });
    }

    public App()
    {
        InitializeComponent();

        // Surface unhandled exceptions to stderr before the process dies.
        // Without this, a managed exception on the UI thread silently exits
        // with a non-descriptive code and we have nothing to debug from.
        // Stays enabled in Debug builds only -- in Release we want WER to
        // capture a real crash dump instead.
#if DEBUG
        UnhandledException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNHANDLED EXCEPTION on UI thread:");
                Console.Error.WriteLine(e.Exception.ToString());
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
            // Leave Handled=false so the runtime still tears the app down --
            // we just wanted to see the exception first.
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNHANDLED EXCEPTION (AppDomain):");
                Console.Error.WriteLine(e.ExceptionObject?.ToString() ?? "(null)");
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            try
            {
                Console.Error.WriteLine("[Ghostty] UNOBSERVED TASK EXCEPTION:");
                Console.Error.WriteLine(e.Exception.ToString());
                Console.Error.Flush();
            }
            catch { /* logging must not throw */ }
        };
#endif
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Set the explicit AppUserModelID. This MUST happen before
        // any shell interop call (jump list registration, taskbar
        // icon operations, toast notifications) — the Shell caches
        // the process-to-AUMID association on first use and reads
        // the jump list from a per-AUMID store. Without this, the
        // jump list silently no-ops on unpackaged exes.
        try
        {
            Ghostty.Interop.ShellInterop.SetCurrentProcessExplicitAppUserModelID(Ghostty.Core.AppIdentity.AumId);
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            // Visible in Release too — Trace goes to attached debuggers
            // and the process's default trace listeners, unlike Debug.
            System.Diagnostics.Trace.TraceWarning("Ghostty: failed to set AUMID: 0x{0:x8} {1}", ex.HResult, ex.Message);
            // Continue anyway; app still functions, just without jump list.
        }

        // Build the jump list once at startup. Rebuilds happen when
        // the profile list changes (TODO(config): hook a config event).
        try
        {
            // Environment.ProcessPath points to the apphost .exe, which
            // is what we want the shell to invoke from the jump list.
            // Assembly.GetEntryAssembly().Location returns the managed
            // .dll on single-file apphost layouts, so prefer ProcessPath.
            var exePath = System.Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
            {
                using var facade = new Ghostty.JumpList.CustomDestinationListFacade();
                var builder = new Ghostty.Core.JumpList.JumpListBuilder(
                    facade,
                    // TODO(config): profiles — swap for config-driven list
                    profilesProvider: () => System.Array.Empty<Ghostty.Core.JumpList.ProfileEntry>(),
                    exePath: exePath,
                    appId: Ghostty.Core.AppIdentity.AumId);
                builder.Build();
            }
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            System.Diagnostics.Trace.TraceWarning("Ghostty: failed to build jump list: 0x{0:x8} {1}", ex.HResult, ex.Message);
            // Jump list is nice-to-have; failure here does not block startup.
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
