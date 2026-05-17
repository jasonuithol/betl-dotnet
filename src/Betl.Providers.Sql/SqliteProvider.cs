using System.Data.Common;
using System.Text;
using Betl.Core;
using Microsoft.Data.Sqlite;

namespace Betl.Providers.Sql;

public sealed class SqliteProvider : ISqlProvider
{
    public string Type => "sqlite";

    public DbConnection OpenConnection(string dsn) => new SqliteConnection(dsn);

    public string FormatUpsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, OnConflictMode mode)
    {
        var cols = string.Join(", ", columns);
        var values = string.Join(", ", columns.Select(c => "@" + c));
        var insert = $"INSERT INTO {table} ({cols}) VALUES ({values})";

        return mode switch
        {
            OnConflictMode.Error => insert,
            OnConflictMode.Ignore => $"{insert} ON CONFLICT({string.Join(", ", keyColumns)}) DO NOTHING",
            OnConflictMode.Update or OnConflictMode.UpdateIfChanged =>
                $"{insert} ON CONFLICT({string.Join(", ", keyColumns)}) DO UPDATE SET " +
                string.Join(", ", columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal))
                    .Select(c => $"{c} = excluded.{c}"))
                + (mode == OnConflictMode.UpdateIfChanged ? BuildSqliteIfChangedClause(table, columns, keyColumns) : ""),
            _ => throw new BetlException($"Unsupported OnConflict mode '{mode}'."),
        };
    }

    private static string BuildSqliteIfChangedClause(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        var nonKey = columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal)).ToList();
        if (nonKey.Count == 0) return "";
        var sb = new StringBuilder(" WHERE ");
        sb.Append(string.Join(" OR ", nonKey.Select(c => $"COALESCE({table}.{c}, '') != COALESCE(excluded.{c}, '')")));
        return sb.ToString();
    }
}
