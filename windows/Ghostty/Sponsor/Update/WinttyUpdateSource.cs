using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Ghostty.Core.Sponsor.Update;
using NuGet.Versioning;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Ghostty.Sponsor.Update;

/// <summary>
/// Velopack <see cref="IUpdateSource"/> that authenticates to
/// api.wintty.io with a bearer JWT, fetches the release manifest, and
/// surfaces it as a <see cref="VelopackAssetFeed"/>. Download path is
/// stubbed pending Task 16.
/// <para>
/// Manifest HTTP logic lives in <see cref="WinttyManifestClient"/>
/// (Core) so it is unit-testable without a Velopack project reference.
/// </para>
/// </summary>
internal sealed class WinttyUpdateSource : IUpdateSource
{
    private readonly WinttyManifestClient _manifest;

    public WinttyUpdateSource(
        HttpClient client,
        ISponsorTokenProvider tokens,
        string channel,
        Uri apiBase)
    {
        // WinttyManifestClient validates its own args; we forward unchanged.
        _manifest = new WinttyManifestClient(client, tokens, channel, apiBase);
    }

    /// <summary>
    /// Test entry point: bypasses Velopack's pipeline and returns the
    /// parsed manifest entries directly. Used by
    /// <c>WinttyManifestClientTests</c> to verify HTTP + auth plumbing.
    /// </summary>
    internal Task<IReadOnlyList<VelopackReleaseEntry>> TestFetchManifestAsync(
        CancellationToken ct)
        => _manifest.FetchManifestAsync(ct);

    // ---------------------------------------------------------------
    // IUpdateSource - exact 0.0.1298 signatures
    // GetReleaseFeed: fetches manifest via WinttyManifestClient,
    // maps our Core VelopackReleaseEntry POCOs to Velopack's native
    // VelopackAsset objects, and wraps them in a VelopackAssetFeed.
    // ---------------------------------------------------------------

    public async Task<VelopackAssetFeed> GetReleaseFeed(
        IVelopackLogger logger,
        string? appId,
        string channel,
        Guid? stagingId = null,
        VelopackAsset? latestLocalRelease = null)
    {
        // WinttyManifestClient handles bearer auth, 401/403/5xx taxonomy,
        // JSON parse failures. Any UpdateCheckException propagates up to
        // VelopackManagerAdapter / the driver.
        var entries = await _manifest.FetchManifestAsync(CancellationToken.None)
            .ConfigureAwait(false);

        var assets = new VelopackAsset[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            assets[i] = new VelopackAsset
            {
                Version = NuGetVersion.Parse(e.Version),
                Type = Enum.TryParse<VelopackAssetType>(e.Type, ignoreCase: true, out var t)
                    ? t
                    : VelopackAssetType.Full,
                FileName = e.FileName,
                Size = e.Size,
                SHA1 = e.SHA1,
            };
        }

        return new VelopackAssetFeed { Assets = assets };
    }

    public Task DownloadReleaseEntry(
        IVelopackLogger logger,
        VelopackAsset releaseEntry,
        string localFile,
        Action<int> progress,
        CancellationToken cancelToken)
    {
        var fileName = releaseEntry.FileName
            ?? throw new Ghostty.Core.Sponsor.Update.UpdateCheckException(
                Ghostty.Core.Sponsor.Update.UpdateErrorKind.ManifestInvalid,
                "asset missing FileName");
        return _manifest.DownloadReleaseAsync(fileName, localFile, progress, cancelToken);
    }
}
