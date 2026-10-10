using Ghostty.Core.Tabs;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Ghostty.Tabs;

/// <summary>
/// The chrome both tab strips share for the program status glyph (the
/// OSC 7501 baseline): one MDL2 glyph and one brush per state, and an
/// automation label so the channel is never color alone.
/// </summary>
/// <remarks>
/// Early-sunrise colors scaled by urgency, the same family the accent
/// bell uses: blocked is flame (act now), working amber (in motion),
/// error dried clay, done a muted receding green rather than a
/// celebration green, which would invert the urgency mapping. A purple
/// gradient here would be off-brand by standing policy.
///
/// The glyph is the redundancy channel: warning triangle, play, error
/// circle-X, checkmark. The automation name carries the state's word,
/// which is the channel screen readers get.
/// </remarks>
internal static class TabProgramStatusChrome
{
    internal static string ProgramStatusGlyph(TabProgramStatus status) => status switch
    {
        TabProgramStatus.Blocked => "",
        TabProgramStatus.Working => "",
        TabProgramStatus.Error => "",
        TabProgramStatus.Done => "",
        _ => "",
    };

    internal static Brush BrushFor(TabProgramStatus status) =>
        new SolidColorBrush(Fill(status));

    internal static Color Fill(TabProgramStatus status) => status switch
    {
        TabProgramStatus.Blocked => Color.FromArgb(0xFF, 0xE2, 0x58, 0x22),  // flame
        TabProgramStatus.Working => Color.FromArgb(0xFF, 0xE8, 0xA3, 0x3D),  // amber
        TabProgramStatus.Error => Color.FromArgb(0xFF, 0xC2, 0x5B, 0x4D),    // dried clay
        TabProgramStatus.Done => Color.FromArgb(0xFF, 0x8F, 0xA8, 0x82),     // muted sage
        _ => Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E),                          // neutral
    };

    internal static string AutomationLabel(TabProgramStatus status) => status switch
    {
        TabProgramStatus.Blocked => "blocked, needs input",
        TabProgramStatus.Working => "working",
        TabProgramStatus.Error => "error",
        TabProgramStatus.Done => "done, not viewed",
        _ => "",
    };

    /// <summary>Whether the glyph shows at all: a state exists to show,
    /// and no richer presentation has claimed the slot (the pro family's
    /// stacked indicator cards do; this glyph is the baseline every tier
    /// ships and it stands down beneath them).</summary>
    internal static bool Visible(TabModel tab) =>
        !TabModel.ProgramStatusPresentationClaimed
        && tab.ProgramStatus != TabProgramStatus.None;
}
