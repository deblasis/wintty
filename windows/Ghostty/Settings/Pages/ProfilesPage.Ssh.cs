using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ghostty.Controls.Settings;
using Ghostty.Core.Profiles;
using Ghostty.Core.Ssh;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ghostty.Settings.Pages;

/// <summary>
/// The SSH connections section of the Profiles page: one card per saved
/// connection (<c>ssh.&lt;id&gt;.*</c> in the config file) with Edit and
/// Remove, a card to add one, and the switch that also lists the hosts
/// ssh already knows about. Every change is written to the config file
/// and comes back through the normal reload, so the file stays the one
/// source of truth.
/// </summary>
internal sealed partial class ProfilesPage
{
    private const string SshDiscoveryKey = "ssh-hosts-discovery";

    // One dialog at a time: a second ContentDialog on the same XamlRoot
    // throws on the async-void stack.
    private bool _sshDialogOpen;

    private IProfileConfigSource? SshSource => _configService as IProfileConfigSource;

    private void RebindSsh()
    {
        var source = SshSource;
        var connections = source?.SshConnections ?? Array.Empty<SshConnection>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Rebuilt wholesale: the rows hold no state of their own, and a
        // connection can change every field (including its name) at once.
        SshGroup.Cards.Clear();

        foreach (var connection in connections)
        {
            var edit = new Button { Content = "Edit", Tag = connection };
            edit.Click += OnSshEditClicked;
            var remove = new Button { Content = "Remove", Tag = connection };
            remove.Click += OnSshRemoveClicked;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);

            SshGroup.Cards.Add(new SettingsCard
            {
                Header = connection.DisplayName,
                Description = connection.Command(home),
                Control = buttons,
            });
        }

        var add = new Button { Content = "Add connection" };
        add.Click += OnSshAddClicked;
        SshGroup.Cards.Add(new SettingsCard
        {
            Header = "New connection",
            Description = "Save a host, login, port and key as a profile in the new-tab menu.",
            Control = add,
        });

        var discovery = new ToggleSwitch { IsOn = source?.SshHostsDiscovery ?? false };
        discovery.Toggled += OnSshDiscoveryToggled;
        SshGroup.Cards.Add(new SettingsCard
        {
            Header = "List hosts ssh already knows",
            Description = "Also show the Host entries in ~/.ssh/config and the named hosts in ~/.ssh/known_hosts.",
            Control = discovery,
        });
    }

    private void OnSshDiscoveryToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle) return;
        _writer.Write(() =>
        {
            // Off is the default, so turning it off removes the line.
            if (toggle.IsOn) _editor.SetValue(SshDiscoveryKey, "true");
            else _editor.RemoveValue(SshDiscoveryKey);
        }, SshDiscoveryKey);
    }

    private async void OnSshAddClicked(object sender, RoutedEventArgs e)
        => await EditSshConnectionAsync(existing: null);

    private async void OnSshEditClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SshConnection connection)
            await EditSshConnectionAsync(connection);
    }

    private void OnSshRemoveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SshConnection connection) return;
        var prefix = SshConnectionParser.KeyPrefix + connection.Id + ".";
        _writer.Write(() =>
        {
            foreach (var subKey in SshConnectionParser.SubKeys)
                _editor.RemoveValue(prefix + subKey);
        }, prefix + "*");
    }

    private async Task EditSshConnectionAsync(SshConnection? existing)
    {
        if (_sshDialogOpen || XamlRoot is null) return;
        _sshDialogOpen = true;
        try
        {
            var name = Field("Name", existing?.Name, "Shown in the new-tab menu. Optional.");
            var host = Field("Host", existing?.Host, "devel.local");
            var user = Field("User", existing?.User, "Leave empty to let ssh decide.");
            var port = Field("Port", existing?.Port?.ToString(System.Globalization.CultureInfo.InvariantCulture), "22");
            var identity = Field("Identity file", existing?.IdentityFile, "~/.ssh/id_ed25519");
            var jump = Field("Jump host", existing?.JumpHost, "[user@]host[:port]");
            var agent = new ToggleSwitch
            {
                Header = "Forward the ssh agent",
                IsOn = existing?.ForwardAgent ?? false,
            };
            var error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                Visibility = Visibility.Collapsed,
            };

            var browse = new Button { Content = "Browse...", VerticalAlignment = VerticalAlignment.Bottom };
            browse.Click += async (_, _) =>
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    var windowId = XamlRoot.ContentIslandEnvironment.AppWindowId;
                    var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(windowId);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                    picker.FileTypeFilter.Add("*");
                    if (await picker.PickSingleFileAsync() is { } file) identity.Text = file.Path;
                }
                catch (Exception)
                {
                    // The picker is a convenience; the path can be typed.
                }
            };
            var identityRow = new Grid { ColumnSpacing = 8 };
            identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(browse, 1);
            identityRow.Children.Add(identity);
            identityRow.Children.Add(browse);

            var panel = new StackPanel { Spacing = 10, MinWidth = 400 };
            panel.Children.Add(host);
            panel.Children.Add(user);
            panel.Children.Add(port);
            panel.Children.Add(identityRow);
            panel.Children.Add(jump);
            panel.Children.Add(agent);
            panel.Children.Add(name);
            panel.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title = existing is null ? "New SSH connection" : "Edit SSH connection",
                Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            SshConnection? result = null;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                var fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["name"] = name.Text,
                    ["host"] = host.Text,
                    ["user"] = user.Text,
                    ["port"] = port.Text,
                    ["identity-file"] = identity.Text,
                    ["jump-host"] = jump.Text,
                    ["forward-agent"] = agent.IsOn ? "true" : "",
                };
                var id = existing?.Id ?? SshConnectionParser.SuggestId(host.Text.Trim(), ExistingSshIds());
                var problem = SshConnectionParser.TryBuild(id, fields, out result);
                if (problem is null) return;

                // Keep the dialog open with the reason shown.
                args.Cancel = true;
                result = null;
                error.Text = char.ToUpperInvariant(problem[0]) + problem[1..] + ".";
                error.Visibility = Visibility.Visible;
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || result is null) return;
            SaveSshConnection(result);
        }
        catch (Exception)
        {
            // Another dialog owns this XamlRoot or the window is closing.
        }
        finally
        {
            _sshDialogOpen = false;
        }
    }

    private void SaveSshConnection(SshConnection connection)
    {
        var prefix = SshConnectionParser.KeyPrefix + connection.Id + ".";
        var lines = SshConnectionParser.ToConfig(connection);
        _writer.Write(() =>
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in lines)
            {
                _editor.SetValue(key, value);
                written.Add(key);
            }
            // A field the user emptied must not survive from the old block.
            foreach (var subKey in SshConnectionParser.SubKeys)
                if (!written.Contains(prefix + subKey)) _editor.RemoveValue(prefix + subKey);
        }, prefix + "*");
    }

    private List<string> ExistingSshIds()
    {
        var ids = new List<string>();
        if (SshSource is { } source)
            foreach (var c in source.SshConnections) ids.Add(c.Id);
        return ids;
    }

    private static TextBox Field(string header, string? value, string placeholder) => new()
    {
        Header = header,
        Text = value ?? string.Empty,
        PlaceholderText = placeholder,
        IsSpellCheckEnabled = false,
    };
}
