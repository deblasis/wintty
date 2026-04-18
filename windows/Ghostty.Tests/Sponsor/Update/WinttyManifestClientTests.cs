using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Ghostty.Core.Sponsor.Update;
using Xunit;

namespace Ghostty.Tests.Sponsor.Update;

public class WinttyManifestClientTests
{
    // ---------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------

    internal sealed class StubTokenProvider : ISponsorTokenProvider
    {
        private string? _token;
        public int InvalidateCount { get; private set; }

        public StubTokenProvider(string? initial) { _token = initial; }

        public Task<string?> GetTokenAsync(CancellationToken ct = default)
            => Task.FromResult(_token);

        public void Invalidate()
        {
            _token = null;
            InvalidateCount++;
        }

        public event EventHandler? TokenInvalidated;
        public void RaiseInvalidated() => TokenInvalidated?.Invoke(this, EventArgs.Empty);
    }

    internal sealed class StubHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = default!;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private const string StableManifestJson = """
        [
          {
            "Version": "1.4.2",
            "Type": "Full",
            "FileName": "Ghostty-1.4.2-stable-full.nupkg",
            "Size": 37635794,
            "SHA1": "abc123"
          }
        ]
        """;

    private static (WinttyManifestClient client, StubHandler handler, StubTokenProvider tokens)
        Make(string? token, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler { Respond = respond };
        var httpClient = new HttpClient(handler);
        var tokens = new StubTokenProvider(token);
        var client = new WinttyManifestClient(
            httpClient, tokens,
            channel: "stable",
            apiBase: new Uri("https://api.wintty.io"));
        return (client, handler, tokens);
    }

    // ---------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------

