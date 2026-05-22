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

    // ----- audit ----------------------------------------------------------

    private static void AssertFileMatches(string expectedPath, string actualPath)
    {
        var expected = File.ReadAllText(expectedPath).Replace("\r\n", "\n").TrimEnd('\n');
        var actual = File.ReadAllText(actualPath).Replace("\r\n", "\n").TrimEnd('\n');
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Audit_appends_string_columns_with_substituted_values_to_every_row()
    {
        var dir = FixtureDir("audit");
        var outCsv = Path.Combine(Path.GetTempPath(), $"p10b-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("audit");
            Run(p, e, sql, new() { ["out"] = outCsv, ["run_id"] = "r-001" });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outCsv);
        }
        finally { if (File.Exists(outCsv)) File.Delete(outCsv); }
    }

    [Fact]
    public void Audit_rejects_column_name_that_shadows_upstream()
    {
        var yaml = """
            betl: 1
            name: bad
            pipeline:
              - id: df
                type: dataflow
                steps:
                  - id: gen
                    type: betl.gen_int64
                    n: 1
                  - id: a
                    type: audit
                    from: gen
                    columns:
                      n: "oops"
                  - id: w
                    type: csv.write
                    from: a
                    path: /tmp/never.csv
            """;
        var p = PipelineLoader.Load(yaml);
        var ctx = ParameterContext.Build(p, new Dictionary<string, string>());
        var engines = new EngineRegistry().Register(new SsisExpressionEngine());
        var sqlReg = new ConnectionRegistry().Register(new SqliteProvider());
        var ex = Assert.Throws<BetlException>(() => new Executor(p, ctx, engines, sqlReg).Run());
        Assert.Contains("shadows an upstream column", ex.Message);
    }

    [Fact]
    public void Audit_rejects_empty_columns_at_load_time()
    {
        var yaml = """
            betl: 1
            name: bad
            pipeline:
              - id: df
                type: dataflow
                steps:
                  - id: gen
                    type: betl.gen_int64
                    n: 1
                  - id: a
                    type: audit
                    from: gen
                    columns: {}
                  - id: w
                    type: csv.write
                    from: a
                    path: /tmp/never.csv
            """;
        var ex = Assert.Throws<PipelineLoadException>(() => PipelineLoader.Load(yaml));
        Assert.Contains("must list at least one column", ex.Message);
    }
}
