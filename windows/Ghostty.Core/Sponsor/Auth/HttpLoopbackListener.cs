using System;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// One-shot loopback HTTP server for OAuth callback capture. Binds an
/// ephemeral port on <c>127.0.0.1</c> and responds to the first
/// <c>/cb</c> request with a confirmation page, then exits. Non-<c>/cb</c>
/// paths return 404 and listening continues until <c>/cb</c> arrives or
/// cancellation fires.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HttpLoopbackListener : ILoopbackListener
{
    private HttpListener? _listener;
    private int _port;
    private bool _started;

    private static readonly byte[] ConfirmPage = Encoding.UTF8.GetBytes(
        "<!doctype html><html><body style='font-family:sans-serif;padding:2em'>"
        + "<h1>You can close this window.</h1>"
        + "<p>wintty is finishing sign-in.</p></body></html>");

    public int Port =>
        _started
            ? _port
            : throw new InvalidOperationException("listener not started");

    public void Start()
    {
        if (_started) throw new InvalidOperationException("already started");

        // Bind port 0 first via TcpListener to claim an ephemeral port,
        // then hand it to HttpListener. HttpListener can't bind port 0
        // directly on Windows.
        var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        _port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _started = true;
    }

    public async Task<LoopbackResult> AwaitCallbackAsync(CancellationToken ct)
    {
        if (_listener is null || !_started)
            throw new InvalidOperationException("listener not started");

        using var ctr = ct.Register(() =>
        {
            // Stop may race Dispose when the caller cancels just as the
            // listener is being torn down. Only ObjectDisposedException
            // is expected on that path; everything else bubbles up.
            try { _listener.Stop(); }
            catch (ObjectDisposedException) { }
        });

        const int maxInvalidRequests = 10;
        int invalidCount = 0;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }

            var req = ctx.Request;
            if (req.Url?.AbsolutePath != "/cb")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                invalidCount++;
                if (invalidCount >= maxInvalidRequests)
                {
                    // Bounds a local process flooding the port with junk
                    // requests. The legitimate OAuth flow makes exactly one
                    // call to /cb; anything else is either a broken client
                    // or an attack and we shouldn't keep the listener open.
                    return new LoopbackResult(null, null, "too many invalid callbacks");
                }
                continue;
            }

            var (token, nonce, error) = ParseQuery(req.Url.Query);

            await WriteConfirmationAsync(ctx, ct).ConfigureAwait(false);
            return new LoopbackResult(token, nonce, error);
        }

        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("unreachable");
    }

    private static (string? token, string? nonce, string? error) ParseQuery(string query)
    {
        string? t = null, n = null, e = null;
        if (string.IsNullOrEmpty(query)) return (t, n, e);
        var qs = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = Uri.UnescapeDataString(pair[..idx]);
            var val = Uri.UnescapeDataString(pair[(idx + 1)..]);
            if (key == "token") t = val;
            else if (key == "nonce") n = val;
            else if (key == "error") e = val;
        }
        return (t, n, e);
    }

    private static async Task WriteConfirmationAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = ConfirmPage.Length;
        await ctx.Response.OutputStream.WriteAsync(ConfirmPage, ct).ConfigureAwait(false);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        // Close is idempotent for the "already stopped" case, but can
        // still surface ObjectDisposedException when racing a second
        // Dispose call. Narrow the swallow accordingly so real bugs
        // (InvalidOperationException from teardown paths) surface.
        try { _listener?.Close(); }
        catch (ObjectDisposedException) { }
        _listener = null;
        _started = false;
    }
}
