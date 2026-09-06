using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Per-tab observable that pairs an <see cref="IconSpec"/> with the
/// tooltip the tab strip shows. Internally maintains two slots: the
/// profile icon (set by <see cref="SetIcon"/> when the profile snapshot
/// changes) and an optional override (set by the runtime active-process
/// tracker via <see cref="SetOverride"/>). The resolved <see cref="Icon"/>
/// is the override when set, else the profile.
///
/// Lives in Core (no WinUI types) so the test project can exercise
/// override semantics without spinning up XAML.
/// </summary>
public sealed class TabIconViewModel : INotifyPropertyChanged
{
    private IconSpec _profileIcon;
    private string _profileTooltip;
    private IconSpec? _overrideIcon;
    private string? _overrideTooltip;
    private bool _settling;

    // What a tab wears while it starts: the app's own icon, the way a
    // loading tab reads in other terminals, above both the profile slot
    // and any foreground override.
    private static readonly IconSpec SettlingIcon = new IconSpec.BundledKey("default");
    private const string SettlingPrefix = "Starting…";

    public TabIconViewModel(IconSpec icon, string tooltipText)
    {
        _profileIcon = icon;
        _profileTooltip = tooltipText;
    }

    public IconSpec Icon => _settling ? SettlingIcon : _overrideIcon ?? _profileIcon;

    // While starting, the word is added above the profile's name rather than
    // replacing it: the moment a person checks a tab is the moment they want
    // to know which profile they opened.
    public string TooltipText => _settling
        ? SettlingPrefix + "\n" + (_overrideTooltip ?? _profileTooltip)
        : _overrideTooltip ?? _profileTooltip;

    /// <summary>
    /// Whether the tab is still starting. While true the resolved icon and
    /// tooltip are the app's own; the profile and override slots keep their
    /// values underneath and come back the moment this clears.
    /// </summary>
    public void SetSettling(bool settling)
    {
        if (_settling == settling) return;
        var oldIcon = Icon;
        var oldTooltip = TooltipText;
        _settling = settling;
        RaiseIfChanged(oldIcon, oldTooltip);
    }

    public bool IsMdl2Glyph => Icon is IconSpec.Mdl2Token;

    public int Mdl2CodePoint => Icon is IconSpec.Mdl2Token m ? m.CodePoint : 0;

    /// <summary>
    /// Updates the profile-icon slot. Existing override (if any) keeps
    /// winning until cleared. Fires PropertyChanged for Icon / TooltipText
    /// only when the resolved values actually change.
    /// </summary>
    public void SetIcon(IconSpec icon, string tooltipText)
    {
        var oldIcon = Icon;
        var oldTooltip = TooltipText;

        _profileIcon = icon;
        _profileTooltip = tooltipText;

        RaiseIfChanged(oldIcon, oldTooltip);
    }

    /// <summary>
    /// Installs an icon override (typically from the foreground-process
    /// tracker). Calls to this method while another override is active
    /// replace it. <see cref="RevertToProfile"/> clears.
    /// </summary>
    public void SetOverride(IconSpec icon, string tooltipText)
    {
        var oldIcon = Icon;
        var oldTooltip = TooltipText;

        _overrideIcon = icon;
        _overrideTooltip = tooltipText;

        RaiseIfChanged(oldIcon, oldTooltip);
    }

    /// <summary>
    /// Clears the override; resolved Icon / TooltipText fall back to the
    /// profile slot.
    /// </summary>
    public void RevertToProfile()
    {
        if (_overrideIcon is null) return;
        var oldIcon = Icon;
        var oldTooltip = TooltipText;

        _overrideIcon = null;
        _overrideTooltip = null;

        RaiseIfChanged(oldIcon, oldTooltip);
    }

    private void RaiseIfChanged(IconSpec oldIcon, string oldTooltip)
    {
        if (!Equals(oldIcon, Icon))
        {
            Raise(nameof(Icon));
            Raise(nameof(IsMdl2Glyph));
            Raise(nameof(Mdl2CodePoint));
        }
        if (!string.Equals(oldTooltip, TooltipText))
        {
            Raise(nameof(TooltipText));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
