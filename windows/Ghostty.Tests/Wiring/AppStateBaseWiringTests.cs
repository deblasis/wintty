using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The state-base override's wiring (#854 item 9): every path the shell
/// derives from <c>AppIdentity.StateDirName</c> must take its root from
/// <c>AppStateBase</c> (the <c>WINTTY_STATE_BASE</c> override), never
/// straight from <c>Environment.GetFolderPath</c> or an injected
/// <c>GetKnownFolder</c> call, or the override cannot move it and the
/// multi-instance GUI tests (#822) that rely on it write into the real
/// per-user state tree.
///
/// These are wiring guards, not behaviour tests (the resolution behaviour
/// lives in <c>AppStateBaseTests</c>). What they prove is shape: a
/// <c>Path.Combine</c> that names the state directory names the helper
/// too. A new state path added without the helper, or a converted site
/// edited back to the raw known-folder root, fails here with the file
/// and the offending argument list.
///
/// The rule is textual over each combine's parsed argument list, so it
/// also catches the hoisted form: a root assigned from
/// <c>GetFolderPath</c> into a local and then combined no longer names
/// the helper anywhere in the combine, and is refused just the same.
/// </summary>
public class AppStateBaseWiringTests
{
    [Fact]
    public void NoStateDirCombine_TakesItsRoot_FromTheRawKnownFolder()
    {
        var offenders = new List<string>();

        foreach (var file in ShellSource.AllFiles())
        {
            foreach (var combine in file.Root.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>()
                         .Where(i => i.CalleeText().EndsWith(
                             "Path.Combine", StringComparison.Ordinal)))
            {
                var args = combine.ArgumentList.ToString();
                if (!args.Contains("StateDirName", StringComparison.Ordinal))
                    continue;

                // The load-bearing half: the state directory's root must
                // come from the override-aware helper, not from the raw
                // known-folder value. This refuses both spellings at once
                // -- a combine over GetFolderPath/GetKnownFolder directly,
                // and a combine over a local that was fed by one.
                if (!args.Contains("AppStateBase", StringComparison.Ordinal))
                    offenders.Add($"{file.Name}: Path.Combine{args}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"{offenders.Count} Path.Combine call(s) build a state path from " +
            "AppIdentity.StateDirName without AppStateBase, so WINTTY_STATE_BASE " +
            "cannot move them:\n  " + string.Join("\n  ", offenders));
    }
}
