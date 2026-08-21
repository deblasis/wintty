using System;
using System.Text;
using Ghostty.Core.Version;

namespace Ghostty.Cli;

/// <summary>
/// Handlers for <c>+</c>-prefixed CLI actions intercepted before the
/// libghostty CLI dispatcher in <see cref="Program"/>. Wintty is a
/// GUI-subsystem binary, so the console these write to is the one
/// <c>Program.AttachToParentConsole</c> borrows from the launching
/// terminal.
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

        // Paint the wintty logo above the version text when the receiving
        // terminal is known to speak the kitty graphics protocol. Silent
        // no-op in pipes and unknown terminals.
        if (KittyLogo.Supported())
        {
            var sb = new StringBuilder();
            KittyLogo.Render(sb);
            sb.Append(output);
            output = sb.ToString();
        }

        Console.Out.Write(output);
        Console.Out.Flush();
        return 0;
    }
}
