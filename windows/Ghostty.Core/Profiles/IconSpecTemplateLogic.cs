namespace Ghostty.Core.Profiles;

/// <summary>
/// Discriminator for the two presenter shapes the tab strip renders:
/// a raster <c>Image</c> (PNG-backed icons) versus a glyph <c>FontIcon</c>
/// (MDL2 code points). The WinUI layer maps these to actual
/// <c>DataTemplate</c> resources.
/// </summary>
public enum IconTemplateKind { Image, FontIcon }

/// <summary>
/// Pure decision: given an <see cref="IconSpec"/>, which presenter
/// template should the tab strip use. Lives in Core so it can be
/// exercised by <c>Ghostty.Tests</c>, which never loads WinUI.
/// </summary>
public static class IconSpecTemplateLogic
{
    public static IconTemplateKind PickKind(IconSpec spec) => spec switch
    {
        IconSpec.Mdl2Token => IconTemplateKind.FontIcon,
        _ => IconTemplateKind.Image,
    };
}
