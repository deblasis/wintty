using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Ghostty.Core.Sponsor.Auth;

/// <summary>
/// Minimal JWT payload decoder. Reads claims by splitting on <c>.</c> and
/// base64url-decoding the middle segment. Does NOT validate the
/// signature: the Worker is authoritative, the client only trusts the
/// Worker's TLS connection. Claims are read purely to drive the
/// proactive refresh schedule and for log diagnostics.
/// </summary>
internal sealed record JwtClaims
{
    public required string Subject { get; init; }
    public string? Login { get; init; }
    public int TierCents { get; init; }
    public IReadOnlyList<string> ChannelAllow { get; init; } = [];
    public string? DefaultChannel { get; init; }
    public string? JwtId { get; init; }
    public required DateTime ExpiresAt { get; init; }

    /// <summary>
    /// Parses a compact JWT string. Throws <see cref="AuthException"/>
    /// with <see cref="AuthErrorKind.Unknown"/> on any shape, base64, or
    /// JSON failure. Caller logs and treats as "no usable token".
    /// </summary>
    public static JwtClaims Parse(string jwt)
    {
        ArgumentException.ThrowIfNullOrEmpty(jwt);

        var parts = jwt.Split('.');
        if (parts.Length != 3)
            throw new AuthException(AuthErrorKind.Unknown, $"malformed JWT: expected 3 segments, got {parts.Length}");

        byte[] payloadBytes;
        try
        {
            payloadBytes = DecodeBase64Url(parts[1]);
        }
        catch (FormatException ex)
        {
            throw new AuthException(AuthErrorKind.Unknown, "malformed JWT: base64url decode failed", ex);
        }

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(payloadBytes);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AuthException(AuthErrorKind.Unknown, "malformed JWT: payload is not JSON", ex);
        }

        var sub = ReadString(root, "sub")
            ?? throw new AuthException(AuthErrorKind.Unknown, "JWT missing required claim 'sub'");
        var expUnix = ReadLong(root, "exp")
            ?? throw new AuthException(AuthErrorKind.Unknown, "JWT missing required claim 'exp'");

        var login     = ReadString(root, "login");
        var tier      = (int)(ReadLong(root, "tier_cents") ?? 0);
        var jti       = ReadString(root, "jti");
        var defaultCh = ReadString(root, "default_channel");
        var channels  = ReadStringArray(root, "channel_allow");

        return new JwtClaims
        {
            Subject        = sub,
            Login          = login,
            TierCents      = tier,
            ChannelAllow   = channels,
            DefaultChannel = defaultCh,
            JwtId          = jti,
            ExpiresAt      = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime,
        };
    }

    private static byte[] DecodeBase64Url(string s)
    {
        // Restore padding. Base64url strips '=' and uses '-'/'_' instead of '+'/'/'.
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "=";  break;
            case 0: break;
            default: throw new FormatException("invalid base64url length");
        }
        return Convert.FromBase64String(padded);
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long? ReadLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>(v.GetArrayLength());
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                list.Add(item.GetString()!);
        }
        return list;
    }
}
