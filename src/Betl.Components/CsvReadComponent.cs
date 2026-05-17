using Betl.Core;
using nietras.SeparatedValues;

namespace Betl.Components;

public sealed class CsvReadComponent : IDataComponent
{
    private readonly string _path;
    private readonly char _delimiter;
    private readonly bool _hasHeader;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public CsvReadComponent(CsvReadStep step, string resolvedPath)
    {
        if (step.Delimiter.Length != 1)
            throw new BetlException($"csv.read delimiter must be a single character (got '{step.Delimiter}').");

        Id = step.Id;
        OutputSchema = step.Schema;
        _path = resolvedPath;
        _delimiter = step.Delimiter[0];
        _hasHeader = step.Header;
    }

    public IEnumerable<Row> Stream()
    {
        var spec = Sep.New(_delimiter);
        using var reader = spec.Reader(o => o with { HasHeader = _hasHeader }).FromFile(_path);

        var cols = OutputSchema.Columns;

        // Resolve column index in the CSV for each schema column.
        var csvIdx = new int[cols.Count];
        if (_hasHeader)
        {
            for (var i = 0; i < cols.Count; i++)
            {
                csvIdx[i] = reader.Header.IndexOf(cols[i].Name);
                if (csvIdx[i] < 0)
                    throw new BetlException(
                        $"csv.read '{Id}': CSV header is missing declared column '{cols[i].Name}'.");
            }
        }
        else
        {
            for (var i = 0; i < cols.Count; i++) csvIdx[i] = i;
        }

        foreach (var srcRow in reader)
        {
            var values = new object?[cols.Count];
            for (var i = 0; i < cols.Count; i++)
            {
                var span = srcRow[csvIdx[i]].Span;
                values[i] = ScalarCodec.Parse(cols[i].ArrowType, span, cols[i].Nullable);
            }
            yield return new Row(OutputSchema, values);
        }
    }
}
