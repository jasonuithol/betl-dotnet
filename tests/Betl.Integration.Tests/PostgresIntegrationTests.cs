using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Integration.Tests.Engines;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Integration.Tests;

public sealed class PostgresIntegrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public PostgresIntegrationTests(PostgresFixture pg) => _pg = pg;

    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "integration", sub);

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [SkippableFact]
    public void EndToEnd_read_upsert_lookup_with_real_postgres()
    {
        Skip.IfNot(_pg.Available, $"Postgres unreachable at {_pg.MaintenanceDsn}.");

        var dir = FixtureDir("postgres");
        var outPath = Path.Combine(Path.GetTempPath(), $"it-pg-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new PostgresProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["dsn"] = _pg.TestDsn,
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
    public void Postgres_exec_per_row_with_positional_parameters_against_real_postgres()
    {
        Skip.IfNot(_pg.Available, $"Postgres unreachable at {_pg.MaintenanceDsn}.");

        var dir = FixtureDir("postgres-exec");
        var outPath = Path.Combine(Path.GetTempPath(), $"it-pg-exec-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new PostgresProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["dsn"] = _pg.TestDsn,
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
    public void Postgres_copy_append_and_truncate_modes_against_real_postgres()
    {
        Skip.IfNot(_pg.Available, $"Postgres unreachable at {_pg.MaintenanceDsn}.");

        var dir = FixtureDir("postgres-copy");
        var outPath = Path.Combine(Path.GetTempPath(), $"it-pg-copy-{Guid.NewGuid():N}.csv");
        try
        {
            var pipeline = PipelineLoader.LoadFile(Path.Combine(dir, "pipeline.betl.yml"));
            var engines = new EngineRegistry().Register(new SsisExpressionEngine());
            var sql = new ConnectionRegistry().Register(new PostgresProvider());
            var ctx = ParameterContext.Build(pipeline, new Dictionary<string, string>
            {
                ["dsn"] = _pg.TestDsn,
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
