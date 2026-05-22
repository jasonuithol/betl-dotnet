# betl.dotnet

A pure-.NET implementation of [betl](https://github.com/jasonuithol/betl) — the
"Better ETL" YAML-driven ETL runtime — targeting **100% pipeline-file
compatibility** with `betl.native`.

The runtime-neutral contract (file format, step semantics, type system,
validation rules) is defined by [`SPEC_CORE.md`][spec-core] in the upstream
repo; `betl.dotnet` is a second reference implementation of that contract.

[spec-core]: https://github.com/jasonuithol/betl/blob/main/SPEC_CORE.md

## What's the same as `betl.native`

- YAML pipeline files are byte-for-byte interchangeable for the supported
  step type set.
- Apache Arrow logical types end-to-end via [`Apache.Arrow`][apache-arrow].
- `ssisexpr` is the portable expression language (the spec LCD).
- Spec-floor step types: `filter`, `map`, `lookup`, `join`, `aggregate`,
  `sort`, `union`, `distinct`, `limit`, `conditional_split`, `multicast`,
  `pivot`, `unpivot`, `dataflow`, `foreach`, plus the standard sink/source
  conventions (`*.upsert`, `*.query`, `csv.*`, `json.*`).
- Tasks: `sql.execute`, `shell`, `file.copy`/`move`/`delete`, `http.get`,
  `http.post`, `smtp.send`.
- Postgres + MSSQL providers (via Npgsql / Microsoft.Data.SqlClient — not
  libpq / unixODBC).
- `dotnet.task`, `dotnet.script`, `dotnet.pipelinecomponent` (the SSIS
  PipelineComponent shim is lifted from `providers/betl-dotnet/shim/`).

For migrating existing SSIS `.dtsx` packages to `.betl.yml`, use the
[`betl-dtsx2yaml`][dtsx2yaml] tool from the upstream repo — it's already
a standalone .NET 9 project and runs cross-platform.

[dtsx2yaml]: https://github.com/jasonuithol/betl/tree/master/tools/betl-dtsx2yaml

[apache-arrow]: https://github.com/apache/arrow/tree/main/csharp

## What's different

- **No Lua.** `lua.map`, `lua.script`, `lua.task`, `lua_init`, and the
  `lang: lua` engine are explicitly unsupported. A YAML file that
  references them fails validation with a clear error naming Lua as
  the missing piece. Use `ssisexpr` or `dotnet.script`/`dotnet.task`.
- **No native plugin ABI.** Providers are `.NET` assemblies loaded through
  `AssemblyLoadContext`; the C ABI / `dlopen` model is gone.
- **Cross-platform first.** Windows, Linux, macOS — anywhere .NET 9
  runs.

## Extending with .NET

`betl.dotnet` ships three levels of .NET extensibility, in increasing
scope. The first two add fields to the existing `dotnet.task`,
`dotnet.script`, and `dotnet.pipelinecomponent` step types; the third
introduces brand-new step types from external assemblies.

### `references:` — point at DLLs on disk

Add managed DLL paths to the Roslyn compile so inline C# can name types
that aren't in the .NET 9 runtime. Paths are CWD-relative with `~/`
expansion, and `${params.X}` substitution is applied at executor time.

```yaml
- id: greet
  type: dotnet.script
  from: gen
  references:
    - ./libs/MyHelper.dll
    - ${params.shared_dll}
  output_schema:
    - { name: n,       type: int64 }
    - { name: message, type: string }
  source: |
    using My.Helper;
    public class GreetScript : BetlScript { ... }
```

Each path is PE-validated and `LoadFromAssemblyPath`-ed into the
default `AssemblyLoadContext` so JIT-time type resolves succeed, not
just compile-time ones.

### `packages:` — pull from NuGet

Declare NuGet packages directly in YAML. Exact version pinning is
required (no floating) for pipeline reproducibility. Transitive deps
and the user's `NuGet.config` (private feeds, etc.) are honored because
resolution shells out to `dotnet restore` against a synthetic csproj
and parses `obj/project.assets.json`.

```yaml
- id: pluralize
  type: dotnet.script
  from: gen
  packages:
    - Humanizer.Core@2.14.1
  output_schema:
    - { name: n,    type: int64 }
    - { name: text, type: string }
  source: |
    using Humanizer;
    public class PluralScript : BetlScript
    {
        public override IEnumerable<object?[]> OnRow(IReadOnlyList<object?> row)
        {
            long n = (long)row[0]!;
            yield return new object?[] { n, "item".ToQuantity((int)n) };
        }
    }
```

First run requires network. Resolved DLL paths are cached at
`%LOCALAPPDATA%\betl\package-cache\<sha-of-package-set>\` (and the
equivalent under `$XDG_DATA_HOME` on Linux/macOS) so subsequent runs
skip the SDK spawn entirely. `packages:` and `references:` compose
freely.

### External component plugins

When inline-C# isn't enough, ship whole new step types as a plugin DLL.
Any managed assembly in `./plugins/` (CWD-relative) or in any path
listed in `$BETL_PLUGINS` (`;`-separated on Windows, `:` on Unix) is
scanned at startup. Non-abstract classes implementing
`IBetlComponentPlugin` (data-flow component) or `IBetlTaskPlugin`
(control-flow task) register the YAML step types they handle.

```csharp
// MyPlugin.dll, dropped into ./plugins/
using Betl.Components;
using Betl.Core;

public sealed class UpperPlugin : IBetlComponentPlugin
{
    public string StepType => "myco.upper";

    public IDataComponent Create(
        string stepId,
        IReadOnlyDictionary<string, object?> body,
        IDataComponent? upstream,
        IReadOnlyDictionary<string, object?> resolvedParams)
    {
        if (upstream is null)
            throw new BetlException($"myco.upper '{stepId}': requires from:");
        var col = (string)body["column"]!;
        return new UpperComponent(stepId, upstream, col);
    }
}
```

```yaml
- id: shout
  type: myco.upper      # step type contributed by the plugin
  from: gen
  column: word
```

`betl run` and `betl validate` both call `PluginRegistry.Discover()`,
so YAML validation and execution agree on which step types are legal.
Duplicate `StepType` registration across plugins is a startup error.

## Status

Active development. As of writing (Phase 9), the runtime covers the
spec-floor step set plus pivots, lookups, joins, JSON/Arrow I/O,
async + error-output `dotnet.pipelinecomponent`, the full lifted
`Microsoft.SqlServer.Dts.Pipeline.*` shim, parallel
queued execution, Postgres + MSSQL integration tests, BenchmarkDotNet
harness, and the three extensibility levels above. SSIS-EL has ~30
functions, ternary, and the full `(DT_*)` cast lattice. 127/127 tests
pass.

Still deferred: full `RecordBatch` data-plane rewrite (gated on
`ssisexpr` type inference), `Decimal128` scale > 28, locale-aware
numeric parsing, `@[User::Foo]` variable refs in `ssisexpr`.

## Build

Requires the .NET 9 SDK (`global.json` pins 9.0.314).

```sh
dotnet build
dotnet test
dotnet run --project src/Betl.Cli -- run path/to/pipeline.betl.yml
```

## License

Apache-2.0, matching upstream.
