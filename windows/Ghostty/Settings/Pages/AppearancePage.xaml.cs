using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ghostty.Core.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

internal sealed partial class AppearancePage : Page
{
    private readonly IConfigService _configService;
    private readonly IConfigFileEditor _editor;
    private readonly SearchableList _fontList;
    private bool _loading = true;

    public AppearancePage(IConfigService configService, IConfigFileEditor editor)
    {
        _configService = configService;
        _editor = editor;
        InitializeComponent();
        _fontList = new SearchableList(FontFamilySearch, chosen => OnValueChanged("font-family", chosen));
        OpacitySlider.Value = configService.BackgroundOpacity;
        SelectWindowTheme(configService.WindowTheme);
        _loading = false;
        LoadFontsAsync();
    }

    private void SelectWindowTheme(string theme)
    {
        foreach (ComboBoxItem item in WindowThemeCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), theme, StringComparison.OrdinalIgnoreCase))
            {
                WindowThemeCombo.SelectedItem = item;
                return;
            }
        }
        // Default to "auto" if the value is unrecognized.
        WindowThemeCombo.SelectedIndex = 0;
    }

    private void LoadFontsAsync()
    {
        FontFamilySearch.PlaceholderText = "Loading fonts...";
        var dispatcher = DispatcherQueue;
        Task.Run(() =>
        {
            var fonts = EnumerateSystemFonts();
            dispatcher.TryEnqueue(() =>
            {
                _fontList.SetItems(fonts);
                FontFamilySearch.PlaceholderText = $"Search {fonts.Count} fonts...";
            });
        });
    }

    private static unsafe List<string> EnumerateSystemFonts()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        IntPtr factory;
        if (DWriteCreateFactory(0, &iid, &factory) != 0 || factory == IntPtr.Zero)
            return new List<string>();

        try
        {
            // IDWriteFactory: IUnknown(3) + GetSystemFontCollection(3)
            // checkForUpdates=1 to include per-user installed fonts.
            var vtable = (IntPtr*)*(IntPtr*)factory;
            IntPtr collection;
            var getCollection = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int, int>)vtable[3];
            if (getCollection(factory, &collection, 1) != 0 || collection == IntPtr.Zero)
                return new List<string>();

            try
            {
                var cvt = (IntPtr*)*(IntPtr*)collection;
                // IDWriteFontCollection: GetFontFamilyCount(3), GetFontFamily(4)
                // (verified against src/font/directwrite.zig:585)
                var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)cvt[3];
                var getFamily = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)cvt[4];
                var count = getCount(collection);

                for (uint i = 0; i < count; i++)
                {
                    IntPtr familyPtr;
                    if (getFamily(collection, i, &familyPtr) != 0 || familyPtr == IntPtr.Zero)
                        continue;
                    try
                    {
                        var name = GetFamilyName(familyPtr);
                        if (name != null) families.Add(name);
                    }
                    finally
                    {
                        Marshal.Release(familyPtr);
                    }
                }
            }
            finally
            {
                Marshal.Release(collection);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }

        // Ghostty embeds JetBrains Mono in the binary so it's always
        // available even if not installed on the system.
        families.Add("JetBrains Mono");

        return families.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static unsafe string? GetFamilyName(IntPtr familyPtr)
    {
        var fvt = (IntPtr*)*(IntPtr*)familyPtr;
        // IDWriteFontFamily: IUnknown(3) + IDWriteFontList(3) + GetFamilyNames(6)
        // (verified against src/font/directwrite.zig:549)
        IntPtr namesPtr;
        var getNames = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)fvt[6];
        if (getNames(familyPtr, &namesPtr) != 0 || namesPtr == IntPtr.Zero)
            return null;

        try
        {
            var nvt = (IntPtr*)*(IntPtr*)namesPtr;
            // IDWriteLocalizedStrings: GetCount(3), GetStringLength(7), GetString(8)
            // (verified against src/font/directwrite.zig:120)
            var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)nvt[3];
            if (getCount(namesPtr) == 0) return null;

            uint len;
            var getLen = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, int>)nvt[7];
            if (getLen(namesPtr, 0, &len) != 0) return null;

            var buf = stackalloc char[(int)len + 1];
            var getString = (delegate* unmanaged[Stdcall]<IntPtr, uint, char*, uint, int>)nvt[8];
            if (getString(namesPtr, 0, buf, len + 1) != 0) return null;

            return new string(buf, 0, (int)len);
        }
        finally
        {
            Marshal.Release(namesPtr);
        }
    }

    private void OnValueChanged(string key, string value)
    {
        if (_loading) return;
        _configService.SuppressWatcher(true);
        _editor.SetValue(key, value);
        _configService.SuppressWatcher(false);
        _configService.Reload();
    }

    private void FontSize_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        OnValueChanged("font-size", sender.Value.ToString());
    }

    private void Opacity_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        OnValueChanged("background-opacity", e.NewValue.ToString("F2"));
    }

    private void WindowTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item)
            OnValueChanged("window-theme", item.Tag?.ToString() ?? "auto");
    }

    private void ShaderPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) OnValueChanged("custom-shader", tb.Text);
    }

    [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe partial int DWriteCreateFactory(int factoryType, Guid* iid, IntPtr* factory);
}
