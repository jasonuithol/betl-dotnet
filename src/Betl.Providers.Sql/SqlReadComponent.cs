using System.Data;
using Betl.Components;
using Betl.Core;

namespace Betl.Providers.Sql;

/// <summary>
/// Generic SQL source: opens a connection, runs the configured SQL with optional
/// named parameters, and materialises the result set. Output schema is the pinned
/// schema if supplied, otherwise inferred from the reader's column types.
/// </summary>
public sealed class SqlReadComponent : IDataComponent
{
    private readonly List<Row> _rows;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public SqlReadComponent(SqlReadStep step, ISqlProvider provider, string dsn, string sql)
    {
        Id = step.Id;

        using var conn = provider.OpenConnection(dsn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in step.Params)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = NormaliseParamName(k);
            p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        using var reader = cmd.ExecuteReader();

        Schema schema;
        if (step.PinnedSchema is not null)
        {
            schema = step.PinnedSchema;
        }
        else
        {
            var cols = new List<Column>(reader.FieldCount);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                cols.Add(new Column
                {
                    Name = reader.GetName(i),
                    ArrowType = AdoTypeMap.Map(reader.GetFieldType(i)),
                    Nullable = true,
                });
            }
            schema = new Schema { Columns = cols };
        }
        OutputSchema = schema;

        _rows = new List<Row>();
        while (reader.Read())
        {
            var values = new object?[schema.Columns.Count];
            for (var i = 0; i < schema.Columns.Count; i++)
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            _rows.Add(new Row(schema, values));
        }
    }

    public IEnumerable<Row> Stream() => _rows;

    /// <summary>YAML <c>params: { foo: 1 }</c> binds to either ":foo" or "@foo" in user SQL — normalize to leading @.</summary>
    internal static string NormaliseParamName(string raw) => raw.StartsWith('@') ? raw : "@" + raw.TrimStart(':');
}
