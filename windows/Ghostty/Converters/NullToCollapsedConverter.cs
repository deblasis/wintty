using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Ghostty.Converters;

/// <summary>
/// Returns <see cref="Visibility.Collapsed"/> when the value is
/// <c>null</c> or an empty string, and <see cref="Visibility.Visible"/>
/// otherwise.
/// </summary>
internal sealed partial class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, string language) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
