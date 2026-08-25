using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ghostty.Core.Clipboard;
using Ghostty.Tests.Clipboard.Fakes;
using Xunit;

namespace Ghostty.Tests.Clipboard;

/// <summary>
/// The Kitty clipboard read path (OSC 5522).
///
/// The distinctions under test here are the ones libghostty acts on:
/// Unsupported versus Empty decides what the mode 5522 report advertises,
/// and `available` versus `contents` decides what a caller learns about
/// representations it did not ask for. Both are easy to collapse and
/// neither collapse shows up as a crash.
/// </summary>
public sealed class ClipboardServiceKittyReadTests
{
    private static (ClipboardService Service, FakeClipboardBackend Backend, FakeClipboardConfirmer Confirmer) Make()
    {
        var backend = new FakeClipboardBackend();
        var confirmer = new FakeClipboardConfirmer();
        return (new ClipboardService(backend, confirmer), backend, confirmer);
    }

    private static readonly IReadOnlyList<string> NoFilter = Array.Empty<string>();

    [Theory]
    [InlineData(ClipboardKind.Selection)]
    [InlineData(ClipboardKind.Primary)]
    public async Task NonStandardClipboard_IsUnsupportedNotEmpty(ClipboardKind kind)
    {
        // Win32 has no PRIMARY-style buffer. Answering Empty would claim we
        // have one and it happens to be empty, and libghostty would then
        // advertise a selection clipboard through mode 5522.
        var (svc, backend, _) = Make();

        var outcome = await svc.HandleKittyReadAsync(kind, NoFilter, listing: false);

        Assert.Equal(ClipboardReadStatus.Unsupported, outcome.Status);
        Assert.Equal(0, backend.ReadCallCount);
    }

    [Fact]
    public async Task EmptyClipboard_IsEmpty()
    {
        var (svc, _, _) = Make();

        var outcome = await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: false);

        Assert.Equal(ClipboardReadStatus.Empty, outcome.Status);
    }

    [Fact]
    public async Task NoFilter_ServesEverything()
    {
        var (svc, backend, _) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextUriList, "file:///C:/a.txt"));
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"));

        var outcome = await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: false);

        Assert.Equal(ClipboardReadStatus.Ok, outcome.Status);
        Assert.Equal(2, outcome.Contents.Count);
        Assert.Equal(
            new[] { ClipboardMime.TextUriList, ClipboardMime.TextPlain },
            outcome.Available);
    }

    [Fact]
    public async Task Filter_NarrowsContentsButNotAvailable()
    {
        // The heart of it. A caller that asked only for text/plain still
        // gets told text/uri-list is on the clipboard; it just does not get
        // handed the paths.
        var (svc, backend, _) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextUriList, "file:///C:/a.txt"));
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"));

        var outcome = await svc.HandleKittyReadAsync(
            ClipboardKind.Standard, new[] { ClipboardMime.TextPlain }, listing: false);

        var only = Assert.Single(outcome.Contents);
        Assert.Equal(ClipboardMime.TextPlain, only.Mime);
        Assert.Contains(ClipboardMime.TextUriList, outcome.Available);
    }

    [Fact]
    public async Task FilterMatchingNothing_StillReportsWhatIsAvailable()
    {
        var (svc, backend, _) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"));

        var outcome = await svc.HandleKittyReadAsync(
            ClipboardKind.Standard, new[] { ClipboardMime.ImagePng }, listing: false);

        Assert.Equal(ClipboardReadStatus.Ok, outcome.Status);
        Assert.Empty(outcome.Contents);
        Assert.Equal(new[] { ClipboardMime.TextPlain }, outcome.Available);
    }

    // --- listing --------------------------------------------------------

    [Fact]
    public async Task Listing_DoesNotReadTheClipboard()
    {
        // Upstream exempts listings from the permission prompt because
        // enumerating formats leaks far less than reading them. That
        // exemption is only sound if a listing genuinely reads nothing.
        var (svc, backend, _) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "secret"));

        var outcome = await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: true);

        Assert.Equal(ClipboardReadStatus.Ok, outcome.Status);
        Assert.Empty(outcome.Contents);
        Assert.Equal(new[] { ClipboardMime.TextPlain }, outcome.Available);
        Assert.Equal(0, backend.ReadCallCount);
        Assert.Equal(1, backend.AvailableCallCount);
    }

    [Fact]
    public async Task Listing_EmptyClipboard_IsEmpty()
    {
        var (svc, backend, _) = Make();

        var outcome = await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: true);

        Assert.Equal(ClipboardReadStatus.Empty, outcome.Status);
        Assert.Equal(0, backend.ReadCallCount);
    }

    [Fact]
    public async Task Listing_IgnoresTheFilter()
    {
        // A listing answers what exists, not what the caller would accept.
        var (svc, backend, _) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"));

        var outcome = await svc.HandleKittyReadAsync(
            ClipboardKind.Standard, new[] { ClipboardMime.ImagePng }, listing: true);

        Assert.Equal(new[] { ClipboardMime.TextPlain }, outcome.Available);
    }

    // --- failure --------------------------------------------------------

    [Fact]
    public async Task BackendThrows_IsEmptyNotUnsupported()
    {
        // A clipboard held open by another process is routine and says
        // nothing about whether this runtime supports the request.
        var (svc, backend, _) = Make();
        backend.OnReadThrow = () => new InvalidOperationException("clipboard locked");

        var outcome = await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: false);

        Assert.Equal(ClipboardReadStatus.Empty, outcome.Status);
        Assert.Empty(outcome.Contents);
    }

    [Fact]
    public async Task NothingIsEverConfirmedOnThisPath()
    {
        // The read path hands contents back to libghostty, which runs the
        // permission flow itself via confirm_read_clipboard_cb. A prompt
        // raised here as well would double-ask.
        var (svc, backend, confirmer) = Make();
        backend.Stored.Add(ClipboardPayload.FromText(ClipboardMime.TextPlain, "hello"));

        await svc.HandleKittyReadAsync(ClipboardKind.Standard, NoFilter, listing: false);

        Assert.Empty(confirmer.Calls);
    }
}
