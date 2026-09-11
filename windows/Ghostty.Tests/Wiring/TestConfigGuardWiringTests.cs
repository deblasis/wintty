using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Ghostty.Tests.Wiring;

/// <summary>
/// The test-config guard's wiring: every path a config byte can travel has
/// a guard call on it, and the one editor construction still sits over the
/// resolved config path.
///
/// These are wiring guards, not behaviour tests (the behaviour lives in
/// <c>Config.TestConfigGuardTests</c> and in the harness matrix the guard's
/// PR ran). What they prove is shape: that <c>Program</c> refuses before
/// anything can touch a config file, that the resolved path and each write
/// boundary still carry the belt checks, and that a new writer cannot reach
/// the file without going through the guarded editor. A regression that
/// keeps the literal and drops the behaviour walks through a substring
/// match; it does not walk through a syntax tree.
/// </summary>
public class TestConfigGuardWiringTests
{
    private const string GuardCall = "TestConfigGuard.AssertUnderTemp";

    /// <summary>
    /// Every guard call in the shell, matched on the callee's tail: the
    /// call sites spell the type namespace-qualified
    /// (<c>Ghostty.Core.Config.TestConfigGuard.AssertUnderTemp</c>) and
    /// <see cref="SyntaxQueries.Calls"/> matches the callee as written.
    /// </summary>
    private static List<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>
        GuardCalls(Microsoft.CodeAnalysis.SyntaxNode node) =>
        node.DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
            .Where(i => i.CalleeText().EndsWith(GuardCall, StringComparison.Ordinal))
            .ToList();

    [Fact]
    public void Program_Refuses_NonTemp_Root_Before_Anything_Touches_Config()
    {
        var source = ShellSource.Load("Program.cs");
        var main = source.Method("MainImpl");

        // The guard call itself, in MainImpl's own body.
        var guard = main.Call("GuardTestConfigRoot");

        // And before the first InitGhostty: libghostty's config parsing and
        // its config-dir/template creates all hang off init, so a guard that
        // runs after it has already lost the race with the native writes.
        var firstInit = main.Calls("InitGhostty").First();
        Assert.True(
            guard.Span.Start < firstInit.Span.Start,
            $"GuardTestConfigRoot ({guard.Span.Start}) must precede the first " +
            $"InitGhostty ({firstInit.Span.Start}) in MainImpl");
    }

    /// <summary>
    /// The env seam, pinned from the source (re-review finding 1): the
    /// ReadEnvironment property must be declared INTERNAL - a public
    /// settable static lets production code swap the guard's environment
    /// source out from under the guard - and its initializer must be the
    /// live ProcessEnvironment reader, not a constant or a snapshot. The
    /// behaviour half of the pin lives in
    /// <c>Config.TestConfigGuardDefaultReaderTests</c>.
    /// </summary>
    [Fact]
    public void The_Env_Seam_Stays_Internal_And_Live()
    {
        var source = ShellSource.Load("Config.TestConfigGuard.cs");
        var property = source.Root.DescendantNodes()
            .OfType<PropertyDeclarationSyntax>()
            .Single(p => p.Identifier.ValueText == "ReadEnvironment");

        Assert.Contains("internal static", property.Modifiers.ToString(),
            StringComparison.Ordinal);
        Assert.DoesNotContain("public", property.Modifiers.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "Environment.GetEnvironmentVariable",
            property.Initializer?.Value.ToString());
    }

    [Fact]
    public void GuardTestConfigRoot_Exits_With_Its_Own_Refusal_Code()
    {
        var source = ShellSource.Load("Program.cs");
        var guard = source.Method("GuardTestConfigRoot");

        // Every refusal path funnels through RefuseTestConfigStart, so the
        // exit code and the stderr line live in exactly one place.
        var refusals = guard.Calls("RefuseTestConfigStart");
        Assert.NotEmpty(refusals);

        var refuse = source.Method("RefuseTestConfigStart");
        // The refusal must be an exit the harness can distinguish from a
        // crash, and the enum member is what keeps that exit code from
        // silently colliding with a different meaning later.
        var exit = refuse.Body!.Statements.OfType<ExpressionStatementSyntax>()
            .Select(s => s.Expression).OfType<InvocationExpressionSyntax>()
            .SingleOrDefault(c => c.CalleeText() == "Environment.Exit");
        Assert.NotNull(exit);
        Assert.Contains("ExitCode.TestConfigRefused", exit.ArgumentList.ToString());

        // A refusal nobody can see is a hang with extra steps: the method
        // must say why on stderr before it exits.
        Assert.NotEmpty(refuse.Calls("WriteStderr"));
        Assert.NotEmpty(refuse.Calls("WriteStartupDiagnostic"));
    }

