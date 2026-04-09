using System;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Ghostty.Tabs;

/// <summary>
/// Collapses the per-tab inline progress bar when the tab is not
/// reporting progress (state == None) and shows it otherwise. Bound
/// in the TabHost header template.
/// </summary>
internal sealed class ProgressVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is TabProgressState.Kind k && k != TabProgressState.Kind.None)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
