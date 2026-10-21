using System.Runtime.Versioning;

namespace Ghostty.Core.DefaultTerm;

/// <summary>Inputs the startup revalidation runs against.</summary>
/// <param name="Engine">The registration engine for this build.</param>
/// <param name="PassesBoundPathCheck">
/// Whether this exe resolves to the bound registration (the normalized
/// bound-path self-check: quotes stripped, environment strings expanded).
/// A dev or materialized build fails this and must never repair.
/// </param>
/// <param name="SingleInstanceEnabled">Whether single-instance mode is on.</param>
public sealed record RevalidationInputs(
    DefaultTermRegistration Engine,
    bool PassesBoundPathCheck,
    bool SingleInstanceEnabled);

/// <summary>What the revalidation pass did.</summary>
public enum RevalidationAction
{
    /// <summary>The registration is consistent; nothing to do.</summary>
    Healthy,

    /// <summary>The selection was lost; the lingering shared group was unwritten.</summary>
    ShadowUnwritten,

    /// <summary>The selection was lost and nothing of ours remains to hold.</summary>
    NothingToHold,

    /// <summary>The bindings drifted (install moved); they were rewritten.</summary>
    BindingsRepaired,

    /// <summary>The bindings drifted but this exe is not the bound one; repair deferred.</summary>
    DriftReported,

    /// <summary>An enable precondition no longer holds; the feature self-disabled.</summary>
    SelfDisabled,
}

/// <summary>The outcome of one revalidation pass, with the surfaced notice.</summary>
public sealed record RevalidationReport(RevalidationAction Action, string Notice);

/// <summary>
/// The startup check for an enabled build: it re-verifies that what the
/// enable wrote is still true, repairs what a moved install broke (only
/// when this exe is the bound one), unwrites shadows a lost selection left
/// behind, and self-disables when an enable precondition no longer holds.
/// A non-installed exe never repairs: it reports.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DefaultTermRevalidator
{
    public RevalidationReport Revalidate(RevalidationInputs inputs)
    {
        var engine = inputs.Engine;

        if (!engine.IsRegistered())
        {
            // The selection is not ours. A console binding we still own
            // lingers as a shadow over whoever holds the selection now;
            // the disable path unwrites exactly that and nothing else.
            if (engine.ConsoleBindingIsOurs())
            {
                engine.Disable();
                return new RevalidationReport(
                    RevalidationAction.ShadowUnwritten,
                    "The default terminal selection moved away; this build's lingering console binding was removed.");
            }

            return new RevalidationReport(RevalidationAction.NothingToHold, string.Empty);
        }

        // The selection is ours: the enable preconditions must still hold.
        if (!inputs.SingleInstanceEnabled)
        {
            engine.Disable();
            return new RevalidationReport(
                RevalidationAction.SelfDisabled,
                "Single instance mode is off, so this build can no longer receive console sessions; the default terminal registration was disabled. Re-enable it with single instance mode on.");
        }

        if (!inputs.PassesBoundPathCheck)
        {
            return new RevalidationReport(
                RevalidationAction.DriftReported,
                "The default terminal bindings point at another install location. This executable is not the bound one, so the repair was deferred to the installed build.");
        }

        // Bindings drift repair: a moved install rewrote nothing by itself;
        // when the console binding no longer names this build's binary,
        // re-running the write set points every binding at this location.
        if (engine.ConsoleBindingIsOurs())
        {
            return new RevalidationReport(RevalidationAction.Healthy, string.Empty);
        }

        engine.RepairBindings();
        return new RevalidationReport(
            RevalidationAction.BindingsRepaired,
            "The default terminal bindings were repaired to this install location.");
    }
}
