using Apache.Arrow.Types;
using Betl.Components;
using Betl.Core;

namespace Betl.Providers.Sql;

/// <summary>
/// 1-sided indexed join against a reference table. Loads the table once at
/// construction (selecting only match + select columns) and probes a hash per
/// upstream row. <see cref="LookupMiss"/> controls what happens on no match.
/// </summary>
public sealed class SqlLookupComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly int[] _matchInputIndices;
    private readonly Dictionary<object?[], object?[]> _table;
    private readonly LookupMiss _onMiss;
    private readonly int _inputWidth;
    private readonly int _selectWidth;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public SqlLookupComponent(LookupStep step, IDataComponent upstream, ISqlProvider provider, string dsn)
    {
        Id = step.Id;
        _upstream = upstream;
        _onMiss = step.OnMiss;
        var input = upstream.OutputSchema;
        _inputWidth = input.Columns.Count;

        _matchInputIndices = new int[step.Match.Count];
        for (var i = 0; i < step.Match.Count; i++)
        {
            _matchInputIndices[i] = input.IndexOf(step.Match[i].Key);
            if (_matchInputIndices[i] < 0)
                throw new BetlException(
                    $"lookup '{step.Id}': match input column '{step.Match[i].Key}' is not in upstream schema.");
        }

        var matchTableCols = step.Match.Select(m => m.Value).ToList();
        var selectTableCols = step.Select.Select(s => s.Value).ToList();
        _selectWidth = selectTableCols.Count;
        var allCols = string.Join(", ", matchTableCols.Concat(selectTableCols));
        var sql = $"SELECT {allCols} FROM {step.Table}";

        using var conn = provider.OpenConnection(dsn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        _table = new Dictionary<object?[], object?[]>(Betl.Components.ObjectArrayComparer.Instance);
        while (reader.Read())
        {
            var key = new object?[matchTableCols.Count];
            for (var i = 0; i < matchTableCols.Count; i++)
                key[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            var val = new object?[_selectWidth];
            for (var i = 0; i < _selectWidth; i++)
            {
                var col = matchTableCols.Count + i;
                val[i] = reader.IsDBNull(col) ? null : reader.GetValue(col);
            }
            _table[key] = val;
        }

        // Output schema: input columns + selected lookup columns (typed as string for now;
        // proper type inference from reader.GetFieldType would require an extra schema probe).
        var outCols = new List<Column>(_inputWidth + _selectWidth);
        outCols.AddRange(input.Columns);
        foreach (var s in step.Select)
            outCols.Add(new Column { Name = s.Key, ArrowType = StringType.Default, Nullable = true });
        OutputSchema = new Schema { Columns = outCols };
    }

    public IEnumerable<Row> Stream()
    {
        foreach (var row in _upstream.Stream())
        {
            var key = new object?[_matchInputIndices.Length];
            for (var i = 0; i < _matchInputIndices.Length; i++)
                key[i] = row.Values[_matchInputIndices[i]];

            if (_table.TryGetValue(key, out var found))
            {
                var values = new object?[_inputWidth + _selectWidth];
                Array.Copy(row.Values, 0, values, 0, _inputWidth);
                Array.Copy(found, 0, values, _inputWidth, _selectWidth);
                yield return new Row(OutputSchema, values);
            }
            else
            {
                switch (_onMiss)
                {
                    case LookupMiss.Error:
                        throw new BetlException(
                            $"lookup '{Id}': no match for key " +
                            $"({string.Join(", ", key.Select(k => k?.ToString() ?? "null"))}).");
                    case LookupMiss.Null:
                        var values = new object?[_inputWidth + _selectWidth];
                        Array.Copy(row.Values, 0, values, 0, _inputWidth);
                        yield return new Row(OutputSchema, values);
                        break;
                    case LookupMiss.Drop:
                        continue;
                }
            }
        }
    }
}
