using System.Globalization;
using System.Text.Json.Nodes;
using Betl.Core;

namespace Betl.Components;

public sealed class JsonWriteComponent : ISink
{
    private readonly string _path;
    private readonly JsonFormat _format;

    public string Id { get; }

    public JsonWriteComponent(JsonWriteStep step, string resolvedPath)
    {
        Id = step.Id;
        _path = resolvedPath;
        _format = step.Format;
    }

    public void Drain(IDataComponent input)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var cols = input.OutputSchema.Columns;

        using var writer = new StreamWriter(_path);
        if (_format == JsonFormat.Array) writer.Write('[');

        var first = true;
        foreach (var row in input.Stream())
        {
            var obj = new JsonObject();
            for (var i = 0; i < cols.Count; i++)
                obj[cols[i].Name] = ToJsonNode(row.Values[i]);

            if (_format == JsonFormat.Array)
            {
                if (!first) writer.Write(',');
                writer.Write(obj.ToJsonString());
            }
            else
            {
                writer.Write(obj.ToJsonString());
                writer.Write('\n');
            }
            first = false;
        }

        if (_format == JsonFormat.Array) writer.Write(']');
    }

    private static JsonNode? ToJsonNode(object? v) => v switch
    {
        null => null,
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        short sh => JsonValue.Create(sh),
        sbyte sb => JsonValue.Create(sb),
        ulong ul => JsonValue.Create(ul),
        uint ui => JsonValue.Create(ui),
        ushort us => JsonValue.Create(us),
        byte b => JsonValue.Create(b),
        double d => double.IsNaN(d) || double.IsInfinity(d) ? null : JsonValue.Create(d),
        float f => float.IsNaN(f) || float.IsInfinity(f) ? null : JsonValue.Create(f),
        bool tb => JsonValue.Create(tb),
        decimal m => JsonValue.Create(m),
        DateOnly d2 => JsonValue.Create(d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(v.ToString()),
    };
}
