using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Betl.Core;

namespace Betl.Providers.Dotnet;

/// <summary>
/// Resolves a list of NuGet packages declared in YAML (as <c>id@version</c>
/// strings) to concrete .NET 9 DLL paths the Roslyn compiler can reference.
///
/// Implementation strategy: instead of programming directly against the
/// NuGet client API, we spawn <c>dotnet restore</c> against a synthetic
/// csproj and parse the resulting <c>obj/project.assets.json</c>. This
/// reuses the SDK's resolver verbatim, gets transitive deps for free, and
/// honors the user's <c>NuGet.config</c> (so private feeds Just Work).
///
/// Results are cached per package-set hash at
/// <c>%LOCALAPPDATA%\betl\package-cache\&lt;sha&gt;\resolved.txt</c> so
/// the second run of the same pipeline skips the SDK spawn entirely.
/// </summary>
public static class PackageResolver
{
    private const string TargetFramework = "net9.0";

    /// <summary>
    /// Resolve <paramref name="idAtVersionList"/> (e.g. <c>["Humanizer.Core@2.14.1"]</c>)
    /// to a flat, deduplicated list of absolute DLL paths suitable for
    /// passing to <see cref="DotnetCompiler"/> via <c>references:</c>.
    /// Empty input returns empty output.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> idAtVersionList, string contextLabel)
    {
        if (idAtVersionList.Count == 0) return Array.Empty<string>();

        var parsed = idAtVersionList.Select(s => Parse(s, contextLabel)).ToList();
        var cacheKey = Hash(string.Join("|",
            parsed.Select(p => $"{p.Id}@{p.Version}").OrderBy(s => s, StringComparer.Ordinal)));

        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "betl", "package-cache", cacheKey);
        var resolvedTxt = Path.Combine(cacheDir, "resolved.txt");

        if (File.Exists(resolvedTxt))
            return File.ReadAllLines(resolvedTxt).Where(File.Exists).ToList();

        Directory.CreateDirectory(cacheDir);
        WriteSyntheticCsproj(cacheDir, parsed);
        RunDotnetRestore(cacheDir, contextLabel);
        var dlls = ParseAssetsJson(cacheDir, contextLabel);

        File.WriteAllLines(resolvedTxt, dlls);
        return dlls;
    }

    private static (string Id, string Version) Parse(string raw, string contextLabel)
    {
        var at = raw.IndexOf('@');
        if (at <= 0 || at == raw.Length - 1)
            throw new BetlException(
                $"{contextLabel}: package '{raw}' must be in 'id@version' form (e.g. 'Humanizer.Core@2.14.1').");
        var id = raw[..at].Trim();
        var version = raw[(at + 1)..].Trim();
        if (id.Length == 0 || version.Length == 0)
            throw new BetlException(
                $"{contextLabel}: package '{raw}' has empty id or version.");
        return (id, version);
    }

    private static void WriteSyntheticCsproj(string dir, IReadOnlyList<(string Id, string Version)> packages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine($"    <TargetFramework>{TargetFramework}</TargetFramework>");
        sb.AppendLine("    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>");
        sb.AppendLine("    <RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>");
        sb.AppendLine("  </PropertyGroup>");
        sb.AppendLine("  <ItemGroup>");
        foreach (var (id, version) in packages)
            sb.AppendLine($"    <PackageReference Include=\"{id}\" Version=\"{version}\" />");
        sb.AppendLine("  </ItemGroup>");
        sb.AppendLine("</Project>");
        File.WriteAllText(Path.Combine(dir, "synthetic.csproj"), sb.ToString());
    }

    private static void RunDotnetRestore(string dir, string contextLabel)
    {
        var psi = new ProcessStartInfo("dotnet", "restore synthetic.csproj --nologo -v:quiet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new BetlException($"{contextLabel}: failed to start `dotnet restore` (is the .NET SDK on PATH?).");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new BetlException(
                $"{contextLabel}: `dotnet restore` failed (exit {p.ExitCode}).\n  stdout: {stdout.Trim()}\n  stderr: {stderr.Trim()}");
    }

    private static List<string> ParseAssetsJson(string dir, string contextLabel)
    {
        var assetsPath = Path.Combine(dir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            throw new BetlException(
                $"{contextLabel}: project.assets.json not found at '{assetsPath}' after restore.");

        using var fs = File.OpenRead(assetsPath);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;

        // packageFolders: { "C:\\Users\\foo\\.nuget\\packages\\": {} } — pick the first.
        var folders = root.GetProperty("packageFolders").EnumerateObject()
            .Select(p => p.Name).ToList();
        if (folders.Count == 0)
            throw new BetlException($"{contextLabel}: project.assets.json has no packageFolders.");

        var targets = root.GetProperty("targets");
        // The key is the full TFM moniker (e.g. ".NETCoreApp,Version=v9.0").
        // There's only one entry per restore in our case — take the first.
        var target = targets.EnumerateObject().FirstOrDefault();
        if (target.Value.ValueKind == JsonValueKind.Undefined)
            throw new BetlException($"{contextLabel}: project.assets.json has no targets.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var pkg in target.Value.EnumerateObject())
        {
            // pkg.Name = "Id/Version", pkg.Value has "compile" / "runtime" objects with relative DLL paths.
            if (!pkg.Value.TryGetProperty("runtime", out var runtime))
                continue;

            foreach (var entry in runtime.EnumerateObject())
            {
                var relPath = entry.Name; // e.g. "lib/net9.0/Humanizer.dll"
                if (relPath.EndsWith("_._", StringComparison.Ordinal)) continue;

                foreach (var folder in folders)
                {
                    var candidate = Path.Combine(folder, pkg.Name.Replace('/', Path.DirectorySeparatorChar), relPath);
                    if (File.Exists(candidate))
                    {
                        if (seen.Add(candidate)) result.Add(candidate);
                        break;
                    }
                }
            }
        }
        return result;
    }

    private static string Hash(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
