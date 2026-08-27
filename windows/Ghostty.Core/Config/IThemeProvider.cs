using System.Collections.Generic;

namespace Ghostty.Core.Config;

/// <summary>
/// Enumerates the theme files available to the user.
/// </summary>
/// <remarks>
/// This used to also carry resolved background/foreground/cursor/selection
/// colours and the font. Nothing read them and nothing refreshed them, so
/// they sat at fixed Catppuccin values regardless of config, waiting for
/// the first caller to trust them. Resolved colours come from
/// <see cref="IConfigService"/>, which is the side that actually tracks
/// the config and the OS colour scheme.
/// </remarks>
public interface IThemeProvider
{
    /// <summary>Available theme names (bundled + user).</summary>
    IReadOnlyList<string> AvailableThemes { get; }
}
