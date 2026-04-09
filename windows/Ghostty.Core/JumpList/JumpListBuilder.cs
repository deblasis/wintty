using System;
using System.Collections.Generic;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Builds the Windows jump list for Ghostty. Consumes an
/// <see cref="ICustomDestinationListFacade"/> so tests can
/// substitute a recording fake; the real call site in
/// <c>App.xaml.cs</c> passes <see cref="CustomDestinationListFacade"/>.
///
/// Tasks: "New Window" and "New Tab in Current Window" are static
/// and always present.
///
/// Pinned profiles: sourced from the <see cref="_profilesProvider"/>
/// factory, which is currently always empty behind a
/// <c>TODO(config)</c> marker. When profiles exist, each becomes
/// an entry in a "Pinned Profiles" custom category with the
/// profile's display name as the title and its shell command as
/// the invocation argument.
/// </summary>
internal sealed class JumpListBuilder
{
    private readonly ICustomDestinationListFacade _facade;
    private readonly Func<IReadOnlyList<ProfileEntry>> _profilesProvider;
    private readonly string _exePath;
    private readonly string _appId;

    public JumpListBuilder(
        ICustomDestinationListFacade facade,
        Func<IReadOnlyList<ProfileEntry>> profilesProvider,
        string exePath,
        string appId)
    {
        _facade = facade;
        _profilesProvider = profilesProvider;
        _exePath = exePath;
        _appId = appId;
    }

    public void Build()
    {
        _facade.SetAppId(_appId);
        _facade.BeginList();

        // Static tasks. These always appear. The CLI argument shape
        // is a placeholder: real wiring happens when libghostty's
        // --action / --jumplist-action plumbing is defined.
        // TODO(jumplist): finalise the CLI arg contract with libghostty
        _facade.AddTask(_exePath, "--jumplist-action=new-window", "New Window");
        _facade.AddTask(_exePath, "--jumplist-action=new-tab", "New Tab in Current Window");

        // Pinned profiles category. Empty today; TODO(config): profiles
        var profiles = _profilesProvider();
        if (profiles.Count > 0)
        {
            var entries = new List<(string exePath, string args, string title)>(profiles.Count);
            foreach (var p in profiles)
            {
                entries.Add((
                    exePath: _exePath,
                    args: $"--jumplist-profile={p.Id}",
                    title: p.DisplayName));
            }
            _facade.AddCategory("Pinned Profiles", entries);
        }

        _facade.Commit();
    }
}
