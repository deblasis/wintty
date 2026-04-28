using System;
using System.Collections.Generic;
using Ghostty.Dialogs;
using Microsoft.UI.Xaml;

namespace Ghostty.Commands;

/// <summary>
/// Single-item command source that opens the Version dialog from the
/// command palette. <see cref="Refresh"/> is a no-op because version data
/// is process-static.
/// </summary>
internal sealed class VersionCommandSource : ICommandSource
{
    private readonly Func<XamlRoot?> _xamlRootProvider;
    private readonly DialogTracker _dialogs;
    private readonly IReadOnlyList<CommandItem> _commands;

    public VersionCommandSource(Func<XamlRoot?> xamlRootProvider, DialogTracker dialogs)
    {
        _xamlRootProvider = xamlRootProvider;
        _dialogs = dialogs;
        _commands = new[]
        {
            new CommandItem
            {
                Id = "version",
                Title = "Version",
                Description = "Show Wintty version, build, and edition",
                Category = CommandCategory.About,
                LeadingIcon = "\uE946", // Segoe Fluent Info glyph
                Execute = _ => OpenDialog(),
            },
        };
    }

    public IReadOnlyList<CommandItem> GetCommands() => _commands;

    public void Refresh() { /* version data is process-static */ }

    private async void OpenDialog()
    {
        var xamlRoot = _xamlRootProvider();
        if (xamlRoot is null) return;

        var dialog = new VersionDialog
        {
            XamlRoot = xamlRoot,
        };
        using (_dialogs.Track(dialog))
        {
            await dialog.ShowAsync();
        }
    }
}
