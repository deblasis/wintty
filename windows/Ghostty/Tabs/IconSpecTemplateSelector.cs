using Ghostty.Core.Profiles;
using Ghostty.Core.Tabs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Tabs;

/// <summary>
/// Picks the Image template for PNG-backed icon specs and the FontIcon
/// template for MDL2 codepoints. Selection logic lives in
/// <see cref="IconSpecTemplateLogic"/> so it stays testable from
/// <c>Ghostty.Tests</c>, which doesn't reference WinUI.
/// </summary>
public sealed partial class IconSpecTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? FontIconTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
    {
        if (item is TabIconViewModel vm)
        {
            return IconSpecTemplateLogic.PickKind(vm.Icon) switch
            {
                IconTemplateKind.FontIcon => FontIconTemplate,
                _ => ImageTemplate,
            };
        }
        return null;
    }

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