    [Fact]
    public void GuardTestConfigRoot_Checks_The_Temp_Environment_Before_The_Root()
    {
        var source = ShellSource.Load("Program.cs");
        var guard = source.Method("GuardTestConfigRoot");

        // M1: a redirected TEMP/TMP is the one environment change that
        // could move the guard's reference point, so it is the FIRST armed
        // question, before the root is even resolved.
        var envCheck = guard.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(i => i.CalleeText().EndsWith(
                "TestConfigGuard.AssertTempEnvironmentIntact",
                StringComparison.Ordinal));
        Assert.NotNull(envCheck);
        var rootResolve = guard.DescendantNodes()
            .OfType<IdentifierNameSyntax>()
            .FirstOrDefault(i => i.Identifier.ValueText == "ResolveConfigRoot");
        Assert.NotNull(rootResolve);
        Assert.True(envCheck!.Span.Start < rootResolve!.Span.Start,
            "the TEMP/TMP integrity check must precede the root resolution");
    }

    [Fact]
    public void GuardTestConfigSources_Covers_The_Flag_And_The_Includes()
    {
        var source = ShellSource.Load("Program.cs");
        var sources = source.Method("GuardTestConfigSources");

        // The CLI flag form: both spellings the rewrite passes through.
        Assert.Contains("--config-file=", sources.Body!.ToString());

        // The include form: the ini-shaped key, scanned from the root's
        // default files and recursed (the ScanConfigIncludes local
        // function is the recursion).
        Assert.Contains("config-file", sources.Body!.ToString());
        Assert.Contains("ScanConfigIncludes", sources.Body!.ToString());

        // GuardTestConfigRoot must call it, or none of this runs.
        Assert.NotEmpty(source.Method("GuardTestConfigRoot")
            .Calls("GuardTestConfigSources"));
    }

    [Fact]
    public void NoConfig_Resolves_The_Path_Without_Creating_It()
    {
        var source = ShellSource.Load("Services.ConfigService.cs");
        var ctor = source.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "ConfigService");

        // The create is native (edit.zig openPath); the no-create variant
        // is the same resolution without it. A --no-config run must not
        // even leave an empty file behind where none existed.
        var conditional = ctor.Body!.Statements
            .SelectMany(s => s.DescendantNodes().OfType<ConditionalExpressionSyntax>())
            .Single(c => c.WhenTrue.ToString().Contains("ConfigOpenPathNoCreate"));
        Assert.Contains("ConfigOpenPath", conditional.WhenFalse.ToString());

