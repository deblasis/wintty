using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// Win32 coordinate conversions whose failure is indistinguishable from
/// success: they signal through the return value and leave the point or rect
/// they were handed untouched, so a discarded BOOL turns unconverted input
/// into confidently-wrong output. <c>ClientToScreen</c> is the one that bit
/// us (issue #896) -- client coordinates returned labelled as screen ones,
/// at two seam sites that decide which pixels a harness samples.
///
/// A corpus rule rather than three named lines, because the defect was that
/// a fourth call site existed and nobody was looking for it.
/// </summary>
public class Win32CoordinateFailureWiringTests
{
    /// <summary>
    /// Calls that report failure ONLY through their return value. Deliberately
    /// not every PInvoke: a call whose out-parameter is checked anyway, or
    /// whose failure is visible, does not need this rule and adding it here
    /// would make the guard noisy enough to be suppressed.
    /// </summary>
    private static readonly string[] Silent =
    {
        "PInvoke.ClientToScreen",
        "PInvoke.ScreenToClient",
        "PInvoke.GetWindowRect",
        "PInvoke.GetClientRect",
        "PInvoke.MapWindowPoints",
    };

    [Fact]
    public void NoSilentCoordinateConversion_HasItsFailureDiscarded()
    {
        var discarded = new List<string>();
        var seen = 0;

        foreach (var (resource, root) in ShellSource.AllShellSources())
        {
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (!Silent.Contains(call.CalleeText())) continue;
                seen++;

                // A bare expression statement is the whole defect: the BOOL
                // is computed and dropped. Anywhere else -- an `if (!...)`,
                // an assignment, a return -- something reads it.
                if (call.Parent is ExpressionStatementSyntax)
                    discarded.Add($"{resource}: {call}");
            }
        }

        // Load-bearing: this rule is worthless the day the callee spelling
        // changes and the scan quietly matches nothing.
        Assert.True(seen > 0, "found no calls to scan: " + string.Join(", ", Silent));

        Assert.True(
            discarded.Count == 0,
            "these Win32 calls signal failure only through their return value, and "
                + "leave their point/rect untouched when they fail -- so a discarded "
                + "result is unconverted coordinates reported as converted:\n  "
                + string.Join("\n  ", discarded));
    }
}
