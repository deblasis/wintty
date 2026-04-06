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
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
