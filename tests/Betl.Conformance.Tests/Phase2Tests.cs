using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase2Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase2", sub);

    private static (Pipeline P, EngineRegistry E) Load(string sub)
    {
        var path = Path.Combine(FixtureDir(sub), "pipeline.betl.yml");
        var pipeline = PipelineLoader.LoadFile(path);
        var engines = new EngineRegistry().Register(new SsisExpressionEngine());
        return (pipeline, engines);
    }

    private static void Run(Pipeline p, EngineRegistry e, Dictionary<string, string> @params)
    {
        var ctx = ParameterContext.Build(p, @params);
        new Executor(p, ctx, e).Run();
    }

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ConditionalSplit_Aggregate_Sort_JsonWrite_round_trip_through_dataflow()
    {
        var dir = FixtureDir("aggregate-stars");
        var outPaid = Path.Combine(Path.GetTempPath(), $"p2-paid-{Guid.NewGuid():N}.ndjson");
        var outRef = Path.Combine(Path.GetTempPath(), $"p2-ref-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e) = Load("aggregate-stars");
            Run(p, e, new()
            {
                ["in"] = Path.Combine(dir, "input.csv"),
                ["out_paid"] = outPaid,
                ["out_refunds"] = outRef,
            });
            AssertFileMatches(Path.Combine(dir, "expected-paid.ndjson"), outPaid);
            AssertFileMatches(Path.Combine(dir, "expected-refunds.csv"), outRef);
        }
        finally
        {
            if (File.Exists(outPaid)) File.Delete(outPaid);
            if (File.Exists(outRef)) File.Delete(outRef);
        }
    }

    [Fact]
    public void Join_Distinct_Limit_writes_top_5_inner_join_rows()
    {
        var dir = FixtureDir("enrich");
        var outPath = Path.Combine(Path.GetTempPath(), $"p2-enrich-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e) = Load("enrich");
            Run(p, e, new()
            {
                ["orders_in"] = Path.Combine(dir, "orders.csv"),
                ["customers_in"] = Path.Combine(dir, "customers.csv"),
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void Foreach_with_vars_runs_inner_dataflow_per_iteration()
    {
        var dir = FixtureDir("loop-regions");
        var outDir = Path.Combine(Path.GetTempPath(), $"p2-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            var (p, e) = Load("loop-regions");
            Run(p, e, new()
            {
                ["in_dir"] = Path.Combine(dir, "in"),
                ["out_dir"] = outDir,
            });

            foreach (var region in new[] { "us", "eu", "jp" })
                AssertFileMatches(
                    Path.Combine(dir, "in", $"{region}.csv"),
                    Path.Combine(outDir, $"{region}.csv"));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Union_then_Multicast_feeds_two_independent_downstreams()
    {
        var dir = FixtureDir("union-multi");
        var outFull = Path.Combine(Path.GetTempPath(), $"p2-um-full-{Guid.NewGuid():N}.csv");
        var outPaid = Path.Combine(Path.GetTempPath(), $"p2-um-paid-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e) = Load("union-multi");
            Run(p, e, new()
            {
                ["in1"] = Path.Combine(dir, "in1.csv"),
                ["in2"] = Path.Combine(dir, "in2.csv"),
                ["out_full"] = outFull,
                ["out_paid"] = outPaid,
            });
            AssertFileMatches(Path.Combine(dir, "expected-full.csv"), outFull);
            AssertFileMatches(Path.Combine(dir, "expected-paid.csv"), outPaid);
        }
        finally
        {
            if (File.Exists(outFull)) File.Delete(outFull);
            if (File.Exists(outPaid)) File.Delete(outPaid);
        }
    }
}
