# betl.dotnet

A pure-.NET implementation of [betl](https://github.com/jasonuithol/betl-native) — the
"Better ETL" YAML-driven ETL runtime — targeting **100% pipeline-file
compatibility** with `betl.native`.

**[▶ Try the live yaml-ui demo](https://jasonuithol.github.io/betl-tools/)** — the
betl-dotnet flavored full-coverage pipeline pre-loaded in the browser-based
viewer/editor; no install required.

The runtime-neutral contract (file format, step semantics, type system,
validation rules) is defined by [`SPEC_CORE.md`][spec-core] in the upstream
repo; `betl.dotnet` is a second reference implementation of that contract.

[spec-core]: https://github.com/jasonuithol/betl-native/blob/master/SPEC_CORE.md

## What's the same as `betl.native`

- YAML pipeline files are byte-for-byte interchangeable for the supported
  step type set.
- Apache Arrow logical types end-to-end via [`Apache.Arrow`][apache-arrow].
- `ssisexpr` is the portable expression language (the spec LCD).
- Spec-floor step types: `filter`, `map`, `lookup`, `join`, `aggregate`,
  `sort`, `union`, `distinct`, `limit`, `conditional_split`, `multicast`,
  `pivot`, `unpivot`, `dataflow`, `foreach`, plus the standard sink/source
  conventions (`*.upsert`, `*.query`, `csv.*`, `json.*`, `xlsx.*`,
  `xml.read`).
- Tasks: `sql.execute`, `shell`, `file.copy`/`move`/`delete`, `http.get`,
  `http.post`, `smtp.send`, `var.set` (literal + SQL modes).
- Reference-impl extensions: `audit` (metadata stamping),
  `postgres.copy` (binary COPY append-only fast path),
  `postgres.exec` (per-row SQL with `$N` positional binds),
  `mssql.bulkinsert` (SqlBulkCopy).
- Postgres + MSSQL providers (via Npgsql / Microsoft.Data.SqlClient — not
  libpq / unixODBC).
- `dotnet.task`, `dotnet.script`, `dotnet.pipelinecomponent` (the SSIS
  PipelineComponent shim is lifted from `providers/betl-dotnet/shim/`).

For migrating existing SSIS `.dtsx` packages to `.betl.yml`, use the
[`betl-dtsx2yaml`][dtsx2yaml] tool from the [betl-tools][betl-tools] repo
— a standalone .NET 9 project that runs cross-platform.

[betl-tools]: https://github.com/jasonuithol/betl-tools
[dtsx2yaml]: https://github.com/jasonuithol/betl-tools/tree/main/betl-dtsx2yaml

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

Active development. As of writing (Phase 10), the runtime covers the
spec-floor step set plus pivots, lookups, joins, JSON/Arrow/XML/XLSX I/O,
async + error-output `dotnet.pipelinecomponent`, the full lifted
`Microsoft.SqlServer.Dts.Pipeline.*` shim, parallel queued execution,
Postgres + MSSQL integration tests (including `postgres.copy`,
`postgres.exec`, and `mssql.bulkinsert`), BenchmarkDotNet harness, and
the three extensibility levels above. SSIS-EL has ~30 functions, ternary,
and the full `(DT_*)` cast lattice. 139/139 tests pass.

All step types that appear in upstream's `tests/integration/full-coverage/`
exercise pipeline (the most comprehensive single audit anchor) are now
implemented except `lua.*` — Lua is explicitly unsupported by design (use
`ssisexpr` or `dotnet.script` / `dotnet.task`).

Still deferred: full `RecordBatch` data-plane rewrite (gated on
`ssisexpr` type inference), `Decimal128` scale > 28, locale-aware
numeric parsing, `@[User::Foo]` variable refs in `ssisexpr`.

## Install

`Betl.Cli` is `<PackAsTool>`-packable as a .NET global tool. Until the
preview package is on NuGet, install from a local pack:

```sh
dotnet pack -c Release src/Betl.Cli/Betl.Cli.csproj
dotnet tool install -g \
    --add-source src/Betl.Cli/bin/Release \
    Betl.Dotnet --version 0.10.0-preview1
```

That installs the `betl-dotnet` command on PATH. The dash form
disambiguates from `betl.native`'s `betl` binary so the two runtimes
can coexist.

End-to-end install (runtime + tools + UI), see
[betl-tools/INSTALL.md][install]. It covers both the Linux/macOS
container path and this native-Windows path.

[install]: https://github.com/jasonuithol/betl-tools/blob/main/INSTALL.md

## Build

Requires the .NET 9 SDK (`global.json` pins 9.0.314).

```sh
dotnet build
dotnet test
dotnet run --project src/Betl.Cli -- run path/to/pipeline.betl.yml
```

## License

Apache-2.0, matching upstream.
