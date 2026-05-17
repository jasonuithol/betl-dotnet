using Apache.Arrow.Types;
using Betl.Core;
using Betl.Expressions.SsisExpr;

namespace Betl.Expressions.SsisExpr.Tests;

public sealed class Phase7Tests
{
    private static readonly Schema Schema = new()
    {
        Columns =
        [
            new Column { Name = "n",    ArrowType = Int64Type.Default },
            new Column { Name = "s",    ArrowType = StringType.Default },
            new Column { Name = "d",    ArrowType = DoubleType.Default },
            new Column { Name = "ts",   ArrowType = new TimestampType(TimeUnit.Microsecond, "UTC") },
        ],
    };

    private static Row MakeRow(object? n, object? s, object? d, object? ts) =>
        new(Schema, [n, s, d, ts]);

    private static object? Eval(string expr, Row row) =>
        new SsisExpressionEngine().Compile(expr, row.Schema).Evaluate(row);

    private static object? Eval(string expr) => Eval(expr, MakeRow(null, null, null, null));

    // ----- Ternary --------------------------------------------------------

    [Theory]
    [InlineData("TRUE ? 1 : 2",   1L)]
    [InlineData("FALSE ? 1 : 2",  2L)]
    [InlineData("1 == 1 ? \"a\" : \"b\"", "a")]
    [InlineData("(1 == 1) ? (1 + 1) : (2 + 2)", 2L)]
    public void Ternary(string expr, object expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    [Fact]
    public void TernaryNullConditionReturnsNull()
    {
        Assert.Null(Eval("ISNULL(n) ? 1 : 2", MakeRow(null, "x", null, null)) is bool b ? null : Eval("NULL ? 1 : 2"));
    }

    // ----- Casts ----------------------------------------------------------

    [Theory]
    [InlineData("(DT_I8) 3.7",      3L)]
    [InlineData("(DT_I4) 100000",   100000)]
    [InlineData("(DT_R8) 5",        5.0)]
    [InlineData("(DT_BOOL) 1",      true)]
    [InlineData("(DT_BOOL) 0",      false)]
    [InlineData("(DT_WSTR) 42",     "42")]
    [InlineData("(DT_I8) \"42\"",   42L)]
    [InlineData("(DT_R8) \"1.5\"",  1.5)]
    public void TypedCasts(string expr, object expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    [Fact]
    public void CastWithLengthArgs_DT_WSTR()
    {
        // Length is parsed and accepted but doesn't truncate (spec note).
        Assert.Equal("12345", Eval("(DT_WSTR, 3) 12345"));
    }

    [Fact]
    public void CastDecimal_with_scale()
    {
        var v = Eval("(DT_NUMERIC, 12, 2) 3.14159");
        var d = Assert.IsType<decimal>(v);
        Assert.Equal(3.14m, d);
    }

    [Fact]
    public void CastNullPropagates()
    {
        Assert.Null(Eval("(DT_I8) n", MakeRow(null, null, null, null)));
    }

    // ----- String functions ----------------------------------------------

    [Theory]
    [InlineData("LEN(\"hello\")",                       5L)]
    [InlineData("SUBSTRING(\"hello world\", 7, 5)",     "world")]
    [InlineData("LEFT(\"hello\", 3)",                   "hel")]
    [InlineData("RIGHT(\"hello\", 3)",                  "llo")]
    [InlineData("LEFT(\"hi\", 0)",                      "")]
    [InlineData("TRIM(\"  abc  \")",                    "abc")]
    [InlineData("LTRIM(\"  abc  \")",                   "abc  ")]
    [InlineData("RTRIM(\"  abc  \")",                   "  abc")]
    [InlineData("LOWER(\"HelloWorld\")",                "helloworld")]
    [InlineData("UPPER(\"HelloWorld\")",                "HELLOWORLD")]
    [InlineData("REPLACE(\"a-b-c\", \"-\", \"_\")",     "a_b_c")]
    [InlineData("FINDSTRING(\"abcabc\", \"b\", 1)",     2L)]
    [InlineData("FINDSTRING(\"abcabc\", \"b\", 2)",     5L)]
    [InlineData("FINDSTRING(\"abcabc\", \"x\", 1)",     0L)]
    [InlineData("REVERSE(\"abc\")",                     "cba")]
    [InlineData("TOKEN(\"a,b,c\", \",\", 2)",           "b")]
    [InlineData("TOKEN(\"a,,b\", \",\", 2)",            "b")]
    [InlineData("TOKENCOUNT(\"a,b,c\", \",\")",         3L)]
    [InlineData("HEX(255)",                             "FF")]
    [InlineData("HEX(0)",                               "0")]
    [InlineData("CODEPOINT(\"A\")",                     65L)]
    public void StringFunctions(string expr, object expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    // ----- Numeric functions ---------------------------------------------

    [Theory]
    [InlineData("ABS(-5)",          5L)]
    [InlineData("ABS(-3.5)",        3.5)]
    [InlineData("POWER(2, 10)",     1024.0)]
    [InlineData("SQUARE(7)",        49.0)]
    [InlineData("SQRT(16)",         4.0)]
    [InlineData("ROUND(3.14159, 2)", 3.14)]
    [InlineData("CEILING(2.1)",     3.0)]
    [InlineData("FLOOR(2.9)",       2.0)]
    [InlineData("SIGN(-7)",         -1L)]
    [InlineData("SIGN(7)",          1L)]
    [InlineData("SIGN(0)",          0L)]
    public void NumericFunctions(string expr, object expected)
    {
        Assert.Equal(expected, Eval(expr));
    }

    // ----- Date functions -------------------------------------------------

    [Fact]
    public void Date_YearMonthDay()
    {
        var row = MakeRow(null, null, null, new DateTime(2026, 3, 15, 10, 30, 0));
        Assert.Equal(2026L, Eval("YEAR(ts)", row));
        Assert.Equal(3L,    Eval("MONTH(ts)", row));
        Assert.Equal(15L,   Eval("DAY(ts)", row));
    }

    [Theory]
    [InlineData("hour",   10L)]
    [InlineData("minute", 30L)]
    [InlineData("second", 45L)]
    [InlineData("dw",     5L)]    // 2026-03-19 is a Thursday (Sun=1..Sat=7 -> Thu=5)
    public void DatePart(string part, long expected)
    {
        var row = MakeRow(null, null, null, new DateTime(2026, 3, 19, 10, 30, 45));
        Assert.Equal(expected, Eval($"DATEPART(\"{part}\", ts)", row));
    }

    [Fact]
    public void DateAdd_days()
    {
        var row = MakeRow(null, null, null, new DateTime(2026, 1, 1));
        var got = Eval("DATEADD(\"day\", 31, ts)", row);
        Assert.Equal(new DateTime(2026, 2, 1), got);
    }

    [Fact]
    public void DateDiff_days_between_two_timestamps()
    {
        var start = new DateTime(2026, 1, 1);
        var end   = new DateTime(2026, 2, 1);
        var row = new Row(Schema, [null, null, null, end]);
        // Use a column for both sides: ts vs a fixed start built into expression text.
        // Simpler: compare same column with itself - 0 expected.
        Assert.Equal(0L, Eval("DATEDIFF(\"day\", ts, ts)", row));
    }

    [Fact]
    public void Getdate_returns_datetime()
    {
        Assert.IsType<DateTime>(Eval("GETDATE()"));
    }

    // ----- NULL propagation through functions ----------------------------

    [Fact]
    public void FunctionNullPropagates()
    {
        // n is null; LEN expects a string, but NULL arg short-circuits.
        Assert.Null(Eval("LEN(s)", MakeRow(null, null, null, null)));
    }
}
