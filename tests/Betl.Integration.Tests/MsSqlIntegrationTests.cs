using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Integration.Tests.Engines;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Integration.Tests;

public sealed class MsSqlIntegrationTests : IClassFixture<MsSqlExpressFixture>
{
    private readonly MsSqlExpressFixture _ms;

    public MsSqlIntegrationTests(MsSqlExpressFixture ms) => _ms = ms;

    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "integration", sub);

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [SkippableFact]
    public void EndToEnd_read_upsert_lookup_with_real_sql_server()
    {
        Skip.IfNot(_ms.Available, $"SQL Server unreachable via {_ms.MasterDsn}.");

        var dir = FixtureDir("mssql");
        var outPath = Path.Combine(Path.GetTempPath(), $"it-ms-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new MsSqlProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["dsn"] = _ms.TestDsn,
                ["out"] = outPath,
            });

            new Executor(pipeline, ctx, engines, sql).Run();

            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [SkippableFact]
    public void MsSql_bulkinsert_append_and_truncate_modes_against_real_sql_server()
    {
        Skip.IfNot(_ms.Available, $"SQL Server unreachable via {_ms.MasterDsn}.");

        var dir = FixtureDir("mssql-bulkinsert");
        var outPath = Path.Combine(Path.GetTempPath(), $"it-ms-bulk-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new MsSqlProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["dsn"] = _ms.TestDsn,
                ["out"] = outPath,
            });

            new Executor(pipeline, ctx, engines, sql).Run();

            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }
}
