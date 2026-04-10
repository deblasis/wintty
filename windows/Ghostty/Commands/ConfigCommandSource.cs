using System.Collections.Generic;

namespace Ghostty.Commands;

internal sealed class ConfigCommandSource : ICommandSource
{
    public IReadOnlyList<CommandItem> GetCommands() => [];

    public void Refresh()
    {
        // Will call ghostty_config_command_list when interop lands
    }
}
