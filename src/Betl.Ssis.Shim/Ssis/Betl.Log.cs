namespace Betl;

/// <summary>
/// Stub for the upstream <c>Betl.Log</c> bridge. In real betl.linux this calls
/// back into the C-side log pipeline. In managed betl.dotnet, the driver
/// installs a <see cref="Sink"/> before invoking user code; calls from inside
/// user code route there. With no sink installed, messages are dropped.
/// </summary>
public static class Log
{
    [ThreadStatic] private static Action<string, string>? _sink;

    /// <summary>Set by the runtime (level, message) before invoking user code; nulled after.</summary>
    public static Action<string, string>? Sink
    {
        get => _sink;
        set => _sink = value;
    }

    public static void Debug(string msg) => _sink?.Invoke("debug", msg);
    public static void Info (string msg) => _sink?.Invoke("info",  msg);
    public static void Warn (string msg) => _sink?.Invoke("warn",  msg);
    public static void Error(string msg) => _sink?.Invoke("error", msg);
}
