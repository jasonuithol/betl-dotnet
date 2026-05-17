using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components.Generators;

/// <summary>Synthetic source emitting N rows with a single int64 column.</summary>
public sealed class BetlGenInt64Component : IDataComponent
{
    private readonly long _n;
    private readonly long _start;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public BetlGenInt64Component(BetlGenInt64Step step)
    {
        Id = step.Id;
        _n = step.N;
        _start = step.Start;
        OutputSchema = new Schema
        {
            Columns = [new Column { Name = step.ColumnName, ArrowType = Int64Type.Default, Nullable = false }],
        };
    }

    public IEnumerable<Row> Stream()
    {
        for (var i = 0L; i < _n; i++) yield return new Row(OutputSchema, [_start + i]);
    }
}
