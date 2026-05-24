using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace Ghostty.Core.Version;

/// <summary>
/// Renders the wintty logo above CLI output using the kitty graphics
/// protocol. Detection is conservative: emit only when stdout is interactive
/// AND an env var positively identifies a known kitty-capable terminal. The
/// APC payload is supposed to be silently ignored by terminals that don't
/// recognize it, but some older terminals print the base64 chunks as garbage
/// on screen, so a false positive is much worse than a false negative.
///
/// The embedded resource is a copy of images/icons/icon_128.png. Keep it in
/// sync when the app icon changes (the file lives alongside the C# code so
/// MSBuild can embed it without resolving paths outside the project).
/// </summary>
public static class KittyLogo
{
    private const string ResourceName = "Ghostty.Core.Branding.wintty_logo.png";

    /// <summary>
    /// True when stdout is interactive and the env identifies a terminal we
    /// have positive reason to believe supports kitty graphics.
    /// </summary>
    public static bool Supported()
    {
        if (Console.IsOutputRedirected) return false;

        // Wintty sets TERM_PROGRAM=wintty (and upstream Ghostty sets =ghostty)
        // when spawning a shell. Accept both so already-running shells from
        // pre-rebrand binaries still get the logo. WezTerm uses the same var.
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        if (termProgram is "wintty" or "ghostty" or "WezTerm") return true;

        // Real kitty sets KITTY_WINDOW_ID; nothing else does.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")))
            return true;

        // TERM=xterm-kitty / kitty-direct.
        var term = Environment.GetEnvironmentVariable("TERM");
        if (term != null && term.Contains("kitty", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Append the kitty graphics escape sequence for the logo, followed by
    /// enough newlines to move the cursor below the rendered image. Caller
    /// is responsible for checking <see cref="Supported"/> first.
    /// </summary>
    public static void Render(StringBuilder sb)
    {
        var png = ReadLogoBytes();
        var encoded = Convert.ToBase64String(png);

        // Render in a fixed 16-col x 8-row cell area. Predictable size across
        // fonts, and tells us exactly how many newlines we need to pad afterwards.
        const int cols = 16;
        const int rows = 8;

        // 4096 chars is the kitty-recommended chunk size.
        const int chunkSize = 4096;
        var offset = 0;
        while (offset < encoded.Length)
        {
            var end = Math.Min(offset + chunkSize, encoded.Length);
            var more = end < encoded.Length ? '1' : '0';
            var chunk = encoded.AsSpan(offset, end - offset);

            if (offset == 0)
            {
                // f=100: PNG. a=T: transmit and display. t=d: direct (inline)
                // data. c/r: cell area. m: more chunks follow.
                sb.Append("\x1b_Gf=100,a=T,t=d,c=").Append(cols)
                  .Append(",r=").Append(rows)
                  .Append(",m=").Append(more).Append(';');
            }
            else
            {
                sb.Append("\x1b_Gm=").Append(more).Append(';');
            }
            sb.Append(chunk);
            sb.Append("\x1b\\");
            offset = end;
        }

        // Kitty advances the cursor by the image's row height after rendering
        // (default a=T behavior). Just one blank line for breathing room
        // between the logo and the version text.
        sb.Append('\n');
    }

    private static byte[] ReadLogoBytes()
    {
        var asm = typeof(KittyLogo).Assembly;
        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"missing embedded resource: {ResourceName}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
