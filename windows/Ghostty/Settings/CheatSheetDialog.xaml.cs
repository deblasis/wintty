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

    private void ApplyFilter()
        => WinUiList.ReplaceItems(RowsList.Items, _catalog.Filter(SearchBox.Text));

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        ApplyFilter();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue) return;
        if (args.ItemContainer.ContentTemplateRoot is not Grid root) return;
        var header = root.FindName("HeaderText") as TextBlock;
        var entry = root.FindName("EntryGrid") as Grid;
        switch (args.Item)
        {
            case KeybindCategoryHeader h:
                if (header is not null)
                {
                    header.Text = h.Name;
                    header.Visibility = Visibility.Visible;
                }
                if (entry is not null) entry.Visibility = Visibility.Collapsed;
                break;
            case KeybindListItem item:
                if (header is not null) header.Visibility = Visibility.Collapsed;
                if (entry is not null)
                {
                    entry.Visibility = Visibility.Visible;
                    if (entry.FindName("FriendlyText") is TextBlock f) f.Text = item.Friendly;
                    if (entry.FindName("LabelText") is TextBlock l) l.Text = item.Label;
                }
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
