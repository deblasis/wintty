using System;
using System.IO;

namespace Ghostty;

/// <summary>
/// Custom entry point that wraps the WinUI 3 startup with diagnostic
/// error capture. The XAML-generated Main is suppressed via
/// DISABLE_XAML_GENERATED_MAIN. This is temporary for debugging
/// NativeAOT startup crashes that produce no output.
/// </summary>
public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            Console.Error.WriteLine("[Ghostty] Program.Main entered");
            Console.Error.Flush();

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Console.Error.WriteLine("[Ghostty] ComWrappers initialized");
            Console.Error.Flush();

            Microsoft.UI.Xaml.Application.Start(p =>
            {
                Console.Error.WriteLine("[Ghostty] Application.Start callback entered");
                Console.Error.Flush();

                var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                System.Threading.SynchronizationContext.SetSynchronizationContext(context);

                Console.Error.WriteLine("[Ghostty] Creating App instance");
                Console.Error.Flush();

                new App();

                Console.Error.WriteLine("[Ghostty] App instance created");
                Console.Error.Flush();
            });

            return 0;
        }
        catch (Exception ex)
        {
            var msg = $"[Ghostty] FATAL: {ex}";
            Console.Error.WriteLine(msg);
            Console.Error.Flush();

            // Also write to a file next to the exe in case stderr is lost
            try
            {
                var logPath = Path.Combine(AppContext.BaseDirectory, "ghostty-crash.log");
                File.WriteAllText(logPath, msg);
            }
            catch { /* best effort */ }

            return 1;
        }
    }
}
