using BenchmarkDotNet.Attributes;

namespace Betl.Perf.Bench;

/// <summary>
/// csv.read → filter (ssisexpr) → map (ssisexpr add column) → csv.write.
/// Single dataflow, file I/O on both ends. Toggles BETL_PARALLEL between
/// iterations to compare pull-only vs producer-thread-backed execution
/// (only one bench shows a measurable difference on >1 vCPU).
/// </summary>
[MemoryDiagnoser]
public class CsvPipelineBench
{
    private string _in = "";
    private string _outDir = "";

    [Params(10_000, 100_000)]
    public int Rows;

    [Params("off", "on")]
    public string Parallel = "off";

    [GlobalSetup]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"perf-csv-{Guid.NewGuid():N}");
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
    public void Read_Filter_Map_Write()
    {
        Environment.SetEnvironmentVariable("BETL_PARALLEL", Parallel);
        var outPath = Path.Combine(_outDir, $"out-{Guid.NewGuid():N}.csv");
        try
        {
            PerfHarness.RunInline(YamlFor(_in, outPath), new()
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

    private static string YamlFor(string inPath, string outPath) => $$"""
        betl: 1
        name: perf-csv
        parameters:
          in:  { type: string, required: true }
          out: { type: string, required: true }
        pipeline:
          - id: flow
            type: dataflow
            steps:
              - id: rd
                type: csv.read
                path: ${params.in}
                schema:
                  - { name: order_id, type: int64 }
                  - { name: region,   type: string }
                  - { name: amount,   type: int64 }
                  - { name: status,   type: string }
              - id: f
                type: filter
                from: rd
                where: { lang: ssisexpr, expr: 'status == "paid"' }
              - id: m
                type: map
                from: f
                add:
                  bumped: { lang: ssisexpr, expr: 'amount * 2' }
              - id: w
                type: csv.write
                from: m
                path: ${params.out}
        """;
}
