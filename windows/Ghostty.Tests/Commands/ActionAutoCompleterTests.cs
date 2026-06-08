using System.Collections.Generic;
using Ghostty.Commands;
using Xunit;

namespace Ghostty.Tests.Commands;

public class ActionAutoCompleterTests
{
    private static ActionAutoCompleter CreateCompleter()
    {
        return new ActionAutoCompleter(new Dictionary<string, ActionSchema>
        {
            ["new_split"] = new()
            {
                Name = "new_split",
                Description = "Create a new split pane",
                Parameters = ["right", "down", "left", "up"],
                RequiresParameter = true,
            },
            ["new_tab"] = new()
            {
                Name = "new_tab",
                Description = "Open a new tab",
                Parameters = null,
                RequiresParameter = false,
            },
            ["new_window"] = new()
            {
                Name = "new_window",
                Description = "Open a new window",
                Parameters = null,
                RequiresParameter = false,
            },
            ["goto_tab"] = new()
            {
                Name = "goto_tab",
                Description = "Jump to a specific tab",
                Parameters = ["1", "2", "3", "previous", "next", "last"],
                RequiresParameter = true,
            },
            ["reset"] = new()
            {
                Name = "reset",
                Description = "Reset the terminal",
                Parameters = null,
                RequiresParameter = false,
            },
        });
    }

    [Fact]
    public void Complete_EmptyInput_ReturnsAllActions()
    {
        var c = CreateCompleter();
        var result = c.Complete("");
        Assert.Equal(5, result.Suggestions.Count);
    }

    [Fact]
    public void Complete_Prefix_FiltersActions()
    {
        var c = CreateCompleter();
        var result = c.Complete("new");
        Assert.Equal(3, result.Suggestions.Count);
        Assert.All(result.Suggestions, s => Assert.StartsWith("new", s.Name));
    }

    [Fact]
    public void Complete_ExactMatch_NoParameter_ShowsAction()
    {
        var c = CreateCompleter();
        var result = c.Complete("reset");
        Assert.Single(result.Suggestions);
        Assert.Equal("reset", result.Suggestions[0].Name);
    }

    [Fact]
    public void Complete_ColonPrefix_ShowsParameters()
    {
        var c = CreateCompleter();
        var result = c.Complete("new_split:");
        Assert.Equal(4, result.Suggestions.Count);
        Assert.Contains(result.Suggestions, s => s.Name == "right");
    }

    [Fact]
    public void Complete_ColonWithPartialParam_FiltersParameters()
    {
        var c = CreateCompleter();
        var result = c.Complete("new_split:r");
        Assert.Single(result.Suggestions);
        Assert.Equal("right", result.Suggestions[0].Name);
    }

    [Fact]
    public void Complete_UnknownAction_ReturnsEmpty()
    {
        var c = CreateCompleter();
        var result = c.Complete("nonexistent");
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Complete_GhostText_ShowsTopSuggestion()
    {
        var c = CreateCompleter();
        var result = c.Complete("new");
        Assert.NotNull(result.GhostText);
    }

    [Fact]
    public void Complete_ColonOnNoParamAction_ReturnsEmpty()
    {
        var c = CreateCompleter();
        var result = c.Complete("reset:");
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void IsValid_CompleteAction_ReturnsTrue()
    {
        var c = CreateCompleter();
        Assert.True(c.IsValid("reset"));
        Assert.True(c.IsValid("new_split:right"));
    }

    [Fact]
    public void IsValid_IncompleteRequired_ReturnsFalse()
    {
        var c = CreateCompleter();
        Assert.False(c.IsValid("new_split"));
        Assert.False(c.IsValid("new_split:bogus"));
    }

    [Fact]
    public void IsValid_Unknown_ReturnsFalse()
    {
        var c = CreateCompleter();
        Assert.False(c.IsValid("does_not_exist"));
    }

    [Fact]
    public void Complete_NoParamAction_ActionKeyIsActionName()
    {
        var c = CreateCompleter();
        var result = c.Complete("reset");
        Assert.Single(result.Suggestions);
        Assert.Equal("reset", result.Suggestions[0].ActionKey);
    }

    [Fact]
    public void Complete_Prefix_ActionKeyEqualsName()
    {
        var c = CreateCompleter();
        var result = c.Complete("new");
        Assert.All(result.Suggestions, s => Assert.Equal(s.Name, s.ActionKey));
    }

    [Fact]
    public void Complete_ColonParam_ActionKeyIsFullActionColonParam()
    {
        var c = CreateCompleter();
        var result = c.Complete("new_split:r");
        Assert.Single(result.Suggestions);
        // Name is just the parameter; ActionKey is the dispatchable full string.
        Assert.Equal("right", result.Suggestions[0].Name);
        Assert.Equal("new_split:right", result.Suggestions[0].ActionKey);
    }

    [Fact]
    public void Complete_ColonAllParams_ActionKeyPrefixedWithAction()
    {
        var c = CreateCompleter();
        var result = c.Complete("goto_tab:");
        Assert.NotEmpty(result.Suggestions);
        Assert.All(result.Suggestions, s => Assert.StartsWith("goto_tab:", s.ActionKey));
    }

    [Fact]
    public void Complete_ColonUnknownParam_ReturnsNoSuggestions()
    {
        // A known action with an unmatched parameter must yield no suggestions,
        // so no dangling ActionKey can be dispatched.
        var c = CreateCompleter();
        var result = c.Complete("new_split:zzz");
        Assert.Empty(result.Suggestions);
    }
}
