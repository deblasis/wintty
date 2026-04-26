namespace Ghostty.Core.Profiles;

/// <summary>
/// Builds the config key that toggles a profile's hidden state.
/// Centralised so the settings page and any future consumer can
/// share a single format invariant.
/// </summary>
public static class ProfileHiddenKey
{
    public static string For(string profileId)
        => $"profile.{profileId}.hidden";
}
