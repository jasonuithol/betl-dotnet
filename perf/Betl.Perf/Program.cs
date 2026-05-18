using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Betl.Perf;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "probe-sql")
        {
            foreach (var d in new[] { "sqlite", "postgres", "mssql" })
            {
                var b = new Bench.SqlUpsertBench { Dialect = d, Rows = 1000 };
                try { b.Setup(); Console.WriteLine($"{d} setup OK"); b.Upsert_N_rows(); Console.WriteLine($"{d} upsert OK"); }
                catch (Exception e) { Console.WriteLine($"{d} FAIL: {e.GetType().Name}: {e.Message}"); }
                finally { try { b.Cleanup(); } catch { } }
            }
            return;
        }

        var cfg = ManualConfig.CreateMinimumViable()
            .AddDiagnoser(MemoryDiagnoser.Default)
            .AddJob(Job.ShortRun.WithWarmupCount(2).WithIterationCount(3));

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, cfg);
    }
}
