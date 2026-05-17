using System.Globalization;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>Per-Arrow-type parse/format helpers for CSV text I/O.</summary>
internal static class ScalarCodec
{
    public static object? Parse(IArrowType type, ReadOnlySpan<char> text, bool nullable)
    {
        if (text.IsEmpty)
        {
            if (nullable) return null;
            throw new BetlException("Empty CSV cell where the schema declares non-nullable.");
        }

        try
        {
            return type switch
            {
                Int64Type   => long.Parse(text, CultureInfo.InvariantCulture),
                Int32Type   => int.Parse(text, CultureInfo.InvariantCulture),
                Int16Type   => short.Parse(text, CultureInfo.InvariantCulture),
                Int8Type    => sbyte.Parse(text, CultureInfo.InvariantCulture),
                UInt64Type  => ulong.Parse(text, CultureInfo.InvariantCulture),
                UInt32Type  => uint.Parse(text, CultureInfo.InvariantCulture),
                UInt16Type  => ushort.Parse(text, CultureInfo.InvariantCulture),
                UInt8Type   => byte.Parse(text, CultureInfo.InvariantCulture),
                DoubleType  => double.Parse(text, CultureInfo.InvariantCulture),
                FloatType   => float.Parse(text, CultureInfo.InvariantCulture),
                StringType  => text.ToString(),
                BooleanType => bool.Parse(text),
                Date32Type  => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                _ => throw new BetlException($"CSV parse not implemented for Arrow type '{type.Name}'."),
            };
        }
        catch (FormatException ex)
        {
            throw new BetlException($"Cannot parse '{text}' as {type.Name}: {ex.Message}", ex);
        }
    }

    public static string Format(IArrowType type, object? value)
    {
        if (value is null) return "";
        return value switch
        {
            string s    => s,
            long l      => l.ToString(CultureInfo.InvariantCulture),
            int i       => i.ToString(CultureInfo.InvariantCulture),
            short sh    => sh.ToString(CultureInfo.InvariantCulture),
            sbyte sb    => sb.ToString(CultureInfo.InvariantCulture),
            ulong ul    => ul.ToString(CultureInfo.InvariantCulture),
            uint ui     => ui.ToString(CultureInfo.InvariantCulture),
            ushort us   => us.ToString(CultureInfo.InvariantCulture),
            byte b      => b.ToString(CultureInfo.InvariantCulture),
            double d    => d.ToString("R", CultureInfo.InvariantCulture),
            float f     => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m   => m.ToString(CultureInfo.InvariantCulture),
            bool tb     => tb ? "True" : "False",
            DateOnly d2 => d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
