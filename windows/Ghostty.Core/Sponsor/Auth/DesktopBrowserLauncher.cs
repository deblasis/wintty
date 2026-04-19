using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Opens URLs in the user's default browser via Shell Execute. The
/// caller (<c>OAuthTokenProvider</c>) does not rely on this to succeed;
/// the loopback listener's timeout is the authoritative "did anything
/// happen" signal.
/// </summary>
internal sealed class DesktopBrowserLauncher : IBrowserLauncher
{
    private readonly ILogger<DesktopBrowserLauncher> _logger;

    public DesktopBrowserLauncher(ILogger<DesktopBrowserLauncher> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public void Open(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url.ToString(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or System.IO.FileNotFoundException
                                      or InvalidOperationException
                                      or ObjectDisposedException)
        {
            // ShellExecute can fail if no default browser is registered,
            // the URL scheme is blocked by policy, or the shell denies
            // the launch. Non-fatal: loopback timeout handles the
            // "user never completed" case identically.
            _logger.LogWarning(ex, "[sponsor/auth] failed to launch browser for {Url}", url);
        }
    }
}
