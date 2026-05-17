using Apache.Arrow.Types;
using Betl.Core;
using Betl.Expressions.SsisExpr;

namespace Betl.Expressions.SsisExpr.Tests;

public sealed class EvaluatorTests
{
    private static readonly Schema Schema = new()
    {
        Columns =
        [
            new Column { Name = "status", ArrowType = StringType.Default },
            new Column { Name = "amount", ArrowType = DoubleType.Default },
            new Column { Name = "qty",    ArrowType = Int64Type.Default },
            new Column { Name = "note",   ArrowType = StringType.Default, Nullable = true },
        ],
    };

    private static Row MakeRow(object? status, object? amount, object? qty, object? note) =>
        new(Schema, [status, amount, qty, note]);

    private static object? Eval(string expr, Row row)
    {
        var engine = new SsisExpressionEngine();
        return engine.Compile(expr, row.Schema).Evaluate(row);
    }

    [Theory]
    [InlineData("1 + 2",         3L)]
    [InlineData("10 / 4",        2L)]
    [InlineData("10.0 / 4",      2.5)]
    [InlineData("1 + 2 * 3",     7L)]
    [InlineData("(1 + 2) * 3",   9L)]
    [InlineData("-5 + 8",        3L)]
    [InlineData("!TRUE",         false)]
    [InlineData("\"a\" + \"b\"", "ab")]
    public void ArithmeticAndUnary(string expr, object expected)
    {
        Assert.Equal(expected, Eval(expr, MakeRow("paid", 100.0, 1L, null)));
    }

    [Theory]
    [InlineData("status == \"paid\"",            true)]
    [InlineData("status != \"paid\"",            false)]
    [InlineData("amount > 50",                   true)]
    [InlineData("amount > 1000",                 false)]
    [InlineData("amount >= 100",                 true)]
    [InlineData("qty < 10",                      true)]
    [InlineData("status == \"paid\" && amount > 0", true)]
    [InlineData("status == \"unpaid\" || amount > 0", true)]
    public void ComparisonsAndLogical(string expr, bool expected)
    {
        Assert.Equal(expected, Eval(expr, MakeRow("paid", 100.0, 1L, null)));
    }

    [Theory]
    [InlineData("ISNULL(note)",                  true)]
    [InlineData("ISNULL(status)",                false)]
    [InlineData("REPLACENULL(note, \"missing\")", "missing")]
    [InlineData("note == \"x\"",                 null)]   // NULL propagates
    [InlineData("FALSE && note == \"x\"",        false)]  // short-circuit AND
    [InlineData("TRUE || note == \"x\"",         true)]   // short-circuit OR
    public void NullHandling(string expr, object? expected)
    {
        Assert.Equal(expected, Eval(expr, MakeRow("paid", 100.0, 1L, null)));
    }

    [Theory]
    [InlineData("[status] == \"paid\"", true)]
    [InlineData("STATUS == \"paid\"",   true)] // case-insensitive column ref
    public void ColumnReferenceFormsAreCaseInsensitive(string expr, bool expected)
    {
        Assert.Equal(expected, Eval(expr, MakeRow("paid", 100.0, 1L, null)));
    }

    [Fact]
    public void NullEqualityReturnsNull()
    {
        Assert.Null(Eval("note == note", MakeRow("paid", 100.0, 1L, null)));
    }

    [Fact]
    public void UnknownColumnIsBetlException()
    {
        var ex = Assert.Throws<BetlException>(() => Eval("nosuch + 1", MakeRow("paid", 100.0, 1L, null)));
        Assert.Contains("nosuch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
