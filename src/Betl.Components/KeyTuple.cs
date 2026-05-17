using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Equality + hash over <c>object?[]</c> with element-wise <see cref="object.Equals(object?)"/>
/// semantics. Used as the key type for HashSet/Dictionary keyed by row projections
/// (distinct, aggregate group-by, join build side, etc.).
/// </summary>
internal sealed class ObjectArrayComparer : IEqualityComparer<object?[]>
{
    public static readonly ObjectArrayComparer Instance = new();

    public bool Equals(object?[]? x, object?[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (!object.Equals(x[i], y[i])) return false;
        return true;
    }

    public int GetHashCode(object?[] obj)
    {
        var h = new HashCode();
        foreach (var v in obj) h.Add(v);
        return h.ToHashCode();
    }
}

internal static class RowOps
{
    /// <summary>Resolves each column name to its index in the schema, erroring on unknown names.</summary>
    public static int[] ResolveColumnIndices(Schema schema, IReadOnlyList<string> names, string context)
    {
        var indices = new int[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            indices[i] = schema.IndexOf(names[i]);
            if (indices[i] < 0)
                throw new BetlException($"{context}: column '{names[i]}' is not in the upstream schema.");
        }
        return indices;
    }

    public static object?[] ExtractKey(Row row, int[] keyIndices)
    {
        var k = new object?[keyIndices.Length];
        for (var i = 0; i < keyIndices.Length; i++)
            k[i] = row.Values[keyIndices[i]];
        return k;
    }

    /// <summary>NULL sorts first (NULLS FIRST). Throws on incompatible scalar types.</summary>
    public static int CompareScalars(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        // Numeric promotion across int/double/long.
        if (a is long la && b is long lb) return la.CompareTo(lb);
        if (a is double da && b is double db) return da.CompareTo(db);
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Convert.ToDouble(a, System.Globalization.CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(b, System.Globalization.CultureInfo.InvariantCulture));
        }

        if (a is IComparable cmp && a.GetType() == b.GetType()) return cmp.CompareTo(b);
        throw new BetlException(
            $"Cannot compare values of type {a.GetType().Name} and {b.GetType().Name}.");
    }

    private static bool IsNumeric(object v) =>
        v is long or int or short or byte or sbyte or ulong or uint or ushort or double or float or decimal;
}
