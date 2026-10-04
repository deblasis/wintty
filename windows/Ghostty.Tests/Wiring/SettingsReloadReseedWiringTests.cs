using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// That a settings page re-seeds every control it can write the config from
/// when the config changes underneath it.
///
/// The settings window caches its page instances, so a page's constructor
/// runs once per settings window while <c>Loaded</c> fires on every
/// navigation. Everything a page shows is therefore seeded once, in the
/// constructor, and from then on describes whatever the config said at that
/// moment. An external edit -- the raw editor in the same dialog, an editor
/// saving, the file watcher picking up a sync -- moves the config without
/// touching the controls, and the next thing the user does with a stale
/// control is a nudge: a slider dragged one notch, a spinner ticked, a
/// toggle clicked. The handler writes the value the control is showing, so
/// the external change is silently clobbered, and the write looks
/// unremarkable in the file afterwards.
///
/// Nothing catches that at runtime. The write is a legitimate config edit,
/// the file stays valid, and the page looks correct the whole time.
///
/// The census below is derived, not listed: it reads each page's markup,
/// takes every named control whose handler can reach a config write, and
/// requires the reload path to seed it. A control added next year is in the
/// census the day it lands, which is the failure mode a hand-written list
/// has.
///
/// Scope, and it is deliberate: the pages that edit config keys through the
/// shared writer or the write scheduler. The other pages in the same folder
/// are not in it -- ProfilesPage owns its own store rather than config keys,
/// KeybindingsPage already rebuilds its list on ConfigChanged, RawEditorPage
/// reloads the editor text, and SearchResultsPage only navigates.
///
/// What this cannot prove: that a seed assigns the right value, or that it
/// runs when the page is not loaded. Both are observable only with a window
/// open. Presence in the reachable set is what a source scan can honestly
/// claim.
/// </summary>
public class SettingsReloadReseedWiringTests
{
    /// <summary>
    /// The pages whose controls edit config keys. See the class summary for
    /// why the folder's other pages are not here.
    /// </summary>
    private static readonly string[] ConfigPages =
    {
        "AppearancePage",
        "ColorsPage",
        "GeneralPage",
        "TerminalPage",
        "AdvancedPage",
    };

    /// <summary>The event an external config change arrives on.</summary>
    private const string ConfigChanged = "ConfigChanged";

    /// <summary>
    /// The field each of these pages guards its own writes with. A re-seed
    /// that is not under it writes the file back what it just read, which is
    /// the same clobber by another route.
    /// </summary>
    private const string LoadingFlag = "_loading";

    /// <summary>
    /// The calls that put a key in the file. Matched on the last segment of
    /// the callee so a page can reach them through the editor or through the
    /// debouncing scheduler without either being named here.
    /// </summary>
    private static readonly string[] WriteVerbs =
    {
        "SetValue",
        "RemoveValue",
        "SetRepeatableValues",
        "Schedule",
    };

