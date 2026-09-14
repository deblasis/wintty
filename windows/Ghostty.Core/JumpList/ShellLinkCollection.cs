using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Turns jump list entries into the IObjectArray that
/// ICustomDestinationList.AddUserTasks and AppendCategory take: one real
/// shell link per entry, collected in one EnumerableObjectCollection.
///
/// Lives in Ghostty.Core (not next to CustomDestinationListFacade in the
/// WinUI 3 shell) so Ghostty.Tests can run it against real shell COM
/// objects, the same reason ShellLinkTitleHelper lives here.
///
/// One entry that cannot be built is skipped and reported through
/// <c>onSkipped</c>, so a single bad path or title does not cost the
/// whole list. A batch where EVERY entry fails throws instead: that is a
/// systematic failure, and committing an empty list would replace a
/// good one the user already has.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal static class ShellLinkCollection
{
    /// <summary>Reports one entry that was left out of the collection.</summary>
    internal delegate void EntrySkipped(Exception error, string exePath, string arguments, string title);

    public static IObjectArray Build(
        IReadOnlyList<(string exePath, string args, string title)> entries,
        EntrySkipped onSkipped)
        => Build(entries, CreateShellLink, onSkipped);

    /// <summary>
    /// <see cref="Build(IReadOnlyList{ValueTuple{string, string, string}}, EntrySkipped)"/>
    /// with the link factory as a seam, so a test can make chosen entries
    /// fail while the rest still go through the real ShellLink.
    /// </summary>
    internal static IObjectArray Build(
        IReadOnlyList<(string exePath, string args, string title)> entries,
        Func<string, string, string, IShellLinkW> createLink,
        EntrySkipped onSkipped)
    {
        var collection = EnumerableObjectCollection.CreateInstance<IObjectCollection>();
        List<(Exception error, string exePath, string args, string title)>? failures = null;
        foreach (var (exePath, args, title) in entries)
        {
            IShellLinkW link;
            try
            {
                link = createLink(exePath, args, title);
            }
            catch (Exception ex)
            {
                (failures ??= new()).Add((ex, exePath, args, title));
                continue;
            }
            collection.AddObject(link);
        }

        if (failures is not null)
        {
            if (failures.Count == entries.Count)
            {
                // Nothing reported per entry here: the caller logs this
                // one exception, which names the first failed entry and
                // carries its error as the inner exception.
                var first = failures[0];
                throw new InvalidOperationException(
                    $"None of the {entries.Count} jump list entries could be built, so the list was not changed. "
                    + $"First failure: \"{first.title}\" ({first.args}) for {first.exePath}.",
                    first.error);
            }
            foreach (var f in failures)
                onSkipped(f.error, f.exePath, f.args, f.title);
        }

        // The generated IObjectCollection derives from IObjectArray, so
        // this is a plain upcast of the same native-backed object. Do NOT
        // route it through a COM-callable wrapper (ComWrappers'
        // GetOrCreateComInterfaceForObject) and re-wrap it: a CCW built
        // over this object answers QueryInterface only for IUnknown, so
        // the IObjectArray cast on the re-wrapped object throws
        // E_NOINTERFACE and the list is never committed.
        return collection;
    }

    /// <summary>Builds one real shell link for a jump list entry.</summary>
    public static unsafe IShellLinkW CreateShellLink(string exePath, string arguments, string title)
    {
        var link = ShellLink.CreateInstance<IShellLinkW>();
        // This project's generation has no string overloads for these
        // setters; each copies the string before returning, so pinning
        // for the duration of the call is enough.
        fixed (char* p = exePath) link.SetPath(new PCWSTR(p));
        fixed (char* p = arguments) link.SetArguments(new PCWSTR(p));
        fixed (char* p = title) link.SetDescription(new PCWSTR(p));
        // Title shown in the jump list comes from System.Title, not
        // the description. Set it via the IPropertyStore side of the
        // same object.
        ShellLinkTitleHelper.SetTitle(link, title);
        return link;
    }
}
