using System.Globalization;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components.Generators;

/// <summary>Synthetic source emitting N rows with a single string column "<prefix><i>".</summary>
public sealed class BetlGenStringsComponent : IDataComponent
{
    private readonly long _n;
    private readonly string _prefix;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public BetlGenStringsComponent(BetlGenStringsStep step)
    {
        Id = step.Id;
        _n = step.N;
        _prefix = step.Prefix;
        OutputSchema = new Schema
        {
            Columns = [new Column { Name = step.ColumnName, ArrowType = StringType.Default, Nullable = false }],
        };
    }

    public IEnumerable<Row> Stream()
    {
        for (var i = 0L; i < _n; i++)
            yield return new Row(OutputSchema, [_prefix + i.ToString(CultureInfo.InvariantCulture)]);
    }
}
