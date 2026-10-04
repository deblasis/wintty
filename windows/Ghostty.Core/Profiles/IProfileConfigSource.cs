using System;
using System.Collections.Generic;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Narrow, read-only view of the parsed profile section of the user's
/// config file. <c>ConfigService</c> implements this alongside
/// <c>IConfigService</c>; <c>ProfileRegistry</c> depends only on this
/// interface so it can be unit-tested cross-platform against a fake.
/// All properties return the last successfully-parsed view and are
/// replaced atomically after a config reload; the
/// <see cref="ProfileConfigChanged"/> event fires on the UI dispatcher
/// once the new values are visible.
/// </summary>
public interface IProfileConfigSource
{
    /// <summary>
    /// Parsed user-defined profiles from <c>profile.&lt;id&gt;.*</c>
    /// blocks. Keys are lowercase-ASCII ids. Replaced on every reload.
    /// </summary>
    IReadOnlyDictionary<string, ProfileDef> ParsedProfiles { get; }

    /// <summary>
    /// Partial <c>profile.&lt;id&gt;.*</c> blocks by id: the ones missing
    /// the <c>name</c> / <c>command</c> a standalone profile needs, and so
    /// not in <see cref="ParsedProfiles"/>. This is the shape the settings
    /// page writes -- one subkey per edit, no definition around it -- and
    /// <c>ProfileOrderResolver</c> merges each entry onto the discovered
    /// profile of the same id. An id nothing was discovered for resolves to
    /// no profile at all and keeps its parser warning.
    /// Replaced on every reload, like the rest of the view.
    /// </summary>
    IReadOnlyDictionary<string, ProfileOverride> ProfileOverrides { get; }

    /// <summary>
    /// Ids from <c>profile-order = a,b,c</c>. Empty when the key is
    /// absent. Order is preserved; ids absent from both
    /// <see cref="ParsedProfiles"/> and the registry's discovered list
    /// are filtered silently by <c>ProfileOrderResolver</c>.
    /// </summary>
    IReadOnlyList<string> ProfileOrder { get; }

    /// <summary>
    /// Value of <c>default-profile</c>, or <see langword="null"/> when
    /// the key is absent. The registry falls back to the first visible
    /// profile when this id is unknown.
    /// </summary>
    string? DefaultProfileId { get; }

    /// <summary>
    /// Ids for which <c>profile.&lt;id&gt;.hidden = true</c> is set.
    /// Primarily used to hide discovered profiles; for user-defined
    /// profiles the <c>Hidden</c> flag on <see cref="ProfileDef"/>
    /// already captures this but inclusion here is idempotent.
    /// </summary>
    IReadOnlySet<string> HiddenProfileIds { get; }

    /// <summary>
    /// Non-fatal warnings from <see cref="ProfileSourceParser.Parse"/>
    /// (missing required keys, malformed visuals, etc.). Surfaced in
    /// the settings UI.
    /// </summary>
    IReadOnlyList<string> ProfileWarnings { get; }

    /// <summary>
    /// Raised on the UI dispatcher after <c>ConfigService</c> finishes
    /// a successful reload. Fires once per reload regardless of whether
    /// the profile-view values actually changed -- consumers must treat
    /// recomposition as idempotent.
    /// </summary>
    event Action? ProfileConfigChanged;
}
