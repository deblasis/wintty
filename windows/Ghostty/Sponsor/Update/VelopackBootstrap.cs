using System;
using Velopack;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Thin wrapper around <see cref="VelopackApp.Build"/> that runs before
/// WinUI 3 starts. Velopack registers --veloapp-* argument handlers
/// here (install, uninstall, first-run, restart-after-update). Always
/// returns to the caller when the current process is NOT a Velopack
/// utility invocation; otherwise Velopack does its work and exits the
/// process itself.
/// </summary>
internal static class VelopackBootstrap
{
    public static int Run(string[] args, Func<int> continueStartup)
    {
        VelopackApp.Build()
            // Hook points reserved for D.2.5 / D.3; no-op today.
            .OnFirstRun(_ => { })
            .OnAfterUpdateFastCallback(_ => { })
            .OnBeforeUpdateFastCallback(_ => { })
            .SetArgs(args)
            .Run();

        return continueStartup();
    }
}
