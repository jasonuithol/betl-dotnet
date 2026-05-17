using Apache.Arrow.Types;
using Betl.Core;

namespace Betl.Components;

/// <summary>
/// Implements `map` with `add:` (additive) or `select:` (replacing). Computes the
/// output schema at construction time so downstream wiring sees a stable shape.
/// </summary>
public sealed class MapComponent : IDataComponent
{
    private readonly IDataComponent _upstream;
    private readonly IReadOnlyList<MapOp> _ops;

    public string Id { get; }
    public Schema OutputSchema { get; }

    public MapComponent(
        MapStep step,
        IDataComponent upstream,
        Func<Expression, ICompiledExpression> compile)
    {
        Id = step.Id;
        _upstream = upstream;
        var input = upstream.OutputSchema;

        var (ops, outSchema) = step switch
        {
            { Add: { } add, Select: null } => BuildAdd(add, input, compile),
            { Select: { } sel, Add: null } => BuildSelect(sel, input, compile),
            _ => throw new BetlException($"map '{step.Id}' requires exactly one of add or select."),
        };
        _ops = ops;
        OutputSchema = outSchema;
    }

    public IEnumerable<Row> Stream()
    {
        foreach (var inRow in _upstream.Stream())
        {
            var values = new object?[OutputSchema.Columns.Count];
            for (var i = 0; i < _ops.Count; i++)
                values[i] = _ops[i].Evaluate(inRow);
            yield return new Row(OutputSchema, values);
        }
    }

    // --- builders ----------------------------------------------------

    private abstract record MapOp
    {
        public abstract object? Evaluate(Row inRow);
    }

    private sealed record PassthroughOp(int InputIndex) : MapOp
    {
        public override object? Evaluate(Row inRow) => inRow.Values[InputIndex];
    }

    private sealed record CompiledOp(ICompiledExpression Expr) : MapOp
    {
        public override object? Evaluate(Row inRow) => Expr.Evaluate(inRow);
    }

    private static (IReadOnlyList<MapOp>, Schema) BuildAdd(
        IReadOnlyDictionary<string, Expression> add,
        Schema input,
        Func<Expression, ICompiledExpression> compile)
    {
        var ops = new List<MapOp>(input.Columns.Count + add.Count);
        var outCols = new List<Column>(input.Columns.Count + add.Count);

        for (var i = 0; i < input.Columns.Count; i++)
        {
            outCols.Add(input.Columns[i]);
            ops.Add(new PassthroughOp(i));
        }

        foreach (var (name, expr) in add)
        {
            outCols.Add(new Column { Name = name, ArrowType = InferType(expr), Nullable = true });
            ops.Add(new CompiledOp(compile(expr)));
        }

        return (ops, new Schema { Columns = outCols });
    }

    private static (IReadOnlyList<MapOp>, Schema) BuildSelect(
        IReadOnlyList<SelectColumn> select,
        Schema input,
        Func<Expression, ICompiledExpression> compile)
    {
        var ops = new List<MapOp>(select.Count);
        var outCols = new List<Column>(select.Count);

        foreach (var c in select)
        {
            switch (c)
            {
                case PassthroughColumn p:
                {
                    var idx = input.IndexOf(p.Name);
                    if (idx < 0) throw new BetlException($"map.select: unknown input column '{p.Name}'.");
                    outCols.Add(input.Columns[idx]);
                    ops.Add(new PassthroughOp(idx));
                    break;
                }
                case RenameColumn r:
                {
                    var idx = input.IndexOf(r.From);
                    if (idx < 0) throw new BetlException($"map.select: unknown input column '{r.From}'.");
                    outCols.Add(input.Columns[idx] with { Name = r.Name });
                    ops.Add(new PassthroughOp(idx));
                    break;
                }
                case LiteralColumn l:
                    outCols.Add(new Column { Name = l.Name, ArrowType = InferLiteralType(l.Value), Nullable = true });
                    ops.Add(new CompiledOp(new LiteralCompiledExpression(l.Value)));
                    break;
                case ComputedColumn cc:
                {
                    outCols.Add(new Column { Name = cc.Name, ArrowType = InferType(cc.Expression), Nullable = true });
                    ops.Add(new CompiledOp(compile(cc.Expression)));
                    break;
                }
                default:
                    throw new BetlException($"Unsupported select column kind {c.GetType().Name}.");
            }
        }

        return (ops, new Schema { Columns = outCols });
    }

    private static IArrowType InferType(Expression expr) => expr switch
    {
        LiteralExpression lit => InferLiteralType(lit.Value),
        // For ssisexpr/other engines we don't yet do type inference — default to string.
        LangExpression => StringType.Default,
        _ => StringType.Default,
    };

    private static IArrowType InferLiteralType(object? v) => v switch
    {
        null => StringType.Default,
        long => Int64Type.Default,
        int => Int32Type.Default,
        double => DoubleType.Default,
        float => FloatType.Default,
        bool => BooleanType.Default,
        string => StringType.Default,
        DateOnly => Date32Type.Default,
        _ => StringType.Default,
    };
}
