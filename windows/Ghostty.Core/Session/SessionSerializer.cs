using System.Text.Json;

namespace Ghostty.Core.Session;

/// <summary>
/// String &lt;-&gt; <see cref="SessionState"/> via the source-generated
/// context. Pure (no file IO) so it is unit-testable; the app's
/// SessionStore wraps this with file access. Malformed input deserializes
/// to null so a corrupt session never blocks startup.
/// </summary>
internal static class SessionSerializer
{
    public static string Serialize(SessionState state) =>
        JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState);

    public static SessionState? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize(json, SessionJsonContext.Default.SessionState);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
