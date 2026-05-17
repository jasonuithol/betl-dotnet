using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Betl.Core;

/// <summary>
/// Validates YAML pipeline text against the embedded upstream
/// <c>pipeline.schema.json</c>. Note this is a structural / necessary check —
/// betl.dotnet additionally rejects some shapes the upstream schema permits
/// (Lua expressions / steps); those are caught at typed-parse time.
/// </summary>
public static class SchemaValidator
{
    private static readonly Lazy<JsonSchema> Schema = new(LoadEmbeddedSchema);

    public static IReadOnlyList<string> Validate(string yamlText)
    {
        var node = YamlToJson(yamlText);
        var results = Schema.Value.Evaluate(node, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        if (results.IsValid) return [];

        var errors = new List<string>();
        Collect(results, errors);
        return errors;
    }

    private static void Collect(EvaluationResults r, List<string> sink)
    {
        if (r.HasErrors && r.Errors is not null)
        {
            var path = r.InstanceLocation.ToString();
            foreach (var (key, msg) in r.Errors)
                sink.Add($"{(string.IsNullOrEmpty(path) ? "<root>" : path)}: {msg} ({key})");
        }
        if (r.Details is not null)
            foreach (var d in r.Details) Collect(d, sink);
    }

    private static JsonNode YamlToJson(string yamlText)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(yamlText);
        try { stream.Load(reader); }
        catch (YamlException ex)
        {
            throw new PipelineLoadException($"YAML parse error: {ex.Message}", ex);
        }
        if (stream.Documents.Count == 0)
            throw new PipelineLoadException("YAML document is empty.");
        return ConvertNode(stream.Documents[0].RootNode)
            ?? throw new PipelineLoadException("Root node is null.");
    }

    private static JsonNode? ConvertNode(YamlNode node) => node switch
    {
        YamlMappingNode m => ConvertMapping(m),
        YamlSequenceNode s => ConvertSequence(s),
        YamlScalarNode sc => ConvertScalar(sc),
        _ => throw new PipelineLoadException($"Unknown YAML node type: {node.GetType().Name}"),
    };

    private static JsonObject ConvertMapping(YamlMappingNode m)
    {
        var obj = new JsonObject();
        foreach (var kv in m.Children)
        {
            var key = ((YamlScalarNode)kv.Key).Value!;
            obj[key] = ConvertNode(kv.Value);
        }
        return obj;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode s)
    {
        var arr = new JsonArray();
        foreach (var child in s.Children) arr.Add(ConvertNode(child));
        return arr;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode s)
    {
        var v = s.Value;
        if (v is null) return null;
        if (s.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
            return JsonValue.Create(v);
        if (string.Equals(v, "null", StringComparison.OrdinalIgnoreCase) || v == "~")
            return null;
        if (string.Equals(v, "true",  StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return JsonValue.Create(i);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    private static JsonSchema LoadEmbeddedSchema()
    {
        var asm = typeof(SchemaValidator).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("pipeline.schema.json", StringComparison.Ordinal))
            ?? throw new BetlException("Embedded resource 'pipeline.schema.json' not found.");
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new BetlException($"Could not open embedded resource '{resourceName}'.");
        using var sr = new StreamReader(stream);
        return JsonSchema.FromText(sr.ReadToEnd());
    }
}
