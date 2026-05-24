using System.ComponentModel;
using System.Runtime.CompilerServices;
using Ghostty.Core.Profiles;

namespace Ghostty.Core.Tabs;

/// <summary>
/// Per-tab observable that pairs an <see cref="IconSpec"/> with the
/// tooltip the tab strip shows. Lives in Core (no WinUI types) so it
/// can be exercised by the test project, which only references
/// <c>Ghostty.Core</c>.
///
/// INPC is hand-rolled with the C# 14 <c>field</c> keyword to match
/// <see cref="TabModel"/> and avoid a source-generator dependency.
/// </summary>
public sealed class TabIconViewModel : INotifyPropertyChanged
{
    public TabIconViewModel(IconSpec icon, string tooltipText)
    {
        Icon = icon;
        TooltipText = tooltipText;
    }

    public IconSpec Icon
    {
        get;
        private set { if (!Equals(field, value)) { field = value; Raise(); } }
    }

    public string TooltipText
    {
        get;
        private set { if (field != value) { field = value; Raise(); } }
    }

    /// <summary>
    /// True when <see cref="Icon"/> is a Segoe MDL2 glyph; the tab-strip
    /// template selector switches to a FontIcon presenter in that case.
    /// </summary>
    public bool IsMdl2Glyph => Icon is IconSpec.Mdl2Token;

    /// <summary>
    /// MDL2 code point when <see cref="IsMdl2Glyph"/> is true; 0 otherwise.
    /// Bound directly by a FontIcon in the tab strip; callers don't need
    /// to pattern-match on <see cref="Icon"/> in XAML.
    /// </summary>
    public int Mdl2CodePoint => Icon is IconSpec.Mdl2Token m ? m.CodePoint : 0;

    /// <summary>
    /// Atomic update of icon + tooltip. Single call site avoids two
    /// PropertyChanged notifications racing the template selector on
    /// profile re-resolution.
    /// </summary>
    public void SetIcon(IconSpec icon, string tooltipText)
    {
        Icon = icon;
        TooltipText = tooltipText;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
