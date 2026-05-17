using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Conformance.Tests;

public sealed class Phase3Tests
{
    private static string FixtureDir(string sub) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase3", sub);

    private static (Pipeline P, EngineRegistry E, ConnectionRegistry Sql) Load(string sub)
    {
        var path = Path.Combine(FixtureDir(sub), "pipeline.betl.yml");
        var pipeline = PipelineLoader.LoadFile(path);
        var engines = new EngineRegistry().Register(new SsisExpressionEngine());
        var sql = new ConnectionRegistry()
            .Register(new SqliteProvider())
            .Register(new PostgresProvider())
            .Register(new MsSqlProvider());
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
    public void Tasks_shell_file_copy_move_delete_run_in_after_order()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"p3-tasks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        var inFile = Path.Combine(outDir, "in.txt");
        File.WriteAllText(inFile, "phase3-tasks");

        try
        {
            var (p, e, sql) = Load("tasks");
            Run(p, e, sql, new()
            {
                ["in_file"] = inFile,
                ["out_dir"] = outDir,
            });

            Assert.True(File.Exists(inFile),                                  "source file should still exist");
            Assert.False(File.Exists(Path.Combine(outDir, "copied.txt")),     "copied.txt should have been moved away");
            Assert.False(File.Exists(Path.Combine(outDir, "moved.txt")),      "moved.txt should have been deleted");
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public void Pivot_long_to_wide_with_missing_cells_as_null()
    {
        var dir = FixtureDir("pivot");
        var outPath = Path.Combine(Path.GetTempPath(), $"p3-pivot-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("pivot");
            Run(p, e, sql, new()
            {
                ["in"] = Path.Combine(dir, "input.csv"),
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void Unpivot_wide_to_long_emits_one_row_per_value_column()
    {
        var dir = FixtureDir("unpivot");
        var outPath = Path.Combine(Path.GetTempPath(), $"p3-unpivot-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("unpivot");
            Run(p, e, sql, new()
            {
                ["in"] = Path.Combine(dir, "input.csv"),
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally { if (File.Exists(outPath)) File.Delete(outPath); }
    }

    [Fact]
    public void Sqlite_endToEnd_ddl_upsert_read_lookup_with_expect_assertion()
    {
        var dir = FixtureDir("sqlite");
        var dbFile = Path.Combine(Path.GetTempPath(), $"p3-sqlite-{Guid.NewGuid():N}.db");
        var outPath = Path.Combine(Path.GetTempPath(), $"p3-sqlite-{Guid.NewGuid():N}.csv");
        try
        {
            var (p, e, sql) = Load("sqlite");
            Run(p, e, sql, new()
            {
                ["db_file"] = dbFile,
                ["out"] = outPath,
            });
            AssertFileMatches(Path.Combine(dir, "expected.csv"), outPath);
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Generators_gen_int64_filter_count_rows_asserts_inline()
    {
        // This pipeline's count_rows step has an inline `expected: 50` so a
        // successful Run() IS the assertion. We add a tautological xUnit assert
        // to mark intent.
        var (p, e, sql) = Load("generators");
        Run(p, e, sql, new() { ["expected"] = "50" });
        Assert.True(true);
    }
}
