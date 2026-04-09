using System.Collections.Generic;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Narrow facade over <see cref="Ghostty.Interop.ShellInterop.ICustomDestinationList"/>.
/// Exposes only the operations <see cref="JumpListBuilder"/> uses
/// so tests can substitute a recording fake without touching COM.
/// </summary>
internal interface ICustomDestinationListFacade
{
    /// <summary>Set the AppUserModelID this list is associated with.</summary>
    void SetAppId(string appId);

    /// <summary>Begin a new list cycle. Returns the max slots.</summary>
    uint BeginList();

    /// <summary>Add a "Tasks" entry: a shell link with path + args + title.</summary>
    void AddTask(string exePath, string arguments, string title);

    /// <summary>Add a custom category with its own entries.</summary>
    void AddCategory(string categoryName, IReadOnlyList<(string exePath, string args, string title)> entries);

    /// <summary>Commit the list to the shell.</summary>
    void Commit();
}
