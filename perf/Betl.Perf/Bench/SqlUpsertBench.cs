using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace Betl.Perf.Bench;

/// <summary>
/// Drives the full betl dataflow N rows wide, sinking via {sqlite,postgres,mssql}.upsert.
/// Per-engine [GlobalSetup] provisions a temp database; [GlobalCleanup] drops it.
/// This measures the realistic round-trip cost of each dialect's UPSERT/MERGE SQL,
/// not just the SQL-generation path.
/// </summary>
[MemoryDiagnoser]
public class SqlUpsertBench
{
    [Params(1_000)]
    public int Rows;

    [Params("sqlite", "postgres", "mssql")]
    public string Dialect = "sqlite";

    private string _dsn = "";
    private string _tempDir = "";
    private string _provisionDsn = "";   // server-level dsn for cleanup (postgres/mssql)
    private string _tempDb = "";         // db name for cleanup

    [GlobalSetup]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"perf-sql-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tempDb = "betl_perf_" + Guid.NewGuid().ToString("N");

        switch (Dialect)
        {
            case "sqlite":
                _dsn = $"Data Source={Path.Combine(_tempDir, "perf.db")};Pooling=False";
                break;

            case "postgres":
                _provisionDsn = Environment.GetEnvironmentVariable("BETL_PG_DSN")
                    ?? "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres;Pooling=false";
                using (var c = new NpgsqlConnection(_provisionDsn))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $"CREATE DATABASE \"{_tempDb}\"";
                    cmd.ExecuteNonQuery();
                }
                var pgb = new NpgsqlConnectionStringBuilder(_provisionDsn) { Database = _tempDb };
                _dsn = pgb.ConnectionString;
                break;

            case "mssql":
                _provisionDsn = Environment.GetEnvironmentVariable("BETL_MSSQL_DSN")
                    ?? @"Server=.\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;Encrypt=false;";
                using (var c = new SqlConnection(_provisionDsn))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = $"CREATE DATABASE [{_tempDb}]";
                    cmd.ExecuteNonQuery();
                }
                var msb = new SqlConnectionStringBuilder(_provisionDsn) { InitialCatalog = _tempDb };
                _dsn = msb.ConnectionString;
                break;

            default:
                throw new InvalidOperationException(Dialect);
        }

        PerfHarness.RunInline(DdlYaml(), new() { ["dsn"] = _dsn });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        switch (Dialect)
        {
            case "postgres":
                try
                {
                    using var c = new NpgsqlConnection(_provisionDsn);
                    c.Open();
                    using (var k = c.CreateCommand())
                    {
                        k.CommandText =
                            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                            "WHERE datname = @d AND pid <> pg_backend_pid()";
                        var p = k.CreateParameter();
                        p.ParameterName = "@d"; p.Value = _tempDb;
                        k.Parameters.Add(p);
                        k.ExecuteNonQuery();
                    }
                    using var d = c.CreateCommand();
                    d.CommandText = $"DROP DATABASE IF EXISTS \"{_tempDb}\"";
                    d.ExecuteNonQuery();
                }
                catch { }
                break;

            case "mssql":
                try
                {
                    using var c = new SqlConnection(_provisionDsn);
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText =
                        $"ALTER DATABASE [{_tempDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                        $"DROP DATABASE [{_tempDb}];";
                    cmd.ExecuteNonQuery();
                }
                catch { }
                break;
        }

        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    [Benchmark]
    public void Upsert_N_rows()
    {
        PerfHarness.RunInline(UpsertYaml(), new()
        {
            ["dsn"] = _dsn,
        });
    }

    private string DdlYaml() => Dialect switch
    {
        "sqlite" => $$"""
            betl: 1
            name: perf-sql-ddl
            parameters: { dsn: { type: string, required: true } }
            connections: { w: { type: sqlite, dsn: "${params.dsn}" } }
            pipeline:
              - id: ddl
                type: sql.execute
                connection: w
                sql: |
                  DROP TABLE IF EXISTS orders;
                  CREATE TABLE orders (order_id INTEGER PRIMARY KEY, customer_id INTEGER, amount INTEGER);
            """,

        "postgres" => $$"""
            betl: 1
            name: perf-sql-ddl
            parameters: { dsn: { type: string, required: true } }
            connections: { w: { type: postgres, dsn: "${params.dsn}" } }
            pipeline:
              - id: ddl
                type: sql.execute
                connection: w
                sql: |
                  DROP TABLE IF EXISTS orders;
                  CREATE TABLE orders (order_id INTEGER PRIMARY KEY, customer_id INTEGER, amount INTEGER);
            """,

        "mssql" => $$"""
            betl: 1
            name: perf-sql-ddl
            parameters: { dsn: { type: string, required: true } }
            connections: { w: { type: mssql, dsn: "${params.dsn}" } }
            pipeline:
              - id: ddl
                type: sql.execute
                connection: w
                sql: |
                  IF OBJECT_ID('orders', 'U') IS NOT NULL DROP TABLE orders;
                  CREATE TABLE orders (order_id INT PRIMARY KEY, customer_id INT, amount INT);
            """,

        _ => throw new InvalidOperationException(Dialect),
    };

    private string UpsertYaml() => $$"""
        betl: 1
        name: perf-sql-upsert
        parameters:
          dsn: { type: string, required: true }
        connections: { w: { type: {{Dialect}}, dsn: "${params.dsn}" } }
        pipeline:
          - id: flow
            type: dataflow
            steps:
              - id: gen
                type: betl.gen_int64
                n: {{Rows}}
                column: order_id
                start: 1
              - id: enrich
                type: map
                from: gen
                add:
                  customer_id: { lang: ssisexpr, expr: '((order_id - 1) % 100) + 1' }
                  amount:      { lang: ssisexpr, expr: 'order_id * 3' }
              - id: ups
                type: {{Dialect}}.upsert
                from: enrich
                connection: w
                table: orders
                key: [order_id]
                on_conflict: update
        """;
}
