using System.Text.RegularExpressions;
using Apache.Arrow.Types;

namespace Betl.Core;

public static partial class ArrowTypes
{
    public static IArrowType Parse(string spelling)
    {
        var s = spelling.Trim();

        var simple = s switch
        {
            "bool" => (IArrowType)BooleanType.Default,
            "int8" => Int8Type.Default,
            "int16" => Int16Type.Default,
            "int32" => Int32Type.Default,
            "int64" => Int64Type.Default,
            "uint8" => UInt8Type.Default,
            "uint16" => UInt16Type.Default,
            "uint32" => UInt32Type.Default,
            "uint64" => UInt64Type.Default,
            "float16" => HalfFloatType.Default,
            "float32" => FloatType.Default,
            "float64" => DoubleType.Default,
            "string" or "utf8" => StringType.Default,
            "large_string" => StringType.Default,
            "binary" or "bytes" => BinaryType.Default,
            "date" or "date32" => Date32Type.Default,
            "date64" => Date64Type.Default,
            _ => null,
        };
        if (simple is not null) return simple;

        var dec = DecimalRegex().Match(s);
        if (dec.Success)
        {
            var width = int.Parse(dec.Groups[1].ValueSpan);
            var p = int.Parse(dec.Groups[2].ValueSpan);
            var scale = int.Parse(dec.Groups[3].ValueSpan);
            return width == 128
                ? new Decimal128Type(p, scale)
                : new Decimal256Type(p, scale);
        }

        var ts = TimestampRegex().Match(s);
        if (ts.Success)
        {
            var unit = ParseTimeUnit(ts.Groups[1].Value);
            var tz = ts.Groups[2].Success ? ts.Groups[2].Value.Trim() : null;
            return new TimestampType(unit, tz);
        }

        var time = TimeRegex().Match(s);
        if (time.Success)
        {
            var width = int.Parse(time.Groups[1].ValueSpan);
            var unit = ParseTimeUnit(time.Groups[2].Value);
            return width == 32 ? new Time32Type(unit) : new Time64Type(unit);
        }

        throw new NotSupportedException(
            $"Arrow type '{spelling}' is not supported by this runtime yet.");
    }

    private static TimeUnit ParseTimeUnit(string u) => u switch
    {
        "s" => TimeUnit.Second,
        "ms" => TimeUnit.Millisecond,
        "us" => TimeUnit.Microsecond,
        "ns" => TimeUnit.Nanosecond,
        _ => throw new NotSupportedException($"Time unit '{u}' unknown."),
    };

    [GeneratedRegex(@"^decimal(128|256)\(\s*(\d+)\s*,\s*(\d+)\s*\)$")]
    private static partial Regex DecimalRegex();

    [GeneratedRegex(@"^timestamp\[\s*(s|ms|us|ns)\s*(?:,\s*([A-Za-z0-9_/+\-]+))?\s*\]$")]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"^time(32|64)\[\s*(s|ms|us|ns)\s*\]$")]
    private static partial Regex TimeRegex();
}
