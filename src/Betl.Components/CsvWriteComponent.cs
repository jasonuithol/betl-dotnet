using Betl.Core;
using nietras.SeparatedValues;

namespace Betl.Components;

public sealed class CsvWriteComponent : ISink
{
    private readonly string _path;
    private readonly char _delimiter;
    private readonly bool _writeHeader;

    public string Id { get; }

    public CsvWriteComponent(CsvWriteStep step, string resolvedPath)
    {
        if (step.Delimiter.Length != 1)
            throw new BetlException($"csv.write delimiter must be a single character (got '{step.Delimiter}').");

        Id = step.Id;
        _path = resolvedPath;
        _delimiter = step.Delimiter[0];
        _writeHeader = step.Header;
    }

    public void Drain(IDataComponent input)
    {
        var cols = input.OutputSchema.Columns;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var spec = Sep.New(_delimiter);
        using var writer = spec.Writer(o => o with { WriteHeader = _writeHeader }).ToFile(_path);

        foreach (var row in input.Stream())
        {
            using var w = writer.NewRow();
            for (var i = 0; i < cols.Count; i++)
            {
                w[cols[i].Name].Set(ScalarCodec.Format(cols[i].ArrowType, row.Values[i]));
            }
        }
    }
}
