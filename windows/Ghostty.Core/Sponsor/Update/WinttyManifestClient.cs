using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;

namespace Ghostty.Core.Sponsor.Update;

/// <summary>
/// Velopack-free HTTP client for the api.wintty.io manifest endpoint.
/// Handles bearer auth injection, HTTP-error taxonomy, and JSON
/// parsing. The shell's <c>WinttyUpdateSource</c> delegates its
/// Velopack <c>IUpdateSource</c> manifest path here and maps the
/// result to <c>VelopackAssetFeed</c>. This type lives in Core so
/// Ghostty.Tests can exercise it without a WinAppSDK project reference.
/// </summary>
internal sealed class WinttyManifestClient
{
    private readonly HttpClient _client;
    private readonly ISponsorTokenProvider _tokens;
    private readonly string _channel;
    private readonly Uri _apiBase;

    public WinttyManifestClient(
        HttpClient client,
        ISponsorTokenProvider tokens,
        string channel,
        Uri apiBase)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(apiBase);

        _client = client;
        _tokens = tokens;
        _channel = channel;
        _apiBase = apiBase;
    }

    /// <summary>
    /// Fetches and parses the release manifest from
    /// <c>{apiBase}/manifest/{channel}</c>. Throws
    /// <see cref="UpdateCheckException"/> on any auth, network, or
    /// parse failure.
    /// </summary>
    public async Task<IReadOnlyList<VelopackReleaseEntry>> FetchManifestAsync(
        CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false)
            ?? throw new UpdateCheckException(UpdateErrorKind.NoToken, "no JWT available");

        // Uri ctor with relative path: new Uri(base, "/manifest/channel") correctly
        // replaces any path in apiBase. The leading slash is intentional.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(_apiBase, $"/manifest/{_channel}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateErrorKind.Offline, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // SendAsync timed out internally (HttpClient.Timeout exceeded)
            // rather than the caller cancelling via ct.
            throw new UpdateCheckException(UpdateErrorKind.Offline, "request timed out", ex);
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            if (status == 401)
            {
                _tokens.Invalidate();
                throw new UpdateCheckException(UpdateErrorKind.AuthExpired, "401 Unauthorized");
            }

            if (status == 403)
                throw new UpdateCheckException(UpdateErrorKind.NotEntitled, "403 Forbidden");

            if (status >= 500 || !response.IsSuccessStatusCode)
                throw new UpdateCheckException(UpdateErrorKind.ServerError, $"HTTP {status}");

            string json = await response.Content
                .ReadAsStringAsync(ct)
                .ConfigureAwait(false);

            try
            {
                // Source-generated context: AOT/trim-safe, no reflection fallback.
                var entries = JsonSerializer.Deserialize(
                    json,
                    VelopackManifestJsonContext.Default.ListVelopackReleaseEntry);
                if (entries is null || entries.Count == 0)
                    throw new UpdateCheckException(
                        UpdateErrorKind.ManifestInvalid, "manifest was empty or null");
                return entries;
            }
            catch (JsonException ex)
            {
                throw new UpdateCheckException(
                    UpdateErrorKind.ManifestInvalid, ex.Message, ex);
            }
        }
    }

    /// <summary>
    /// Download a release NUPKG. The Worker's /releases/ endpoint 302s to a
    /// presigned R2 URL; R2 rejects extra headers, so we manually follow the
    /// redirect and strip the Bearer on the second hop. Streams the body to
    /// <paramref name="localPath"/> with 1%-coalesced progress and a final
    /// 100 emit.
    /// </summary>
    public async Task DownloadReleaseAsync(
        string fileName,
        string localPath,
        Action<int> progress,
        CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false)
            ?? throw new UpdateCheckException(UpdateErrorKind.NoToken, "no JWT");

        // Hop 1: Worker with Bearer; expect a 302.
        using var hop1 = new HttpRequestMessage(
            HttpMethod.Get, new Uri(_apiBase, $"/releases/{_channel}/{fileName}"));
        hop1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage hop1Response;
        try
        {
            hop1Response = await _client.SendAsync(
                hop1, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateCheckException(UpdateErrorKind.Offline, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new UpdateCheckException(UpdateErrorKind.Offline, "timeout", ex);
        }

        using (hop1Response)
        {
            if ((int)hop1Response.StatusCode == 401)
            {
                _tokens.Invalidate();
                throw new UpdateCheckException(UpdateErrorKind.AuthExpired, "401");
            }
            if ((int)hop1Response.StatusCode == 403)
                throw new UpdateCheckException(UpdateErrorKind.NotEntitled, "403");
            if ((int)hop1Response.StatusCode >= 500)
                throw new UpdateCheckException(UpdateErrorKind.ServerError, $"{(int)hop1Response.StatusCode}");

            if ((int)hop1Response.StatusCode is 301 or 302 or 307)
            {
                var location = hop1Response.Headers.Location
                    ?? throw new UpdateCheckException(UpdateErrorKind.ServerError, "302 without Location");

                // Hop 2: no Bearer. R2 presigned URL carries its own signature.
                using var hop2Request = new HttpRequestMessage(HttpMethod.Get, location);
                HttpResponseMessage hop2Response;
                try
                {
                    hop2Response = await _client.SendAsync(
                        hop2Request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    throw new UpdateCheckException(UpdateErrorKind.Offline, ex.Message, ex);
                }

                using (hop2Response)
                {
                    if (!hop2Response.IsSuccessStatusCode)
                        throw new UpdateCheckException(
                            UpdateErrorKind.ServerError, $"R2 {(int)hop2Response.StatusCode}");
                    await StreamBodyToFileAsync(hop2Response, localPath, progress, ct).ConfigureAwait(false);
                }
            }
            else if (hop1Response.IsSuccessStatusCode)
            {
                // Defensive: if Worker ever serves bytes directly (no redirect),
                // stream hop1's body. Bearer was already included.
                await StreamBodyToFileAsync(hop1Response, localPath, progress, ct).ConfigureAwait(false);
            }
            else
            {
                throw new UpdateCheckException(
                    UpdateErrorKind.ServerError, $"{(int)hop1Response.StatusCode}");
            }
        }
    }

    private static async Task StreamBodyToFileAsync(
        HttpResponseMessage response,
        string localPath,
        Action<int> progress,
        CancellationToken ct)
    {
        // Stream to a .partial sibling and rename on success. A mid-stream
        // failure (network drop, disk full, cancellation) would otherwise
        // leave a truncated file at localPath that the next Velopack
        // verification step would happily pick up and fail to unpack.
        var partialPath = localPath + ".partial";
        var totalBytes = response.Content.Headers.ContentLength;
        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(partialPath))
            {
                var buffer = new byte[81920];
                long read = 0;
                int lastEmitted = -1;
                int n;
                while ((n = await src.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        var percent = (int)(read * 100 / totalBytes.Value);
                        if (percent > lastEmitted)
                        {
                            progress(percent);
                            lastEmitted = percent;
                        }
                    }
                }
                if (lastEmitted < 100) progress(100);
            }

            // Atomic replace: File.Move with overwrite is a single NTFS
            // rename so a crash between Close and Move can never leave
            // localPath in a torn state.
            File.Move(partialPath, localPath, overwrite: true);
        }
        catch
        {
            // Best-effort cleanup. Leaving a .partial behind is benign
            // (gets overwritten on retry); crashing inside a crash path
            // would hide the original failure.
            try { if (File.Exists(partialPath)) File.Delete(partialPath); }
            catch { }
            throw;
        }
    }
}
