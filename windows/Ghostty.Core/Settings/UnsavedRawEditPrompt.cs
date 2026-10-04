namespace Ghostty.Core.Settings;

/// <summary>
/// What the user answered when a surface with unsaved Raw Editor text was
/// about to close.
/// </summary>
public enum UnsavedRawEditAnswer
{
    /// <summary>Keep the window and the text; nothing was written or dropped.</summary>
    Cancel,

    /// <summary>Write the buffer, then let the close proceed.</summary>
    Save,

    /// <summary>Drop the buffer and let the close proceed.</summary>
    Discard,
}

/// <summary>
/// Whether a close with unsaved Raw Editor text may proceed, for each answer.
/// </summary>
/// <remarks>
/// Pure so the decision is testable without a window: the dialog and the
/// XamlRoot live in the shell, but "a Save that failed must not close the
/// window" is the rule that matters and is the one a mutation would quietly
/// invert.
/// <para>
/// The failed save is the case that needs spelling out. A disk error during
/// the prompt's Save leaves the window open with the text still in it, which
/// is the only state from which the user can do anything about it -- closing
/// as if it had saved would be the same silent loss the prompt exists to
/// prevent, one step later.
/// </para>
/// </remarks>
public static class UnsavedRawEditPrompt
{
    /// <summary>
    /// May the close proceed after <paramref name="answer"/>?
    /// </summary>
    /// <param name="answer">What the user chose.</param>
    /// <param name="saveSucceeded">
    /// Whether the write landed, for <see cref="UnsavedRawEditAnswer.Save"/>.
    /// Ignored for the other two.
    /// </param>
    public static bool ShouldClose(UnsavedRawEditAnswer answer, bool saveSucceeded)
        => answer switch
        {
            UnsavedRawEditAnswer.Save => saveSucceeded,
            UnsavedRawEditAnswer.Discard => true,
            _ => false,
        };
}
