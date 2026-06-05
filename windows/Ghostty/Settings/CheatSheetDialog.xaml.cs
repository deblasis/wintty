using System;
using System.Collections.Generic;
using Ghostty.Core.Input;
using Ghostty.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

namespace Ghostty.Settings;

/// <summary>Picks header vs entry template by row type (AOT-safe, no reflection).</summary>
internal sealed class CheatRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? EntryTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is KeybindCategoryHeader ? HeaderTemplate : EntryTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

internal sealed partial class CheatSheetDialog : ContentDialog
{
    private readonly KeybindCatalog _catalog;
    private readonly IntPtr _ownerHwnd;

    public CheatSheetDialog(KeybindCatalog catalog, IntPtr ownerHwnd)
    {
        _catalog = catalog;
        _ownerHwnd = ownerHwnd;
        InitializeComponent();
        RowsList.ContainerContentChanging += OnContainerContentChanging;
        ApplyFilter();
    }

    private void ApplyFilter() => RowsList.ItemsSource = _catalog.Filter(SearchBox.Text);

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        var root = args.ItemContainer.ContentTemplateRoot;
        switch (args.Item)
        {
            case KeybindCategoryHeader header when root is TextBlock headerText:
                headerText.Text = header.Name;
                break;
            case KeybindListItem item when root is Grid grid:
                if (grid.FindName("FriendlyText") is TextBlock f) f.Text = item.Friendly;
                if (grid.FindName("LabelText") is TextBlock l) l.Text = item.Label;
                break;
        }
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(KeybindMarkdownExporter.Export(_catalog));
            WinClipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            StaticLoggers.CheatSheet.LogCheatSheetExportFailed(ex);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "keybindings" };
            picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _ownerHwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, KeybindMarkdownExporter.Export(_catalog));
        }
        catch (Exception ex)
        {
            StaticLoggers.CheatSheet.LogCheatSheetExportFailed(ex);
        }
    }
}
