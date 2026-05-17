namespace Betl.Expressions.SsisExpr;

internal enum TokenKind
{
    Eof,
    LParen, RParen, Comma,
    Plus, Minus, Star, Slash, Percent,
    Bang, EqEq, BangEq, Lt, LtEq, Gt, GtEq,
    AmpAmp, PipePipe,
    QMark, Colon,
    Identifier, BracketedIdent, StringLit, IntLit, FloatLit,
}

internal readonly record struct Token(TokenKind Kind, string Text, int Position)
{
    public override string ToString() => $"{Kind}({Text})@{Position}";
}
