using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Velopack-free HTTP client for the api.wintty.io auth endpoints
/// (<c>/auth/refresh</c> and <c>/auth/revoke</c>). Mirrors
/// <see cref="Ghostty.Core.Sponsor.Update.WinttyManifestClient"/> in
/// style. All public methods throw <see cref="AuthException"/> on
/// failure; the <see cref="AuthErrorKind"/> drives retry policy in
/// <c>OAuthTokenProvider</c>.
/// </summary>
internal sealed class WinttyAuthClient
{
    private readonly HttpClient _http;
    private readonly Uri _apiBase;

    public WinttyAuthClient(HttpClient http, Uri apiBase)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiBase);
        _http = http;
        _apiBase = apiBase;
    }

    /// <summary>
    /// POSTs to <c>/auth/refresh</c> with the current bearer. Returns
    /// the new JWT string on success. Throws on any non-2xx, network
    /// failure, or malformed response body.
    /// </summary>
    public async Task<string> RefreshAsync(string currentToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentToken);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBase, "/auth/refresh"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException(AuthErrorKind.Network, "refresh: network failure", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AuthException(AuthErrorKind.Network, "refresh: timed out", ex);
        }

        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new AuthException(AuthErrorKind.Unauthorized, $"refresh: {(int)resp.StatusCode}");
            if ((int)resp.StatusCode >= 500)
                throw new AuthException(AuthErrorKind.ServerError, $"refresh: {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode)
                throw new AuthException(AuthErrorKind.ServerError, $"refresh: unexpected {(int)resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? newToken;
            try
            {
                using var doc = JsonDocument.Parse(body);
                newToken = doc.RootElement.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String
                    ? tok.GetString()
                    : null;
            }
            catch (JsonException ex)
            {
                throw new AuthException(AuthErrorKind.ServerError, "refresh: malformed JSON response", ex);
            }

            if (string.IsNullOrEmpty(newToken))
                throw new AuthException(AuthErrorKind.ServerError, "refresh: response missing 'token' field");

            return newToken;
        }
    }

    /// <summary>
    /// POSTs to <c>/auth/revoke</c> with the current bearer. No return
    /// value - success is a 2xx. Non-success throws so the caller can
    /// log; production callers (<c>OAuthTokenProvider.SignOutAsync</c>)
    /// swallow all exceptions and delete locally regardless.
    /// </summary>
    public async Task RevokeAsync(string currentToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(currentToken);

        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBase, "/auth/revoke"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException(AuthErrorKind.Network, "revoke: network failure", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AuthException(AuthErrorKind.Network, "revoke: timed out", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new AuthException(AuthErrorKind.ServerError, $"revoke: {(int)resp.StatusCode}");
        }
    }
}
