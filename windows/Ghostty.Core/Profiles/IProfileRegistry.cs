using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Ordered, UI-dispatched view of user-defined + discovered profiles.
/// Recomposed on config reload and on discovery refresh. Consumers
/// subscribe to <see cref="ProfilesChanged"/> and re-read
/// <see cref="Profiles"/> / <see cref="DefaultProfileId"/> when it
/// fires. Safe to read from any thread -- <see cref="Profiles"/> is a
/// volatile immutable snapshot.
/// </summary>
public interface IProfileRegistry : IDisposable
{
    /// <summary>
    /// Ordered composition of user + discovered profiles, with
    /// user-defined entries winning on id conflicts. Never null.
    /// </summary>
    IReadOnlyList<ResolvedProfile> Profiles { get; }

    /// <summary>
    /// Profiles whose hidden flag is set (either
    /// <see cref="ProfileDef.Hidden"/> or
    /// <see cref="IProfileConfigSource.HiddenProfileIds"/>). Same record
    /// shape as <see cref="Profiles"/>. <see cref="ResolvedProfile.OrderIndex"/>
    /// restarts at 0 within this list. <see cref="ResolvedProfile.IsDefault"/>
    /// is always false on entries here -- the resolver never picks a
    /// hidden profile as default.
    /// </summary>
    IReadOnlyList<ResolvedProfile> HiddenProfiles { get; }

    /// <summary>
    /// Id of the entry with <c>IsDefault = true</c> in
    /// <see cref="Profiles"/>, or null when the list is empty.
    /// </summary>
    string? DefaultProfileId { get; }

    /// <summary>
    /// Monotonic counter for generation id. Bumped by 1 per successful
    /// recompose. Starts at 0 pre-ctor, reaches 1 after the ctor's
    /// initial synchronous compose, reaches 2 after the first
    /// background discovery completes.
    /// </summary>
    long Version { get; }

    /// <summary>
    /// Fired on the UI dispatcher after every successful recompose
    /// including the two ctor-triggered emissions.
    /// </summary>
    event Action<IProfileRegistry>? ProfilesChanged;

    /// <summary>
    /// O(1) lookup by id against the current snapshot. Returns null
    /// when the id is unknown.
    /// </summary>
    ResolvedProfile? Resolve(string profileId);

    /// <summary>
    /// Forces a probe run bypassing the 24h cache, then recomposes.
    /// Exposed for the settings-UI "Refresh discovery" affordance.
    /// </summary>
    Task RefreshDiscoveryAsync(CancellationToken ct);
}