    [Fact]
    public async Task FetchManifest_SendsBearerAuth()
    {
        var (client, handler, _) = Make(
            token: "eyJ.abc.def",
            respond: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    StableManifestJson, Encoding.UTF8, "application/json"),
            });

        var entries = await client.FetchManifestAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal(
            "https://api.wintty.io/manifest/stable",
            req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("eyJ.abc.def", req.Headers.Authorization.Parameter);
        Assert.Single(entries);
        Assert.Equal("1.4.2", entries[0].Version);
        Assert.Equal("Full", entries[0].Type);
        Assert.Equal("Ghostty-1.4.2-stable-full.nupkg", entries[0].FileName);
        Assert.Equal(37635794L, entries[0].Size);
        Assert.Equal("abc123", entries[0].SHA1);
    }

    // ---------------------------------------------------------------
    // Auth errors
    // ---------------------------------------------------------------

    [Fact]
    public async Task FetchManifest_NoToken_ThrowsNoToken()
    {
        var (client, _, _) = Make(
            token: null,
            respond: _ => new HttpResponseMessage(HttpStatusCode.OK));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.NoToken, ex.Kind);
    }

    [Fact]
    public async Task FetchManifest_401_ThrowsAuthExpiredAndInvalidatesToken()
    {
        var (client, _, tokens) = Make(
            token: "stale-token",
            respond: _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.AuthExpired, ex.Kind);
        // Token must be invalidated so the next check can decide whether
        // to re-auth or surface the error to the user.
        Assert.Equal(1, tokens.InvalidateCount);
    }

    [Fact]
    public async Task FetchManifest_403_ThrowsNotEntitled()
    {
        var (client, _, _) = Make(
            token: "valid-token",
            respond: _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.NotEntitled, ex.Kind);
    }

    // ---------------------------------------------------------------
    // Server / network errors
    // ---------------------------------------------------------------

    [Fact]
    public async Task FetchManifest_500_ThrowsServerError()
    {
        var (client, _, _) = Make(
            token: "valid-token",
            respond: _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.ServerError, ex.Kind);
    }

    [Fact]
    public async Task FetchManifest_NetworkFailure_ThrowsOffline()
    {
        var handler = new StubHandler
        {
            Respond = _ => throw new HttpRequestException("network error"),
        };
        var tokens = new StubTokenProvider("valid-token");
        var client = new WinttyManifestClient(
            new HttpClient(handler), tokens,
            channel: "stable",
            apiBase: new Uri("https://api.wintty.io"));

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.Offline, ex.Kind);
    }

    // ---------------------------------------------------------------
    // Parse failure
    // ---------------------------------------------------------------

    [Fact]
    public async Task FetchManifest_InvalidJson_ThrowsManifestInvalid()
    {
        var (client, _, _) = Make(
            token: "valid-token",
            respond: _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "not-json", Encoding.UTF8, "application/json"),
            });

        var ex = await Assert.ThrowsAsync<UpdateCheckException>(
            () => client.FetchManifestAsync(CancellationToken.None));

        Assert.Equal(UpdateErrorKind.ManifestInvalid, ex.Kind);
    }

    // ---------------------------------------------------------------
    // Download path
    // ---------------------------------------------------------------

    [Fact]
    public async Task DownloadRelease_StreamFailure_CleansUpPartialFile()
    {
        // Serve a 200 whose body stream throws midway. Stream-to-file must
        // surface the failure AND leave no partial artifact at localPath;
        // the torn .partial would otherwise masquerade as a valid nupkg on
        // the next retry.
        var failingBody = new ThrowingStream(
            prefix: new byte[] { 0x50, 0x4B },
            throwAfter: 2);

        var step = 0;
        var handler = new StubHandler
        {
            Respond = _ =>
            {
                step++;
                if (step == 1)
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri("https://r2.cf.example.com/wintty/x.nupkg");
                    return redirect;
                }
                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(failingBody),
                };
                // ContentLength set large enough that we never emit 100 via the
                // progress coalescer before the stream throws.
                ok.Content.Headers.ContentLength = 1024;
                return ok;
            },
        };
        var tokens = new StubTokenProvider("eyJ.abc.def");
        var client = new WinttyManifestClient(
            new HttpClient(handler), tokens, "stable", new Uri("https://api.wintty.io"));

        var tmp = Path.Combine(Path.GetTempPath(), "wintty-test-" + Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                client.DownloadReleaseAsync("x.nupkg", tmp, _ => { }, CancellationToken.None));

            Assert.False(File.Exists(tmp), "torn localPath must not exist");
            Assert.False(File.Exists(tmp + ".partial"), ".partial sibling must be cleaned up");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            if (File.Exists(tmp + ".partial")) File.Delete(tmp + ".partial");
        }
    }

    private sealed class ThrowingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly int _throwAfter;
        private int _read;

        public ThrowingStream(byte[] prefix, int throwAfter)
        {
            _prefix = prefix;
            _throwAfter = throwAfter;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_read >= _throwAfter) throw new IOException("simulated mid-stream failure");
            var remain = _prefix.Length - _read;
            if (remain <= 0) throw new IOException("simulated mid-stream failure");
            var take = System.Math.Min(remain, count);
            System.Array.Copy(_prefix, _read, buffer, offset, take);
            _read += take;
            return take;
        }
    }

    [Fact]
    public async Task DownloadRelease_FollowsRedirect_AndStripsBearer()
    {
        var step = 0;
        var handler = new StubHandler
        {
            Respond = req =>
            {
                step++;
                if (step == 1)
                {
                    Assert.Equal("https://api.wintty.io/releases/stable/Ghostty-1.4.2-stable-full.nupkg",
                        req.RequestUri!.ToString());
                    Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
                    Assert.Equal("eyJ.abc.def", req.Headers.Authorization.Parameter);

                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new Uri(
                        "https://r2.cf.example.com/wintty/Ghostty-1.4.2-stable-full.nupkg?X-Amz-Signed=true");
                    return redirect;
                }
                Assert.Equal("https://r2.cf.example.com/wintty/Ghostty-1.4.2-stable-full.nupkg?X-Amz-Signed=true",
                    req.RequestUri!.ToString());
                Assert.Null(req.Headers.Authorization);  // Bearer stripped for the R2 hop

                var ok = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),  // "PK\3\4" ZIP magic
                };
                return ok;
            },
        };
        var tokens = new StubTokenProvider("eyJ.abc.def");
        var client = new WinttyManifestClient(
            new HttpClient(handler), tokens, "stable", new Uri("https://api.wintty.io"));

        var tmp = Path.Combine(Path.GetTempPath(), "wintty-test-" + Guid.NewGuid().ToString("N") + ".nupkg");
        try
        {
            var observed = new List<int>();
            await client.DownloadReleaseAsync(
                "Ghostty-1.4.2-stable-full.nupkg",
                tmp,
                p => observed.Add(p),
                CancellationToken.None);

            Assert.Equal(2, step);
            Assert.True(File.Exists(tmp));
            var bytes = await File.ReadAllBytesAsync(tmp);
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, bytes);
            Assert.Contains(100, observed);  // final 100 always emits
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
