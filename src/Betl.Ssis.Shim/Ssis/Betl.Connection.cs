namespace Betl;

/// <summary>
/// Stub for the upstream <c>Betl.Connection</c> bridge that real betl.native
/// satisfies via a C function-pointer back to the host's <c>connections:</c>
/// block. In managed betl.dotnet, the driver (<c>DotnetPipelineComponent</c>)
/// installs a <see cref="Resolver"/> before invoking user code that resolves
/// connection names to the connection's DSN string.
/// </summary>
public static class Connection
{
    [ThreadStatic] private static Func<string, string?>? _resolver;

    /// <summary>Set by the runtime before invoking user code; nulled after.</summary>
    public static Func<string, string?>? Resolver
    {
        get => _resolver;
        set => _resolver = value;
    }

    /// <summary>Returns the connection's resolved DSN, or null if unknown.</summary>
    public static string? Get(string name) => _resolver?.Invoke(name);
}
