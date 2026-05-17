using System.Globalization;
using System.Text;
using Betl.Core;

namespace Betl.Expressions.SsisExpr;

internal static class Evaluator
{
    public static object? Eval(AstNode node, Row row) => node switch
    {
        LiteralNode lit       => lit.Value,
        ColumnRefNode col     => LookupColumn(col.Name, row),
        UnaryNode u           => EvalUnary(u, row),
        BinaryNode b          => EvalBinary(b, row),
        TernaryNode t         => EvalTernary(t, row),
        CastNode c            => EvalCast(c, row),
        FunctionCallNode f    => EvalFunction(f, row),
        _ => throw new BetlException($"Unknown AST node: {node}"),
    };

    private static object? LookupColumn(string name, Row row)
    {
        var idx = row.Schema.IndexOf(name);
        if (idx < 0) throw new BetlException($"Column '{name}' not in input schema.");
        return row.Values[idx];
    }

    private static object? EvalUnary(UnaryNode node, Row row)
    {
        var v = Eval(node.Operand, row);
        if (v is null) return null; // 3VL propagation
        return node.Op switch
        {
            UnaryOp.Not => !ToBool(v),
            UnaryOp.Negate => v switch
            {
                long l    => -l,
                double d  => -d,
                decimal m => -m,
                _ => throw new BetlException($"Unary '-' not defined for {v.GetType().Name}."),
            },
            _ => throw new BetlException($"Unknown unary op: {node.Op}"),
        };
    }

    private static object? EvalBinary(BinaryNode node, Row row)
    {
        // && and || implement 3VL short-circuit per SPEC.md/SSISEXPR.md.
        if (node.Op == BinaryOp.And)
        {
            var l = Eval(node.Left, row);
            if (l is bool lb && !lb) return false;
            var r = Eval(node.Right, row);
            return ThreeValuedAnd(l, r);
        }
        if (node.Op == BinaryOp.Or)
        {
            var l = Eval(node.Left, row);
            if (l is bool lb && lb) return true;
            var r = Eval(node.Right, row);
            return ThreeValuedOr(l, r);
        }

        var left = Eval(node.Left, row);
        var right = Eval(node.Right, row);
        if (left is null || right is null) return null;

        return node.Op switch
        {
            BinaryOp.Add => Add(left, right),
            BinaryOp.Sub => Arith(left, right, (a, b) => a - b, (a, b) => a - b),
            BinaryOp.Mul => Arith(left, right, (a, b) => a * b, (a, b) => a * b),
            BinaryOp.Div => Divide(left, right),
            BinaryOp.Mod => Arith(left, right, (a, b) => a % b, (a, b) => a % b),
            BinaryOp.Eq  => Compare(left, right) == 0,
            BinaryOp.Ne  => Compare(left, right) != 0,
            BinaryOp.Lt  => Compare(left, right) < 0,
            BinaryOp.Le  => Compare(left, right) <= 0,
            BinaryOp.Gt  => Compare(left, right) > 0,
            BinaryOp.Ge  => Compare(left, right) >= 0,
            _ => throw new BetlException($"Unknown binary op: {node.Op}"),
        };
    }

    private static object? EvalTernary(TernaryNode node, Row row)
    {
        var c = Eval(node.Condition, row);
        if (c is null) return null;
        if (c is not bool b) throw new BetlException("Ternary condition must be boolean.");
        return Eval(b ? node.Then : node.Else, row);
    }

    // ============================================================
    // Casts: (DT_xx) value  /  (DT_WSTR, N) /  (DT_NUMERIC, p, s) /  ...
    // ============================================================
    private static object? EvalCast(CastNode node, Row row)
    {
        var v = Eval(node.Operand, row);
        if (v is null) return null;
        var t = NormaliseTypeName(node.TypeName);
        return t switch
        {
            "DT_I1"  => unchecked((sbyte)ToInt64(v)),
            "DT_UI1" => unchecked((byte)ToInt64(v)),
            "DT_I2"  => unchecked((short)ToInt64(v)),
            "DT_UI2" => unchecked((ushort)ToInt64(v)),
            "DT_I4"  => unchecked((int)ToInt64(v)),
            "DT_UI4" => unchecked((uint)ToInt64(v)),
            "DT_I8"  => ToInt64(v),
            "DT_UI8" => unchecked((ulong)ToInt64(v)),
            "DT_R4"  => (float)ToDouble(v),
            "DT_R8"  => ToDouble(v),
            "DT_BOOL" => ToBool(v),
            "DT_WSTR" or "DT_STR" => ToString(v),
            "DT_BYTES" => ToBytes(v),
            "DT_DBDATE" => ToDate(v),
            "DT_DBTIMESTAMP" => ToTimestamp(v),
            "DT_DBTIME" => ToTime(v),
            "DT_GUID" => ToGuid(v),
            "DT_NUMERIC" => ToDecimal(v, node.Args.Count >= 2 ? (int)node.Args[1] : 0),
            "DT_DATE" => ToOleDate(v),
            _ => throw new BetlException($"Cast to '{node.TypeName}' is not supported."),
        };
    }

