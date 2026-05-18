using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Betl.Conformance.Tests;

public sealed class Phase9Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase9", sub);

    private static (Pipeline P, EngineRegistry E, ConnectionRegistry Sql) Load(string sub)
    {
        var pipeline = PipelineLoader.LoadFile(Path.Combine(FixtureDir(sub), "pipeline.betl.yml"));
        var engines = new EngineRegistry().Register(new SsisExpressionEngine());
        var sql = new ConnectionRegistry().Register(new SqliteProvider());
        return (pipeline, engines, sql);
    }

    private static void Run(Pipeline p, EngineRegistry e, ConnectionRegistry sql, Dictionary<string, string> args)
    {
        var ctx = ParameterContext.Build(p, args);
        new Executor(p, ctx, e, sql).Run();
    }

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Emit a tiny standalone assembly to disk so the pipeline can reference it
    /// via `references:`. Uses Roslyn directly (already pulled in transitively
    /// via Betl.Providers.Dotnet) rather than shelling out to the SDK.
    /// </summary>
    private static string EmitHelperAssembly(string namespaceName, string typeName, string body)
    {
        var src = $$"""
            namespace {{namespaceName}};
            public static class {{typeName}}
            {
                {{body}}
            }
            """;
        var tree = CSharpSyntaxTree.ParseText(src);
        var coreLib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "BetlTestHelper-" + Guid.NewGuid().ToString("N")[..8],
            [tree],
            [coreLib],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));
        var dllPath = Path.Combine(Path.GetTempPath(), $"betl-test-helper-{Guid.NewGuid():N}.dll");
        var result = compilation.Emit(dllPath);
        if (!result.Success)
        {
            var errs = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException("Helper compile failed:\n" + errs);
        }
        return dllPath;
    }

    [Fact]
    public void DotnetScript_can_reference_external_assembly_via_references_field()
    {
        var helperDll = EmitHelperAssembly(
            namespaceName: "Betl.Test.Helper",
            typeName: "Greeter",
            body: """public static string Greet(string s) => "hello " + s;""");
        var outPath = Path.Combine(Path.GetTempPath(), $"p9-refs-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("dotnet-references");
            Run(p, e, sql, new()
            {
                ["helper_dll"] = helperDll,
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(FixtureDir("dotnet-references"), "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            // Helper DLL is now mmap'd into the process by AssemblyLoadContext,
            // so Windows won't let us delete it. Leave it for the OS to reap;
            // the file name is GUID'd so there's no test-cross-run conflict.
            try { if (File.Exists(helperDll)) File.Delete(helperDll); } catch { }
        }
    }

    [Fact]
    public void Missing_reference_path_surfaces_clear_error()
    {
        // Reuse the dotnet-references fixture but point helper_dll at a non-existent file.
        var bogus = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.dll");
        var outPath = Path.Combine(Path.GetTempPath(), $"p9-missing-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("dotnet-references");
            var ex = Assert.Throws<BetlException>(() => Run(p, e, sql, new()
            {
                ["helper_dll"] = bogus,
                ["out"] = outPath,
            }));
            Assert.Contains("reference", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
