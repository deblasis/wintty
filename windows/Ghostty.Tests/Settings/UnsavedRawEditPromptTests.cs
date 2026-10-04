using Ghostty.Core.Settings;
using Xunit;

namespace Ghostty.Tests.Settings;

/// <summary>
/// The Raw Editor is the only settings page that keeps its edits in the editor
/// buffer instead of writing them out, so closing the settings window -- or
/// quitting, which closes it -- used to drop a hand-written config with no
/// word and no way back.
/// <para>
/// The decision after the prompt is Core's so it can be tested here: the
/// dialog needs a live XamlRoot and cannot be, and the rule that matters is
/// the one a mutation inverts quietly -- a Save that did not land must not
/// close the window, because that is the same loss one step later.
/// </para>
/// </summary>
public sealed class UnsavedRawEditPromptTests
{
    [Fact]
    public void A_landed_save_lets_the_close_proceed()
    {
        Assert.True(UnsavedRawEditPrompt.ShouldClose(UnsavedRawEditAnswer.Save, saveSucceeded: true));
    }

    [Fact]
    public void A_failed_save_keeps_the_window_and_the_text_open()
    {
        // The editor still holds the text and the file still does not have it,
        // which is the only state the user can retry from.
        Assert.False(UnsavedRawEditPrompt.ShouldClose(UnsavedRawEditAnswer.Save, saveSucceeded: false));
    }

    [Fact]
    public void Discard_lets_the_close_proceed()
    {
        Assert.True(UnsavedRawEditPrompt.ShouldClose(UnsavedRawEditAnswer.Discard, saveSucceeded: false));
    }

    [Fact]
    public void Cancel_keeps_the_window_open()
    {
        Assert.False(UnsavedRawEditPrompt.ShouldClose(UnsavedRawEditAnswer.Cancel, saveSucceeded: true));
    }
}
