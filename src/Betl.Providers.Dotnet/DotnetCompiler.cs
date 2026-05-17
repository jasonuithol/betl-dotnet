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
    private static readonly Lazy<IReadOnlyList<MetadataReference>> References = new(LoadReferences);

    private const string ImplicitUsings = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Betl.Ssis.Shim.Script;
        using Betl.Ssis.Shim.Task;
        using Betl.Ssis.Shim.PipelineComponent;

        """;

    public static Type CompileAndFindSubclass<TBase>(string source, string contextLabel)
        => CompileAndFindSubclass(typeof(TBase), source, contextLabel);

    public static Type CompileAndFindSubclass(Type baseType, string source, string contextLabel)
    {
        var fullSource = ImplicitUsings + source;
        var key = $"{baseType.FullName}::" + Hash(fullSource);

        var asm = Cache.GetOrAdd(key, _ => Compile(fullSource, contextLabel));

        var match = asm.GetTypes()
            .FirstOrDefault(t => !t.IsAbstract && baseType.IsAssignableFrom(t))
            ?? throw new BetlException(
                $"{contextLabel}: user source did not declare any class deriving from {baseType.FullName}.");
        return match;
    }

    private static Assembly Compile(string fullSource, string contextLabel)
    {
        var tree = CSharpSyntaxTree.ParseText(fullSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: "betl-dyn-" + Guid.NewGuid().ToString("N")[..8],
            syntaxTrees: new[] { tree },
            references: References.Value,
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

    private static IReadOnlyList<MetadataReference> LoadReferences()
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

    private static string Hash(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
