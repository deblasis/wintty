using System;
using Ghostty.Core.Profiles;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Ghostty.Tabs;

/// <summary>
/// XAML value converter that turns an <see cref="IconSpec"/> into a
/// <see cref="BitmapImage"/> backed by PNG bytes returned by the
/// process-wide <see cref="TabIconBytesCache"/>. Used by the tab
/// strip's image template when the spec is anything other than an
/// MDL2 glyph; MDL2 glyphs flow through the FontIcon template.
/// </summary>
public sealed partial class IconSpecToBitmapImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not IconSpec spec) return null;
        var bytes = TabIconBytesCache.GetBytesSync(spec);
        if (bytes is null || bytes.Length == 0) return null;

        var bmp = new BitmapImage();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            writer.StoreAsync().GetResults();
        }
        stream.Seek(0);
        bmp.SetSource(stream);
        return bmp;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
