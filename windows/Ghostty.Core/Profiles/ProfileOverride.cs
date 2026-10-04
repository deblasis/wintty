namespace Ghostty.Core.Profiles;

/// <summary>
/// A <c>profile.&lt;id&gt;.*</c> block that lacks either of the two keys a
/// standalone profile needs (<c>name</c>, then <c>command</c>), and so is
/// not a <see cref="ProfileDef"/> on its own.
///
/// This is the shape the settings page writes: exactly one subkey for the
/// profile it is editing, with no definition around it. When the id names a
/// DISCOVERED profile -- the only kind of row the page can edit that has no
/// user block yet -- the override is merged onto what discovery found, and
/// discovery's name / command / working-directory / ProbeId survive. That
/// merge happens in <see cref="ProfileOrderResolver"/>, which is also the
/// only place that knows discovery's answer; an override whose id discovery
/// did not find resolves to nothing, and the parser's warning for it stands.
///
/// Every field is "was this key present": null / false means absent. That
/// is what makes the record mergeable rather than a <see cref="ProfileDef"/>
/// with holes in it. <see cref="TabIconTracksForeground"/> is trinary for
/// the reason it is tri-state at parse time in the def as well: the key's
/// default is true, so "said nothing" and "said false" cannot be spelled the
/// same way.
/// </summary>
public sealed record ProfileOverride(
    string Id,
    string? Name = null,
    string? Command = null,
    string? WorkingDirectory = null,
    IconSpec? Icon = null,
    string? TabTitle = null,
    bool Hidden = false,
    EffectiveVisualOverrides? VisualsOrNull = null,
    bool? TabIconTracksForeground = null);