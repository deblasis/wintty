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
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.NotNull(backend.LastWrite);
        var written = Assert.Single(backend.LastWrite!);
        Assert.Equal(ClipboardMime.TextPlain, written.Mime);
        Assert.Equal("hello", written.Text);
    }

    [Fact]
    public async Task HandleWriteAsync_TextHtmlOnly_WritesHtml()
    {
        var (svc, backend, _) = Make();
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextHtml, "<b>hi</b>") };

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
            ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"),
            ClipboardPayload.FromText(ClipboardMime.TextHtml, "<b>hello</b>"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: false);

        Assert.Equal(1, backend.WriteCallCount);
        Assert.NotNull(backend.LastWrite);
        Assert.Equal(2, backend.LastWrite!.Count);
        Assert.Contains(backend.LastWrite, p => p.Mime == ClipboardMime.TextPlain && p.Text == "hello");
        Assert.Contains(backend.LastWrite, p => p.Mime == ClipboardMime.TextHtml && p.Text == "<b>hello</b>");
    }

    [Fact]
    public async Task HandleWriteAsync_UnknownMime_SkippedSilently()
    {
        var (svc, backend, _) = Make();
        var payloads = new[]
        {
            ClipboardPayload.FromText(ClipboardMime.TextPlain, "kept"),
            ClipboardPayload.FromText("application/x-something", "dropped"),
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
        var payloads = new[] { ClipboardPayload.FromText("image/png", "binary blob") };

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
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello") };

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
            ClipboardPayload.FromText(ClipboardMime.TextPlain, "preview text"),
            ClipboardPayload.FromText(ClipboardMime.TextHtml, "<b>preview text</b>"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal("preview text", call.Snapshot.PreviewText);
        Assert.Equal(ClipboardConfirmRequest.Osc52Write, call.Request);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_UserAccepts_WritesPayload()
    {
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, "ok") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        Assert.Equal(1, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_UserDeclines_DoesNotWrite()
    {
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(false);
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, "nope") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        Assert.Equal(0, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_NoTextPlainEntry_StillPrompts()
    {
        // This used to drop the write outright, on the grounds that there
        // was no text/plain to preview. That silently discarded an
        // html-only write: the user was never asked, and nothing was
        // written. The dialog picks its own preview now, so the write is
        // prompted for like any other.
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextHtml, "<b>html only</b>") };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal(new[] { ClipboardMime.TextHtml }, call.Snapshot.Available);
        Assert.Equal(1, backend.WriteCallCount);
    }

    [Fact]
    public async Task HandleWriteAsync_ConfirmTrue_PromptCarriesEveryRepresentation()
    {
        // Allow grants the whole write, so the prompt has to describe the
        // whole write and not just the entry it happens to preview.
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[]
        {
            ClipboardPayload.FromText(ClipboardMime.TextPlain, "plain"),
            ClipboardPayload.FromText(ClipboardMime.TextHtml, "<b>plain</b>"),
        };

        await svc.HandleWriteAsync(ClipboardKind.Standard, payloads, confirm: true);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal(2, call.Snapshot.Contents.Count);
        Assert.Equal(new[] { ClipboardMime.TextPlain, ClipboardMime.TextHtml }, call.Snapshot.Available);
    }

    [Fact]
    public async Task HandleWriteAsync_PrimaryKind_IsANoOp()
    {
        // Windows has no primary selection. Core rejects a Kitty write to
        // one before any prompt; this is the belt to that braces.
        var (svc, backend, confirmer) = Make();
        confirmer.EnqueueResponse(true);
        var payloads = new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, "nope") };

        await svc.HandleWriteAsync(ClipboardKind.Primary, payloads, confirm: false);

        Assert.Equal(0, backend.WriteCallCount);
        Assert.Empty(confirmer.Calls);
    }

    // Confirm path (libghostty -> dialog -> response)

    private static ClipboardConfirmSnapshot TextSnapshot(string text, bool canRemember = false) =>
        new(
            new[] { ClipboardPayload.FromText(ClipboardMime.TextPlain, text) },
            new[] { ClipboardMime.TextPlain },
            Name: null,
            CanRemember: canRemember);

    [Fact]
    public async Task HandleConfirmAsync_Paste_AsksConfirmerWithPasteRequest()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(true);

        await svc.HandleConfirmAsync(TextSnapshot("dangerous text"), ClipboardConfirmRequest.Paste);

        var call = Assert.Single(confirmer.Calls);
        Assert.Equal("dangerous text", call.Snapshot.PreviewText);
        Assert.Equal(ClipboardConfirmRequest.Paste, call.Request);
    }

    [Theory]
    [InlineData(ClipboardConfirmRequest.Osc52Read)]
    [InlineData(ClipboardConfirmRequest.Osc52Write)]
    [InlineData(ClipboardConfirmRequest.KittyRead)]
    [InlineData(ClipboardConfirmRequest.KittyWrite)]
    public async Task HandleConfirmAsync_PassesTheRequestKindThrough(ClipboardConfirmRequest request)
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(false);

        await svc.HandleConfirmAsync(TextSnapshot("contents"), request);

        Assert.Equal(request, Assert.Single(confirmer.Calls).Request);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HandleConfirmAsync_ReturnsConfirmerDecision(bool decision)
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(decision);

        var result = await svc.HandleConfirmAsync(TextSnapshot("text"), ClipboardConfirmRequest.Paste);

        Assert.Equal(decision, result.Accepted);
    }

    [Fact]
    public async Task HandleConfirmAsync_RememberIsCarriedBack()
    {
        var (svc, _, confirmer) = Make();
        confirmer.EnqueueResponse(ClipboardConfirmResult.Allow(remember: true));

        var result = await svc.HandleConfirmAsync(
            TextSnapshot("text", canRemember: true), ClipboardConfirmRequest.KittyRead);

        Assert.True(result.Accepted);
        Assert.True(result.Remember);
    }

    [Fact]
    public async Task HandleConfirmAsync_DeniedByDefaultWhenConfirmerHasNoAnswer()
    {
        // The fake runs its queue dry here on purpose: the production
        // confirmer returns Denied on every failure path, and a service that
        // turned a missing answer into an approval would be the worst
        // possible bug in this file.
        var (svc, _, _) = Make();

        var result = await svc.HandleConfirmAsync(TextSnapshot("text"), ClipboardConfirmRequest.Paste);

        Assert.False(result.Accepted);
        Assert.False(result.Remember);
    }
}
