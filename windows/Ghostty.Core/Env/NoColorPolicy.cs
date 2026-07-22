using System;

namespace Ghostty.Core.Env;

/// <summary>
/// Result of the <c>NO_COLOR</c> startup decision: whether to remove the
/// variable from the child-shell environment and whether to surface the
/// one-time notice to the user.
/// </summary>
public readonly record struct NoColorOutcome(bool Strip, bool Notify);

/// <summary>
/// Pure decision for how Wintty reacts to a <c>NO_COLOR</c> value inherited
/// from the launching environment.
///
/// <para>
/// <c>NO_COLOR</c> (see https://no-color.org) makes color-aware programs
/// disable ANSI color. PowerShell 7.2+ honors it by switching
/// <c>$PSStyle.OutputRendering</c> to <c>PlainText</c>, which strips color
/// from everything it renders — including a powerline prompt's background
/// segments. When the variable was set unintentionally (e.g. inherited from
/// a parent process) that reads as "colors are broken". Wintty can remove it
/// from the shell environment it spawns so color works, and tell the user it
/// did so.
/// </para>
///
/// <para>
/// Kept I/O-free so it unit-tests without the environment or XAML runtime;
/// the caller supplies the observed presence and the user's preference (as
/// already normalized by <c>WindowsOnlyKeyParsers.ParseStringAllowed</c>).
/// </para>
/// </summary>
public static class NoColorPolicy
{
    /// <summary>Strip <c>NO_COLOR</c> and show the one-time notice.</summary>
    public const string Notify = "notify";

    /// <summary>Strip <c>NO_COLOR</c> silently (no notice).</summary>
    public const string Strip = "strip";

    /// <summary>Honor <c>NO_COLOR</c>: leave it untouched, show nothing.</summary>
    public const string Keep = "keep";

    /// <summary>Allowed values for the <c>no-color-override</c> config key.</summary>
    public static readonly string[] Allowed = { Notify, Strip, Keep };

    /// <summary>Default when the key is unset or invalid.</summary>
    public const string Default = Notify;

    /// <summary>
    /// Decide what to do given whether <c>NO_COLOR</c> is present in the
    /// environment and the user's override preference. Any unrecognized
    /// override behaves like <see cref="Notify"/> so the decision is
    /// self-consistent even if a raw value slips past normalization.
    /// </summary>
    public static NoColorOutcome Decide(bool present, string @override)
    {
        if (!present) return new NoColorOutcome(Strip: false, Notify: false);

        return @override switch
        {
            Keep => new NoColorOutcome(Strip: false, Notify: false),
            Strip => new NoColorOutcome(Strip: true, Notify: false),
            _ => new NoColorOutcome(Strip: true, Notify: true), // notify (default)
        };
    }
}
