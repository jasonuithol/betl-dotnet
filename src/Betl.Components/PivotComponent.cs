using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Long → wide. Materialises the upstream once at construction to discover the
/// distinct values of <c>name_col</c> (which become the new column names) and
/// build one output row per unique <c>pivot_keys</c> tuple. Within a group, if
/// the same (key, name_col_value) appears more than once, the last value wins.
/// </summary>
public sealed class PivotComponent : IDataComponent
{
    private readonly List<Row> _materialised;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public PivotComponent(PivotStep step, IDataComponent upstream)
    {
        Id = step.Id;
        var input = upstream.OutputSchema;

        var keyIndices = RowOps.ResolveColumnIndices(input, step.PivotKeys, $"pivot '{step.Id}'");
        var nameIdx = input.IndexOf(step.NameColumn);
        if (nameIdx < 0) throw new BetlException($"pivot '{step.Id}': name_col '{step.NameColumn}' not in input schema.");
        var valueIdx = input.IndexOf(step.ValueColumn);
        if (valueIdx < 0) throw new BetlException($"pivot '{step.Id}': value_col '{step.ValueColumn}' not in input schema.");
        var valueType = input.Columns[valueIdx].ArrowType;

        // Single pass: collect distinct name values (in first-seen order, OR
        // restricted to the user's declared pivot_values list), and per
        // (key, name_value), the latest value.
        var declaredValues = step.PivotValues;
        var declaredSet = declaredValues is null
            ? null
            : new HashSet<string>(declaredValues, StringComparer.Ordinal);
        var distinctNames = declaredValues is null
            ? new List<string>()
            : new List<string>(declaredValues);
        var seenNames = declaredValues is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(declaredValues, StringComparer.Ordinal);
        var groupRows = new Dictionary<object?[], Dictionary<string, object?>>(ObjectArrayComparer.Instance);
        var groupOrder = new List<object?[]>();

        foreach (var row in upstream.Stream())
        {
            var name = row.Values[nameIdx]?.ToString() ?? "";
            if (declaredSet is not null && !declaredSet.Contains(name)) continue;

            var key = RowOps.ExtractKey(row, keyIndices);
            if (!groupRows.TryGetValue(key, out var cells))
            {
                groupRows[key] = cells = new Dictionary<string, object?>(StringComparer.Ordinal);
                groupOrder.Add(key);
            }
            if (seenNames.Add(name)) distinctNames.Add(name);
            cells[name] = row.Values[valueIdx];
        }

        // Build output schema: pivot_keys + each distinct name as a value column.
        var outCols = new List<Column>(keyIndices.Length + distinctNames.Count);
        foreach (var i in keyIndices) outCols.Add(input.Columns[i]);
        foreach (var n in distinctNames)
            outCols.Add(new Column { Name = n, ArrowType = valueType, Nullable = true });
        OutputSchema = new Schema { Columns = outCols };

        _materialised = new List<Row>(groupOrder.Count);
        foreach (var key in groupOrder)
        {
            var values = new object?[outCols.Count];
            Array.Copy(key, values, key.Length);
            var cells = groupRows[key];
            for (var i = 0; i < distinctNames.Count; i++)
                values[key.Length + i] = cells.TryGetValue(distinctNames[i], out var v) ? v : null;
            _materialised.Add(new Row(OutputSchema, values));
        }
    }

    public IEnumerable<Row> Stream() => _materialised;
}
