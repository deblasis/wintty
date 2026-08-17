using System;
using System.Collections.Generic;

namespace Ghostty.Core.JumpList;

/// <summary>
/// Jump-list tasks and pinned-profile clicks. The builder emits
/// <c>--jumplist-action=</c> / <c>--jumplist-profile=</c>; the running
/// instance (or a cold start) parses the same argv.
/// </summary>
internal enum JumpListAction
{
    None,
    NewWindow,
    NewTab,
}

internal readonly record struct JumpListLaunch(JumpListAction Action, string? ProfileId)
{
    private const string ActionPrefix = "--jumplist-action=";
    private const string ProfilePrefix = "--jumplist-profile=";

    public static JumpListLaunch Parse(IReadOnlyList<string>? args)
    {
        var action = JumpListAction.None;
        string? profileId = null;
        if (args is null) return new(action, profileId);

        foreach (var arg in args)
        {
            if (arg.StartsWith(ActionPrefix, StringComparison.Ordinal))
            {
                action = arg.AsSpan(ActionPrefix.Length) switch
                {
                    "new-window" => JumpListAction.NewWindow,
                    "new-tab" => JumpListAction.NewTab,
                    _ => JumpListAction.None,
                };
            }
            else if (arg.StartsWith(ProfilePrefix, StringComparison.Ordinal))
            {
                var id = arg[ProfilePrefix.Length..];
                profileId = id.Length == 0 ? null : id;
            }
        }

        // A pinned-profile click carries only --jumplist-profile=id.
        // That is a new window seeded with that profile.
        if (action == JumpListAction.None && profileId is not null)
            action = JumpListAction.NewWindow;

        return new(action, profileId);
    }
}
