using Betl.Core;
using Betl.Expressions.SsisExpr;
using Betl.Providers.Sql;
using Betl.Runtime;

namespace Betl.Perf.Bench;

internal static class PerfHarness
{
    public static void GenerateCsv(string path, int rows)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("order_id,region,amount,status");
        var regions = new[] { "us", "eu", "jp", "uk", "au" };
        var statuses = new[] { "paid", "refunded", "pending" };
        for (int i = 1; i <= rows; i++)
            w.WriteLine($"{i},{regions[i % 5]},{(i * 7) % 9971},{statuses[i % 3]}");
    }

    public static (EngineRegistry e, ConnectionRegistry sql) Registries()
        => (new EngineRegistry().Register(new SsisExpressionEngine()),
            new ConnectionRegistry()
                .Register(new SqliteProvider())
                .Register(new PostgresProvider())
                .Register(new MsSqlProvider()));

    public static void RunInline(string yaml, Dictionary<string, string> @params)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"perf-{Guid.NewGuid():N}.betl.yml");
        File.WriteAllText(tmp, yaml);
        try
        {
            var p = PipelineLoader.LoadFile(tmp);
            var (e, sql) = Registries();
            var ctx = ParameterContext.Build(p, @params);
            new Executor(p, ctx, e, sql).Run();
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
