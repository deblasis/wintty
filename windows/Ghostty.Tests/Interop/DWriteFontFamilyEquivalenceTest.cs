// Equivalence test: reference raw-vtable path (baseline) vs
// migrated CsWin32 path (production). Fails if the two lists
// diverge in ANY family name, which catches locale-index drift,
// vtable-slot mistakes, and PWSTR truncation bugs that a weak
// "Count > 20" sanity test would silently miss.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Ghostty.Core.DirectWrite;
using Xunit;

namespace Ghostty.Tests.Interop;

// Ghostty.Tests targets plain net9.0; DWriteFontEnumerator is
// marked [SupportedOSPlatform("windows10.0.17763")]. This test only
// runs on Windows hosts in CI/local, so propagate the platform gate
// here too instead of suppressing CA1416 globally.
[SupportedOSPlatform("windows10.0.17763")]
public sealed class DWriteFontFamilyEquivalenceTest
{
    [Fact]
    public void ReferenceAndMigratedMatch()
    {
        var reference = DWriteFontEnumerator.EnumerateReference();
        var migrated = DWriteFontEnumerator.EnumerateMigrated();

        // Both paths must see Segoe UI on any Windows 10/11 host.
        Assert.Contains("Segoe UI", reference, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Segoe UI", migrated, StringComparer.OrdinalIgnoreCase);

        // Hard equivalence: any divergence is a migration regression.
        Assert.True(
            reference.SequenceEqual(migrated, StringComparer.OrdinalIgnoreCase),
            BuildDiffMessage(reference, migrated));
    }

    private static string BuildDiffMessage(IList<string> a, IList<string> b)
    {
        var onlyInA = a.Except(b, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyInB = b.Except(a, StringComparer.OrdinalIgnoreCase).ToList();
        return $"Font lists differ. ref_only={string.Join(",", onlyInA)} " +
               $"mig_only={string.Join(",", onlyInB)}";
    }
}
