using Betl.Core;

namespace Betl.Runtime;

public sealed class EngineRegistry
{
    private readonly Dictionary<string, IExpressionEngine> _engines = new(StringComparer.Ordinal);

    public EngineRegistry Register(IExpressionEngine engine)
    {
        _engines[engine.LanguageId] = engine;
        return this;
    }

    public IExpressionEngine Get(string languageId)
    {
        if (_engines.TryGetValue(languageId, out var e)) return e;
        throw new BetlException(
            $"Expression language '{languageId}' is not registered. " +
            $"Available: {string.Join(", ", _engines.Keys)}.");
    }

    public ICompiledExpression Compile(Expression expr, Schema inputSchema) => expr switch
    {
        LiteralExpression lit => new LiteralCompiledExpression(lit.Value),
        LangExpression le when le.Lang == "literal" => new LiteralCompiledExpression(le.Source),
        LangExpression le => Get(le.Lang).Compile(le.Source, inputSchema),
        _ => throw new BetlException($"Unknown expression kind {expr.GetType().Name}."),
    };
}
