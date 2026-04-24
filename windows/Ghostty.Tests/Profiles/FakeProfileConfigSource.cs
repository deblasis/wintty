using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using Ghostty.Core.Profiles;

namespace Ghostty.Tests.Profiles;

/// <summary>
/// In-memory <see cref="IProfileConfigSource"/> for registry tests.
/// Each setter replaces the backing field and does NOT raise
/// <see cref="ProfileConfigChanged"/>; call <see cref="Raise"/>
/// explicitly when the test wants to simulate a config reload.
/// </summary>
internal sealed class FakeProfileConfigSource : IProfileConfigSource
{
    public IReadOnlyDictionary<string, ProfileDef> ParsedProfiles { get; set; } =
        new Dictionary<string, ProfileDef>();
    public IReadOnlyList<string> ProfileOrder { get; set; } = [];
    public string? DefaultProfileId { get; set; }
    public IReadOnlySet<string> HiddenProfileIds { get; set; } = FrozenSet<string>.Empty;
    public IReadOnlyList<string> ProfileWarnings { get; set; } = [];

    public event Action? ProfileConfigChanged;

    public void Raise() => ProfileConfigChanged?.Invoke();
}
