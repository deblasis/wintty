using System.Globalization;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Which of the icon picker's three tabs an icon belongs on, in the order
/// the dialog lays them out.
/// </summary>
public enum IconPickerTab { Bundled, Mdl2, FromFile }

/// <summary>
/// What the icon picker should show for the icon a profile already has:
/// the tab to open on, the bundled row to select, the text to put in the
/// MDL2 box, and the path to name.
/// <para>
/// This is the whole of "Change" opening on the current icon, split out
/// because the dialog itself cannot be loaded by <c>Ghostty.Tests</c>: the
/// mapping is the part with decisions in it (which tab, which spelling of a
/// code point, what a key the bundled grid does not carry means), and the
/// dialog is left applying three fields.
/// </para>
/// </summary>
public sealed record IconPickerPrefill(
    IconPickerTab Tab,
    string? BundledKey = null,
    string? Mdl2Text = null,
    string? FilePath = null)
{
    /// <summary>
    /// Nothing to show: a profile with no explicit icon, and the two spec
    /// kinds the resolver picks for itself and no config value spells.
    /// </summary>
    public static readonly IconPickerPrefill None = new(IconPickerTab.Bundled);

    /// <summary>
    /// The prefill for <paramref name="spec"/>, or <see cref="None"/> for a
    /// null spec.
    /// <para>
    /// <see cref="BundledKey"/> is the key to select, not an index: the
    /// dialog's bundled grid carries a subset of the keys the tab strip's
    /// exe table can produce ("git" is in one and not the other), and a key
    /// with no row has nothing to select. The dialog leaves the selection
    /// alone for a key it does not carry, because selecting the nearest row
    /// instead would write an icon the user never chose.
    /// </para>
    /// </summary>
    public static IconPickerPrefill FromSpec(IconSpec? spec) => spec switch
    {
        IconSpec.BrandKey b => new(IconPickerTab.Bundled, BundledKey: b.Key),
        IconSpec.BundledKey b => new(IconPickerTab.Bundled, BundledKey: b.Key),
        // X4 / invariant, because the box parses with NumberStyles.HexNumber
        // and the resolver's own label spells it the same way.
        IconSpec.Mdl2Token m => new(
            IconPickerTab.Mdl2,
            Mdl2Text: m.CodePoint.ToString("X4", CultureInfo.InvariantCulture)),
        IconSpec.Path p => new(IconPickerTab.FromFile, FilePath: p.FilePath),
        _ => None,
    };
}