using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Sponsor.Auth;
using Xunit;

namespace Ghostty.Tests.Sponsor.Auth;

[SupportedOSPlatform("windows")]
public class DpapiJwtStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly byte[] _entropy = Encoding.UTF8.GetBytes("test-entropy-value");

    public DpapiJwtStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "wintty-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* tolerate locks */ }
    }

    [Fact]
    public async Task Roundtrip_WritesAndReadsSameBytes()
    {
        var store = new DpapiJwtStore(_tempDir, _entropy);
        var payload = Encoding.UTF8.GetBytes("header.payload.signature");

        await store.WriteAsync(payload, CancellationToken.None);
        var read = await store.ReadAsync(CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }

    [Fact]
    public async Task Read_WhenNoFile_ReturnsNull()
    {
        var store = new DpapiJwtStore(_tempDir, _entropy);

        var read = await store.ReadAsync(CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task Delete_RemovesFile()
    {
        var store = new DpapiJwtStore(_tempDir, _entropy);
        await store.WriteAsync(Encoding.UTF8.GetBytes("x"), CancellationToken.None);

        await store.DeleteAsync(CancellationToken.None);

        var read = await store.ReadAsync(CancellationToken.None);
        Assert.Null(read);
    }

    [Fact]
    public async Task Delete_WhenNoFile_IsNoOp()
    {
        var store = new DpapiJwtStore(_tempDir, _entropy);

        await store.DeleteAsync(CancellationToken.None);
        // no throw = pass
    }

    [Fact]
    public async Task Read_WithWrongEntropy_ReturnsNull()
    {
        var writer = new DpapiJwtStore(_tempDir, _entropy);
        await writer.WriteAsync(Encoding.UTF8.GetBytes("payload"), CancellationToken.None);

        var reader = new DpapiJwtStore(_tempDir, Encoding.UTF8.GetBytes("different-entropy"));

        var read = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(read);
    }

    [Fact]
    public async Task Write_OverwritesExisting()
    {
        var store = new DpapiJwtStore(_tempDir, _entropy);
        await store.WriteAsync(Encoding.UTF8.GetBytes("first"), CancellationToken.None);

        await store.WriteAsync(Encoding.UTF8.GetBytes("second"), CancellationToken.None);

        var read = await store.ReadAsync(CancellationToken.None);
        Assert.Equal("second", Encoding.UTF8.GetString(read!));
        Assert.False(File.Exists(Path.Combine(_tempDir, "auth.bin.partial")),
            "partial file should be renamed away after atomic write");
    }
}
