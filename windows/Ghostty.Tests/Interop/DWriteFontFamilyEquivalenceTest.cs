using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Ghostty.Core.DirectWrite;
using Xunit;

namespace Ghostty.Tests.Interop;

// Propagate the DWriteFontEnumerator platform gate instead of suppressing CA1416.
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
