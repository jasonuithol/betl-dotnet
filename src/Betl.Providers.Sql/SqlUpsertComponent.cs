using System.Data;
using Betl.Components;
using Betl.Core;

namespace Betl.Providers.Sql;

/// <summary>
/// Generic SQL upsert sink. Builds dialect-specific UPSERT/MERGE via the
/// configured provider, runs in a single transaction, and binds row values
/// via reused parameter objects.
/// </summary>
public sealed class SqlUpsertComponent : ISink
{
    private readonly ISqlProvider _provider;
    private readonly string _dsn;
    private readonly string _table;
    private readonly IReadOnlyList<string>? _columnsOverride;
    private readonly IReadOnlyList<string> _key;
    private readonly OnConflictMode _mode;

    public string Id { get; }

    public SqlUpsertComponent(SqlUpsertStep step, ISqlProvider provider, string dsn)
    {
        Id = step.Id;
        _provider = provider;
        _dsn = dsn;
        _table = step.Table;
        _columnsOverride = step.Columns;
        _key = step.Key;
        _mode = step.OnConflict;
    }

    public void Drain(IDataComponent input)
    {
        var cols = _columnsOverride
            ?? input.OutputSchema.Columns.Select(c => c.Name).ToList();

        var sql = _provider.FormatUpsert(_table, cols, _key, _mode);

        using var conn = _provider.OpenConnection(_dsn);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;

        var paramObjs = cols.Select(c =>
        {
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + c;
            cmd.Parameters.Add(p);
            return p;
        }).ToArray();

        var srcIndices = cols.Select(c =>
        {
            var i = input.OutputSchema.IndexOf(c);
            if (i < 0) throw new BetlException(
                $"upsert '{Id}': declared column '{c}' is not in the upstream schema.");
            return i;
        }).ToArray();

        foreach (var row in input.Stream())
        {
            for (var i = 0; i < cols.Count; i++)
                paramObjs[i].Value = row.Values[srcIndices[i]] ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
