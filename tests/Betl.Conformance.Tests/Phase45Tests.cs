using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase45Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase45", sub);

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

    [Fact]
    public void Arrow_round_trip_csv_to_arrow_ipc_to_csv()
    {
        var dir = FixtureDir("arrow-roundtrip");
        var tmpArrow = Path.Combine(Path.GetTempPath(), $"p45-{Guid.NewGuid():N}.arrow");
        var outCsv = Path.Combine(Path.GetTempPath(), $"p45-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("arrow-roundtrip");
            Run(p, e, sql, new()
            {
                ["in_csv"] = Path.Combine(dir, "input.csv"),
                ["tmp_arrow"] = tmpArrow,
                ["out_csv"] = outCsv,
            });
            // Round-trip: the CSV-out should be byte-equal to the CSV-in.
            AssertFileMatches(Path.Combine(dir, "input.csv"), outCsv);
            // Arrow IPC file should exist and be non-trivial.
            Assert.True(new FileInfo(tmpArrow).Length > 100);
        }
        finally
        {
            if (File.Exists(tmpArrow)) File.Delete(tmpArrow);
            if (File.Exists(outCsv)) File.Delete(outCsv);
        }
    }

    [Fact]
    public void DotnetScript_compiles_user_csharp_and_emits_rows()
    {
        var dir = FixtureDir("dotnet-script");
        var outPath = Path.Combine(Path.GetTempPath(), $"p45-script-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("dotnet-script");
            Run(p, e, sql, new() { ["out"] = outPath });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void DotnetPipelineComponent_drives_lifecycle_and_emits_via_buffer()
    {
        var dir = FixtureDir("dotnet-pipelinecomponent");
        var outPath = Path.Combine(Path.GetTempPath(), $"p45-pc-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("dotnet-pipelinecomponent");
            Run(p, e, sql, new() { ["out"] = outPath });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void DotnetTask_executes_user_csharp_with_BetlTaskContext()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"p45-task-{Guid.NewGuid():N}.txt");
        try
        {
            var (p, e, sql) = Load("dotnet-task");
            Run(p, e, sql, new() { ["out"] = outPath });
            Assert.Equal("hello from dotnet.task", File.ReadAllText(outPath));
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

}
