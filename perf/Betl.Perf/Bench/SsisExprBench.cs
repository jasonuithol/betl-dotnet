using Betl.Core;
using Betl.Expressions.SsisExpr;
using BenchmarkDotNet.Attributes;

namespace Betl.Perf.Bench;

/// <summary>
/// Pure ssisexpr evaluator throughput — parse once, eval many. Strips out
/// I/O and the pipeline overhead so we can see expression engine
/// per-evaluation cost in isolation.
/// </summary>
[MemoryDiagnoser]
public class SsisExprBench
{
    private ICompiledExpression _arith = null!;
    private ICompiledExpression _str = null!;
    private ICompiledExpression _ternary = null!;
    private Row _row = null!;

    [GlobalSetup]
    public void Setup()
    {
        var engine = new SsisExpressionEngine();
        var schema = new Schema
        {
            Columns = new[]
            {
                new Column { Name = "a", ArrowType = ArrowTypes.Parse("int64")! },
                new Column { Name = "b", ArrowType = ArrowTypes.Parse("int64")! },
                new Column { Name = "s", ArrowType = ArrowTypes.Parse("string")! },
            }
        };
        _arith = engine.Compile("a * 2 + b - 7", schema);
        _str = engine.Compile("UPPER(SUBSTRING(s, 1, 4))", schema);
        _ternary = engine.Compile("a > b ? a - b : b - a", schema);
        _row = new Row(schema, [42L, 17L, "hello world"]);
    }

    [Benchmark(OperationsPerInvoke = 100_000)]
    public long Arithmetic_100k()
    {
        long acc = 0;
        for (int i = 0; i < 100_000; i++)
            acc += (long)_arith.Evaluate(_row)!;
        return acc;
    }

    [Benchmark(OperationsPerInvoke = 100_000)]
    public int StringFn_100k()
    {
        int acc = 0;
        for (int i = 0; i < 100_000; i++)
            acc += ((string)_str.Evaluate(_row)!).Length;
        return acc;
    }

    [Benchmark(OperationsPerInvoke = 100_000)]
    public long Ternary_100k()
    {
        long acc = 0;
        for (int i = 0; i < 100_000; i++)
            acc += (long)_ternary.Evaluate(_row)!;
        return acc;
    }
}
