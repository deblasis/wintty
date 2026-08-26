using System;

namespace Ghostty.Core.Settings;

/// <summary>
/// The quiet window that pauses the preview's autoplay demo while the
/// user types: every user keystroke re-arms it, and the demo waits (by
/// polling <see cref="Expired"/>) until it expires, then resumes exactly
/// where it stopped. Time comes from an injected clock, so the pause is
/// unit-testable without sleeping.
/// </summary>
internal sealed class UserQuietWindow
{
    private readonly Func<DateTime> _clock;
    private readonly TimeSpan _quiet;
    private DateTime _resumeAt = DateTime.MinValue;

    public UserQuietWindow(Func<DateTime> clock, TimeSpan quiet)
    {
        _clock = clock;
        _quiet = quiet;
    }

    /// <summary>Push the resume deadline a full quiet span into the future.</summary>
    public void Arm() => _resumeAt = _clock() + _quiet;

    /// <summary>
    /// True when autoplay may run. A window that has never been armed is
    /// expired, so the demo starts freely and only ever pauses in response
    /// to actual user keystrokes.
    /// </summary>
    public bool Expired => _clock() >= _resumeAt;
}
