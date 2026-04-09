using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Ghostty.Tests.Clipboard.Fakes;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// Pure-logic tests for ClipboardService. Uses real Ghostty.Core types
/// with hand-written fake backend and confirmer; no mocking framework,
/// no source-stubs.
/// </summary>
public sealed class ClipboardServiceTests
{
    private static (ClipboardService Service, FakeClipboardBackend Backend, FakeClipboardConfirmer Confirmer) Make()
    {
        var backend = new FakeClipboardBackend();
        var confirmer = new FakeClipboardConfirmer();
        return (new ClipboardService(backend, confirmer), backend, confirmer);
    }

    // Read path

    [Fact]
    public async Task HandleReadAsync_Standard_BackendHasText_ReturnsText()
    {
        var (svc, backend, _) = Make();
        backend.StoredText = "hello";

        var result = await svc.HandleReadAsync(ClipboardKind.Standard);

        Assert.Equal("hello", result);
    }

    [Fact]
    public async Task HandleReadAsync_Standard_BackendEmpty_ReturnsNull()
    {
        var (svc, backend, _) = Make();
        backend.StoredText = null;

        var result = await svc.HandleReadAsync(ClipboardKind.Standard);

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleReadAsync_Selection_AlwaysReturnsNull()
    {
        var (svc, backend, _) = Make();
        backend.StoredText = "this should never be returned";

        var result = await svc.HandleReadAsync(ClipboardKind.Selection);

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleReadAsync_BackendThrows_ReturnsNull()
    {
        var (svc, backend, _) = Make();
        backend.OnRead = () => throw new InvalidOperationException("clipboard locked");

        var result = await svc.HandleReadAsync(ClipboardKind.Standard);

        Assert.Null(result);
    }

    // Write path (no confirmation)

    [Fact]
    public async Task HandleWriteAsync_TextPlainOnly_WritesPlainText()
    {
        var (svc, backend, _) = Make();
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextPlain, "hello") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.NotNull(backend.LastWrite);
        var written = Assert.Single(backend.LastWrite!);
        Assert.Equal(ClipboardMime.TextPlain, written.Mime);
        Assert.Equal("hello", written.Data);
    }

    [Fact]
    public async Task HandleWriteAsync_TextHtmlOnly_WritesHtml()
    {
        var (svc, backend, _) = Make();
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextHtml, "<b>hi</b>") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.NotNull(backend.LastWrite);
        var written = Assert.Single(backend.LastWrite!);
        Assert.Equal(ClipboardMime.TextHtml, written.Mime);
    }

    [Fact]
    public async Task HandleWriteAsync_TextPlainAndHtml_WritesBothInOneCall()
    {
        // The mixed-format case: libghostty's `mixed` copy format sends
        // both text/plain and text/html in a single write. We must
        // forward both atomically (one backend call), so a Notepad
        // paste gets text and a Word paste gets HTML.
        var (svc, backend, _) = Make();
        var payloads = new[]
        {
            new ClipboardPayload(ClipboardMime.TextPlain, "hello"),
            new ClipboardPayload(ClipboardMime.TextHtml, "<b>hello</b>"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.Equal(1, backend.WriteCallCount);
        Assert.NotNull(backend.LastWrite);
        Assert.Equal(2, backend.LastWrite!.Count);
        Assert.Contains(backend.LastWrite, p => p.Mime == ClipboardMime.TextPlain && p.Data == "hello");
        Assert.Contains(backend.LastWrite, p => p.Mime == ClipboardMime.TextHtml && p.Data == "<b>hello</b>");
    }

    [Fact]
    public async Task HandleWriteAsync_UnknownMime_SkippedSilently()
    {
        var (svc, backend, _) = Make();
        var payloads = new[]
        {
            new ClipboardPayload(ClipboardMime.TextPlain, "kept"),
            new ClipboardPayload("application/x-something", "dropped"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.NotNull(backend.LastWrite);
        var written = Assert.Single(backend.LastWrite!);
        Assert.Equal(ClipboardMime.TextPlain, written.Mime);
    }

    [Fact]
    public async Task HandleWriteAsync_AllUnknownMimes_DoesNotCallBackend()
    {
        // Crucially: do NOT clear the clipboard by sending an empty
        // package. Stay quiet.
        var (svc, backend, _) = Make();
        var payloads = new[] { new ClipboardPayload("image/png", "binary blob") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.Equal(0, backend.WriteCallCount);
        Assert.Null(backend.LastWrite);
    }

    [Fact]
    public async Task HandleWriteAsync_EmptyPayloadList_DoesNotCallBackend()
    {
        var (svc, backend, _) = Make();

        await svc.HandleWriteAsync(ClipboardKind.Standard, Array.Empty<ClipboardPayload>(), confirm: false);

        Assert.Equal(0, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_Selection_DoesNotCallBackend()
    {
        var (svc, backend, _) = Make();
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextPlain, "hello") };

        await svc.HandleWriteAsync(ClipboardKind.Selection, payloads, confirm: false);

        Assert.Equal(0, backend.WriteCallCount);
    }

    // Write path with confirmation

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_AsksConfirmer_PreviewIsTextPlain()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[]
        {
            new ClipboardPayload(ClipboardMime.TextPlain, "preview text"),
            new ClipboardPayload(ClipboardMime.TextHtml, "<b>preview text</b>"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal("preview text", call.Preview);
        Assert.Equal(ClipboardConfirmRequest.Osc52Write, call.Request);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_UserAccepts_WritesPayload()
    {
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextPlain, "ok") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        Assert.Equal(1, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_UserDeclines_DoesNotWrite()
    {
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(false);
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextPlain, "nope") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        Assert.Equal(0, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_NoTextPlainEntry_DoesNotWrite()
    {
        // Without a text/plain payload there is nothing to show in the
        // confirmation preview, so the safe default is to drop the
        // write rather than show an empty dialog or HTML-only dialog.
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[] { new ClipboardPayload(ClipboardMime.TextHtml, "<b>html only</b>") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        Assert.Equal(0, backend.WriteCallCount);
        Assert.Empty(confirmer.Calls);
    }

    // Confirm path (libghostty -> dialog -> response)

    [Fact]
    public async Task HandleConfirmAsync_Paste_AsksConfirmerWithPasteRequest()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(true);

        await svc.HandleConfirmAsync("dangerous text", ClipboardConfirmRequest.Paste);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal("dangerous text", call.Preview);
        Assert.Equal(ClipboardConfirmRequest.Paste, call.Request);
    }

    [Fact]
    public async Task HandleConfirmAsync_Osc52Read_AsksConfirmerWithOsc52ReadRequest()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(false);

        await svc.HandleConfirmAsync("clipboard contents", ClipboardConfirmRequest.Osc52Read);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal(ClipboardConfirmRequest.Osc52Read, call.Request);
    }

    [Fact]
    public async Task HandleConfirmAsync_Osc52Write_AsksConfirmerWithOsc52WriteRequest()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(true);

        await svc.HandleConfirmAsync("incoming text", ClipboardConfirmRequest.Osc52Write);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal(ClipboardConfirmRequest.Osc52Write, call.Request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleConfirmAsync_ReturnsConfirmerDecision(bool decision)
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(decision);

        var result = await svc.HandleConfirmAsync("text", ClipboardConfirmRequest.Paste);

        Assert.Equal(decision, result);
    }
}