    private static readonly XNamespace XamlNamespace =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Every page re-seeds on an external change, and the subscription is
    /// both halves of a pair.
    ///
    /// The pair is the part that is easy to get half right. A page that
    /// subscribes in its constructor and unsubscribes in <c>Unloaded</c> is
    /// subscribed exactly once -- the first time the user navigates away the
    /// handler is gone and nothing restores it, so the page is deaf from then
    /// on. So: at least one <c>+=</c>, at least one <c>-=</c>, and the seeds
    /// below have to hang off the added one.
    /// </summary>
    [Fact]
    public void EveryConfigPageReSeedsWhenTheConfigChanges()
    {
        var problems = new List<string>();

        foreach (var page in ConfigPages)
        {
            var code = CodeFor(page);
            var added = Subscriptions(code, add: true);
            var removed = Subscriptions(code, add: false);

            if (added.Count == 0)
            {
                problems.Add(
                    $"  {page}: never subscribes to {ConfigChanged}, so an external edit leaves "
                    + "every control on it stale and the next nudge clobbers the change");
            }

            if (removed.Count == 0)
            {
                problems.Add(
                    $"  {page}: subscribes to {ConfigChanged} but never unsubscribes. Page "
                    + "instances are cached, so the handler outlives the navigation that dropped "
                    + "it and re-seeds a page nobody is looking at");
            }
        }

        Assert.True(
            problems.Count == 0,
            "settings pages whose controls do not follow an external config change:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// And every control that can write the config is re-seeded by that
    /// path.
    ///
    /// A control counts as seeded when the reload path assigns to it or names
    /// it as an argument. Both spellings are real: the Appearance tint row
    /// assigns through a helper, and the Colors rows name their picker and
    /// its reset button as arguments to a shared one, which is the shape that
    /// keeps five identical rows from being five copies.
    /// </summary>
    [Fact]
    public void EveryConfigWritingControlIsReSeededUnderTheLoadingGuard()
    {
        var problems = new List<string>();
        var counted = 0;

        foreach (var page in ConfigPages)
        {
            var code = CodeFor(page);
            var markup = PageMarkup(page);
            var path = ReloadPath(code);

            if (path.Count == 0)
            {
                problems.Add(
                    $"  {page}: no re-seed path at all (see the census above), so every control "
                    + "on it is seeded once in the constructor and never again");
                continue;
            }

            // The guard is asserted against the same set the seeds are, so a
            // seed reached only from a write-back path cannot pass as a seed.
            var guarded = path.Any(m => m.DescendantNodes()
                .OfType<AssignmentExpressionSyntax>()
                .Any(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression)
                          && a.Left is IdentifierNameSyntax id
                          && id.Identifier.ValueText == LoadingFlag
                          && a.Right.IsKind(SyntaxKind.TrueLiteralExpression)));
            if (!guarded)
            {
                problems.Add(
                    $"  {page}: the {ConfigChanged} path never raises {LoadingFlag}, so re-seeding "
                    + "a control fires that control's own handler and writes the file back what it "
                    + "just read");
            }

            foreach (var control in ConfigWritingControls(markup, code))
            {
                counted++;
                var seeds = SeedTargets(markup, control);
                if (!seeds.Any(name => ReloadedBy(path, name)))
                {
                    problems.Add(Offender(page, control));
                }
            }

            foreach (var box in PickerBoxes(code))
            {
                counted++;
                if (!ReloadedBy(path, box)) problems.Add(Offender(page, box));
            }
        }

        Assert.True(
            problems.Count == 0,
            "settings controls that can write the config and are not re-seeded when it changes:\n"
            + string.Join("\n", problems));

        // Load-bearing: an empty census is a query that stopped matching, and
        // everything above reads "nothing to fix" out of it. Asserted after
        // the offenders so a scan that lost its subject reports what it
        // missed rather than only that it is short.
        Assert.True(
            counted > ConfigPages.Length,
            $"the census found {counted} config-writing controls across {ConfigPages.Length} "
            + "pages, which is fewer than one per page; the scan is broken, not the pages");
    }

    private static string Offender(string page, string control) =>
        $"  {page}: {control} can write the config but the {ConfigChanged} path never touches it. "
        + "The control keeps showing the value from when the page was built, and the next nudge "
        + "writes that stale value over the change the user just made elsewhere";

    /// <summary>
    /// The controls on a page that can write the config.
    ///
    /// A handler is recognised by its attribute value naming a method in the
    /// code-behind, which is what keeps <c>Header="Scrollback"</c> and
    /// <c>MinWidth="160"</c> out without carrying a list of WinUI event names
    /// that would go stale. Only a plain, unprefixed attribute can be one:
    /// <c>x:Name</c> is namespaced and <c>ctrl:SettingsCard.ConfigKey</c>
    /// carries both a prefix and a dot.
    ///
    /// Containers are not excluded, because the value on a container is not
    /// always its own: a <c>RadioButtons</c> group is seeded by its radios.
    /// <see cref="SeedTargets"/> is where that is handled, and it is why this
    /// census can keep the combo boxes -- every one of them has item children
    /// in the markup, and a "skip anything with children" filter silently
    /// dropped all of them.
    /// </summary>
    private static IEnumerable<string> ConfigWritingControls(XDocument markup, ShellSource code)
    {
        var methods = MethodsNamed(code);
        var found = new List<string>();

        foreach (var element in markup.Descendants())
        {
            var name = element.Attribute(XamlNamespace + "Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;

            var handlers = element.Attributes()
                .Where(a => a.Name.NamespaceName.Length == 0
                            && !a.Name.LocalName.Contains('.')
                            && methods.ContainsKey(a.Value))
                .Select(a => a.Value)
                .ToList();

            if (handlers.Any(h => ReachesAWrite(code, h)))
            {
                found.Add(name!);
            }
        }

        return found;
    }

    /// <summary>
    /// The names whose assignment re-seeds <paramref name="control"/>: its
    /// own, plus those of its named descendants.
    ///
    /// The descendant half is the container case. <c>ShaderModeButtons</c> is
    /// the control that carries the <c>SelectionChanged</c> and the only thing
    /// its handler writes, but nothing assigns to it: the page checks the
    /// three radios, which moves the group's selection as a side effect. So
    /// seeding a radio IS seeding the group, and a rule that stops at the
    /// name would ask for something no assignment can express.
    /// </summary>
    private static IReadOnlyList<string> SeedTargets(XDocument markup, string control)
    {
        var element = markup.Descendants()
            .FirstOrDefault(e => e.Attribute(XamlNamespace + "Name")?.Value == control);
        if (element is null) return new[] { control };

        var names = new List<string> { control };
        names.AddRange(element.Descendants()
            .Select(e => e.Attribute(XamlNamespace + "Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!));
        return names;
    }

    /// <summary>
    /// Whether a handler can put a key in the file, however indirectly.
    ///
    /// Every one of these pages writes through a shared helper --
    /// <c>OnValueChanged</c>, <c>ResetColorOverride</c>,
    /// <c>WriteShaderPathValue</c> -- so the verb is looked for among the
    /// calls the reachable set makes, not among the names of the methods it
    /// reaches. Reading the names instead finds nothing but a handler called
    /// <c>SetValue</c>, and a census that finds four controls for five pages
    /// of settings is a census that has quietly stopped working.
    /// </summary>
    private static bool ReachesAWrite(ShellSource code, string method)
    {
        foreach (var reached in Reached(code, new[] { method }))
        {
            foreach (var call in reached.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var tail = call.CalleeText().Replace("?", "").Split('.').Last();
                if (WriteVerbs.Contains(tail)) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every box handed to <c>SearchableList</c> by a page.
    ///
    /// Counted separately from the markup census because the pickers wire
    /// themselves in code: their boxes carry no event attribute at all, so a
    /// markup-only census cannot see that the theme and font boxes write the
    /// config -- which is the same defect the census is about.
    /// </summary>
    private static IEnumerable<string> PickerBoxes(ShellSource code) =>
        code.Root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Where(o => o.Type.ToString() == "SearchableList")
            .Select(o => o.ArgumentList?.Arguments.FirstOrDefault()?.Expression)
            .OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .ToList();

    private static IEnumerable<MethodDeclarationSyntax> Reached(
        ShellSource code, IEnumerable<string> roots)
    {
        var byName = MethodsNamed(code);
        var reached = new HashSet<MethodDeclarationSyntax>();
        var queue = new Queue<string>(roots);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            var method = byName.TryGetValue(name, out var found) ? found : null;
            if (method is null || !reached.Add(method)) continue;

            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var tail = call.CalleeText().Replace("?", "").Split('.').Last();
                if (byName.ContainsKey(tail)) queue.Enqueue(tail);
            }
        }

        return reached;
    }

    /// <summary>Every method in a file, by name.</summary>
    private static Dictionary<string, MethodDeclarationSyntax> MethodsNamed(ShellSource code) =>
        code.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .GroupBy(m => m.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>
    /// Every method reachable from the page's ConfigChanged handlers: the
    /// reload path, by whatever the page happens to call it.
    ///
    /// A call graph rather than a named method, because the pages spell this
    /// differently -- one seeds inline, four go through a LoadValues-shaped
    /// helper, and AppearancePage fans out into several. Naming one method
    /// would have the rule assert three of the five pages.
    /// </summary>
    private static IReadOnlyList<MethodDeclarationSyntax> ReloadPath(ShellSource code) =>
        Reached(code, Subscriptions(code, add: true)).ToList();

    /// <summary>
    /// The handlers a page adds to or removes from the config service.
    ///
    /// Read off the parsed subscription rather than by naming a handler,
    /// because the whole rule is that the page must have one: a page whose
    /// handler got renamed would otherwise stop being checked rather than
    /// start failing.
    /// </summary>
    private static List<string> Subscriptions(ShellSource code, bool add) =>
        code.Root.DescendantNodes()
            .OfType<AssignmentExpressionSyntax>()
            .Where(a => (add
                            ? a.IsKind(SyntaxKind.AddAssignmentExpression)
                            : a.IsKind(SyntaxKind.SubtractAssignmentExpression))
                        && a.Left is MemberAccessExpressionSyntax member
                        && member.Name.Identifier.ValueText == ConfigChanged)
            .Select(a => a.Right)
            .OfType<IdentifierNameSyntax>()
            .Select(i => i.Identifier.ValueText)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Whether the reload path seeds <paramref name="control"/>: assigns to
    /// one of its properties, or names it as an argument.
    /// </summary>
    private static bool ReloadedBy(IReadOnlyList<MethodDeclarationSyntax> path, string control)
    {
        foreach (var node in path.SelectMany(m => m.DescendantNodes()))
        {
            if (node is AssignmentExpressionSyntax a
                && a.Left is MemberAccessExpressionSyntax member
                && member.Expression is IdentifierNameSyntax target
                && target.Identifier.ValueText == control)
            {
                return true;
            }

            if (node is ArgumentSyntax argument
                && argument.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Any(i => i.Identifier.ValueText == control))
            {
                return true;
            }
        }

        return false;
    }

    private static ShellSource CodeFor(string page) =>
        // The ".xaml" is part of the name: the embedded resource carries the
        // code-behind's real file name, so asking for the page without it
        // matches nothing.
        ShellSource.Load($"Settings.Pages.{page}.xaml.cs");

    private static XDocument PageMarkup(string page)
    {
        var resource = "Ghostty.Tests.Settings.Pages." + page + ".xaml";
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resource);
        Assert.True(
            stream is not null,
            $"{resource} is not embedded; see Ghostty.Tests.csproj. Without it the census reads "
            + "as an empty page and passes");

        try
        {
            return XDocument.Load(stream!);
        }
        catch (XmlException ex)
        {
            Assert.Fail($"{page} is not well-formed XML: {ex.Message}");
            throw;
        }
    }
}