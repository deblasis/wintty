using System;

namespace Ghostty.Core.Config;

/// <summary>
/// Upstream <c>confirm-close-surface</c> (false / true / always).
/// Tab close on Windows maps:
/// <list type="bullet">
/// <item><c>false</c> — never prompt</item>
/// <item><c>true</c> — prompt when the tab has more than one pane</item>
/// <item><c>always</c> — prompt on every tab close (even a single pane)</item>
/// </list>
/// Process-at-prompt detection (Mac/GTK <c>true</c>) is not wired yet.
/// </summary>
public enum ConfirmCloseSurfaceMode
{
    False,
    True,
    Always,
}

public static class ConfirmCloseSurfaceParser
{
    public static ConfirmCloseSurfaceMode Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ConfirmCloseSurfaceMode.True;
        var trimmed = raw.Trim();
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase))
            return ConfirmCloseSurfaceMode.False;
        if (trimmed.Equals("always", StringComparison.OrdinalIgnoreCase))
            return ConfirmCloseSurfaceMode.Always;
        return ConfirmCloseSurfaceMode.True;
    }

    public static bool ShouldConfirmTabClose(ConfirmCloseSurfaceMode mode, int paneCount)
        => mode switch
        {
            ConfirmCloseSurfaceMode.False => false,
            ConfirmCloseSurfaceMode.Always => paneCount >= 1,
            _ => paneCount > 1,
        };
}
