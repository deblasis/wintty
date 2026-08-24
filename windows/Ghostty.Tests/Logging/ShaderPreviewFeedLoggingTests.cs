using System;
using System.Threading;
using System.Threading.Tasks;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Ghostty.Core.Settings;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class ShaderPreviewFeedLoggingTests
{
    private static Task NoDelay(int milliseconds, CancellationToken ct) => Task.CompletedTask;

    [Fact]
    public void SinkThatThrows_EmitsWarningWithFeedStoppedEventIdAndTheException()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        using var feed = new ShaderPreviewFeed(
            (ReadOnlySpan<byte> _) => throw new InvalidOperationException("injected"),
            factory.CreateLogger<ShaderPreviewFeed>(),
            NoDelay);
        feed.Start();

        var entry = Assert.Single(capture.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(LogEvents.ShaderPreview.FeedStopped, entry.EventId.Id);
        // The exception object, not just its Message: a feed that died takes
        // the whole preview with it, and the stack is the only clue why.
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public void FeedLogsUnderItsOwnCategory_NotABorrowedOne()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(capture));

        using var feed = new ShaderPreviewFeed(
            (ReadOnlySpan<byte> _) => throw new InvalidOperationException("injected"),
            factory.CreateLogger<ShaderPreviewFeed>(),
            NoDelay);
        feed.Start();

        // The point of the category: raising SettingsConfigWriter to Debug to
        // chase a config bug must not also turn on shader preview noise.
        var entry = Assert.Single(capture.Entries);
        Assert.Equal("Ghostty.Core.Settings.ShaderPreviewFeed", entry.Category);
    }
}