    private static string NormaliseTypeName(string s) => s.ToUpperInvariant() switch
    {
        "DT_DECIMAL" => "DT_NUMERIC",
        "DT_IMAGE" => "DT_BYTES",
        "DT_NTEXT" => "DT_WSTR",
        "DT_TEXT" => "DT_STR",
        "DT_DBTIMESTAMP2" or "DT_DBTIMESTAMPOFFSET" => "DT_DBTIMESTAMP",
        "DT_DBTIME2" => "DT_DBTIME",
        "DT_FILETIME" => "DT_DBTIMESTAMP",
        var x => x,
    };

    // ============================================================
    // Functions (~30 from docs/SSISEXPR.md, names case-insensitive)
    // ============================================================
    private static object? EvalFunction(FunctionCallNode node, Row row)
    {
        var name = node.Name.ToUpperInvariant();
        var rawArgs = node.Args;

        // NULL-observing functions: don't evaluate operand strictness ahead.
        if (name == "ISNULL")
        {
            Require(rawArgs, 1, name);
            return Eval(rawArgs[0], row) is null;
        }
        if (name == "REPLACENULL")
        {
            Require(rawArgs, 2, name);
            return Eval(rawArgs[0], row) ?? Eval(rawArgs[1], row);
        }

        // Everything else propagates NULL: evaluate args, short-circuit on any null.
        var args = rawArgs.Select(a => Eval(a, row)).ToArray();
        if (name != "GETDATE" && args.Any(a => a is null)) return null;

        return name switch
        {
            // --- String -------------------------------------------------
            "LEN"        => (long)Encoding.UTF8.GetByteCount(ToString(args[0]!)),
            "SUBSTRING"  => Substring(ToString(args[0]!), ToInt64(args[1]!), ToInt64(args[2]!)),
            "LEFT"       => Left(ToString(args[0]!), ToInt64(args[1]!)),
            "RIGHT"      => Right(ToString(args[0]!), ToInt64(args[1]!)),
            "TRIM"       => ToString(args[0]!).Trim(),
            "LTRIM"      => ToString(args[0]!).TrimStart(),
            "RTRIM"      => ToString(args[0]!).TrimEnd(),
            "LOWER"      => ToString(args[0]!).ToLowerInvariant(),
            "UPPER"      => ToString(args[0]!).ToUpperInvariant(),
            "REPLACE"    => Replace(ToString(args[0]!), ToString(args[1]!), ToString(args[2]!)),
            "FINDSTRING" => FindString(ToString(args[0]!), ToString(args[1]!), ToInt64(args[2]!)),
            "REVERSE"    => Reverse(ToString(args[0]!)),
            "TOKEN"      => Token(ToString(args[0]!), ToString(args[1]!), ToInt64(args[2]!)),
            "TOKENCOUNT" => (long)TokenCount(ToString(args[0]!), ToString(args[1]!)),
            "HEX"        => Hex(ToInt64(args[0]!)),
            "CODEPOINT"  => Codepoint(ToString(args[0]!)),

            // --- Numeric ------------------------------------------------
            "ABS"        => Abs(args[0]!),
            "POWER"      => Math.Pow(ToDouble(args[0]!), ToDouble(args[1]!)),
            "SQUARE"     => Square(ToDouble(args[0]!)),
            "SQRT"       => Math.Sqrt(ToDouble(args[0]!)),
            "ROUND"      => Math.Round(ToDouble(args[0]!), (int)ToInt64(args[1]!)),
            "CEILING"    => Math.Ceiling(ToDouble(args[0]!)),
            "FLOOR"      => Math.Floor(ToDouble(args[0]!)),
            "SIGN"       => (long)Math.Sign(ToDouble(args[0]!)),

            // --- Date / time --------------------------------------------
            "GETDATE"    => DateTime.UtcNow,
            "YEAR"       => (long)ToTimestamp(args[0]!).Year,
            "MONTH"      => (long)ToTimestamp(args[0]!).Month,
            "DAY"        => (long)ToTimestamp(args[0]!).Day,
            "DATEPART"   => DatePart(ToString(args[0]!), ToTimestamp(args[1]!)),
            "DATEADD"    => DateAdd(ToString(args[0]!), ToInt64(args[1]!), args[2]!),
            "DATEDIFF"   => DateDiff(ToString(args[0]!), ToTimestamp(args[1]!), ToTimestamp(args[2]!)),

            _ => throw new BetlException($"SSIS-EL function '{name}' is not implemented."),
        };
    }

