using System;
using System.Linq;
using Ghostty.Core.Logging;
using Ghostty.Core.Logging.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ghostty.Tests.Logging;

public class LoggingBootstrapTests
{
    [Fact]
    public void ParseFilterOptions_DefaultsToInformation_WhenLevelEmpty()
    {
        var opts = LoggingBootstrap.ParseFilterOptions(logLevel: null, logFilter: null);

        Assert.Equal(LogLevel.Information, opts.MinLevel);
        Assert.Empty(opts.Rules);
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("TRACE", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("off", LogLevel.None)]
    [InlineData("none", LogLevel.None)]
    public void ParseFilterOptions_ParsesLevelCaseInsensitively(string input, LogLevel expected)
    {
        var opts = LoggingBootstrap.ParseFilterOptions(input, null);
        Assert.Equal(expected, opts.MinLevel);
    }

    [Fact]
    public void ParseFilterOptions_UnknownLevel_FallsBackToInformation()
    {
        var opts = LoggingBootstrap.ParseFilterOptions("bogus", null);
        Assert.Equal(LogLevel.Information, opts.MinLevel);
    }

    [Fact]
    public void ParseFilterOptions_SingleFilterRule_IsApplied()
    {
        var opts = LoggingBootstrap.ParseFilterOptions(
            "info",
            "Ghostty.Services.ThemePreviewService=trace");

        Assert.Single(opts.Rules);
        Assert.Equal("Ghostty.Services.ThemePreviewService", opts.Rules[0].CategoryName);
        Assert.Equal(LogLevel.Trace, opts.Rules[0].LogLevel);
    }

    [Fact]
    public void ParseFilterOptions_MultipleCommaSeparatedRules_AreAllApplied()
    {
        var opts = LoggingBootstrap.ParseFilterOptions(
            "info",
            "Ghostty.Services.ThemePreviewService=trace, Ghostty.Core.Config=warn");

        Assert.Equal(2, opts.Rules.Count);
        Assert.Contains(opts.Rules, r =>
            r.CategoryName == "Ghostty.Services.ThemePreviewService" && r.LogLevel == LogLevel.Trace);
        Assert.Contains(opts.Rules, r =>
            r.CategoryName == "Ghostty.Core.Config" && r.LogLevel == LogLevel.Warning);
    }

    [Fact]
    public void ParseFilterOptions_MalformedPair_IsSkipped()
    {
        var opts = LoggingBootstrap.ParseFilterOptions(
            "info",
            "Ghostty.Services=warn, NoEqualsSign, =warn, Ghostty.Core=");

        Assert.Single(opts.Rules);
        Assert.Equal("Ghostty.Services", opts.Rules[0].CategoryName);
    }

    [Fact]
    public void ParseFilterOptions_UnknownLevelInRule_IsSkipped()
    {
        var opts = LoggingBootstrap.ParseFilterOptions(
            "info",
            "Ghostty.Services=bogus, Ghostty.Core=warn");

        Assert.Single(opts.Rules);
        Assert.Equal("Ghostty.Core", opts.Rules[0].CategoryName);
    }

    // Regression: proves the live-reload filter swap actually takes
    // effect on subsequent log calls. MEL caches per-(provider, category)
    // thresholds internally; the filter delegate closed over FilterState
    // must be consulted on every log call (not just once at builder
    // time), otherwise ApplyFilters is a no-op in practice.
    [Fact]
    public void ApplyFilters_SwappingInPlace_AppliesToSubsequentLogCalls()
    {
        var filters = new FilterState(
            LoggingBootstrap.ParseFilterOptions("error", null));

        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(capture);
            // Go through the production filter path so a regression in
            // FilterState.Replace cache invalidation or the shared
            // LoggingBootstrap.IsEnabled helper surfaces here.
            b.AddFilter((providerName, category, level) =>
                LoggingBootstrap.IsEnabled(filters, category, level));
        });
        var logger = factory.CreateLogger("Ghostty.Services.ThemePreviewService");

        // Baseline: log-level=error suppresses Warning.
        logger.LogWarning(new EventId(99, "Before"), "warning-before-swap");
        Assert.Empty(capture.Entries);

        // Live swap to info.
        LoggingBootstrap.ApplyFilters(filters, "info", null);

        // Now Warning must pass through.
        logger.LogWarning(new EventId(99, "After"), "warning-after-swap");

        var entries = capture.Entries.Where(e => e.Level == LogLevel.Warning).ToArray();
        Assert.Single(entries);
        Assert.Equal("warning-after-swap", entries[0].Message);
    }

    // Regression: per-category filter rules must also live-swap.
    [Fact]
    public void ApplyFilters_AddingPerCategoryOverride_AppliesToSubsequentLogCalls()
    {
        var filters = new FilterState(
            LoggingBootstrap.ParseFilterOptions("info", null));

        using var capture = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b =>
        {
            b.AddProvider(capture);
            // Go through the production filter path so a regression in
            // FilterState.Replace cache invalidation or the shared
            // LoggingBootstrap.IsEnabled helper surfaces here.
            b.AddFilter((providerName, category, level) =>
                LoggingBootstrap.IsEnabled(filters, category, level));
        });
        var logger = factory.CreateLogger("Ghostty.Services.ThemePreviewService");

        // Baseline: log-level=info suppresses Debug.
        logger.LogDebug(new EventId(100, "Debug1"), "debug-before");
        Assert.DoesNotContain(capture.Entries, e => e.Level == LogLevel.Debug);

        // Live-swap: add a per-category override for this category to debug.
        LoggingBootstrap.ApplyFilters(
            filters, "info", "Ghostty.Services.ThemePreviewService=debug");

        // Now Debug must pass through for this category.
        logger.LogDebug(new EventId(100, "Debug2"), "debug-after");

        var debugEntries = capture.Entries
            .Where(e => e.Level == LogLevel.Debug && e.Category == "Ghostty.Services.ThemePreviewService")
            .ToArray();
        Assert.Single(debugEntries);
        Assert.Equal("debug-after", debugEntries[0].Message);

        // Unrelated category must still be suppressed at Debug level.
        var otherLogger = factory.CreateLogger("Ghostty.Core.Config.SomeOtherType");
        otherLogger.LogDebug(new EventId(101, "Other"), "other-debug");
        Assert.DoesNotContain(capture.Entries, e =>
            e.Category == "Ghostty.Core.Config.SomeOtherType" && e.Level == LogLevel.Debug);
    }
}
