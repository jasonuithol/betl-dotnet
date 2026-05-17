using Betl.Core;

namespace Betl.Expressions.SsisExpr;

public sealed class SsisExpressionEngine : IExpressionEngine
{
    public string LanguageId => "ssisexpr";

    public ICompiledExpression Compile(string source, Schema inputSchema)
    {
        var tokens = new Lexer(source).Tokenize();
        var ast = new Parser(tokens).ParseExpression();
        return new Compiled(ast);
    }

    private sealed class Compiled(AstNode root) : ICompiledExpression
    {
        public object? Evaluate(Row row) => Evaluator.Eval(root, row);
    }
}
