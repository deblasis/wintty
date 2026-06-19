using Ghostty.Core.Accessibility;
using Xunit;

namespace Ghostty.Tests.Accessibility;

public class TerminalOutputAnnouncerTests
{
    [Fact]
    public void FirstObserve_SeedsAndAnnouncesNothing()
    {
        var a = new TerminalOutputAnnouncer();
        Assert.Null(a.Observe("PS C:\\> existing screen\n"));
    }

    [Fact]
    public void NoChange_AnnouncesNothing()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("prompt\n");
        Assert.Null(a.Observe("prompt\n"));
    }

    [Fact]
    public void AppendCompleteLine_AnnouncesIt()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("prompt\n");
        Assert.Equal("hello", a.Observe("prompt\nhello\n"));
    }

    [Fact]
    public void PartialLine_IsHeldUntilNewline()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("prompt\n");
        Assert.Null(a.Observe("prompt\nhel"));        // no newline yet
        Assert.Equal("hello", a.Observe("prompt\nhello\n"));
    }

    [Fact]
    public void MultipleLines_AreBatched()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("p\n");
        Assert.Equal("one\ntwo\nthree", a.Observe("p\none\ntwo\nthree\n"));
    }

    [Fact]
    public void LargeBurst_IsSummarized()
    {
        var a = new TerminalOutputAnnouncer(maxLines: 3, maxChars: 1000);
        a.Observe("p\n");
        var added = "a\nb\nc\nd\ne\n";
        Assert.Equal("5 new lines", a.Observe("p\n" + added));
    }

    [Fact]
    public void AllWhitespaceAppend_AnnouncesNothing()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("p\n");
        Assert.Null(a.Observe("p\n   \n\n"));
    }

    [Fact]
    public void Divergence_ReBaselinesSilently_ThenAppendsWork()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("prompt\nold\n");
        Assert.Null(a.Observe("cleared screen\n")); // not a prefix -> re-baseline, silent
        Assert.Equal("new", a.Observe("cleared screen\nnew\n"));
    }

    [Fact]
    public void Reseed_MakesOnlyLaterAppendsAnnounce()
    {
        var a = new TerminalOutputAnnouncer();
        a.Observe("p\n");
        a.Reseed("p\nbacklog\n");                 // adopt backlog as baseline, no announce
        Assert.Equal("fresh", a.Observe("p\nbacklog\nfresh\n"));
    }
}
