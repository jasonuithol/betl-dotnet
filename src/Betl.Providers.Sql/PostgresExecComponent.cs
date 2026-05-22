using System.Data;
using System.Text.RegularExpressions;
using Betl.Components;
using Betl.Core;
using Npgsql;

namespace Betl.Providers.Sql;

/// <summary>
/// Per-row SQL execution against a Postgres connection. The configured
/// statement is prepared once and executed once per input row, with the
/// listed input columns bound to <c>$1, $2, …</c> in declared order. The
/// input row passes through unchanged (output schema = input schema).
///
/// Internally rewrites <c>$N</c> to Npgsql's <c>@pN</c> named-parameter form
/// for portability across Npgsql versions.
/// </summary>
public sealed partial class PostgresExecComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly string _dsn;
    private readonly string _sql;
    private readonly IReadOnlyList<string> _paramColumns;

    public string Id { get; }
    public Schema OutputSchema => _upstream.OutputSchema;

    public PostgresExecComponent(PostgresExecStep step, IDataComponent upstream, string dsn)
    {
        Id = step.Id;
        _upstream = upstream;
        _dsn = dsn;
        _sql = RewritePositionalPlaceholders(step.Sql);
        _paramColumns = step.Parameters;
    }

    public IEnumerable<Row> Stream()
    {
        var srcIndices = _paramColumns.Select(c =>
        {
            var i = _upstream.OutputSchema.IndexOf(c);
            if (i < 0) throw new BetlException(
                $"postgres.exec '{Id}': parameter column '{c}' is not in the upstream schema.");
            return i;
        }).ToArray();

        using var conn = new NpgsqlConnection(_dsn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _sql;

        var paramObjs = new NpgsqlParameter[_paramColumns.Count];
        for (var i = 0; i < paramObjs.Length; i++)
        {
            paramObjs[i] = new NpgsqlParameter { ParameterName = $"p{i + 1}" };
            cmd.Parameters.Add(paramObjs[i]);
        }

        foreach (var row in _upstream.Stream())
        {
            for (var i = 0; i < paramObjs.Length; i++)
                paramObjs[i].Value = row.Values[srcIndices[i]] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
            yield return row;
        }
    }

    private static string RewritePositionalPlaceholders(string sql) =>
        PositionalRegex().Replace(sql, "@p$1");

    [GeneratedRegex(@"\$(\d+)")]
    private static partial Regex PositionalRegex();
}
