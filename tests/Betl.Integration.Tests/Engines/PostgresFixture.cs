using Npgsql;

namespace Betl.Integration.Tests.Engines;

/// <summary>
/// Connects to a locally-installed PostgreSQL (default: localhost:5433 / user
/// postgres / pw postgres — matches the dev install in this repo) and
/// provisions a fresh per-class database. The database is dropped on disposal.
///
/// Override with BETL_PG_DSN if the developer's Postgres is elsewhere.
/// </summary>
public sealed class PostgresFixture : IDisposable
{
    private const string DefaultMaintenanceDsn =
        "Host=localhost;Port=5433;Username=postgres;Password=postgres;Database=postgres;Pooling=false";

    public bool Available { get; }
    public string MaintenanceDsn { get; }
    public string TestDsn { get; }
    public string TestDb { get; }

    public PostgresFixture()
    {
        MaintenanceDsn = Environment.GetEnvironmentVariable("BETL_PG_DSN") ?? DefaultMaintenanceDsn;
        TestDb = "betl_it_" + Guid.NewGuid().ToString("N");

        try
        {
            using var conn = new NpgsqlConnection(MaintenanceDsn);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{TestDb}\"";
            cmd.ExecuteNonQuery();
            Available = true;

            var b = new NpgsqlConnectionStringBuilder(MaintenanceDsn) { Database = TestDb };
            TestDsn = b.ConnectionString;
        }
        catch (Exception)
        {
            Available = false;
            TestDsn = "";
        }
    }

    public void Dispose()
    {
        if (!Available) return;
        try
        {
            using var conn = new NpgsqlConnection(MaintenanceDsn);
            conn.Open();
            using (var kill = conn.CreateCommand())
            {
                kill.CommandText =
                    "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
                    "WHERE datname = @db AND pid <> pg_backend_pid()";
                var p = kill.CreateParameter();
                p.ParameterName = "@db";
                p.Value = TestDb;
                kill.Parameters.Add(p);
                kill.ExecuteNonQuery();
            }
            using var drop = conn.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{TestDb}\"";
            drop.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
