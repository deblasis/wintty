using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// A confirmed deletion once recorded the session's default-file count as
/// zero (issue #1150), an assumption rather than a reading, and a wrong one
/// for a user whose other layers still exist: it disarms the absent guard
/// until the next applied reload re-establishes the count from disk. The
/// confirmation now decides nothing about the count. It logs, and the one
/// writer of the count stays the applied reload, which records the count
/// the load read.
/// </summary>
public class VanishCountWiringTests
{
    private static ShellSource ConfigService() => ShellSource.Load("Services.ConfigService.cs");

    [Fact]
    public void The_vanish_confirmation_logs_and_records_nothing()
    {
        var creation = Assert.Single(ConfigService().Root
            .DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "ConfigVanishProtocol"));

        var onAccept = Assert.Single(creation.ArgumentList.Arguments,
            a => a.NameColon?.Name.ToString() == "onAccept");

        var calls = onAccept.Expression.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression.ToString())
            .ToArray();
        Assert.Contains("StaticLoggers.ConfigService.LogConfigFileVanished", calls);
        Assert.DoesNotContain("RecordDefaultFiles", calls);

        Assert.DoesNotContain(onAccept.Expression.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString() == "_defaultFilesFound");
    }

    [Fact]
    public void The_vanished_callback_just_reloads()
    {
        var method = ConfigService().Method("OnConfigFileVanished");

        Assert.NotEmpty(method.Calls("Reload"));
        Assert.Empty(method.Calls("RecordDefaultFiles"));
        Assert.DoesNotContain(method.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>(),
            a => a.Left.ToString().StartsWith("_defaultFiles", StringComparison.Ordinal));
    }
}
