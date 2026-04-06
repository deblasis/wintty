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
        _window = new MainWindow();
        _window.Activate();
    }
}
