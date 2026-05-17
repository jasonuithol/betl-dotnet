using Betl.Core;

namespace Betl.Components.Tasks;

internal static class HttpHelpers
{
    /// <summary>Process-wide client; HttpClient is thread-safe and meant to be reused.</summary>
    public static readonly HttpClient SharedClient = new();

    public static void AddHeaders(HttpRequestMessage req, IReadOnlyList<string>? headers)
    {
        if (headers is null) return;
        foreach (var line in headers)
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
                throw new BetlException($"Invalid header (no ':'): \"{line}\".");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            // Content headers go on Content; everything else on the request.
            if (!req.Headers.TryAddWithoutValidation(name, value))
            {
                req.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    public static async Task SaveResponseAsync(HttpResponseMessage resp, string savePath, string stepId)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var preview = await resp.Content.ReadAsStringAsync();
            if (preview.Length > 500) preview = preview[..500] + "...(truncated)";
            throw new BetlException(
                $"http '{stepId}': non-2xx status {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                $"body preview: {preview}");
        }
        var dir = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await using var fs = File.Create(savePath);
        await resp.Content.CopyToAsync(fs);
    }
}

public sealed class HttpGetTask(string id, string url, string saveTo,
    IReadOnlyList<string>? headers, TimeSpan? timeout) : IControlTask
{
    public string Id { get; } = id;

    public void Execute(Action<string>? log)
    {
        log?.Invoke($"   GET {url} -> {saveTo}");
        using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        HttpHelpers.AddHeaders(req, headers);
        try
        {
            using var resp = HttpHelpers.SharedClient.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            HttpHelpers.SaveResponseAsync(resp, saveTo, Id).GetAwaiter().GetResult();
        }
        catch (TaskCanceledException) when (cts.IsCancellationRequested)
        {
            throw new BetlException($"http.get '{Id}': timed out after {timeout}.");
        }
    }
}

public sealed class HttpPostTask(string id, string url, string saveTo,
    string? body, string? bodyFile, IReadOnlyList<string>? headers, TimeSpan? timeout) : IControlTask
{
    public string Id { get; } = id;

    public void Execute(Action<string>? log)
    {
        log?.Invoke($"   POST {url} -> {saveTo}");
        using var cts = timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        if (bodyFile is not null)
            req.Content = new ByteArrayContent(File.ReadAllBytes(bodyFile));
        else if (body is not null)
            req.Content = new StringContent(body);
        else
            req.Content = new StringContent("");

        HttpHelpers.AddHeaders(req, headers);

        try
        {
            using var resp = HttpHelpers.SharedClient.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            HttpHelpers.SaveResponseAsync(resp, saveTo, Id).GetAwaiter().GetResult();
        }
        catch (TaskCanceledException) when (cts.IsCancellationRequested)
        {
            throw new BetlException($"http.post '{Id}': timed out after {timeout}.");
        }
    }
}
