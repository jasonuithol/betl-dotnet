using System.Globalization;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Single-pass group-by aggregator. Groups by an arbitrary subset of input columns
/// (zero group_by columns yields a single row, like SQL with no GROUP BY) and
/// emits one row per group at upstream EOF, holding the result in insertion order
/// of group keys.
/// </summary>
public sealed class AggregateComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int[] _groupIndices;
    private readonly AggSpec[] _aggs;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public AggregateComponent(AggregateStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        var input = upstream.OutputSchema;

        _groupIndices = RowOps.ResolveColumnIndices(input, step.GroupBy, $"aggregate '{step.Id}'");

        _aggs = new AggSpec[step.Compute.Count];
        var outCols = new List<Column>(_groupIndices.Length + _aggs.Length);
        foreach (var i in _groupIndices) outCols.Add(input.Columns[i]);

        for (var i = 0; i < step.Compute.Count; i++)
        {
            var (outName, compute) = (step.Compute[i].Key, step.Compute[i].Value);

            int? overIdx = null;
            IArrowType overType = StringType.Default;
            if (compute.Over is not null)
            {
                var idx = input.IndexOf(compute.Over);
                if (idx < 0) throw new BetlException(
                    $"aggregate '{step.Id}.{outName}': over column '{compute.Over}' is not in upstream schema.");
                overIdx = idx;
                overType = input.Columns[idx].ArrowType;
            }

            var outType = compute.Op switch
            {
                AggregateOp.Count or AggregateOp.CountDistinct => (IArrowType)Int64Type.Default,
                AggregateOp.Avg => DoubleType.Default,
                AggregateOp.Sum => IsIntegerType(overType) ? Int64Type.Default : DoubleType.Default,
                _ => overType, // min, max, first, last preserve input type
            };

            _aggs[i] = new AggSpec(outName, compute.Op, overIdx, IsIntegerType(overType), outType);
            outCols.Add(new Column { Name = outName, ArrowType = outType, Nullable = true });
        }

        OutputSchema = new Schema { Columns = outCols };
    }

    public IEnumerable<Row> Stream()
    {
        var groups = new Dictionary<object?[], AggState[]>(ObjectArrayComparer.Instance);
        var keyOrder = new List<object?[]>();

        foreach (var row in _upstream.Stream())
        {
            var key = RowOps.ExtractKey(row, _groupIndices);
            if (!groups.TryGetValue(key, out var states))
            {
                states = new AggState[_aggs.Length];
                for (var i = 0; i < _aggs.Length; i++) states[i] = CreateState(_aggs[i]);
                groups[key] = states;
                keyOrder.Add(key);
            }
            for (var i = 0; i < _aggs.Length; i++)
            {
                var v = _aggs[i].OverIdx.HasValue ? row.Values[_aggs[i].OverIdx!.Value] : null;
                states[i].Update(v);
            }
        }

        foreach (var key in keyOrder)
        {
            var states = groups[key];
            var values = new object?[_groupIndices.Length + _aggs.Length];
            Array.Copy(key, values, key.Length);
            for (var i = 0; i < states.Length; i++)
                values[key.Length + i] = states[i].Finalise();
            yield return new Row(OutputSchema, values);
        }
    }

    private static bool IsIntegerType(IArrowType t) =>
        t is Int8Type or Int16Type or Int32Type or Int64Type
            or UInt8Type or UInt16Type or UInt32Type or UInt64Type;

    private static AggState CreateState(AggSpec spec) => spec.Op switch
    {
        AggregateOp.Sum           => new SumState(spec.IsIntegerOver),
        AggregateOp.Count         => new CountState(),
        AggregateOp.CountDistinct => new CountDistinctState(),
        AggregateOp.Min           => new MinMaxState(min: true),
        AggregateOp.Max           => new MinMaxState(min: false),
        AggregateOp.Avg           => new AvgState(),
        AggregateOp.First         => new FirstLastState(first: true),
        AggregateOp.Last          => new FirstLastState(first: false),
        _ => throw new BetlException($"Unknown aggregate op {spec.Op}."),
    };

    private sealed record AggSpec(string OutName, AggregateOp Op, int? OverIdx, bool IsIntegerOver, IArrowType OutType);

    private abstract class AggState
    {
        public abstract void Update(object? v);
        public abstract object? Finalise();
    }

    private sealed class SumState(bool isInt) : AggState
    {
        private double _acc;
        private bool _seen;
        public override void Update(object? v)
        {
            if (v is null) return;
            _acc += Convert.ToDouble(v, CultureInfo.InvariantCulture);
            _seen = true;
        }
        public override object? Finalise()
        {
            if (!_seen) return null;
            return isInt ? (object)(long)_acc : _acc;
        }
    }

    private sealed class CountState : AggState
    {
        private long _n;
        public override void Update(object? v) => _n++;     // SQL COUNT(*) — every row counts
        public override object? Finalise() => _n;
    }

    private sealed class CountDistinctState : AggState
    {
        private readonly HashSet<object> _seen = [];
        public override void Update(object? v) { if (v is not null) _seen.Add(v); }
        public override object? Finalise() => (long)_seen.Count;
    }

    private sealed class MinMaxState(bool min) : AggState
    {
        private object? _current;
        public override void Update(object? v)
        {
            if (v is null) return;
            if (_current is null) { _current = v; return; }
            var cmp = RowOps.CompareScalars(v, _current);
            if ((min && cmp < 0) || (!min && cmp > 0)) _current = v;
        }
        public override object? Finalise() => _current;
    }

    private sealed class AvgState : AggState
    {
        private double _sum;
        private long _n;
        public override void Update(object? v)
        {
            if (v is null) return;
            _sum += Convert.ToDouble(v, CultureInfo.InvariantCulture);
            _n++;
        }
        public override object? Finalise() => _n == 0 ? null : _sum / _n;
    }

    private sealed class FirstLastState(bool first) : AggState
    {
        private object? _value;
        private bool _seen;
        public override void Update(object? v)
        {
            if (v is null) return;
            if (first && _seen) return;
            _value = v;
            _seen = true;
        }
        public override object? Finalise() => _value;
    }
}
