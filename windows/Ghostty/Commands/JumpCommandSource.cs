using System;
using System.Collections.Generic;
using Ghostty.Core.Tabs;

namespace Ghostty.Commands;

internal sealed class JumpCommandSource : ICommandSource
{
    private readonly TabManager _tabs;
    private readonly Action<int, int?> _jumpAction;
    private List<CommandItem>? _cache;

    public JumpCommandSource(TabManager tabs, Action<int, int?> jumpAction)
    {
        _tabs = tabs;
        _jumpAction = jumpAction;
    }

    public IReadOnlyList<CommandItem> GetCommands()
    {
        _cache ??= BuildCommands();
        return _cache;
    }

    public void Refresh() => _cache = null;

    private List<CommandItem> BuildCommands()
    {
        var commands = new List<CommandItem>();
        if (_tabs.Tabs.Count == 0) return commands;

        for (int tabIdx = 0; tabIdx < _tabs.Tabs.Count; tabIdx++)
        {
            var tab = _tabs.Tabs[tabIdx];
            var paneCount = tab.PaneHost.PaneCount;

            if (paneCount <= 1)
            {
                var capturedTab = tabIdx;
                commands.Add(new CommandItem
                {
                    Id = $"jump:tab{tabIdx}",
                    Title = $"Focus: {tab.EffectiveTitle}",
                    Description = $"Switch to tab {tabIdx + 1}",
                    Category = CommandCategory.Navigation,
                    LeadingIcon = "\uE737",
                    Execute = _ => _jumpAction(capturedTab, null),
                });
            }
            else
            {
                for (int paneIdx = 0; paneIdx < paneCount; paneIdx++)
                {
                    var capturedTab = tabIdx;
                    var capturedPane = paneIdx;
                    commands.Add(new CommandItem
                    {
                        Id = $"jump:tab{tabIdx}:pane{paneIdx}",
                        Title = $"Focus: {tab.EffectiveTitle} \u2014 pane {paneIdx + 1}",
                        Description = $"Switch to tab {tabIdx + 1}, pane {paneIdx + 1}",
                        Category = CommandCategory.Navigation,
                        LeadingIcon = "\uE737",
                        Execute = _ => _jumpAction(capturedTab, capturedPane),
                    });
                }
            }
        }

        return commands;
    }
}
