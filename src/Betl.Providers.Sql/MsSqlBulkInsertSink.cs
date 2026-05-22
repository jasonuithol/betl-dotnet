using System.Data;
using Betl.Components;
using Betl.Core;
using Microsoft.Data.SqlClient;

namespace Betl.Providers.Sql;

/// <summary>
/// MS SQL bulk insert via <see cref="SqlBulkCopy"/>. The destination table
/// schema is queried up-front to pick a column order; rows stream through a
/// <see cref="DataTable"/> batched at the configured <c>BatchSize</c>.
/// </summary>
public sealed class MsSqlBulkInsertSink : ISink
{
    private readonly string _dsn;
    private readonly string _table;
    private readonly bool _truncate;
    private readonly int _batchSize;
    private readonly IReadOnlyList<string>? _columnsOverride;

    public string Id { get; }

    public MsSqlBulkInsertSink(MsSqlBulkInsertStep step, string dsn)
    {
        Id = step.Id;
        _dsn = dsn;
        _table = step.Table;
        _truncate = step.Truncate;
        _batchSize = step.BatchSize;
        _columnsOverride = step.Columns;
    }

    public void Drain(IDataComponent input)
    {
        var cols = _columnsOverride
            ?? input.OutputSchema.Columns.Select(c => c.Name).ToList();

        var srcIndices = cols.Select(c =>
        {
            var i = input.OutputSchema.IndexOf(c);
            if (i < 0) throw new BetlException(
                $"mssql.bulkinsert '{Id}': declared column '{c}' is not in the upstream schema.");
            return i;
        }).ToArray();

        using var conn = new SqlConnection(_dsn);
        conn.Open();

        if (_truncate)
        {
            using var trunc = conn.CreateCommand();
            trunc.CommandText = $"TRUNCATE TABLE {_table}";
            trunc.ExecuteNonQuery();
        }

        using var bulk = new SqlBulkCopy(conn)
        {
            DestinationTableName = _table,
            BatchSize = _batchSize,
        };
        for (var i = 0; i < cols.Count; i++)
            bulk.ColumnMappings.Add(i, cols[i]);

        // Stage rows through a DataTable so SqlBulkCopy can pull them in
        // batched chunks without us building an IDataReader by hand.
        var table = new DataTable();
        foreach (var name in cols)
            table.Columns.Add(name, typeof(object));

        var rowCount = 0;
        foreach (var row in input.Stream())
        {
            var dataRow = table.NewRow();
            for (var i = 0; i < cols.Count; i++)
                dataRow[i] = row.Values[srcIndices[i]] ?? DBNull.Value;
            table.Rows.Add(dataRow);
            rowCount++;

            if (rowCount % _batchSize == 0)
            {
                bulk.WriteToServer(table);
                table.Clear();
            }
        }
        if (table.Rows.Count > 0) bulk.WriteToServer(table);
    }
}
