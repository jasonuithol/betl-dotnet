using Betl.Core;

namespace Betl.Providers.Sql;

public sealed class ConnectionRegistry
{
    private readonly Dictionary<string, ISqlProvider> _providers = new(StringComparer.Ordinal);

    public ConnectionRegistry Register(ISqlProvider provider)
    {
        _providers[provider.Type] = provider;
        return this;
    }

    public ISqlProvider Get(string type)
    {
        if (_providers.TryGetValue(type, out var p)) return p;
        throw new BetlException(
            $"No SQL provider registered for connection type '{type}'. " +
            $"Registered: {string.Join(", ", _providers.Keys)}.");
    }

    public bool Has(string type) => _providers.ContainsKey(type);
}
