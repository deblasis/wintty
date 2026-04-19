using System;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Abstraction over opening the default browser. Production impl uses
/// <c>Process.Start</c> with <c>UseShellExecute = true</c>; tests assert
/// the URL shape without spawning a browser.
/// </summary>
internal interface IBrowserLauncher
{
    /// <summary>
    /// Opens the URL in the user's default browser. Does not block
    /// waiting for the browser to close. MUST NOT throw; failures are
    /// logged by the impl and the caller relies on the loopback timeout
    /// to notice nothing arrived.
    /// </summary>
    void Open(Uri url);
}
