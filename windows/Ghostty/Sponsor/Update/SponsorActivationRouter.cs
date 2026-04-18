using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Dispatching;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Parses <c>wintty://update/...</c> URIs and dispatches the
/// corresponding action on <see cref="UpdateService"/>. The App's
/// activation path calls <see cref="Handle"/> after URI decode.
/// </summary>
internal sealed class SponsorActivationRouter
{
    private readonly UpdateService _service;
    private readonly DispatcherQueue _dispatcher;

    public SponsorActivationRouter(UpdateService service, DispatcherQueue dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Parse a query string like <c>?action=install-restart</c> from a
    /// protocol activation. Toast button args use the same dictionary.
    /// </summary>
    public void HandleArguments(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("action", out var action)) return;
        _dispatcher.TryEnqueue(() => Dispatch(action));
    }

    public void HandleUri(Uri uri)
    {
        if (!string.Equals(uri.Scheme, "wintty", StringComparison.OrdinalIgnoreCase)) return;
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string? k in q.AllKeys)
        {
            if (k is null) continue;
            dict[k] = q[k] ?? string.Empty;
        }
        HandleArguments(dict);
    }

    private void Dispatch(string action)
    {
        switch (action)
        {
            case "install-restart":
                _ = _service.ApplyAndRestartAsync();
                break;
            case "view-update":
                // D.3 will focus the pill/popover; D.1 just logs.
                Debug.WriteLine("[sponsor/update] router: view-update");
                break;
            default:
                // Raw action is attacker-controllable (any local process can
                // invoke wintty://update?action=...) so sanitize before
                // logging to prevent log-injection if this path is ever
                // routed to structured logging verbatim.
                Debug.WriteLine($"[sponsor/update] router: unknown action (len={action.Length})");
                break;
        }
    }
}
