using System;
using System.Collections.Generic;
using System.Linq;

namespace Ghostty.Commands;

internal sealed class ActionAutoCompleter
{
    private readonly Dictionary<string, ActionSchema> _actions;
    private readonly List<string> _sortedNames;

    public ActionAutoCompleter(Dictionary<string, ActionSchema> actions)
    {
        _actions = actions;
        _sortedNames = actions.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public AutocompleteResult Complete(string input)
    {
        var colonIndex = input.IndexOf(':');

        if (colonIndex < 0)
        {
            var matches = _sortedNames
                .Where(n => n.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                .Select(n => new AutocompleteSuggestion(n, _actions[n].Description))
                .ToList();

            var ghost = matches.Count > 0 ? matches[0].Name[input.Length..] : null;
            return new AutocompleteResult(matches, ghost);
        }

        var actionPart = input[..colonIndex];
        var paramPart = input[(colonIndex + 1)..];

        if (!_actions.TryGetValue(actionPart, out var schema))
            return AutocompleteResult.Empty;

        if (schema.Parameters is null || schema.Parameters.Length == 0)
            return AutocompleteResult.Empty;

        var paramMatches = schema.Parameters
            .Where(p => p.StartsWith(paramPart, StringComparison.OrdinalIgnoreCase))
            .Select(p => new AutocompleteSuggestion(p, $"{schema.Name}:{p}"))
            .ToList();

        var paramGhost = paramMatches.Count > 0 ? paramMatches[0].Name[paramPart.Length..] : null;
        return new AutocompleteResult(paramMatches, paramGhost);
    }

    public bool IsValid(string input)
    {
        var colonIndex = input.IndexOf(':');

        if (colonIndex < 0)
            return _actions.TryGetValue(input, out var schema) && !schema.RequiresParameter;

        var actionPart = input[..colonIndex];
        var paramPart = input[(colonIndex + 1)..];

        if (!_actions.TryGetValue(actionPart, out var s))
            return false;

        if (s.Parameters is null || s.Parameters.Length == 0)
            return false;

        return Array.Exists(s.Parameters, p => p.Equals(paramPart, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record ActionSchema
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string[]? Parameters { get; init; }
    public required bool RequiresParameter { get; init; }
}

internal sealed record AutocompleteSuggestion(string Name, string Description);

internal sealed record AutocompleteResult(
    IReadOnlyList<AutocompleteSuggestion> Suggestions,
    string? GhostText)
{
    public static AutocompleteResult Empty { get; } = new([], null);
}
