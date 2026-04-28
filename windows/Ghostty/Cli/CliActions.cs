using System;
using Ghostty.Core.Version;

namespace Ghostty.Cli;

/// <summary>
/// Handlers for <c>+</c>-prefixed CLI actions intercepted before the
/// libghostty CLI dispatcher in <see cref="Program"/>. Console
/// inheritance is provided by Wintty.exe being a console-subsystem app
/// (see Program.cs comments) - no AttachConsole plumbing is needed.
/// </summary>
internal static class CliActions
{
    /// <summary>Render version info to stdout. Returns the process
    /// exit code the caller should pass to <see cref="Environment.Exit"/>.</summary>
    public static int PrintVersion()
    {
        var info = VersionRenderer.Build();
        var output = Console.IsOutputRedirected
            ? VersionRenderer.RenderPlain(info)
            : VersionRenderer.RenderAnsi(info);
        Console.Out.Write(output);
        Console.Out.Flush();
        return 0;
    }
}
