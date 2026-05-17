using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Wide → long. Each input row becomes one output row per <c>value_cols</c>
/// entry. The output schema is (all non-value-col input columns) + name_col
/// + value_col. The value_col's Arrow type is the common type of all
/// value_cols if they agree, otherwise string.
/// </summary>
public sealed class UnpivotComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int[] _passthroughIndices;
    private readonly int[] _valueIndices;
    private readonly string[] _valueNames;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public UnpivotComponent(UnpivotStep step, IDataComponent upstream)
    {
        Id = step.Id;
        _upstream = upstream;
        var input = upstream.OutputSchema;

        var valueSet = new HashSet<string>(step.ValueColumns, StringComparer.OrdinalIgnoreCase);
        _valueNames = [.. step.ValueColumns];
        _valueIndices = new int[_valueNames.Length];
        for (var i = 0; i < _valueNames.Length; i++)
        {
            _valueIndices[i] = input.IndexOf(_valueNames[i]);
            if (_valueIndices[i] < 0)
                throw new BetlException($"unpivot '{step.Id}': value_col '{_valueNames[i]}' is not in input schema.");
        }

        var passList = new List<int>();
        for (var i = 0; i < input.Columns.Count; i++)
            if (!valueSet.Contains(input.Columns[i].Name)) passList.Add(i);
        _passthroughIndices = [.. passList];

        var common = _valueIndices.Select(i => input.Columns[i].ArrowType.Name).Distinct().ToArray();
        var valueArrowType = common.Length == 1
            ? input.Columns[_valueIndices[0]].ArrowType
            : (IArrowType)StringType.Default;

        var outCols = new List<Column>(_passthroughIndices.Length + 2);
        foreach (var i in _passthroughIndices) outCols.Add(input.Columns[i]);
        outCols.Add(new Column { Name = step.NameColumn, ArrowType = StringType.Default, Nullable = false });
        outCols.Add(new Column { Name = step.ValueColumn, ArrowType = valueArrowType, Nullable = true });

        OutputSchema = new Schema { Columns = outCols };
    }

    public IEnumerable<Row> Stream()
    {
        var passCount = _passthroughIndices.Length;
        foreach (var inRow in _upstream.Stream())
        {
            for (var vi = 0; vi < _valueIndices.Length; vi++)
            {
                var values = new object?[passCount + 2];
                for (var p = 0; p < passCount; p++) values[p] = inRow.Values[_passthroughIndices[p]];
                values[passCount]     = _valueNames[vi];
                values[passCount + 1] = inRow.Values[_valueIndices[vi]];
                yield return new Row(OutputSchema, values);
            }
        }
    }
}
