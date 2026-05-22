using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase10Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase10", sub);

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

    // ----- var.set --------------------------------------------------------

    [Fact]
    public void VarSet_literal_mode_substitutes_param_then_binds_var_for_downstream_use()
    {
        var outCsv = Path.Combine(Path.GetTempPath(), $"p10a-lit-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("var-set-literal");
            Run(p, e, sql, new() { ["out"] = outCsv, ["prefix"] = "greetings" });
            var rows = File.ReadAllLines(outCsv);
            // header + one data row
            Assert.Equal(2, rows.Length);
            Assert.Contains("greetings-world", rows[1]);
        }
        finally { if (File.Exists(outCsv)) File.Delete(outCsv); }
    }

    [Fact]
    public void VarSet_sql_mode_takes_first_column_first_row_and_binds_as_string()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"p10a-sql-{Guid.NewGuid():N}.db");
        var outCsv = Path.Combine(Path.GetTempPath(), $"p10a-sql-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("var-set-sql");
            Run(p, e, sql, new() { ["db"] = dbPath, ["out"] = outCsv });
            var rows = File.ReadAllLines(outCsv);
            Assert.Equal(2, rows.Length);
            Assert.Contains("42", rows[1]);
        }
        finally
        {
            if (File.Exists(outCsv)) File.Delete(outCsv);
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    [Fact]
    public void VarSet_rejects_both_literal_and_sql_modes_set()
    {
        var yaml = """
            betl: 1
            name: bad
            pipeline:
              - id: bad
                type: var.set
                name: x
                value: "1"
                connection: c
                sql: SELECT 1
            """;
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load(yaml));
        Assert.Contains("either 'value:'", ex.Message);
    }

    [Fact]
    public void VarSet_rejects_missing_both_modes()
    {
        var yaml = """
            betl: 1
            name: bad
            pipeline:
              - id: bad
                type: var.set
                name: x
            """;
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load(yaml));
        Assert.Contains("missing", ex.Message);
    }
}
