using System.Text.Json;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Reads NDJSON (default) or a JSON array of objects. Per upstream spec, every
/// column comes out as utf8 — downstream `map` + ssisexpr handle further casts.
/// Missing keys become null.
/// </summary>
public sealed class JsonReadComponent : IDataComponent
{
    private readonly string _path;
    private readonly JsonFormat _format;
    private readonly string[] _columnNames;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public JsonReadComponent(JsonReadStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _format = step.Format;
        _columnNames = [.. step.Columns];
        OutputSchema = new Schema
        {
            Columns = step.Columns
                .Select(name => new Column { Name = name, ArrowType = StringType.Default, Nullable = true })
                .ToList(),
        };
    }

    public IEnumerable<Row> Stream() => _format switch
    {
        JsonFormat.Ndjson => StreamNdjson(),
        JsonFormat.Array => StreamArray(),
        _ => throw new BetlException($"json.read '{Id}': unknown format {_format}."),
    };

    private IEnumerable<Row> StreamNdjson()
    {
        foreach (var rawLine in File.ReadLines(_path))
        {
            var line = rawLine.AsSpan().Trim();
            if (line.IsEmpty) continue;
            using var doc = JsonDocument.Parse(line.ToString());
            yield return ExtractRow(doc.RootElement);
        }
    }

    private IEnumerable<Row> StreamArray()
    {
        using var stream = File.OpenRead(_path);
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new BetlException($"json.read '{Id}': format 'array' requires a top-level JSON array.");
        foreach (var el in doc.RootElement.EnumerateArray())
            yield return ExtractRow(el);
    }

    private Row ExtractRow(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new BetlException($"json.read '{Id}': expected JSON object per row, got {el.ValueKind}.");

        var values = new object?[_columnNames.Length];
        for (var i = 0; i < _columnNames.Length; i++)
        {
            values[i] = el.TryGetProperty(_columnNames[i], out var prop)
                ? CellToString(prop)
                : null;
        }
        return new Row(OutputSchema, values);
    }

    private static string? CellToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => el.GetString(),
        _ => el.GetRawText(),
    };
}
