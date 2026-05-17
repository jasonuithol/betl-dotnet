# betl.dotnet

A pure-.NET implementation of [betl](https://github.com/jasonuithol/betl) — the
"Better ETL" YAML-driven ETL runtime — targeting **100% pipeline-file
compatibility** with `betl.linux`.

The runtime-neutral contract (file format, step semantics, type system,
validation rules) is defined by [`SPEC_CORE.md`][spec-core] in the upstream
repo; `betl.dotnet` is a second reference implementation of that contract.

[spec-core]: https://github.com/jasonuithol/betl/blob/main/SPEC_CORE.md

## What's the same as `betl.linux`

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
- `betl-dtsx2yaml` (lifted from `tools/betl-dtsx2yaml/`).

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

## Status

Pre-alpha. Phase 1 scope: load a YAML pipeline, validate it against
`pipeline.schema.json`, and execute the minimal slice `csv.read → filter
→ map → csv.write` with `literal` + a subset of `ssisexpr`.

## Build

Requires the .NET 9 SDK (`global.json` pins 9.0.314).

```sh
dotnet build
dotnet test
dotnet run --project src/Betl.Cli -- run path/to/pipeline.betl.yml
```

## License

Apache-2.0, matching upstream.
