namespace Betl.Expressions.SsisExpr;

internal abstract record AstNode;

internal sealed record LiteralNode(object? Value) : AstNode;

internal sealed record ColumnRefNode(string Name) : AstNode;

internal enum UnaryOp { Negate, Not }

internal sealed record UnaryNode(UnaryOp Op, AstNode Operand) : AstNode;

internal enum BinaryOp
{
    Add, Sub, Mul, Div, Mod,
    Eq, Ne, Lt, Le, Gt, Ge,
    And, Or,
}

internal sealed record BinaryNode(BinaryOp Op, AstNode Left, AstNode Right) : AstNode;

internal sealed record FunctionCallNode(string Name, IReadOnlyList<AstNode> Args) : AstNode;

internal sealed record TernaryNode(AstNode Condition, AstNode Then, AstNode Else) : AstNode;

internal sealed record CastNode(string TypeName, IReadOnlyList<long> Args, AstNode Operand) : AstNode;
