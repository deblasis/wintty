using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ghostty.Mvvm;

/// <summary>
/// Minimal INotifyPropertyChanged base. Consumers use C# 14 semi-auto
/// properties with the <c>field</c> keyword:
/// <code>
/// public bool IsOpen
/// {
///     get;
///     set { if (field == value) return; field = value; Raise(); }
/// }
/// </code>
/// Replaces the ad-hoc "_backing + get/set + Raise()" boilerplate
/// that was duplicated in CommandPaletteViewModel / TabModel after the
/// CommunityToolkit.Mvvm drop (PR # 256).
/// </summary>
internal abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raise PropertyChanged for the caller's property.</summary>
    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
