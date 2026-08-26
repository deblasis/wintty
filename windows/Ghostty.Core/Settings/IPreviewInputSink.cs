namespace Ghostty.Core.Settings;

/// <summary>
/// Keystrokes destined for a preview surface's fake session. The WinUI
/// side (TerminalControl) calls into this INSTEAD of forwarding to the
/// pty: a preview surface's placeholder child is asleep, so its stdin is
/// exactly where keystrokes should stop. Implemented by
/// <see cref="ShaderPreviewFeed"/>, which routes both user keys and the
/// autoplay script through one <see cref="DosShellCore"/>.
///
/// All calls arrive on the UI thread (WinUI input events), which is also
/// where the feed's loop continuations run, so implementations need no
/// locking.
/// </summary>
internal interface IPreviewInputSink
{
    /// <summary>
    /// A non-printable key press. Returns true when the key was consumed;
    /// false tells the caller to leave the event unhandled.
    /// </summary>
    bool KeyDown(DosShellKey key);

    /// <summary>
    /// Any key press into the preview, whether or not the shell maps it.
    /// The website stamps its idle clock on every keydown, consumed or
    /// not, so holding a key the fake shell ignores pauses the demo too.
    /// </summary>
    void NoteKeyDown();

    /// <summary>One text unit (the WM_CHAR path).</summary>
    void Character(char ch);
}
