using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Stamps fixed string-valued audit columns onto every row. Values are
/// pre-substituted by the executor before construction, so the component
/// itself sees only resolved strings.
/// </summary>
public sealed class AuditComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly object?[] _appendedValues;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public AuditComponent(
        AuditStep step,
        IDataComponent upstream,
        IReadOnlyList<KeyValuePair<string, string>> resolvedColumns)
    {
        Id = step.Id;
        _upstream = upstream;

        // Detect name collisions against upstream — audit is additive, so
        // shadowing an existing column would be ambiguous and likely a mistake.
        foreach (var (name, _) in resolvedColumns)
        {
            if (upstream.OutputSchema.IndexOf(name) >= 0)
                throw new BetlException(
                    $"audit '{step.Id}': appended column '{name}' shadows an upstream column.");
        }

        var cols = new List<Column>(upstream.OutputSchema.Columns.Count + resolvedColumns.Count);
        cols.AddRange(upstream.OutputSchema.Columns);
        var utf8 = (IArrowType)StringType.Default;
        foreach (var (name, _) in resolvedColumns)
            cols.Add(new Column { Name = name, ArrowType = utf8, Nullable = true });
        OutputSchema = new Schema { Columns = cols };

        _appendedValues = resolvedColumns.Select(kv => (object?)kv.Value).ToArray();
    }

    public IEnumerable<Row> Stream()
    {
        var nUp = _upstream.OutputSchema.Columns.Count;
        var nAdd = _appendedValues.Length;
        foreach (var row in _upstream.Stream())
        {
            var values = new object?[nUp + nAdd];
            Array.Copy(row.Values, values, nUp);
            Array.Copy(_appendedValues, 0, values, nUp, nAdd);
            yield return new Row(OutputSchema, values);
        }
    }
}
