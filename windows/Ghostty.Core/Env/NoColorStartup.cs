using System;
using Ghostty.Core.Notifications;

namespace Ghostty.Core.Env;

/// <summary>
/// Applies the NO_COLOR startup policy: given whether <c>NO_COLOR</c> is present
/// and the configured <c>no-color-override</c>, performs the environment strip
/// (if the policy calls for one) via the injected callback and returns the
/// <see cref="Notice"/> to surface, or null when none is warranted.
///
/// <para>
/// Kept dependency-injected and UI-free so the branching and the notice's action
/// wiring are unit-testable without the WinUI runtime. The app layer supplies the
/// environment and persistence callbacks (and the logging inside them) and shows
/// the returned notice; this type owns only the decision, the copy, and which
/// action does what.
/// </para>
/// </summary>
public static class NoColorStartup
{
    /// <param name="present">Whether <c>NO_COLOR</c> is set in the environment.</param>
    /// <param name="overrideMode">The <c>no-color-override</c> config value.</param>
    /// <param name="removeFromEnv">
    /// Removes <c>NO_COLOR</c> from the process environment. Invoked now when the
    /// policy strips, and again by the notice's "enable color" action.
    /// </param>
    /// <param name="persistMode">
    /// Persists a resolved <c>no-color-override</c> value so the notice does not
    /// recur.
    /// </param>
    /// <returns>The notice to show, or null when none is warranted.</returns>
    public static Notice? Resolve(
        bool present,
        string overrideMode,
        Action removeFromEnv,
        Action<string> persistMode)
    {
        ArgumentNullException.ThrowIfNull(removeFromEnv);
        ArgumentNullException.ThrowIfNull(persistMode);

        var outcome = NoColorPolicy.Decide(present, overrideMode);
        if (outcome.Strip) removeFromEnv();
        if (!outcome.Notify) return null;

        return new Notice
        {
            Title = "NO_COLOR is set",
            Message = "NO_COLOR is set in your environment, so programs are rendering "
                + "without color (the no-color.org standard). Wintty honors it by "
                + "default; enabling color applies to tabs you open next.",
            Severity = NoticeSeverity.Informational,
            DedupKey = "no-color",
            Actions = new[]
            {
                // Enable color for subsequently-opened tabs: drop NO_COLOR from the
                // process env (new surfaces re-snapshot it) and remember the choice.
                new NoticeAction(
                    "Enable color",
                    () => { removeFromEnv(); persistMode(NoColorPolicy.Strip); },
                    IsPrimary: true),
                // Honor NO_COLOR going forward without nagging.
                new NoticeAction(
                    "Keep it off",
                    () => persistMode(NoColorPolicy.Keep)),
            },
        };
    }
}
