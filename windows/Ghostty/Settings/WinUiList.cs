using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings;

/// <summary>
/// WinUI 3 <c>ItemsSource</c> cannot marshal mixed CLR records from
/// Ghostty.Core (ArgumentException on the setter). Pointing an
/// ItemTemplateSelector at those same types then InvalidCastExceptions
/// inside MeasureOverride. The command palette already avoids both by
/// stuffing items through <c>Items.Clear</c> + <c>Add</c> and a single
/// ItemTemplate populated from ContainerContentChanging.
/// </summary>
internal static class WinUiList
{
    public static void ReplaceItems(ItemCollection items, IEnumerable<object> rows)
    {
        items.Clear();
        foreach (var row in rows)
            items.Add(row);
    }
}
