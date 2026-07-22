using System;

namespace Ghostty.Core.Env;

/// <summary>
/// Result of the <c>NO_COLOR</c> startup decision: whether to remove the
/// variable from the child-shell environment and whether to surface the notice
/// to the user.
/// </summary>
public readonly record struct NoColorOutcome(bool Strip, bool Notify);

/// <summary>
/// Pure decision for how Wintty reacts to a <c>NO_COLOR</c> value inherited
/// from the launching environment.
///
/// <para>
/// <c>NO_COLOR</c> (see https://no-color.org) is a user-facing convention that
/// tells color-aware programs to disable ANSI color. A terminal emulator
/// normally passes it through untouched — the programs inside decide. So the
/// default here is to <b>honor</b> it. PowerShell 7.2+ obeys it by switching
/// <c>$PSStyle.OutputRendering</c> to <c>PlainText</c>, which drops color from
/// everything it renders (including a powerline prompt's background segments).
/// Because that can be surprising when <c>NO_COLOR</c> was inherited without
/// the user realizing, the default also shows a one-time notice explaining why
/// output is monochrome and offering to enable color for Wintty. Users who want
/// color unconditionally can opt into stripping; users who set <c>NO_COLOR</c>
/// deliberately can silence the notice.
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
    /// <summary>Honor <c>NO_COLOR</c> and show the one-time notice (offering to enable color).</summary>
    public const string Notify = "notify";

    /// <summary>Strip <c>NO_COLOR</c> so color always works; show no notice.</summary>
    public const string Strip = "strip";

    /// <summary>Honor <c>NO_COLOR</c> silently — leave it untouched, show nothing.</summary>
    public const string Keep = "keep";

    /// <summary>Allowed values for the <c>no-color-override</c> config key.</summary>
    public static readonly string[] Allowed = { Notify, Strip, Keep };

    /// <summary>Default when the key is unset or invalid: honor + notify.</summary>
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
            Strip => new NoColorOutcome(Strip: true, Notify: false),
            Keep => new NoColorOutcome(Strip: false, Notify: false),
            _ => new NoColorOutcome(Strip: false, Notify: true), // notify (default): honor + inform
        };
    }
}
