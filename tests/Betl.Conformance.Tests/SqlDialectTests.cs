using Betl.Core;
using Betl.Providers.Sql;

namespace Betl.Conformance.Tests;

/// <summary>
/// Unit-level checks of the dialect-specific UPSERT SQL builders. These don't
/// hit a live database — that's deferred for Postgres/MSSQL until a real
/// instance is available (see project memory).
/// </summary>
public sealed class SqlDialectTests
{
    private static readonly string[] Cols = ["id", "name", "amount"];
    private static readonly string[] Keys = ["id"];

    [Fact]
    public void Sqlite_update_uses_on_conflict_do_update_with_excluded()
    {
        var sql = new SqliteProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Update);
        Assert.Contains("INSERT INTO t (id, name, amount) VALUES (@id, @name, @amount)", sql);
        Assert.Contains("ON CONFLICT(id) DO UPDATE SET name = excluded.name, amount = excluded.amount", sql);
    }

    [Fact]
    public void Sqlite_ignore_uses_on_conflict_do_nothing()
    {
        var sql = new SqliteProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Ignore);
        Assert.EndsWith("ON CONFLICT(id) DO NOTHING", sql);
    }

    [Fact]
    public void Sqlite_error_omits_on_conflict_clause()
    {
        var sql = new SqliteProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Error);
        Assert.DoesNotContain("ON CONFLICT", sql);
    }

    [Fact]
    public void Postgres_update_uses_uppercase_EXCLUDED_and_parens()
    {
        var sql = new PostgresProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Update);
        Assert.Contains("ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, amount = EXCLUDED.amount", sql);
    }

    [Fact]
    public void Postgres_update_if_changed_uses_is_distinct_from()
    {
        var sql = new PostgresProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.UpdateIfChanged);
        Assert.Contains("IS DISTINCT FROM", sql);
    }

    [Fact]
    public void MsSql_uses_MERGE_with_HOLDLOCK_and_src_alias()
    {
        var sql = new MsSqlProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Update);
        Assert.Contains("MERGE INTO t WITH (HOLDLOCK) AS tgt USING (VALUES (@id, @name, @amount)) AS src(id, name, amount)", sql);
        Assert.Contains("WHEN MATCHED THEN UPDATE SET name = src.name, amount = src.amount", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT (id, name, amount) VALUES (src.id, src.name, src.amount);", sql);
    }

    [Fact]
    public void MsSql_ignore_skips_when_matched_clause()
    {
        var sql = new MsSqlProvider().FormatUpsert("t", Cols, Keys, OnConflictMode.Ignore);
        Assert.DoesNotContain("WHEN MATCHED THEN UPDATE", sql);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", sql);
    }

    [Theory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public void ConnectionRegistry_resolves_all_three_builtin_providers(string type)
    {
        var reg = new ConnectionRegistry()
            .Register(new SqliteProvider())
            .Register(new PostgresProvider())
            .Register(new MsSqlProvider());
        Assert.True(reg.Has(type));
        var p = reg.Get(type);
        Assert.Equal(type, p.Type);
    }

    [Fact]
    public void ConnectionRegistry_unknown_type_throws_with_helpful_message()
    {
        var reg = new ConnectionRegistry().Register(new SqliteProvider());
        var ex = Assert.Throws<BetlException>(() => reg.Get("oracle"));
        Assert.Contains("oracle", ex.Message);
        Assert.Contains("sqlite", ex.Message);
    }
}
