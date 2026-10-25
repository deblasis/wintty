using System.Collections.Generic;
using Ghostty.Core.Input;
using Xunit;

namespace Ghostty.Tests.Input;

/// <summary>
/// The IME commit contract against the production <see cref="ImeForwarder"/>.
/// These are the cases the review's failing model established: composition
/// end closes the session without forwarding the reading (the candidate
/// lands afterwards), a live preedit is not committed by a key, ASCII
/// preedits are visible, an already-delivered ASCII character is not typed
/// twice, and a shrinking composition keeps its tail.
/// </summary>
public class ImeForwarderTests
{
    private static (ImeForwarder Forwarder, List<string> Forwarded, List<string?> Preedits) New()
    {
        var f = new ImeForwarder();
        var forwarded = new List<string>();
        var preedits = new List<string?>();
        f.ForwardedText += forwarded.Add;
        f.PreeditChanged += preedits.Add;
        return (f, forwarded, preedits);
    }

    [Fact]
    public void CompositionEnd_DefersTheReading_ThenForwardsTheLandedText()
    {
        var (f, forwarded, preedits) = New();
        f.SetSinkText("ni'hao");
        f.OnCompositionStarted(0);

        f.OnCompositionEnded();

        // The reading is provisional: TSF writes the selected text after
        // Ended, so End itself must forward nothing.
        Assert.False(f.Composing);
        Assert.Empty(forwarded);
        Assert.Null(f.Preedit);
        Assert.Null(preedits[^1]);

        f.SetSinkText("你好");
        f.Commit();
        Assert.Equal("你好", string.Concat(forwarded));
    }

    [Fact]
    public void AsciiPreedit_IsVisible()
    {
        // Microsoft Pinyin composes the raw pinyin as ASCII. The preedit is
        // display-only; it must not be filtered by script.
        var (f, _, _) = New();
        f.SetSinkText("nihao");
        f.OnCompositionStarted(0);

        f.OnCompositionChanged();

        Assert.Equal("nihao", f.Preedit);
    }

    [Fact]
    public void AKey_DoesNotCommitALivePreedit()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("にほ");
        f.OnCompositionStarted(0);

        var committed = f.OnKeyDown();

        Assert.False(committed);
        Assert.Empty(forwarded);
        Assert.True(f.Composing);
    }

    [Fact]
    public void EndedThenKey_ForwardsExactlyOnce()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("日本語");
        f.OnCompositionStarted(0);
        f.OnCompositionEnded();

        // Ended alone forwards nothing; the landed text is committed once.
        Assert.Empty(forwarded);
        f.Commit();
        Assert.Single(forwarded);
        Assert.Equal("日本語", forwarded[0]);

        f.OnKeyDown();
        Assert.Single(forwarded);
    }

    [Fact]
    public void AsciiTypedWithNoComposition_IsForwardedOnce_AndCreditedTextIsNot()
    {
        var (credited, creditedForwarded, _) = New();
        credited.SetSinkText("a");
        credited.NoteAsciiDelivered();
        credited.OnKeyDown();
        Assert.Empty(creditedForwarded);

        var (uncredited, uncreditedForwarded, _) = New();
        uncredited.SetSinkText("a");
        uncredited.OnKeyDown();
        Assert.Equal("a", string.Concat(uncreditedForwarded));
    }

    [Fact]
    public void AShrinkingComposition_DoesNotLoseItsTail()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("aaaaa가");
        for (var i = 0; i < 5; i++) f.NoteAsciiDelivered();
        f.OnCompositionStarted(5);

        f.SetSinkText("aa가");
        f.OnCompositionChanged();
        f.OnCompositionEnded();
        f.Commit();

        Assert.Contains("가", string.Concat(forwarded));
    }

    [Fact]
    public void Cancel_DropsTheLivePreeditWithoutForwarding()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("にほ");
        f.OnCompositionStarted(0);

        f.Cancel();

        Assert.Empty(forwarded);
        Assert.False(f.Composing);
        Assert.Null(f.Preedit);
    }
}
