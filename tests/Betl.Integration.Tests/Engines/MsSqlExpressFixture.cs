using Microsoft.Data.SqlClient;

namespace Betl.Integration.Tests.Engines;

/// <summary>
/// Connects to a locally-installed SQL Server (default: .\SQLEXPRESS via
/// Integrated Security) and provisions a fresh per-class database whose name
/// embeds a GUID. The database is dropped on disposal.
///
/// Override the master DSN via env var BETL_MSSQL_DSN if the developer's
/// MSSQL instance isn't .\SQLEXPRESS.
/// </summary>
public sealed class MsSqlExpressFixture : IDisposable
{
    private const string DefaultMasterDsn =
        @"Server=.\SQLEXPRESS;Database=master;Integrated Security=true;TrustServerCertificate=true;Encrypt=false;";

    public bool Available { get; }
    public string MasterDsn { get; }
    public string TestDsn { get; }
    public string TestDb { get; }

    public MsSqlExpressFixture()
    {
        MasterDsn = Environment.GetEnvironmentVariable("BETL_MSSQL_DSN") ?? DefaultMasterDsn;
        TestDb = "betl_it_" + Guid.NewGuid().ToString("N");

        try
        {
            using var conn = new SqlConnection(MasterDsn);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{TestDb}]";
            cmd.ExecuteNonQuery();
            Available = true;

            var b = new SqlConnectionStringBuilder(MasterDsn) { InitialCatalog = TestDb };
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
            using var conn = new SqlConnection(MasterDsn);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                $"ALTER DATABASE [{TestDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{TestDb}];";
            cmd.ExecuteNonQuery();
        }
        catch
        {
        }
    }
}
