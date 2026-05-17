using Betl.Core;

namespace Betl.Components.Tasks;

public sealed class FileCopyTask(string id, string src, string dst) : IControlTask
{
    public string Id { get; } = id;
    public void Execute(Action<string>? log)
    {
        var dstDir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
        File.Copy(src, dst, overwrite: true);
        log?.Invoke($"   copied {src} -> {dst}");
    }
}

public sealed class FileMoveTask(string id, string src, string dst) : IControlTask
{
    public string Id { get; } = id;
    public void Execute(Action<string>? log)
    {
        var dstDir = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
        if (File.Exists(dst)) File.Delete(dst);
        File.Move(src, dst);
        log?.Invoke($"   moved {src} -> {dst}");
    }
}

public sealed class FileDeleteTask(string id, string path) : IControlTask
{
    public string Id { get; } = id;
    public void Execute(Action<string>? log)
    {
        if (!File.Exists(path)) throw new BetlException($"file.delete '{Id}': '{path}' does not exist.");
        File.Delete(path);
        log?.Invoke($"   deleted {path}");
    }
}
