using System.Diagnostics;
using Betl.Core;

namespace Betl.Components.Tasks;

public sealed class ShellTask : IControlTask
{
    private readonly IReadOnlyList<string> _argv;
    private readonly TimeSpan? _timeout;
    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly CapturePolicy _stdout;
    private readonly CapturePolicy _stderr;

    public string Id { get; }

    public ShellTask(string id, IReadOnlyList<string> argv, TimeSpan? timeout,
        IReadOnlyDictionary<string, string> env, CapturePolicy stdout, CapturePolicy stderr)
    {
        if (argv.Count == 0) throw new BetlException($"shell '{id}': argv must have at least one entry.");
        Id = id;
        _argv = argv;
        _timeout = timeout;
        _env = env;
        _stdout = stdout;
        _stderr = stderr;
    }

    public void Execute(Action<string>? log)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _argv[0],
            UseShellExecute = false,
            RedirectStandardOutput = _stdout != CapturePolicy.Inherit,
            RedirectStandardError = _stderr != CapturePolicy.Inherit,
            CreateNoWindow = true,
        };
        for (var i = 1; i < _argv.Count; i++) psi.ArgumentList.Add(_argv[i]);
        foreach (var (k, v) in _env) psi.Environment[k] = v;

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = _stdout == CapturePolicy.Capture
            ? proc.StandardOutput.ReadToEndAsync()
            : Task.FromResult<string>("");
        var stderrTask = _stderr == CapturePolicy.Capture
            ? proc.StandardError.ReadToEndAsync()
            : Task.FromResult<string>("");

        bool exited;
        if (_timeout is { } t)
        {
            exited = proc.WaitForExit((int)t.TotalMilliseconds);
        }
        else
        {
            proc.WaitForExit();
            exited = true;
        }

        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new BetlException($"shell '{Id}': timed out after {_timeout}.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (proc.ExitCode != 0)
            throw new BetlException(
                $"shell '{Id}': exit code {proc.ExitCode}." +
                (string.IsNullOrEmpty(stderr) ? "" : $" stderr: {stderr.Trim()}"));

        if (!string.IsNullOrEmpty(stdout) && log is not null) log($"   [stdout] {stdout.TrimEnd()}");
        if (!string.IsNullOrEmpty(stderr) && log is not null) log($"   [stderr] {stderr.TrimEnd()}");
    }
}