        // The early single-instance read resolves too, under the same rule.
        var program = ShellSource.Load("Program.cs");
        var reader = program.Method("ReadSingleInstanceSetting");
        Assert.Contains("ConfigOpenPathNoCreate", reader.Body!.ToString());
        Assert.Contains("ConfigOpenPath", reader.Body!.ToString());
    }

    [Fact]
    public void ConfigService_Guards_The_Resolved_Path_Before_Seed_Or_Reads()
    {
        var source = ShellSource.Load("Services.ConfigService.cs");
        var ctor = source.Root.DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "ConfigService");

        var resolved = ctor.AssignsTo("ConfigFilePath").Single();
        var assert = GuardCalls(ctor).Single();
        var seed = ctor.Body!.Statements
            .SelectMany(s => s.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Single(c => c.CalleeText() == "SeedConfigIfEmpty");

        Assert.True(resolved.Span.Start < assert.Span.Start,
            "the guard must see the resolved ConfigFilePath, not the raw one");
        Assert.True(assert.Span.Start < seed.Span.Start,
            "the seed write happens after the guard, or it writes unguarded");
    }

    [Fact]
    public void SeedConfigIfEmpty_Guards_Outside_The_Recovery_Try()
    {
        var source = ShellSource.Load("Services.ConfigService.cs");
        var seed = source.Method("SeedConfigIfEmpty");

        var write = seed.Calls("File.WriteAllText").Single();
        var assert = GuardCalls(seed).Single();
        var swallowingTry = write.Ancestors().OfType<TryStatementSyntax>()
            .SingleOrDefault(t => t.Catches.Count > 0);

        // The try exists so a flaky config dir cannot take startup down.
        // A guard inside it would be exactly that failure, smoothed over:
        // logged, swallowed, and the launch continues unisolated.
        Assert.NotNull(swallowingTry);
        Assert.DoesNotContain(assert.Ancestors(), a => a == swallowingTry);
        Assert.True(assert.Span.Start < write.Span.Start,
            "the guard must run before the write it guards");
    }

    [Fact]
    public void WriteAtomic_Is_One_Guarded_Choke_Point_For_Every_Write()
    {
        var source = ShellSource.Load("Services.ConfigFileEditor.cs");
        var overloads = source.Root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == "WriteAtomic")
            .ToList();
        Assert.Equal(2, overloads.Count);

        var stringOverload = overloads.Single(m =>
            m.ParameterList.Parameters.Count == 1 &&
            m.ParameterList.Parameters[0].Type?.ToString() == "string");
        var arrayOverload = overloads.Single(o => o != stringOverload);

        var assert = GuardCalls(stringOverload).Single();
        var write = stringOverload.Body!.Statements
            .SelectMany(s => s.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Single(c => c.CalleeText() == "File.WriteAllText");
        Assert.True(assert.Span.Start < write.Span.Start,
            "the guard must run before the write it guards");

        // The array overload routes through the guarded string overload, so
        // a new mutating method added above the editor still lands on the
        // one guarded boundary as long as it writes the way the existing
        // ones do.
        var routes = arrayOverload.Calls("WriteAtomic");
        Assert.Single(routes);
    }

    [Fact]
    public void The_Real_Editor_Is_Constructed_Once_Over_The_Resolved_Path()
    {
        // Corpus-wide, not just App.xaml.cs: "only one construction" is the
        // invariant that makes the WriteAtomic choke point cover every
        // writer, and a second construction anywhere would be a second
        // writer with its own opinion about the path.
        var constructions = ShellSource.AllFiles()
            .SelectMany(f => f.Root.DescendantNodes()
                .OfType<ObjectCreationExpressionSyntax>()
                .Where(c => c.Type.ToString() == "ConfigFileEditor")
                .Select(c => (File: f.Name, Creation: c)))
            .ToList();
        var construction = Assert.Single(constructions);
        Assert.Equal("App.xaml.cs", construction.File);

        // Over the service's resolved path: the one path the guards
        // already vouch for.
        Assert.Equal(
            "_configService.ConfigFilePath",
            construction.Creation.ArgumentList.Arguments[0].ToString());
    }

    [Fact]
    public void NoConfig_Run_Gets_The_Refusing_Editor_Not_The_Real_One()
    {
        var source = ShellSource.Load("App.xaml.cs");
        var inert = source.Root.DescendantNodes()
            .OfType<ObjectCreationExpressionSyntax>()
            .Single(c => c.Type.ToString().EndsWith(
                "NoConfigFileEditor", System.StringComparison.Ordinal));

        // The branch condition names the flag, so the inert editor cannot
        // quietly become the unconditional one (which would break every
        // real run's persistence) nor the unreachable one.
        var branch = inert.Ancestors().OfType<ConditionalExpressionSyntax>()
            .SingleOrDefault();
        Assert.NotNull(branch);
        Assert.Contains("NoConfig", branch.Condition.ToString());
        Assert.Equal(inert, branch.WhenTrue);

        // And the real editor is the other arm, over the resolved path.
        var real = branch.WhenTrue == inert
            ? branch.WhenFalse
            : branch.WhenTrue;
        Assert.Contains("ConfigFileEditor", real.ToString());
        Assert.Contains("ConfigFilePath", real.ToString());
    }

    [Fact]
    public void Startup_Migration_Skips_Config_Appends_Under_NoConfig()
    {
        var source = ShellSource.Load("Settings.WindowStateMigration.cs");
        var run = source.Method("TryRun");

        // The appends initializer must branch on the flag. Computing the
        // appends and then handing them to the refusing editor would turn
        // every --no-config launch with legacy settings into a logged
        // refusal; the placement-only half below is allowed to still run.
        var appends = run.DescendantNodes()
            .OfType<VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.ValueText == "appends");
        var conditional = Assert.IsType<ConditionalExpressionSyntax>(
            appends.Initializer?.Value);
        Assert.Contains("NoConfig", conditional.Condition.ToString());
    }
}
