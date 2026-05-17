using System.Data;
using System.Globalization;
using Betl.Components;
using Betl.Core;

namespace Betl.Providers.Sql;

public sealed class SqlExecuteTask : IControlTask
{
    private readonly ISqlProvider _provider;
    private readonly string _dsn;
    private readonly string _sql;
    private readonly IReadOnlyDictionary<string, object?> _params;
    private readonly IReadOnlyList<KeyValuePair<string, SqlExpect>>? _expect;
    private readonly string _expectRow;

    public string Id { get; }

    public SqlExecuteTask(SqlExecuteStep step, ISqlProvider provider, string dsn, string sql)
    {
        Id = step.Id;
        _provider = provider;
        _dsn = dsn;
        _sql = sql;
        _params = step.Params;
        _expect = step.Expect;
        _expectRow = step.ExpectRow;
    }

    public void Execute(Action<string>? log)
    {
        using var conn = _provider.OpenConnection(_dsn);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = _sql;
        foreach (var (k, v) in _params)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = SqlReadComponent.NormaliseParamName(k);
            p.Value = v ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }

        if (_expect is null)
        {
            var n = cmd.ExecuteNonQuery();
            log?.Invoke($"   {Id}: executed (affected: {n})");
            return;
        }

        using var reader = cmd.ExecuteReader();
        var rowsSeen = 0;
        while (reader.Read())
        {
            rowsSeen++;
            EvaluateExpectations(reader);
            if (_expectRow == "first") break;
        }
        if (rowsSeen == 0)
            throw new BetlException($"sql.execute '{Id}': expectations declared but query returned no rows.");
        log?.Invoke($"   {Id}: executed + expectations passed over {rowsSeen} row(s)");
    }

    private void EvaluateExpectations(IDataReader reader)
    {
        var byName = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            byName[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        foreach (var (col, e) in _expect!)
        {
            if (!byName.TryGetValue(col, out var actual))
                throw new BetlException($"sql.execute '{Id}': expect references unknown column '{col}'.");

            if (e.Equal is not null && !object.Equals(NumericNormalise(actual), NumericNormalise(e.Equal)))
                Fail(col, $"expected {e.Equal}", actual);

            if (e.NotNull == true && actual is null) Fail(col, "expected NOT NULL", actual);

            if (e.Min is { } min)
            {
                if (actual is null || ToDouble(actual) < min) Fail(col, $"expected >= {min}", actual);
            }
            if (e.Max is { } max)
            {
                if (actual is null || ToDouble(actual) > max) Fail(col, $"expected <= {max}", actual);
            }
            if (e.Between is { } b)
            {
                if (actual is null || ToDouble(actual) < b.Lo || ToDouble(actual) > b.Hi)
                    Fail(col, $"expected between {b.Lo} and {b.Hi}", actual);
            }
            if (e.OneOf is not null && !e.OneOf.Any(v => object.Equals(NumericNormalise(actual), NumericNormalise(v))))
                Fail(col, $"expected one of [{string.Join(", ", e.OneOf)}]", actual);
        }
    }

    private void Fail(string col, string what, object? got) =>
        throw new BetlException($"sql.execute '{Id}': {col} {what}, got {got ?? "null"}.");

    private static double ToDouble(object v) =>
        Convert.ToDouble(v, CultureInfo.InvariantCulture);

    /// <summary>Normalize 1 == 1L == 1.0 when comparing scalar expectations.</summary>
    private static object? NumericNormalise(object? v) => v switch
    {
        null => null,
        long or int or short or sbyte or ulong or uint or ushort or byte
            => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        double or float or decimal
            => Convert.ToDouble(v, CultureInfo.InvariantCulture),
        _ => v,
    };
}
