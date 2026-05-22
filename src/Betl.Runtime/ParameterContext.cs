using System.Globalization;
using System.Text.RegularExpressions;
using Betl.Core;

namespace Betl.Runtime;

/// <summary>
/// Resolves declared pipeline parameters from CLI overrides, environment variables,
/// and declared defaults (including the `today` / `now` sentinels). Expands
/// <c>${params.x}</c>, <c>${env.X}</c>, and <c>${vars.x}</c> placeholders in
/// template strings.
/// </summary>
public sealed partial class ParameterContext
{
    private readonly Dictionary<string, object?> _params;
    private readonly Dictionary<string, string> _vars;

    private ParameterContext(Dictionary<string, object?> resolved)
    {
        _params = resolved;
        _vars = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static ParameterContext Build(Pipeline pipeline, IReadOnlyDictionary<string, string> cliOverrides)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, decl) in pipeline.Parameters)
        {
            if (cliOverrides.TryGetValue(name, out var rawCli))
            {
                resolved[name] = ParseScalar(decl.TypeSpelling, rawCli);
            }
            else if (decl.Default is not null)
            {
                resolved[name] = ResolveDefault(decl);
            }
            else if (decl.Required)
            {
                throw new BetlException($"Required parameter '{name}' was not supplied (--param {name}=<value>).");
            }
            else
            {
                resolved[name] = null;
            }
        }

        // Any --param key that isn't declared is an error per the spec's
        // fail-loud posture — typos shouldn't silently succeed.
        foreach (var key in cliOverrides.Keys)
        {
            if (!pipeline.Parameters.ContainsKey(key))
                throw new BetlException($"--param '{key}' was supplied but no parameter named '{key}' is declared.");
        }

        return new ParameterContext(resolved);
    }

    public object? Get(string name) =>
        _params.TryGetValue(name, out var v)
            ? v
            : throw new BetlException($"Unknown parameter '{name}'.");

    /// <summary>
    /// Binds a <c>${vars.<name>}</c> for the lifetime of the returned disposable.
    /// Throws if the same name is already bound (no shadowing — the spec wants
    /// foreach loop variables to be unambiguous).
    /// </summary>
    public IDisposable PushVar(string name, string value)
    {
        if (_vars.ContainsKey(name))
            throw new BetlException(
                $"Loop variable '{name}' is already bound (nested foreach with the same `as:` is rejected).");
        _vars[name] = value;
        return new VarPop(_vars, name);
    }

    /// <summary>
    /// Permanently binds <c>${vars.&lt;name&gt;}</c> for the rest of the pipeline.
    /// Used by <c>var.set</c>. Re-setting an already-bound name overwrites it —
    /// that matches upstream behavior and lets var.set inside a loop body refresh.
    /// </summary>
    public void SetVar(string name, string value) => _vars[name] = value;

    private sealed class VarPop(Dictionary<string, string> vars, string name) : IDisposable
    {
        public void Dispose() => vars.Remove(name);
    }

    public string Substitute(string template)
    {
        return PlaceholderRegex().Replace(template, m =>
        {
            var scope = m.Groups[1].Value;
            var key = m.Groups[2].Value;
            return scope switch
            {
                "params" => FormatForTemplate(Get(key)),
                "env" => Environment.GetEnvironmentVariable(key)
                    ?? throw new BetlException($"Environment variable '{key}' is not set."),
                "vars" => _vars.TryGetValue(key, out var v)
                    ? v
                    : throw new BetlException($"Loop variable '{key}' is not bound."),
                _ => throw new BetlException($"Unknown placeholder scope '${{{scope}.{key}}}'."),
            };
        });
    }

    private static string FormatForTemplate(object? v) => v switch
    {
        null => "",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private static object? ResolveDefault(Parameter p)
    {
        // The sentinels `today` / `now` are spec-defined defaults for date/timestamp.
        if (p.Default is string s)
        {
            if (s == "today" && p.TypeSpelling is "date" or "date32")
                return DateOnly.FromDateTime(DateTime.Now);
            if (s == "now" && p.TypeSpelling.StartsWith("timestamp", StringComparison.Ordinal))
                return DateTime.UtcNow;
            return ParseScalar(p.TypeSpelling, s);
        }
        return p.Default;
    }

    private static object? ParseScalar(string typeSpelling, string raw)
    {
        return typeSpelling switch
        {
            "string" => raw,
            "bool" => bool.Parse(raw),
            "int32" => int.Parse(raw, CultureInfo.InvariantCulture),
            "int64" => long.Parse(raw, CultureInfo.InvariantCulture),
            "float32" => float.Parse(raw, CultureInfo.InvariantCulture),
            "float64" => double.Parse(raw, CultureInfo.InvariantCulture),
            "date" or "date32" => DateOnly.ParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ when typeSpelling.StartsWith("timestamp", StringComparison.Ordinal)
                => DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => raw, // fall back to raw string for types we don't yet understand
        };
    }

    [GeneratedRegex(@"\$\{(params|env|vars)\.([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();
}
