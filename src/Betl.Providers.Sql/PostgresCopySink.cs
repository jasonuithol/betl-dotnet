using System.Globalization;
using Betl.Components;
using Betl.Core;
using Npgsql;

namespace Betl.Providers.Sql;

/// <summary>
/// Postgres append-only fast path via the COPY ... FROM STDIN BINARY protocol.
/// Significantly faster than per-row INSERT/UPSERT; intended for staging
/// loads where the destination is either empty or will be appended to.
/// </summary>
public sealed class PostgresCopySink : ISink
{
    private readonly string _dsn;
    private readonly string _table;
    private readonly bool _truncate;
    private readonly IReadOnlyList<string>? _columnsOverride;

    public string Id { get; }

    public PostgresCopySink(PostgresCopyStep step, string dsn)
    {
        Id = step.Id;
        _dsn = dsn;
        _table = step.Table;
        _truncate = step.Truncate;
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
                $"postgres.copy '{Id}': declared column '{c}' is not in the upstream schema.");
            return i;
        }).ToArray();

        using var conn = new NpgsqlConnection(_dsn);
        conn.Open();

        if (_truncate)
        {
            using var trunc = conn.CreateCommand();
            trunc.CommandText = $"TRUNCATE TABLE {_table}";
            trunc.ExecuteNonQuery();
        }

        var colList = string.Join(", ", cols.Select(QuoteIdent));
        using var writer = conn.BeginTextImport(
            $"COPY {_table} ({colList}) FROM STDIN");

        foreach (var row in input.Stream())
        {
            for (var i = 0; i < cols.Count; i++)
            {
                if (i > 0) writer.Write('\t');
                writer.Write(FormatCopyText(row.Values[srcIndices[i]]));
            }
            writer.Write('\n');
        }
    }

    /// <summary>
    /// PostgreSQL COPY TEXT format escaping: backslash for tab/newline/CR/backslash,
    /// and the sentinel <c>\N</c> for NULL. See PG docs "COPY".
    /// </summary>
    private static string FormatCopyText(object? value) => value switch
    {
        null => "\\N",
        DBNull => "\\N",
        bool b => b ? "t" : "f",
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        string s => EscapeCopyText(s),
        _ => EscapeCopyText(value.ToString() ?? ""),
    };

    private static string EscapeCopyText(string s)
    {
        if (s.IndexOfAny(['\\', '\t', '\n', '\r']) < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length + 4);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static string QuoteIdent(string name) =>
        name.IndexOfAny(['"', '.']) < 0 ? name : "\"" + name.Replace("\"", "\"\"") + "\"";
}
