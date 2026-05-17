namespace Betl.Core;

public sealed class Row
{
    public Schema Schema { get; }
    public object?[] Values { get; }

    public Row(Schema schema, object?[] values)
    {
        if (values.Length != schema.Columns.Count)
            throw new ArgumentException(
                $"Row has {values.Length} values but schema has {schema.Columns.Count} columns.");
        Schema = schema;
        Values = values;
    }

    public object? this[int i] => Values[i];

    public object? this[string column]
    {
        get
        {
            var i = Schema.IndexOf(column);
            if (i < 0) throw new KeyNotFoundException($"Column '{column}' not in schema.");
            return Values[i];
        }
    }
}

public interface ICompiledExpression
{
    object? Evaluate(Row row);
}

public interface IExpressionEngine
{
    string LanguageId { get; }
    ICompiledExpression Compile(string source, Schema inputSchema);
}

public sealed class LiteralCompiledExpression(object? value) : ICompiledExpression
{
    public object? Evaluate(Row row) => value;
}
