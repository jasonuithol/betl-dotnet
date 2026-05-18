using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace Betl.Core;

/// <summary>
/// Converts a <see cref="YamlMappingNode"/> step body to a plain dictionary
/// of CLR primitives + nested collections, so plugin code never has to
/// depend on YamlDotNet types. Used by plugin step dispatch only.
/// </summary>
public static class YamlBodyConverter
{
    public static IReadOnlyDictionary<string, object?> ToDict(YamlMappingNode node)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in node.Children)
        {
            if (entry.Key is not YamlScalarNode k || k.Value is null) continue;
            dict[k.Value] = ToObject(entry.Value);
        }
        return dict;
    }

    public static object? ToObject(YamlNode node) => node switch
    {
        YamlScalarNode s => ScalarToObject(s),
        YamlSequenceNode seq => seq.Children.Select(ToObject).ToList(),
        YamlMappingNode m => ToDict(m),
        _ => null,
    };

    private static object? ScalarToObject(YamlScalarNode s)
    {
        var v = s.Value;
        if (v is null) return null;
        if (v.Length == 0) return "";

        // Honour explicit YAML quoting: if the scalar was quoted in the
        // source (Style is Single/Double), treat it as a string even if
        // it looks numeric — the user said it was a string.
        if (s.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted
                    or YamlDotNet.Core.ScalarStyle.DoubleQuoted)
            return v;

        if (v == "null" || v == "~" || v == "Null" || v == "NULL") return null;
        if (v == "true" || v == "True" || v == "TRUE") return true;
        if (v == "false" || v == "False" || v == "FALSE") return false;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
            return i;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return v;
    }
}
