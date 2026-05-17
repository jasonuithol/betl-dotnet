using Betl.Core;

namespace Betl.Expressions.SsisExpr;

internal sealed class Parser
{
    private readonly List<Token> _t;
    private int _i;

    public Parser(List<Token> tokens) { _t = tokens; _i = 0; }

    public AstNode ParseExpression()
    {
        var node = ParseOr();
        if (Peek().Kind != TokenKind.Eof)
            throw Err($"Unexpected token '{Peek().Text}' at end of expression.");
        return node;
    }

    // Precedence (lowest first):
    //   || -> && -> == != -> < <= > >= -> + - -> * / % -> unary -> primary
    private AstNode ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenKind.PipePipe))
        {
            var right = ParseAnd();
            left = new BinaryNode(BinaryOp.Or, left, right);
        }
        return left;
    }

    private AstNode ParseAnd()
    {
        var left = ParseEquality();
        while (Match(TokenKind.AmpAmp))
        {
            var right = ParseEquality();
            left = new BinaryNode(BinaryOp.And, left, right);
        }
        return left;
    }

    private AstNode ParseEquality()
    {
        var left = ParseComparison();
        while (true)
        {
            if (Match(TokenKind.EqEq))      left = new BinaryNode(BinaryOp.Eq, left, ParseComparison());
            else if (Match(TokenKind.BangEq)) left = new BinaryNode(BinaryOp.Ne, left, ParseComparison());
            else return left;
        }
    }

    private AstNode ParseComparison()
    {
        var left = ParseAdditive();
        while (true)
        {
            if      (Match(TokenKind.Lt))   left = new BinaryNode(BinaryOp.Lt, left, ParseAdditive());
            else if (Match(TokenKind.LtEq)) left = new BinaryNode(BinaryOp.Le, left, ParseAdditive());
            else if (Match(TokenKind.Gt))   left = new BinaryNode(BinaryOp.Gt, left, ParseAdditive());
            else if (Match(TokenKind.GtEq)) left = new BinaryNode(BinaryOp.Ge, left, ParseAdditive());
            else return left;
        }
    }

    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            if      (Match(TokenKind.Plus))  left = new BinaryNode(BinaryOp.Add, left, ParseMultiplicative());
            else if (Match(TokenKind.Minus)) left = new BinaryNode(BinaryOp.Sub, left, ParseMultiplicative());
            else return left;
        }
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            if      (Match(TokenKind.Star))    left = new BinaryNode(BinaryOp.Mul, left, ParseUnary());
            else if (Match(TokenKind.Slash))   left = new BinaryNode(BinaryOp.Div, left, ParseUnary());
            else if (Match(TokenKind.Percent)) left = new BinaryNode(BinaryOp.Mod, left, ParseUnary());
            else return left;
        }
    }

    private AstNode ParseUnary()
    {
        if (Match(TokenKind.Bang))  return new UnaryNode(UnaryOp.Not,    ParseUnary());
        if (Match(TokenKind.Minus)) return new UnaryNode(UnaryOp.Negate, ParseUnary());
        return ParsePrimary();
    }

    private AstNode ParsePrimary()
    {
        var t = Peek();
        switch (t.Kind)
        {
            case TokenKind.IntLit:
                _i++;
                return new LiteralNode(long.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture));
            case TokenKind.FloatLit:
                _i++;
                return new LiteralNode(double.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture));
            case TokenKind.StringLit:
                _i++;
                return new LiteralNode(t.Text);
            case TokenKind.BracketedIdent:
                _i++;
                return new ColumnRefNode(t.Text);
            case TokenKind.Identifier:
            {
                _i++;
                var upper = t.Text.ToUpperInvariant();
                if (upper is "TRUE") return new LiteralNode(true);
                if (upper is "FALSE") return new LiteralNode(false);
                if (upper is "NULL") return new LiteralNode(null);
                if (Match(TokenKind.LParen))
                {
                    var args = new List<AstNode>();
                    if (Peek().Kind != TokenKind.RParen)
                    {
                        args.Add(ParseOr());
                        while (Match(TokenKind.Comma)) args.Add(ParseOr());
                    }
                    Expect(TokenKind.RParen, "function call");
                    return new FunctionCallNode(upper, args);
                }
                return new ColumnRefNode(t.Text);
            }
            case TokenKind.LParen:
            {
                _i++;
                var inner = ParseOr();
                Expect(TokenKind.RParen, "parenthesised expression");
                return inner;
            }
            default:
                throw Err($"Expected primary, got '{t.Text}' ({t.Kind}).");
        }
    }

    private Token Peek() => _t[_i];

    private bool Match(TokenKind k)
    {
        if (_t[_i].Kind != k) return false;
        _i++;
        return true;
    }

    private void Expect(TokenKind k, string context)
    {
        if (_t[_i].Kind != k)
            throw Err($"Expected {k} in {context}, got '{_t[_i].Text}'.");
        _i++;
    }

    private BetlException Err(string msg) =>
        new BetlException($"SSIS-EL parse error at column {_t[_i].Position + 1}: {msg}");
}
