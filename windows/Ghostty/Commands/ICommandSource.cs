using System.Collections.Generic;

namespace Ghostty.Commands;

internal interface ICommandSource
{
    IReadOnlyList<CommandItem> GetCommands();
    void Refresh();
}
