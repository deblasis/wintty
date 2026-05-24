using System;
using Microsoft.UI.Xaml.Data;

namespace Ghostty.Tabs;

/// <summary>
/// XAML value converter that turns an MDL2 / Fluent code point (int)
/// into the one- or two-char string FontIcon.Glyph expects. Empty
/// string for zero / negative, which keeps the FontIcon harmlessly
/// blank when bound against a non-glyph icon spec.
/// </summary>
public sealed partial class CodePointToGlyphConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int codePoint && codePoint > 0)
            return char.ConvertFromUtf32(codePoint);
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
