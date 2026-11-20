using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Ghostty.Core.Ssh;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Ghostty.Ssh;

/// <summary>
/// The dialogs in front of a transfer. The remote path is a guess (the
/// folder comes from the window title, the file name from text on screen).
/// A download always shows it for correction; an upload asks only when
/// the folder could not be found at all.
/// </summary>
internal static class SftpDialogs
{
    private const int MaxListedFiles = 8;

    // One at a time: a second ContentDialog on the same XamlRoot throws,
    // and a second drop while one is being confirmed should not queue.
    private static bool _open;

    /// <summary>
    /// Ask where on <paramref name="target"/> to put the dropped
    /// <paramref name="localPaths"/>. Returns the remote folder, or null
    /// when the user cancels.
    /// </summary>
    public static async Task<string?> AskUploadAsync(
        XamlRoot? root,
        SshTarget target,
        IReadOnlyList<string> localPaths,
        string? guessedDirectory)
    {
        if (_open || root is null) return null;
        _open = true;
        try
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };

            var names = new List<string>();
            for (var i = 0; i < localPaths.Count && i < MaxListedFiles; i++)
                names.Add(Path.GetFileName(localPaths[i].TrimEnd('\\', '/')));
            if (localPaths.Count > MaxListedFiles)
                names.Add($"and {localPaths.Count - MaxListedFiles} more");
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", names),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });

            var folder = new TextBox
            {
                Header = "Remote folder",
                Text = guessedDirectory ?? "~",
                IsSpellCheckEnabled = false,
            };
            panel.Children.Add(folder);
            panel.Children.Add(Caption(guessedDirectory is null
                ? "The shell did not report its folder. ~ is your home folder on the host."
                : "Taken from the window title. Change it if the shell is somewhere else."));

            var dialog = new ContentDialog
            {
                Title = $"Upload to {target.Display}",
                Content = panel,
                PrimaryButtonText = "Upload",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            folder.TextChanged += (_, _) =>
                dialog.IsPrimaryButtonEnabled = RemotePath.IsValid(folder.Text.Trim());

            // Enter or Escape closes it back onto the pane; the key's
            // character must not follow into the shell.
            Ghostty.Input.ConsumedCloseKey.Watch(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

            var chosen = folder.Text.Trim();
            return RemotePath.IsValid(chosen) ? chosen : null;
        }
        catch (Exception)
        {
            // Another dialog owns this XamlRoot or the window is closing:
            // not transferring is the safe answer.
            return null;
        }
        finally
        {
            _open = false;
        }
    }

    /// <summary>
    /// Confirm a download: which remote path, and which local folder it
    /// lands in. Returns null when the user cancels.
    /// </summary>
    public static async Task<(string RemotePath, string LocalDirectory)?> AskDownloadAsync(
        XamlRoot? root,
        IntPtr ownerHwnd,
        SshTarget target,
        string remotePath,
        bool directoryKnown)
    {
        if (_open || root is null) return null;
        _open = true;
        try
        {
            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };

            var remote = new TextBox
            {
                Header = "Remote path",
                Text = remotePath,
                IsSpellCheckEnabled = false,
            };
            panel.Children.Add(remote);
            panel.Children.Add(Caption(directoryKnown
                ? "Built from the window title and the name you clicked. Change it if it is wrong."
                : "The shell did not report its folder, so this is relative to your home folder on the host."));

            var local = new TextBox
            {
                Header = "Save to",
                Text = DefaultDownloadDirectory(),
                IsSpellCheckEnabled = false,
            };
            var browse = new Button { Content = "Browse...", VerticalAlignment = VerticalAlignment.Bottom };
            browse.Click += async (_, _) =>
            {
                try
                {
                    var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
                    if (await picker.PickSingleFolderAsync() is { } picked) local.Text = picked.Path;
                }
                catch (Exception)
                {
                    // The picker is a convenience; the path can be typed.
                }
            };
            var localRow = new Grid { ColumnSpacing = 8 };
            localRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            localRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(browse, 1);
            localRow.Children.Add(local);
            localRow.Children.Add(browse);
            panel.Children.Add(localRow);

            var dialog = new ContentDialog
            {
                Title = $"Download from {target.Display}",
                Content = panel,
                PrimaryButtonText = "Download",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            void Validate() => dialog.IsPrimaryButtonEnabled =
                RemotePath.IsValid(remote.Text.Trim()) && Directory.Exists(local.Text.Trim());
            remote.TextChanged += (_, _) => Validate();
            local.TextChanged += (_, _) => Validate();
            Validate();

            Ghostty.Input.ConsumedCloseKey.Watch(dialog);
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

            var chosenRemote = remote.Text.Trim();
            var chosenLocal = local.Text.Trim();
            if (!RemotePath.IsValid(chosenRemote) || !Directory.Exists(chosenLocal)) return null;
            return (chosenRemote, chosenLocal);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _open = false;
        }
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.7,
        FontSize = 12,
    };

    private static string DefaultDownloadDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(home, "Downloads");
        return Directory.Exists(downloads) ? downloads : home;
    }
}
