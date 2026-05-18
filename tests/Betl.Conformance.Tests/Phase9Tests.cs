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
    public void DotnetScript_can_pull_nuget_package_via_packages_field()
    {
        // Note: first run requires network access (dotnet restore against nuget.org).
        // Subsequent runs hit the local %LOCALAPPDATA%\betl\package-cache and are offline-OK.
        var outPath = Path.Combine(Path.GetTempPath(), $"p9-pkg-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("dotnet-packages");
            Run(p, e, sql, new() { ["out"] = outPath });
            AssertFileMatches(Path.Combine(FixtureDir("dotnet-packages"), "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void Malformed_package_id_surfaces_clear_error()
    {
        // Build a synthetic pipeline pointing at a bad package string.
        var src = """
            betl: 1
            name: bad-pkg
            pipeline:
              - id: flow
                type: dataflow
                steps:
                  - id: gen
                    type: betl.gen_int64
                    n: 1
                  - id: s
                    type: dotnet.script
                    from: gen
                    packages:
                      - "no-at-sign-here"
                    output_schema:
                      - { name: n, type: int64 }
                    source: |
                      public class X : BetlScript {
                        public override IEnumerable<object?[]> OnRow(IReadOnlyList<object?> row) {
                          yield return new object?[] { row[0] };
                        }
                      }
            """;
        var p = PipelineLoader.Load(src);
        var e = new EngineRegistry().Register(new SsisExpressionEngine());
        var sql = new ConnectionRegistry().Register(new SqliteProvider());
        var ex = Assert.Throws<BetlException>(() =>
            new Executor(p, ParameterContext.Build(p, new Dictionary<string, string>()), e, sql).Run());
        Assert.Contains("package", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id@version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Compile and emit a betl plugin DLL on disk. We grab MetadataReferences
    /// from the running test process so the plugin can name our types
    /// (IBetlComponentPlugin, IDataComponent, Schema, Row) directly.
    /// </summary>
    private static string EmitPluginAssembly(string sourceCode, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        var refs = new List<MetadataReference>();
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
        {
            try
            {
                using (var fs = File.OpenRead(dll))
                using (var pe = new System.Reflection.PortableExecutable.PEReader(fs))
                {
                    if (!pe.HasMetadata) continue;
                }
                refs.Add(MetadataReference.CreateFromFile(dll));
            }
            catch { }
        }
        foreach (var asm in new[] {
            typeof(Betl.Components.IDataComponent).Assembly,
            typeof(Betl.Core.Row).Assembly,
            typeof(Apache.Arrow.Types.IArrowType).Assembly,
        }) refs.Add(MetadataReference.CreateFromFile(asm.Location));

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var compilation = CSharpCompilation.Create(
            "BetlTestPlugin-" + Guid.NewGuid().ToString("N")[..8],
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release));

        var dllPath = Path.Combine(targetDir, $"BetlTestPlugin-{Guid.NewGuid():N}.dll");
        var result = compilation.Emit(dllPath);
        if (!result.Success)
        {
            var errs = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            throw new InvalidOperationException("Plugin compile failed:\n" + errs);
        }
        return dllPath;
    }

    [Fact]
    public void External_component_plugin_loads_from_drop_folder_and_dispatches()
    {
        const string pluginSource = """
            using System.Collections.Generic;
            using Betl.Components;
            using Betl.Core;

            namespace Betl.Test.Plugin;

            public sealed class UpperPlugin : IBetlComponentPlugin
            {
                public string StepType => "test.upper";

                public IDataComponent Create(
                    string stepId,
                    IReadOnlyDictionary<string, object?> body,
                    IDataComponent? upstream,
                    IReadOnlyDictionary<string, object?> resolvedParams)
                {
                    if (upstream is null)
                        throw new BetlException($"test.upper '{stepId}': requires from:");
                    if (!body.TryGetValue("column", out var colObj) || colObj is not string col)
                        throw new BetlException($"test.upper '{stepId}': requires 'column:' (string).");
                    var idx = upstream.OutputSchema.IndexOf(col);
                    if (idx < 0)
                        throw new BetlException($"test.upper '{stepId}': column '{col}' not in upstream schema.");
                    return new UpperComponent(stepId, upstream, idx);
                }
            }

            public sealed class UpperComponent : IDataComponent
            {
                private readonly IDataComponent _u;
                private readonly int _colIdx;
                public string Id { get; }
                public Schema OutputSchema => _u.OutputSchema;
                public UpperComponent(string id, IDataComponent u, int colIdx)
                {
                    Id = id; _u = u; _colIdx = colIdx;
                }
                public IEnumerable<Row> Stream()
                {
                    foreach (var row in _u.Stream())
                    {
                        var v = (object?[])row.Values.Clone();
                        v[_colIdx] = ((string?)v[_colIdx])?.ToUpperInvariant();
                        yield return new Row(OutputSchema, v);
                    }
                }
            }
            """;

        var pluginDir = Path.Combine(Path.GetTempPath(), $"betl-plugin-{Guid.NewGuid():N}");
        EmitPluginAssembly(pluginSource, pluginDir);

        var outPath = Path.Combine(Path.GetTempPath(), $"p9-plugin-{Guid.NewGuid():N}.csv");
        try
        {
            var registry = new PluginRegistry([pluginDir]);
            Assert.Contains("test.upper", registry.StepTypes);

            var pipeline = PipelineLoader.LoadFile(
                Path.Combine(FixtureDir("dotnet-plugin"), "pipeline.betl.yml"),
                registry.StepTypes);
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string> { ["out"] = outPath });
            new Executor(pipeline, ctx,
                new EngineRegistry().Register(new SsisExpressionEngine()),
                new ConnectionRegistry().Register(new SqliteProvider()),
                registry).Run();

            AssertFileMatches(Path.Combine(FixtureDir("dotnet-plugin"), "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            // Plugin DLLs are mmap'd by the default ALC; leave them for the OS to reap.
            try { Directory.Delete(pluginDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Unregistered_plugin_step_type_surfaces_clear_error_at_load_time()
    {
        // No plugin registry → unknown type fails at loader (existing behavior, not regressed).
        var src = """
            betl: 1
            name: bad
            pipeline:
              - id: x
                type: not.a.real.type
            """;
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load(src));
        Assert.Contains("not.a.real.type", ex.Message);
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