    private static double Square(double d) => d * d;

    private static void Require(IReadOnlyList<AstNode> args, int n, string name)
    {
        if (args.Count != n)
            throw new BetlException($"{name}: expected {n} arg(s), got {args.Count}.");
    }

    // ----- String helpers -------------------------------------------------

    private static string Substring(string s, long start, long length)
    {
        if (start < 1) throw new BetlException($"SUBSTRING: start must be >= 1 (got {start}).");
        if (length < 0) length = 0;
        var bytes = Encoding.UTF8.GetBytes(s);
        var off = (int)(start - 1);
        if (off >= bytes.Length) return "";
        var len = Math.Min((int)length, bytes.Length - off);
        return Encoding.UTF8.GetString(bytes, off, len);
    }

    private static string Left(string s, long n)
    {
        if (n <= 0) return "";
        var bytes = Encoding.UTF8.GetBytes(s);
        var len = Math.Min((int)n, bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    private static string Right(string s, long n)
    {
        if (n <= 0) return "";
        var bytes = Encoding.UTF8.GetBytes(s);
        var len = (int)Math.Min(n, bytes.Length);
        return Encoding.UTF8.GetString(bytes, bytes.Length - len, len);
    }

    private static string Replace(string s, string find, string replacement)
    {
        if (find.Length == 0) return s;
        return s.Replace(find, replacement);
    }

    private static long FindString(string s, string needle, long occurrence)
    {
        if (occurrence < 1 || needle.Length == 0) return 0;
        var pos = 0;
        for (long i = 0; i < occurrence; i++)
        {
            pos = s.IndexOf(needle, pos, StringComparison.Ordinal);
            if (pos < 0) return 0;
            if (i + 1 == occurrence) return pos + 1; // 1-based
            pos += needle.Length;
        }
        return 0;
    }

    private static string Reverse(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        Array.Reverse(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Token(string s, string delim, long n)
    {
        if (n < 1) throw new BetlException($"TOKEN: n must be >= 1 (got {n}).");
        if (delim.Length == 0) return n == 1 ? s : "";
        var tokens = SplitTokens(s, delim);
        var idx = (int)(n - 1);
        return idx < tokens.Count ? tokens[idx] : "";
    }

    private static int TokenCount(string s, string delim)
    {
        if (delim.Length == 0) return s.Length == 0 ? 0 : 1;
        return SplitTokens(s, delim).Count;
    }

    private static List<string> SplitTokens(string s, string delim)
    {
        var set = new HashSet<char>(delim);
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (set.Contains(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static string Hex(long n)
    {
        if (n < 0) throw new BetlException("HEX: negative input not supported.");
        return n.ToString("X", CultureInfo.InvariantCulture);
    }

    private static long Codepoint(string s)
    {
        if (s.Length == 0) throw new BetlException("CODEPOINT: empty string.");
        return char.ConvertToUtf32(s, 0);
    }

    // ----- Numeric helpers ------------------------------------------------

    private static object Abs(object v) => v switch
    {
        long l => Math.Abs(l),
        int i  => Math.Abs(i),
        double d => Math.Abs(d),
        float f => Math.Abs(f),
        decimal m => Math.Abs(m),
        _ => Math.Abs(ToDouble(v)),
    };

    // ----- Date helpers ---------------------------------------------------

    private static long DatePart(string part, DateTime d) => part.ToLowerInvariant() switch
    {
        "year" or "yyyy" or "yy" => d.Year,
        "quarter" or "qq" or "q" => (d.Month - 1) / 3 + 1,
        "month" or "mm" or "m" => d.Month,
        "dayofyear" or "dy" or "y" => d.DayOfYear,
        "day" or "dd" or "d" => d.Day,
        "week" or "wk" or "ww" => CultureInfo.InvariantCulture.Calendar
            .GetWeekOfYear(d, CalendarWeekRule.FirstDay, DayOfWeek.Sunday),
        "weekday" or "dw" => (int)d.DayOfWeek + 1, // Sun=1..Sat=7
        "hour" or "hh" => d.Hour,
        "minute" or "mi" or "n" => d.Minute,
        "second" or "ss" or "s" => d.Second,
        _ => throw new BetlException($"DATEPART: unknown part '{part}'."),
    };

    private static object DateAdd(string part, long n, object dateObj)
    {
        var dt = ToTimestamp(dateObj);
        var added = part.ToLowerInvariant() switch
        {
            "year" or "yyyy" or "yy" => dt.AddYears((int)n),
            "quarter" or "qq" or "q" => dt.AddMonths((int)(n * 3)),
            "month" or "mm" or "m" => dt.AddMonths((int)n),
            "dayofyear" or "dy" or "y" or "day" or "dd" or "d" => dt.AddDays(n),
            "week" or "wk" or "ww" => dt.AddDays(n * 7),
            "weekday" or "dw" => dt.AddDays(n),
            "hour" or "hh" => dt.AddHours(n),
            "minute" or "mi" or "n" => dt.AddMinutes(n),
            "second" or "ss" or "s" => dt.AddSeconds(n),
            _ => throw new BetlException($"DATEADD: unknown part '{part}'."),
        };
        return dateObj is DateOnly ? DateOnly.FromDateTime(added) : (object)added;
    }

    private static long DateDiff(string part, DateTime a, DateTime b)
    {
        var span = b - a;
        return part.ToLowerInvariant() switch
        {
            "year" or "yyyy" or "yy" => b.Year - a.Year,
            "quarter" or "qq" or "q" => (b.Year - a.Year) * 4 + ((b.Month - 1) / 3) - ((a.Month - 1) / 3),
            "month" or "mm" or "m" => (b.Year - a.Year) * 12 + b.Month - a.Month,
            "dayofyear" or "dy" or "y" or "day" or "dd" or "d" => (long)span.TotalDays,
            "week" or "wk" or "ww" => (long)(span.TotalDays / 7),
            "weekday" or "dw" => (long)span.TotalDays,
            "hour" or "hh" => (long)span.TotalHours,
            "minute" or "mi" or "n" => (long)span.TotalMinutes,
            "second" or "ss" or "s" => (long)span.TotalSeconds,
            _ => throw new BetlException($"DATEDIFF: unknown part '{part}'."),
        };
    }

    // ----- Conversion primitives -----------------------------------------

    private static long ToInt64(object v) => v switch
    {
        long l => l,
        int i => i,
        short s => s,
        sbyte sb => sb,
        ulong ul => (long)ul,
        uint u => u,
        ushort us => us,
        byte b => b,
        double d => (long)d,
        float f => (long)f,
        decimal m => (long)m,
        bool tb => tb ? 1 : 0,
        string s => long.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    private static double ToDouble(object v) => v switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        string s => double.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v, CultureInfo.InvariantCulture),
    };

    private static decimal ToDecimal(object v, int targetScale)
    {
        var d = v switch
        {
            decimal m => m,
            string s => decimal.Parse(s, CultureInfo.InvariantCulture),
            _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        };
        if (targetScale > 0) d = Math.Round(d, targetScale, MidpointRounding.AwayFromZero);
        return d;
    }

    private static bool ToBool(object v) => v switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0.0,
        decimal m => m != 0m,
        string s => bool.Parse(s),
        _ => throw new BetlException($"Cannot convert {v.GetType().Name} to bool."),
    };

    private static string ToString(object v) => v switch
    {
        string s => s,
        double d => d.ToString("G", CultureInfo.InvariantCulture),
        float f => f.ToString("G", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "True" : "False",
        DateOnly d2 => d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("o", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        byte[] bytes => Convert.ToHexString(bytes).ToLowerInvariant(),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    private static byte[] ToBytes(object v) => v switch
    {
        byte[] b => b,
        string s => Convert.FromHexString(s),
        _ => throw new BetlException($"Cannot cast {v.GetType().Name} to DT_BYTES."),
    };

    private static DateOnly ToDate(object v) => v switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => throw new BetlException($"Cannot cast {v.GetType().Name} to DT_DBDATE."),
    };

    private static DateTime ToTimestamp(object v) => v switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dto => dto.UtcDateTime,
        string s => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new BetlException($"Cannot cast {v.GetType().Name} to DT_DBTIMESTAMP."),
    };

    private static TimeSpan ToTime(object v) => v switch
    {
        TimeSpan ts => ts,
        DateTime dt => dt.TimeOfDay,
        string s => TimeSpan.Parse(s, CultureInfo.InvariantCulture),
        _ => throw new BetlException($"Cannot cast {v.GetType().Name} to DT_DBTIME."),
    };

    private static Guid ToGuid(object v) => v switch
    {
        Guid g => g,
        string s => Guid.ParseExact(s, "D"),
        byte[] b when b.Length == 16 => new Guid(b),
        _ => throw new BetlException($"Cannot cast {v.GetType().Name} to DT_GUID."),
    };

    /// <summary>OLE Automation date: numeric = days since 1899-12-30 with fractional day-of-day.</summary>
    private static DateTime ToOleDate(object v) => v switch
    {
        double d => DateTime.FromOADate(d),
        long l => DateTime.FromOADate(l),
        _ => ToTimestamp(v),
    };

    // ----- legacy / shared ------------------------------------------------

    private static object Add(object l, object r)
    {
        if (l is string ls && r is string rs) return ls + rs;
        return Arith(l, r, (a, b) => a + b, (a, b) => a + b);
    }

    private static object Arith(object l, object r,
        Func<long, long, long> intFn, Func<double, double, double> floatFn)
    {
        var (li, ld, isFloat) = Promote(l);
        var (ri, rd, rIsFloat) = Promote(r);
        if (isFloat || rIsFloat) return floatFn(ld, rd);
        return intFn(li, ri);
    }

    private static object Divide(object l, object r)
    {
        var (li, ld, isFloat) = Promote(l);
        var (ri, rd, rIsFloat) = Promote(r);
        if (isFloat || rIsFloat) return ld / rd;
        if (ri == 0) throw new BetlException("Integer divide by zero.");
        return li / ri;
    }

    private static (long IntVal, double FloatVal, bool IsFloat) Promote(object v) => v switch
    {
        long l    => (l, l, false),
        int i     => (i, i, false),
        short s   => (s, s, false),
        byte b    => (b, b, false),
        double d  => (0L, d, true),
        float f   => (0L, f, true),
        decimal m => (0L, (double)m, true),
        bool   bv => (bv ? 1L : 0L, bv ? 1.0 : 0.0, false),
        _ => throw new BetlException($"Cannot promote {v.GetType().Name} to numeric."),
    };

    private static int Compare(object l, object r)
    {
        if (l is string ls && r is string rs) return string.CompareOrdinal(ls, rs);
        if (l is bool lb && r is bool rb) return lb.CompareTo(rb);

        if (IsNumeric(l) && IsNumeric(r))
        {
            var (_, ld, lf) = Promote(l);
            var (_, rd, rf) = Promote(r);
            if (lf || rf) return ld.CompareTo(rd);
            return ((long)ld).CompareTo((long)rd);
        }

        if (l is IComparable cmp && l.GetType() == r.GetType()) return cmp.CompareTo(r);

        throw new BetlException($"Cannot compare {l.GetType().Name} and {r.GetType().Name}.");
    }

    private static bool IsNumeric(object v) =>
        v is long or int or short or byte or sbyte or ulong or uint or ushort or double or float or decimal;

    private static object? ThreeValuedAnd(object? l, object? r)
    {
        if (l is bool lb && !lb) return false;
        if (r is bool rb && !rb) return false;
        if (l is null || r is null) return null;
        return (bool)l && (bool)r;
    }

    private static object? ThreeValuedOr(object? l, object? r)
    {
        if (l is bool lb && lb) return true;
        if (r is bool rb && rb) return true;
        if (l is null || r is null) return null;
        return (bool)l || (bool)r;
    }

    public static string FormatScalar(object? v) => v switch
    {
        null => "",
        bool b => b ? "True" : "False",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        DateOnly d2 => d2.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };
}

