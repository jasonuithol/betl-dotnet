using BenchmarkDotNet.Attributes;

namespace Betl.Perf.Bench;

/// <summary>
/// Same shape as CsvPipelineBench, but with deliberately CPU-heavy per-row
/// work in the map stage (UPPER + SUBSTRING + arithmetic chain). The point
/// is to find the per-row workload above which BETL_PARALLEL's
/// producer/consumer threading actually wins on multi-vCPU hosts. If even
/// THIS loses, the threading model is paying queue overhead that exceeds
/// the throughput it enables, and the default needs reconsidering.
/// </summary>
[MemoryDiagnoser]
public class HeavyPipelineBench
{
    private string _in = "";
    private string _outDir = "";

    [Params(50_000)]
    public int Rows;

    [Params("off", "on")]
    public string Parallel = "off";

    [GlobalSetup]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"perf-heavy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
        _in = Path.Combine(_outDir, "in.csv");
        PerfHarness.GenerateCsv(_in, Rows);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_outDir)) Directory.Delete(_outDir, recursive: true);
    }

    [Benchmark]
    public void Read_HeavyMap_Write()
    {
        Environment.SetEnvironmentVariable("BETL_PARALLEL", Parallel);
        var outPath = Path.Combine(_outDir, $"out-{Guid.NewGuid():N}.csv");
        try
        {
            PerfHarness.RunInline(YamlFor(), new()
            {
                ["in"] = _in,
                ["out"] = outPath,
            });
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    private static string YamlFor() => $$""""
        betl: 1
        name: perf-heavy
        parameters:
          in:  { type: string, required: true }
          out: { type: string, required: true }
        pipeline:
          - id: flow
            type: dataflow
            steps:
              - id: rd
                type: csv.read
                path: "${params.in}"
                schema:
                  - { name: order_id, type: int64 }
                  - { name: region,   type: string }
                  - { name: amount,   type: int64 }
                  - { name: status,   type: string }
              - id: m
                type: map
                from: rd
                add:
                  big_calc:  { lang: ssisexpr, expr: '((amount * 7 + 13) % 1009) * ((amount * 3 + 17) % 997) + LEN(region) * 100' }
                  tag1:      { lang: ssisexpr, expr: 'UPPER(SUBSTRING(status, 1, 3)) + "-" + UPPER(SUBSTRING(region, 1, 2))' }
                  tag2:      { lang: ssisexpr, expr: 'LOWER(SUBSTRING(status, 1, 4)) + "/" + LOWER(SUBSTRING(region, 1, 2))' }
                  tag3:      { lang: ssisexpr, expr: 'SUBSTRING(status, 1, 1) + SUBSTRING(region, 1, 1) + SUBSTRING(status, 2, 1)' }
              - id: f
                type: filter
                from: m
                where: { lang: ssisexpr, expr: 'big_calc % 2 == 0' }
              - id: w
                type: csv.write
                from: f
                path: "${params.out}"
        """";
}
