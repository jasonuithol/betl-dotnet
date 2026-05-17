using System.Data.Common;
using Betl.Core;
using Npgsql;

namespace Betl.Providers.Sql;

/// <summary>
/// Postgres skeleton: connection open + upsert dialect implemented; unit-tested
/// only at the SQL-generation level. Integration testing against a live Postgres
/// is deferred (the runtime here can't provision one).
/// </summary>
public sealed class PostgresProvider : ISqlProvider
{
    public string Type => "postgres";

    public DbConnection OpenConnection(string dsn) => new NpgsqlConnection(dsn);

    public string FormatUpsert(string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, OnConflictMode mode)
    {
        var cols = string.Join(", ", columns);
        var values = string.Join(", ", columns.Select(c => "@" + c));
        var insert = $"INSERT INTO {table} ({cols}) VALUES ({values})";

        return mode switch
        {
            OnConflictMode.Error => insert,
            OnConflictMode.Ignore => $"{insert} ON CONFLICT ({string.Join(", ", keyColumns)}) DO NOTHING",
            OnConflictMode.Update =>
                $"{insert} ON CONFLICT ({string.Join(", ", keyColumns)}) DO UPDATE SET " +
                string.Join(", ", columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal))
                    .Select(c => $"{c} = EXCLUDED.{c}")),
            OnConflictMode.UpdateIfChanged =>
                $"{insert} ON CONFLICT ({string.Join(", ", keyColumns)}) DO UPDATE SET " +
                string.Join(", ", columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal))
                    .Select(c => $"{c} = EXCLUDED.{c}"))
                + " WHERE "
                + string.Join(" OR ", columns.Where(c => !keyColumns.Contains(c, StringComparer.Ordinal))
                    .Select(c => $"{table}.{c} IS DISTINCT FROM EXCLUDED.{c}")),
            _ => throw new BetlException($"Unsupported OnConflict mode '{mode}'."),
        };
    }
}
