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
    public void ACharacterCreditedBeforeTheBufferArrives_IsNotForwarded()
    {
        // The control credits the character before the commit syncs the box, so
        // the buffer arrives *after* the credit. This is the production order
        // that the round-8 tests did not model.
        var (f, forwarded, _) = New();
        f.NoteAsciiDelivered();
        f.SetSinkText("a");
        f.Commit();
        Assert.Empty(forwarded);
    }

    [Fact]
    public void ATypedCharacterAfterAPendingTail_IsNotCreditedAgainstTheTail()
    {
        // "uv" is the composition's text and "j" is the character that ends its
        // wait and is typed. The credit names j, so the commit forwards uv and
        // the typed j lands once: uvj, never vjj.
        var (f, forwarded, _) = New();
        f.SetSinkText("uv");
        f.OnCompositionStarted(2);
        f.OnCompositionChanged();
        f.OnCompositionEnded();
        f.SetSinkText("uvj");
        f.NoteAsciiDelivered('j');

        f.Commit();

        Assert.Equal("uv", string.Concat(forwarded));
    }

    [Fact]
    public void ACreditThatNamesItsCharacter_SkipsItWhereverItSits()
    {
        // The credit was created when "a" was typed; a later write appends "b".
        // The commit must skip a and forward b.
        var (f, forwarded, _) = New();
        f.NoteAsciiDelivered('a');
        f.SetSinkText("ab");

        f.Commit();

        Assert.Equal("b", string.Concat(forwarded));
    }

    [Fact]
    public void ACreditedCharacterIsMatchedAtTheCaret_NotTheFirstCopy()
    {
        // j is the character the user typed; "ju" is the composition's text.
        // The box appended j at the caret, so the credit must skip that last j
        // and forward the composition: ju, with the typed j landing once.
        var (f, forwarded, _) = New();
        f.SetSinkText("juj");
        f.NoteAsciiDelivered('j');

        f.Commit();

        Assert.Equal("ju", string.Concat(forwarded));
    }

    [Fact]
    public void ACreditDoesNotSkipACopyOfTheCharacterInsideTheComposition()
    {
        // The typed u is the last one; the composition's own u must survive
        // (the count's front-skip would drop it and forward "vuu").
        var (f, forwarded, _) = New();
        f.SetSinkText("uvuu");
        f.NoteAsciiDelivered('u');

        f.Commit();

        Assert.Equal("uvu", string.Concat(forwarded));
    }

    [Fact]
    public void AKeyOverAnEmptySink_DoesNotSpendTheRedrawGuard()
    {
        // The key that releases a quiet discard forwards nothing. It must not
        // spend the by-value guard the discard armed, or the text TSF writes
        // back is admitted and typed by the next key.
        var (f, forwarded, _) = New();
        f.SetSinkText("q");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.OnKeyDown();   // the composition reported: withheld
        f.OnKeyDown();   // one quiet key is not evidence
        f.OnKeyDown();   // two are: the text is dropped, the guard is armed
        Assert.True(f.DiscardWasQuiet);

        f.OnKeyDown();   // the released key: the sink is empty
        f.SetSinkText("q");   // TSF writes the discarded text back
        f.OnKeyDown();        // and the next key must not type it
        Assert.Empty(forwarded);
    }

    [Fact]
    public void TheEndedRevival_DoesNotSendThePrefixTwice()
    {
        // A composition that started inside existing text already delivered its
        // prefix. When a quiet discard is followed by the composition's own end,
        // the settle delivers the result: only the unsent tail may go out.
        var (f, forwarded, _) = New();
        f.SetSinkText("ab");
        f.OnCompositionStarted(2);
        f.SetSinkText("abc");
        f.OnCompositionChanged();
        f.OnKeyDown();
        f.OnKeyDown();
        f.OnKeyDown();
        Assert.True(f.DiscardWasQuiet);

        f.ClearDiscard();
        f.SetSinkText("abc");
        f.OnCompositionEnded();
        f.Commit();
        Assert.Equal("abc", string.Concat(forwarded));
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

        // The two 'a' were delivered before the composition started, so the
        // only text left is the one character the rewrite kept. A reset to
        // zero here would forward "aa가" and type them a second time.
        Assert.Equal("가", string.Concat(forwarded));
    }

    [Fact]
    public void ARewoundComposition_DoesNotResendTheDeliveredPrefix()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("にほか");
        f.OnCompositionStarted(0);
        f.SetSinkText("にほに");
        f.OnCompositionStarted(2);

        Assert.Equal("にほ", string.Concat(forwarded));

        // TSF rewrites the buffer shorter than the watermark: "にほ" is
        // already on the surface, so lowering the watermark to the unchanged
        // prefix keeps the delivered text delivered.
        f.SetSinkText("に");
        f.OnCompositionChanged();
        Assert.Equal(1, f.Forwarded);

        f.OnCompositionEnded();
        f.Commit();
        Assert.Equal("にほ", string.Concat(forwarded));
    }

    [Fact]
    public void UntrackedComposition_IsCommittedByTheNextKey()
    {
        // Live trace of "안녕" then Space: the second syllable's Started
        // arrives before the first one's Ended, and the second syllable
        // never raises Changed or Ended.
        var (f, forwarded, _) = New();
        f.SetSinkText("ㅇ");
        f.OnCompositionStarted(0);
        f.SetSinkText("안");
        f.OnCompositionChanged();
        f.SetSinkText("안ㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();

        Assert.Equal("안", string.Concat(forwarded));
        Assert.True(f.Untracked);

        f.SetSinkText("안녕");
        f.RefreshPreedit();
        Assert.Equal("녕", f.Preedit);

        Assert.True(f.OnKeyDown());
        Assert.Equal("안녕", string.Concat(forwarded));
        Assert.False(f.Composing);
        Assert.False(f.Untracked);
    }

    [Fact]
    public void UntrackedBackspace_OnUnsentText_IsSpentThere()
    {
        // Live trace of "ㅁㄴㅇㄹ" then Backspace twice: the IME takes the
        // first, the box applies the second before KeyDown. Only 'ㅇ' may
        // go; sending the key as well deleted 'ㄴ' too.
        var (f, forwarded, _) = New();
        f.SetSinkText("ㅁ");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        foreach (var sink in new[] { "ㅁㄴ", "ㅁㄴㅇ", "ㅁㄴㅇㄹ" })
        {
            f.SetSinkText(sink);
            f.OnCompositionStarted(0);
            f.OnCompositionEnded();
        }
        f.SetSinkText("ㅁㄴㅇ");
        f.RefreshPreedit();

        f.SetSinkText("ㅁㄴ");
        Assert.True(f.OnKeyDown(deletionKey: true));

        Assert.True(f.KeyConsumed);
        Assert.Equal("ㅁㄴ", string.Concat(forwarded));
        Assert.False(f.Composing);
    }

    [Fact]
    public void UntrackedBackspace_OnSentText_IsNotResent()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("안");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.SetSinkText("안ㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();
        f.SetSinkText("안");
        f.RefreshPreedit();

        f.SetSinkText("");
        Assert.True(f.OnKeyDown(deletionKey: true));

        Assert.False(f.KeyConsumed);
        Assert.Equal("안", string.Concat(forwarded));
    }

    [Fact]
    public void NestedComposition_StaysTracked_WhileItRaisesChanged()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("a");
        f.OnCompositionStarted(0);
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();
        f.SetSinkText("ab");
        f.OnCompositionChanged();

        Assert.False(f.Untracked);
        Assert.False(f.OnKeyDown());
        Assert.Equal("a", string.Concat(forwarded));
    }

    [Fact]
    public void EndingTheLastComposition_ClearsUntracked()
    {
        var (f, _, _) = New();
        f.SetSinkText("ㅁ");
        f.OnCompositionStarted(0);
        f.SetSinkText("ㅁㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();

        Assert.True(f.Composing);
        Assert.True(f.Untracked);

        f.OnCompositionEnded();

        // Nothing is composing, so nothing can be untracked about it. A mark
        // left standing keeps the poll timer alive and tells the apprt the
        // surface is idle while a preedit is still drawn.
        Assert.False(f.Composing);
        Assert.False(f.Untracked);
    }

    [Fact]
    public void ANewComposition_StartsTracked()
    {
        var (f, _, _) = New();
        f.SetSinkText("ㅁ");
        f.OnCompositionStarted(0);
        f.SetSinkText("ㅁㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();
        Assert.True(f.Untracked);

        // A composition that has not stopped reporting must not inherit the
        // previous one's mark: the next key would commit a live preedit.
        f.SetSinkText("ㅁㄴㅇ");
        f.OnCompositionStarted(2);

        Assert.False(f.Untracked);
        Assert.False(f.OnKeyDown());
    }

    [Fact]
    public void ADeletionBelowTheWatermark_KeepsTheUnsentTail()
    {
        var (f, forwarded, _) = New();
        f.SetSinkText("안");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.SetSinkText("안ㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();
        Assert.Equal("안", string.Concat(forwarded));

        // The box deleted the syllable that was already delivered. The 'ㄴ'
        // was never sent, so it must survive; the key itself has to reach the
        // shell, because the surface still shows the text it just removed.
        f.SetSinkText("ㄴ");
        Assert.True(f.OnKeyDown(deletionKey: true));

        Assert.False(f.KeyConsumed);
        Assert.Equal("안ㄴ", string.Concat(forwarded));
    }

    [Fact]
    public void AQuietComposition_IsDiscardedAfterTwoQuietKeys()
    {
        // An IME that raises Started and Changed and then stops reporting
        // would otherwise withhold every key forever. One quiet key is not
        // evidence -- a live IME declines keys too -- so two in a row drop the
        // text and let the next key through. Nothing is committed: a dead
        // composition's preedit was never deliverable, and a live one must not
        // be typed as final.
        var (f, forwarded, _) = New();
        f.SetSinkText("ㅁ");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.SetSinkText("ㅁㄴ");

        Assert.False(f.OnKeyDown());   // the composition reported an event
        Assert.False(f.OnKeyDown());   // one quiet key: still not evidence
        Assert.False(f.OnKeyDown());   // two quiet keys: the text is dropped, the key flows
        Assert.False(f.Composing);
        Assert.Equal(string.Empty, f.SinkText);
        Assert.Empty(forwarded);

        // A silent IME that comes back writes the same buffer; it is the text
        // that was just discarded, not new input.
        f.SetSinkText("ㅁㄴ");
        Assert.Equal(string.Empty, f.SinkText);

        Assert.True(f.OnKeyDown());    // the next key flows
        Assert.Empty(forwarded);       // and commits nothing
    }

    [Fact]
    public void ACompositionThatSpeaksAgain_IsTakenBack()
    {
        // Two quiet keys discard the buffer. A Changed afterwards proves the
        // composition was alive, so its text is a preedit again -- including
        // when it is byte-identical, and including after the key the discard
        // released (which forwards nothing and must not spend the proof).
        var (f, forwarded, _) = New();
        f.SetSinkText("に");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.OnKeyDown();
        f.OnKeyDown();
        f.OnKeyDown();                 // quiet discard
        Assert.Empty(forwarded);

        Assert.True(f.OnKeyDown());    // the released key: flows, forwards nothing
        Assert.Empty(forwarded);

        f.ClearDiscard();
        f.SetSinkText("に");
        f.OnCompositionChanged();

        Assert.True(f.Composing);
        Assert.Equal("に", f.Preedit);
        Assert.False(f.OnKeyDown());
        Assert.Empty(forwarded);
    }

    [Fact]
    public void ARevivedComposition_DoesNotSendTheDeliveredPrefix()
    {
        // A composition that started inside delivered text keeps that prefix
        // through a discard: the surface already has it.
        var (f, forwarded, _) = New();
        f.SetSinkText("ab");
        f.OnCompositionStarted(2);
        f.SetSinkText("abc");
        f.OnCompositionChanged();
        Assert.Equal("ab", string.Concat(forwarded));

        f.OnKeyDown();
        f.OnKeyDown();
        f.OnKeyDown();                 // quiet discard

        f.ClearDiscard();
        f.SetSinkText("abc");
        f.OnCompositionChanged();

        Assert.Equal("c", f.Preedit);
        f.OnCompositionEnded();
        f.Commit();
        Assert.Equal("abc", string.Concat(forwarded));
    }

    [Fact]
    public void TheDiscardMarkDoesNotOutliveTheComposition()
    {
        // Ended proves the composition is over; a later Changed is a new one's
        // and must not be taken back as the old preedit.
        var (f, _, _) = New();
        f.SetSinkText("に");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.OnKeyDown();
        f.OnKeyDown();
        f.OnKeyDown();                 // quiet discard

        f.OnCompositionEnded();
        f.SetSinkText("日");
        f.OnCompositionChanged();

        Assert.False(f.Composing);
        Assert.Null(f.Preedit);
    }

    [Fact]
    public void TextAfterAQuietDiscard_IsAccepted()
    {
        // The discard drops the abandoned buffer, not the forwarder: input the
        // user types afterwards must still be delivered.
        var (f, forwarded, _) = New();
        f.SetSinkText("q");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.SetSinkText("qq");
        f.OnKeyDown();
        f.OnKeyDown();
        f.OnKeyDown();                 // discard + arm
        Assert.False(f.Composing);

        f.SetSinkText("zz");           // new input, not the abandoned buffer
        Assert.Equal("zz", f.SinkText);
        f.OnKeyDown(deletionKey: false);
        Assert.Equal("zz", string.Concat(forwarded));
    }

    [Fact]
    public void ARewriteBetweenKeys_DoesNotSpendAKeyThatCannotDelete()
    {
        // A shrink the user did not cause (a TSF rewrite) must not be charged
        // to the next Space.
        var (f, forwarded, _) = New();
        f.SetSinkText("안");
        f.OnCompositionStarted(0);
        f.OnCompositionChanged();
        f.SetSinkText("안ㄴ");
        f.OnCompositionStarted(1);
        f.OnCompositionEnded();

        f.SetSinkText("안");   // no key: the IME rewrote the buffer

        Assert.True(f.OnKeyDown(deletionKey: false));
        Assert.False(f.KeyConsumed);
        Assert.Equal("안", string.Concat(forwarded));
    }

    [Fact]
    public void DiscardedText_IsNotCommittedByTheNextKey()
    {
        // A cancel cannot always clear the box: TSF owns it while the
        // composition is live and reverts managed writes. The text it puts
        // back was discarded deliberately, so the next key must not type it.
        var (f, forwarded, _) = New();
        f.SetSinkText("にほ");
        f.OnCompositionStarted(0);
        f.Cancel();
        f.DiscardNextText("にほ");

        f.SetSinkText("にほ");
        Assert.Equal(string.Empty, f.SinkText);

        f.OnKeyDown(deletionKey: false);
        Assert.Empty(forwarded);
    }

    [Fact]
    public void AnEmptySync_DoesNotSpendTheDiscardGuard()
    {
        // Every KeyDown syncs the sink, and an unrelated empty sync must not
        // disarm the guard before TSF writes the cancelled text back.
        var (f, forwarded, _) = New();
        f.SetSinkText("にほ");
        f.OnCompositionStarted(0);
        f.Cancel();
        f.DiscardNextText("にほ");

        f.SetSinkText(string.Empty);
        f.SetSinkText("にほ");
        Assert.Equal(string.Empty, f.SinkText);

        f.OnKeyDown(deletionKey: false);
        Assert.Empty(forwarded);
    }

    [Fact]
    public void DifferentTextAfterADiscard_StillFlows()
    {
        // The guard is for the text that was cancelled, not for whatever
        // arrives next (an emoji-panel or dictation insertion).
        var (f, forwarded, _) = New();
        f.SetSinkText("にほ");
        f.OnCompositionStarted(0);
        f.Cancel();
        f.DiscardNextText("にほ");

        f.SetSinkText("😀");
        Assert.Equal("😀", f.SinkText);

        f.OnKeyDown(deletionKey: false);
        Assert.Equal("😀", string.Concat(forwarded));
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
