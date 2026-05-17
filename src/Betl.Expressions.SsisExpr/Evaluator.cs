using System.Globalization;
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
            if (l is bool lb && !lb) return false;  // F && _ = F
            var r = Eval(node.Right, row);
            return ThreeValuedAnd(l, r);
        }
        if (node.Op == BinaryOp.Or)
        {
            var l = Eval(node.Left, row);
            if (l is bool lb && lb) return true;    // T || _ = T
            var r = Eval(node.Right, row);
            return ThreeValuedOr(l, r);
        }

        var left = Eval(node.Left, row);
        var right = Eval(node.Right, row);

        // All other ops propagate NULL.
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

    private static object? EvalFunction(FunctionCallNode node, Row row) => node.Name switch
    {
        "ISNULL" => node.Args.Count == 1
            ? Eval(node.Args[0], row) is null
            : throw new BetlException("ISNULL takes exactly 1 argument."),
        "REPLACENULL" => node.Args.Count == 2
            ? (Eval(node.Args[0], row) ?? Eval(node.Args[1], row))
            : throw new BetlException("REPLACENULL takes exactly 2 arguments."),
        _ => throw new BetlException($"Function '{node.Name}' not implemented in Phase 1."),
    };

    // --- helpers -----------------------------------------------------

    private static object Add(object l, object r)
    {
        // String concat takes priority (SSIS rule).
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
        if (isFloat || rIsFloat)
        {
            // IEEE 754: 0/0 = NaN, x/0 = ±Inf.
            return ld / rd;
        }
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
        if (l is string ls && r is string rs)
            return string.CompareOrdinal(ls, rs);

        if (l is bool lb && r is bool rb) return lb.CompareTo(rb);

        if (IsNumeric(l) && IsNumeric(r))
        {
            var (_, ld, lf) = Promote(l);
            var (_, rd, rf) = Promote(r);
            if (lf || rf) return ld.CompareTo(rd);
            return ((long)ld).CompareTo((long)rd);
        }

        throw new BetlException($"Cannot compare {l.GetType().Name} and {r.GetType().Name}.");
    }

    private static bool IsNumeric(object v) =>
        v is long or int or short or byte or double or float or decimal;

    private static bool ToBool(object v) => v switch
    {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0.0,
        _ => throw new BetlException($"Cannot convert {v.GetType().Name} to bool."),
    };

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

    // Used for testing string output (e.g. CSV write).
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
