using System.Data.Common;
using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Providers.Sql;

public interface ISqlProvider
{
    /// <summary>The value matched against connection.type in the pipeline YAML.</summary>
    string Type { get; }

    DbConnection OpenConnection(string dsn);

    /// <summary>SQL parameter prefix in generated upsert SQL (e.g. "@" for SQLite/MSSQL/Npgsql).</summary>
    string ParameterPrefix => "@";

    /// <summary>Build a dialect-specific upsert statement.</summary>
    string FormatUpsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, OnConflictMode mode);
}

internal static class AdoTypeMap
{
    public static IArrowType Map(Type clrType)
    {
        if (clrType == typeof(long))    return Int64Type.Default;
        if (clrType == typeof(int))     return Int32Type.Default;
        if (clrType == typeof(short))   return Int16Type.Default;
        if (clrType == typeof(sbyte))   return Int8Type.Default;
        if (clrType == typeof(ulong))   return UInt64Type.Default;
        if (clrType == typeof(uint))    return UInt32Type.Default;
        if (clrType == typeof(ushort))  return UInt16Type.Default;
        if (clrType == typeof(byte))    return UInt8Type.Default;
        if (clrType == typeof(double))  return DoubleType.Default;
        if (clrType == typeof(float))   return FloatType.Default;
        if (clrType == typeof(decimal)) return new Decimal128Type(38, 9);
        if (clrType == typeof(string))  return StringType.Default;
        if (clrType == typeof(bool))    return BooleanType.Default;
        if (clrType == typeof(DateOnly))   return Date32Type.Default;
        if (clrType == typeof(DateTime))   return new TimestampType(TimeUnit.Microsecond, "UTC");
        if (clrType == typeof(DateTimeOffset)) return new TimestampType(TimeUnit.Microsecond, "UTC");
        if (clrType == typeof(byte[])) return BinaryType.Default;
        if (clrType == typeof(Guid))   return new FixedSizeBinaryType(16);
        // Default: stringify whatever it is.
        return StringType.Default;
    }
}
