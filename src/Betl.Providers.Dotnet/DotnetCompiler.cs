using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Betl.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Betl.Providers.Dotnet;

/// <summary>
/// Roslyn-based on-the-fly C# compiler. Compiles a user-supplied source string
/// to an in-memory assembly and returns the first class deriving from
/// <typeparamref name="TBase"/>. Compiled assemblies are cached per
/// (Source-SHA256, TBase) so repeated runs of the same pipeline don't recompile.
/// </summary>
public static class DotnetCompiler
{
    private static readonly ConcurrentDictionary<string, Assembly> Cache = new();
    private static readonly Lazy<IReadOnlyList<MetadataReference>> BaseReferences = new(LoadBaseReferences);

    private const string ImplicitUsings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Betl.Ssis.Shim.Script;
        using Betl.Ssis.Shim.Task;
        using Betl.Ssis.Shim.PipelineComponent;
        using Microsoft.SqlServer.Dts.Pipeline;
        using Microsoft.SqlServer.Dts.Pipeline.Wrapper;

        """;

    public static Type CompileAndFindSubclass<TBase>(string source, string contextLabel,
        IReadOnlyList<string>? extraReferencePaths = null)
        => CompileAndFindSubclass(typeof(TBase), source, contextLabel, extraReferencePaths);

    public static Type CompileAndFindSubclass(Type baseType, string source, string contextLabel,
        IReadOnlyList<string>? extraReferencePaths = null)
    {
        var fullSource = ImplicitUsings + source;
        var extras = ResolveExtraReferences(extraReferencePaths, contextLabel);
        var key = $"{baseType.FullName}::" + Hash(fullSource) + "::" + ExtrasKey(extras);

        var asm = Cache.GetOrAdd(key, _ => Compile(fullSource, contextLabel, extras));

        var match = asm.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && baseType.IsAssignableFrom(t))
            ?? throw new BetlException(
                $"{contextLabel}: user source did not declare any class deriving from {baseType.FullName}.");
        return match;
    }

    /// <summary>
    /// Compile once and return the first non-abstract class deriving from ANY
    /// of <paramref name="baseTypes"/>. Used by <c>dotnet.pipelinecomponent</c>
    /// to accept either the simple <c>BetlPipelineComponent</c> base or the
    /// full SSIS-compat <c>Microsoft.SqlServer.Dts.Pipeline.PipelineComponent</c>.
    /// </summary>
    public static (Type Type, Type MatchedBase) CompileAndFindAnyOf(
        IReadOnlyList<Type> baseTypes, string source, string contextLabel,
        IReadOnlyList<string>? extraReferencePaths = null)
    {
        var fullSource = ImplicitUsings + source;
        var extras = ResolveExtraReferences(extraReferencePaths, contextLabel);
        var key = "any::" + Hash(fullSource) + "::" + ExtrasKey(extras);
        var asm = Cache.GetOrAdd(key, _ => Compile(fullSource, contextLabel, extras));

        foreach (var t in asm.GetTypes())
        {
            if (t.IsAbstract) continue;
            foreach (var b in baseTypes)
                if (b.IsAssignableFrom(t)) return (t, b);
        }
        throw new BetlException(
            $"{contextLabel}: user source did not declare any class deriving from " +
            $"{string.Join(" or ", baseTypes.Select(b => b.FullName))}.");
    }

    private static Assembly Compile(string fullSource, string contextLabel,
        IReadOnlyList<MetadataReference> extras)
    {
        var tree = CSharpSyntaxTree.ParseText(fullSource);
        var allRefs = extras.Count == 0
            ? BaseReferences.Value
            : (IEnumerable<MetadataReference>)BaseReferences.Value.Concat(extras);

        var compilation = CSharpCompilation.Create(
            assemblyName: "betl-dyn-" + Guid.NewGuid().ToString("N")[..8],
            syntaxTrees: new[] { tree },
            references: allRefs,
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            var msgs = string.Join("\n  ", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => $"({d.Location.GetLineSpan().StartLinePosition.Line + 1},{d.Location.GetLineSpan().StartLinePosition.Character + 1}): {d.GetMessage()}"));
            throw new BetlException($"{contextLabel}: C# compile errors:\n  {msgs}");
        }
        ms.Seek(0, SeekOrigin.Begin);
        return AssemblyLoadContext.Default.LoadFromStream(ms);
    }

    private static IReadOnlyList<MetadataReference> LoadBaseReferences()
    {
        var refs = new List<MetadataReference>();
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new BetlException("Cannot locate .NET runtime directory.");

        // Use PEReader to verify each .dll has managed metadata before referencing.
        // The runtime dir also contains native DLLs (clrjit, clrgc, hostfxr, ...)
        // that Roslyn can't load as MetadataReferences and which raise compile-time
        // diagnostics if added blindly.
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
        {
            try
            {
                using (var fs = File.OpenRead(dll))
                using (var pe = new PEReader(fs))
                {
                    if (!pe.HasMetadata) continue;
                }
                refs.Add(MetadataReference.CreateFromFile(dll));
            }
            catch { /* skip unloadable */ }
        }

        // Our shim assemblies — accessible from user source via the implicit usings.
        var shimAsm = typeof(Betl.Ssis.Shim.Script.BetlScript).Assembly;
        refs.Add(MetadataReference.CreateFromFile(shimAsm.Location));

        return refs;
    }

    private static IReadOnlyList<MetadataReference> ResolveExtraReferences(
        IReadOnlyList<string>? paths, string contextLabel)
    {
        if (paths is null || paths.Count == 0) return Array.Empty<MetadataReference>();

        var list = new List<MetadataReference>(paths.Count);
        foreach (var raw in paths)
        {
            var expanded = raw.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), raw[2..])
                : raw;
            var full = Path.GetFullPath(expanded);

            if (!File.Exists(full))
                throw new BetlException(
                    $"{contextLabel}: reference '{raw}' not found (resolved to '{full}').");
            try
            {
                using var fs = File.OpenRead(full);
                using var pe = new PEReader(fs);
                if (!pe.HasMetadata)
                    throw new BetlException(
                        $"{contextLabel}: reference '{raw}' (resolved '{full}') is not a managed .NET assembly.");
            }
            catch (BadImageFormatException ex)
            {
                throw new BetlException(
                    $"{contextLabel}: reference '{raw}' (resolved '{full}') is not a valid PE file: {ex.Message}");
            }
            list.Add(MetadataReference.CreateFromFile(full));

            // Roslyn only needs metadata to compile, but the CLR also needs the
            // assembly loaded so JIT-time resolves of types from the reference
            // succeed. Loading into the Default ALC is idempotent: if the
            // assembly is already there (same FullName) the existing instance
            // is returned.
            try { AssemblyLoadContext.Default.LoadFromAssemblyPath(full); }
            catch (FileLoadException) { /* already loaded — fine */ }
        }
        return list;
    }

    private static string ExtrasKey(IReadOnlyList<MetadataReference> extras)
    {
        if (extras.Count == 0) return "0";
        // Stable cache key: sort by file path so order in YAML doesn't fragment the cache.
        var paths = extras.Select(r => r.Display ?? "").OrderBy(s => s, StringComparer.Ordinal);
        return Hash(string.Join("|", paths));
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
