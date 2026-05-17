using Apache.Arrow.Types;

namespace Betl.Core;

public sealed record Pipeline
{
    public required int BetlVersion { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Includes { get; init; } = [];
    public IReadOnlyDictionary<string, Parameter> Parameters { get; init; } = new Dictionary<string, Parameter>();
    public IReadOnlyDictionary<string, Connection> Connections { get; init; } = new Dictionary<string, Connection>();
    public required IReadOnlyList<Step> Steps { get; init; }
}

public sealed record Parameter
{
    public required string TypeSpelling { get; init; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public string? Doc { get; init; }
    public IReadOnlyList<object>? Enum { get; init; }
}

public sealed record Connection
{
    public required string Type { get; init; }
    public required string Dsn { get; init; }
    public IReadOnlyDictionary<string, object?> ExtraOptions { get; init; } = new Dictionary<string, object?>();
}

public enum OnFailureMode { Stop, Continue, Retry }

public abstract record Step
{
    public required string Id { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> After { get; init; } = [];
    public OnFailureMode OnFailure { get; init; } = OnFailureMode.Stop;
    public int Retries { get; init; }
    public string? RetryBackoff { get; init; }
    public string? Timeout { get; init; }
    public Expression? Condition { get; init; }
}

public sealed record DataflowStep : Step
{
    public required IReadOnlyList<Step> Steps { get; init; }
}

public sealed record CsvReadStep : Step
{
    public required string Path { get; init; }
    public string Delimiter { get; init; } = ",";
    public bool Header { get; init; } = true;
    public string Encoding { get; init; } = "utf-8";
    public required Schema Schema { get; init; }
}

public sealed record CsvWriteStep : Step
{
    public required string From { get; init; }
    public required string Path { get; init; }
    public string Delimiter { get; init; } = ",";
    public bool Header { get; init; } = true;
}

public enum JsonFormat { Ndjson, Array }

public sealed record JsonReadStep : Step
{
    public required string Path { get; init; }
    public JsonFormat Format { get; init; } = JsonFormat.Ndjson;
    public required IReadOnlyList<string> Columns { get; init; }
}

public sealed record JsonWriteStep : Step
{
    public required string From { get; init; }
    public required string Path { get; init; }
    public JsonFormat Format { get; init; } = JsonFormat.Ndjson;
}

public sealed record FilterStep : Step
{
    public required string From { get; init; }
    public required Expression Where { get; init; }
}

public sealed record MapStep : Step
{
    public required string From { get; init; }
    public IReadOnlyDictionary<string, Expression>? Add { get; init; }
    public IReadOnlyList<SelectColumn>? Select { get; init; }
}

public sealed record DistinctStep : Step
{
    public required string From { get; init; }
    /// <summary>Optional column subset to dedupe on. Null/empty = full-row dedupe.</summary>
    public IReadOnlyList<string>? Keys { get; init; }
}

public sealed record LimitStep : Step
{
    public required string From { get; init; }
    public required long N { get; init; }
}

public sealed record UnionStep : Step
{
    /// <summary>List of upstream step ids; schemas must match.</summary>
    public required IReadOnlyList<string> From { get; init; }
}

public enum SortDirection { Asc, Desc }

public sealed record SortKey(string Column, SortDirection Direction);

public sealed record SortStep : Step
{
    public required string From { get; init; }
    public required IReadOnlyList<SortKey> By { get; init; }
}

public enum AggregateOp { Sum, Count, CountDistinct, Min, Max, Avg, First, Last }

public sealed record AggregateCompute(AggregateOp Op, string? Over);

public sealed record AggregateStep : Step
{
    public required string From { get; init; }
    public required IReadOnlyList<string> GroupBy { get; init; }
    /// <summary>Output column name -> aggregation spec. Order preserved.</summary>
    public required IReadOnlyList<KeyValuePair<string, AggregateCompute>> Compute { get; init; }
}

public sealed record ConditionalSplitStep : Step
{
    public required string From { get; init; }
    /// <summary>Output port name -> predicate. Evaluated in declared order; first match wins.</summary>
    public required IReadOnlyList<KeyValuePair<string, Expression>> Cases { get; init; }
    /// <summary>Optional port name that receives non-matching rows. Null means non-matching rows are dropped.</summary>
    public string? DefaultCase { get; init; }
}

public sealed record MulticastStep : Step
{
    public required string From { get; init; }
    /// <summary>Names of the output ports; each receives a copy of every upstream row.</summary>
    public required IReadOnlyList<string> Outputs { get; init; }
}

public enum JoinKind { Inner, Left, Right, Full }

public sealed record JoinStep : Step
{
    public required string Left { get; init; }
    public required string Right { get; init; }
    /// <summary>Equi-join keys: left column -> right column.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> On { get; init; }
    public JoinKind Kind { get; init; } = JoinKind.Inner;
}

public sealed record ForeachStep : Step
{
    public required IReadOnlyList<string> Over { get; init; }
    public required string Variable { get; init; }
    public required IReadOnlyList<Step> Body { get; init; }
}

public abstract record SelectColumn(string Name);
public sealed record PassthroughColumn(string Name) : SelectColumn(Name);
public sealed record RenameColumn(string Name, string From) : SelectColumn(Name);
public sealed record ComputedColumn(string Name, Expression Expression) : SelectColumn(Name);
public sealed record LiteralColumn(string Name, object? Value) : SelectColumn(Name);

public sealed record Schema
{
    public required IReadOnlyList<Column> Columns { get; init; }

    public int IndexOf(string name)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public Column? Find(string name)
    {
        var i = IndexOf(name);
        return i < 0 ? null : Columns[i];
    }
}

public sealed record Column
{
    public required string Name { get; init; }
    public required IArrowType ArrowType { get; init; }
    public bool Nullable { get; init; } = true;
    public string? Doc { get; init; }
}

public abstract record Expression;
public sealed record LiteralExpression(object? Value) : Expression;
public sealed record LangExpression(string Lang, string Source) : Expression;
