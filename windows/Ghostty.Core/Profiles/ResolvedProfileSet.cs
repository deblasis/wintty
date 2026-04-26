using System.Collections.Generic;

namespace Ghostty.Core.Profiles;

/// <summary>
/// Output of <see cref="ProfileOrderResolver.Resolve"/>: the visible
/// profiles UI consumers iterate plus the hidden ones the settings
/// inspector needs in order to offer an unhide affordance.
/// </summary>
public sealed record ResolvedProfileSet(
    IReadOnlyList<ResolvedProfile> Visible,
    IReadOnlyList<ResolvedProfile> Hidden);
