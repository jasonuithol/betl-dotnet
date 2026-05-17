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
