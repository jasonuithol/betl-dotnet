using System.Data.Common;
using Betl.Core;
using Microsoft.Data.SqlClient;

namespace Betl.Providers.Sql;

/// <summary>
/// SQL Server skeleton: connection open + MERGE-based upsert; unit-tested only
/// at the SQL-generation level. Integration testing against a live MSSQL is
/// deferred (the runtime here can't provision one).
/// </summary>
public sealed class MsSqlProvider : ISqlProvider
{
    public string Type => "mssql";

    public DbConnection OpenConnection(string dsn) => new SqlConnection(dsn);

    public string FormatUpsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, OnConflictMode mode)
    {
        var cols = string.Join(", ", columns);
        var values = string.Join(", ", columns.Select(c => "@" + c));

        if (mode == OnConflictMode.Error)
            return $"INSERT INTO {table} ({cols}) VALUES ({values})";

        var nonKey = columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal)).ToList();
        var onClause = string.Join(" AND ", keyColumns.Select(k => $"tgt.{k} = src.{k}"));
        var insertCols = cols;
        var insertVals = string.Join(", ", columns.Select(c => $"src.{c}"));

        var matched = mode switch
        {
            OnConflictMode.Update => nonKey.Count == 0
                ? ""
                : "WHEN MATCHED THEN UPDATE SET " + string.Join(", ", nonKey.Select(c => $"{c} = src.{c}")) + " ",
            OnConflictMode.UpdateIfChanged when nonKey.Count > 0 =>
                "WHEN MATCHED AND (" +
                string.Join(" OR ", nonKey.Select(c => $"(tgt.{c} <> src.{c} OR (tgt.{c} IS NULL AND src.{c} IS NOT NULL) OR (tgt.{c} IS NOT NULL AND src.{c} IS NULL))"))
                + ") THEN UPDATE SET " + string.Join(", ", nonKey.Select(c => $"{c} = src.{c}")) + " ",
            OnConflictMode.UpdateIfChanged => "",
            OnConflictMode.Ignore => "",
            _ => throw new BetlException($"Unsupported OnConflict mode '{mode}'."),
        };

        return $"MERGE INTO {table} WITH (HOLDLOCK) AS tgt USING (VALUES ({values})) AS src({cols}) " +
               $"ON {onClause} {matched}WHEN NOT MATCHED THEN INSERT ({insertCols}) VALUES ({insertVals});";
    }
}
