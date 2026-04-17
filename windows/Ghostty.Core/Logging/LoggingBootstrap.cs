using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Ghostty.Core.Logging;

internal static class LoggingBootstrap
{
    /// <summary>
    /// Parses Ghostty config keys <c>log-level</c> and <c>log-filter</c>
    /// into a <see cref="LoggerFilterOptions"/> carrying a default minimum
    /// plus per-category override rules.
    /// </summary>
    /// <param name="logLevel">
    /// Default minimum level; one of trace / debug / info / warn / error / off.
    /// Case-insensitive. Null or empty falls back to Information.
    /// </param>
    /// <param name="logFilter">
    /// Comma-separated <c>CATEGORY=LEVEL</c> pairs. Category match uses
    /// standard Microsoft.Extensions.Logging prefix semantics: the longest
    /// category prefix wins. Unknown level names are skipped with no
    /// effect on the returned options.
    /// </param>
    internal static LoggerFilterOptions ParseFilterOptions(
        string? logLevel, string? logFilter)
    {
        var options = new LoggerFilterOptions
        {
            MinLevel = ParseLevelOrDefault(logLevel, LogLevel.Information),
        };

        if (!string.IsNullOrWhiteSpace(logFilter))
        {
            foreach (var rawPair in logFilter.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = rawPair.IndexOf('=');
                if (eq <= 0 || eq == rawPair.Length - 1)
                    continue; // malformed; skip

                var category = rawPair[..eq].Trim();
                var levelText = rawPair[(eq + 1)..].Trim();

                if (!TryParseLevel(levelText, out var level))
                    continue; // unknown level; skip

                options.Rules.Add(new LoggerFilterRule(
                    providerName: null,
                    categoryName: category,
                    logLevel: level,
                    filter: null));
            }
        }

        return options;
    }

    private static LogLevel ParseLevelOrDefault(string? text, LogLevel fallback)
        => TryParseLevel(text, out var level) ? level : fallback;

    private static bool TryParseLevel(string? text, out LogLevel level)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            level = default;
            return false;
        }

        // Map Ghostty config vocabulary to Microsoft.Extensions.Logging levels.
        switch (text.Trim().ToLowerInvariant())
        {
            case "trace": level = LogLevel.Trace; return true;
            case "debug": level = LogLevel.Debug; return true;
            case "info":
            case "information": level = LogLevel.Information; return true;
            case "warn":
            case "warning": level = LogLevel.Warning; return true;
            case "error": level = LogLevel.Error; return true;
            case "off":
            case "none": level = LogLevel.None; return true;
            default: level = default; return false;
        }
    }

    /// <summary>
    /// Constructs the process-wide <see cref="ILoggerFactory"/> wired to
    /// the ETW EventSource provider and the rolling-file sink. Returns
    /// the factory plus the file sink so the caller can dispose the
    /// file sink with an await-able teardown.
    /// </summary>
    internal static (ILoggerFactory Factory, FileLoggerProvider FileSink, FilterState Filters) Build(
        string? logLevel,
        string? logFilter,
        string fileLogDirectory)
    {
        var filters = new FilterState(ParseFilterOptions(logLevel, logFilter));
        var fileSink = new FileLoggerProvider(new FileLoggerOptions
        {
            Directory = fileLogDirectory,
        });

        var factory = LoggerFactory.Create(builder =>
        {
            // Single filter delegate closes over FilterState so
            // ApplyFilters(filters, ...) can swap in a new rule set
            // without rebuilding the factory. The per-category resolve
            // result is memoized in FilterState.Cache; Replace() swaps
            // the cache instance so stale thresholds cannot outlive a
            // config reload.
            builder.AddFilter((providerName, category, level) => IsEnabled(filters, category, level));
            builder.AddEventSourceLogger();
            builder.AddProvider(fileSink);
        });

        return (factory, fileSink, filters);
    }

    /// <summary>
    /// Rebuilds the filter rule set in place so a live config reload
    /// takes effect without tearing down the factory. Safe to call from
    /// the dispatcher thread that owns <see cref="FilterState"/>.
    /// </summary>
    internal static void ApplyFilters(FilterState filters, string? logLevel, string? logFilter)
        => filters.Replace(ParseFilterOptions(logLevel, logFilter));

    /// <summary>
    /// Evaluates whether a log call should pass through, using the
    /// memoized per-category cache on <see cref="FilterState"/>. Shared
    /// by the production filter delegate in <see cref="Build"/> and by
    /// tests so any regression in cache invalidation surfaces through
    /// the same code path as production.
    /// </summary>
    internal static bool IsEnabled(FilterState filters, string? category, LogLevel level)
    {
        if (category is null) return level >= filters.Options.MinLevel;
        var threshold = filters.Cache.GetOrAdd(category, ResolveThreshold, filters);
        return level >= threshold;
    }

    /// <summary>
    /// Longest matching category prefix wins; falls back to
    /// <see cref="LoggerFilterOptions.MinLevel"/>. Called once per
    /// previously-unseen category per filter-rule set via
    /// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>. Kept
    /// as a <c>static</c> method so the delegate is cached and the
    /// dictionary doesn't close over the delegate instance.
    /// </summary>
    private static LogLevel ResolveThreshold(string category, FilterState state)
    {
        LoggerFilterRule? best = null;
        foreach (var rule in state.Options.Rules)
        {
            if (rule.CategoryName is null) continue;
            if (!category.StartsWith(rule.CategoryName, StringComparison.Ordinal))
                continue;
            if (best is null || rule.CategoryName.Length > best.CategoryName!.Length)
                best = rule;
        }
        return best?.LogLevel ?? state.Options.MinLevel;
    }
}
