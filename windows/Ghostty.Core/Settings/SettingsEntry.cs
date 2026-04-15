namespace Ghostty.Core.Settings;

/// <summary>
/// Shape of a settings control. Used by the future search overlay
/// to render result rows inline. Phase 1 only needs the values to
/// exist in the index; no rendering logic consumes this yet.
/// </summary>
public enum SettingType
{
    Color,
    Number,
    Toggle,
    Combo,
    Text,
    Slider,
    Custom,
}

/// <summary>
/// One entry in the hand-maintained settings index. Adding a new
/// config key to the settings UI requires one new record in
/// <see cref="SettingsIndex"/>.
///
/// This record lives in Ghostty.Core so the index can be unit-tested
/// without pulling in WinUI 3. The Tags and Description fields drive
/// the future search overlay; Page and Section drive the current
/// grouped layout.
///
/// Tags is a <see cref="string"/>[] which does not participate in
/// structural value equality -- two entries with the same Key but
/// different Tags arrays compare as unequal. This is fine because
/// entries are only ever looked up by Key; the record is not placed
/// in a hash set and not compared elsewhere.
/// </summary>
public sealed record SettingsEntry(
    string Key,
    string Label,
    string Description,
    string Page,
    string Section,
    string[] Tags,
    SettingType Type);
