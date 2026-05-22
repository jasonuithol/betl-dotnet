# full-coverage example (betl-dotnet flavor)

A sprawling `pipeline.betl.yml` that exercises every dataflow and task
component the betl-dotnet runtime currently supports. Ported from
[upstream](https://github.com/.../betl-source) `tests/integration/full-coverage/`
with adaptations for the .NET runtime's deliberate Lua rejection.

Primary use: stress-test the **yaml-ui renderer** at
<http://127.0.0.1:8765/>. Secondary: run end-to-end against the local
dev Postgres + MSSQL instances.

## What it covers

**Connections** — postgres + mssql, DSNs sourced from parameters.
**Parameters** — `string`, `int64`, `date`, `bool`, plus DSN params.
**Control flow** — `dataflow`, `foreach (over:)`, task stages,
  `after:` ordering, `condition:` rendering, `on_failure:`.
**Expressions** — `lang: ssisexpr` (everywhere), `lang: literal`,
  plus `dotnet.script` (Roslyn-compiled C#) where row-level logic
  outgrows what ssisexpr expresses cleanly.
**Dataflow sources** — `csv.read`, `json.read`, `xml.read`,
  `postgres.read`, `mssql.read`, `xlsx.read`, `betl.gen_int64`,
  `betl.gen_strings`.
**Dataflow transforms** — `filter`, `map` (add + select), `audit`,
  `multicast`, `join`, `conditional_split`, `union`, `sort`, `distinct`,
  `limit`, `pivot`, `unpivot`, `aggregate`, `postgres.lookup`,
  `mssql.lookup`, `dotnet.script`, `postgres.exec`.
**Dataflow sinks** — `postgres.copy`, `postgres.upsert`, `mssql.upsert`,
  `mssql.bulkinsert`, `csv.write`, `xlsx.write`, `json.write`,
  `betl.count_rows`.
**Tasks** — `sql.execute`, `var.set` (literal + sql mode), `shell`,
  `file.copy`, `file.move`, `file.delete`, `http.get`, `http.post`,
  `smtp.send`, `dotnet.task`.

## Layout

```
.
|-- README.md             - this file
|-- pipeline.betl.yml     - the kitchen sink
|-- schemas.postgres.sql  - postgres DDL + seed rows
|-- schemas.mssql.sql     - mssql DDL + seed rows
|-- fixtures/             - inputs the pipeline reads
|   |-- orders-today.csv
|   |-- inventory.xml
|   |-- api-orders.ndjson
|   |-- http-body.json
|   |-- smtp-body.txt
|   |-- seed.txt
|-- out/                  - pipeline writes here at run time
|-- tmp/                  - file.copy / file.move / file.delete scratch
```

## Bootstrap

Local dev DBs already running (Postgres on `localhost:5433`, SQL Server
Express via `localhost\SQLEXPRESS`). Create the `betl_coverage`
database in each, then apply the DDL:

```powershell
# Postgres
createdb -h localhost -p 5433 -U postgres betl_coverage
psql      -h localhost -p 5433 -U postgres -d betl_coverage -f schemas.postgres.sql

# SQL Server (Windows auth)
sqlcmd -S localhost\SQLEXPRESS -E -Q "CREATE DATABASE betl_coverage"
sqlcmd -S localhost\SQLEXPRESS -E -d betl_coverage -i schemas.mssql.sql
```

## Run it

DSNs default to the local instances above. Only `batch_label` is
required (no default).

```powershell
# Render-only sanity check (JSON-schema + typed parse).
betl-dotnet validate examples/full-coverage/pipeline.betl.yml

# End-to-end run.
betl-dotnet run      examples/full-coverage/pipeline.betl.yml --param batch_label=local-001
```

### Via the yaml-ui

Open <http://127.0.0.1:8765/> (rooted at `C:\Users\sausage`), browse to
`betl-dotnet/examples/full-coverage/pipeline.betl.yml`, and use the
**run** dialog. `batch_label` is the only required param; everything
else has a sensible default.

The `http.get` / `http.post` / `smtp.send` stages are gated by
`condition: ${params.enable_remote}` and render but won't fire in
practice unless that param flips to true and the remote endpoints are
reachable. (See compat note about `condition:` evaluation below.)

## Mapping to upstream sections

Upstream's `pipeline.betl.yml` is structured A-N; we follow the same
layout. One-to-one mapping where possible:

| Upstream | This file | Notes |
| --- | --- | --- |
| A. Bootstrap         | `clear_pg_staging` ... `count_customers` | identical shape |
| A'. gen + postgres.copy | `load_gen_scratch`                    | lua expr swapped for ssisexpr string concat (`"scratch-" + (DT_WSTR, 32) seq`) |
| B. CSV ingest        | `load_csv_orders`                        | `multicast.taps` -> `outputs`; lua math -> ssisexpr |
| B'. Audit fan        | `audit_csv_load`                         | identical shape |
| C. NDJSON -> mssql   | `load_ndjson_to_bulk`                    | lua `tonumber` + `string.sub` chain replaced by a `dotnet.script` block (Roslyn C#) |
| D. XML -> postgres   | `load_xml_vendor_metrics`                | lua tonumber -> ssisexpr `(DT_I4)`; two map stages (add + select) instead of one mixed-mode map |
| E. Analytics daily   | `build_analytics_daily`                  | identical shape |
| F. mssql round-trip  | `build_order_facts`                      | quarter expression rewritten using SUBSTRING + DT_I4 (no MONTH() in ssisexpr) |
| G. SCD routing       | `classify_routes`                        | `join` uses `left:`/`right:` (not `from: [..]`); `conditional_split` uses `default_case:` and `cases:` is a mapping; tier classification reworked into two booleans because ssisexpr lacks chained `or` over string literals |
| H. Reshape           | `reshape_demo`                           | region/month spread done in `dotnet.script` (ssisexpr can't index arrays); `pivot.id_cols` -> `pivot.pivot_keys` (different semantics, see compat gaps); `aggregate.group_by` accepts string keys (no int64 floor like upstream) |
| I. xlsx round-trip   | `xlsx_roundtrip`                         | identical shape |
| J. http + smtp       | `fetch_http`, `post_http`, `notify_email`| condition fields are inert in current runtime (see gap) |
| K. File ops + shell  | `copy_seed` ... `run_shell`              | shell argv changed to cmd.exe rem for Windows; swap for `/bin/true` on Linux |
| L. Foreach           | `per_sku`                                | only the literal `over:` form is supported by the .NET loader; `over_glob:` and `over_query:` are not ported (see compat gaps) |
| M. lua.task / dotnet.task | `dotnet_hello`                      | lua.task dropped (no lua provider in dotnet); kept the dotnet.task |
| N. Reporting         | `write_run_summary`, `close_audit`       | identical shape |

## Compat gaps surfaced during the port

These are intentionally NOT worked around in the YAML — they're listed
here so the user can decide whether to fix them upstream. Every gap is
also flagged at its use-site via an inline comment in the YAML.

### Loader-side

1. **`foreach` only accepts literal `over:` lists.** No `over_glob:` (file
   pattern enumerator) and no `over_query:` (SQL recordset enumerator).
   The upstream pipeline uses both for sections L2 and L3; we ported
   only L1. The loader currently has no codepath that recognises those
   keys at all.

2. **`condition:` is parsed but never evaluated.** The runtime accepts
   `condition: <expr>` on any step, stores it on the typed record, but
   no executor branch consults `step.Condition` before running. So
   the `enable_remote` gate on `http.get` / `http.post` / `smtp.send`
   renders correctly in the UI but does NOT actually skip the steps
   at run time. (Upstream betl-native does honor this.)

3. **`pivot.id_cols` is unrecognised; semantics differ.** Upstream
   splits pivot config into `id_cols:` (grouping) + `pivot_keys:`
   (the declared value-axis names). betl-dotnet collapses both into
   `pivot_keys:` and *auto-discovers* the value-axis names from the
   row stream. The `pivot_values:` opt-in upstream is mirrored here
   but the key name is the same. No `id_cols:` key exists at all.

4. **`unpivot.id_cols` is unrecognised.** Same story — betl-dotnet's
   unpivot has no `id_cols:`; it preserves all non-`value_cols:` columns
   automatically.

5. **`http.get` / `http.post` don't accept `timeout:` on the parser
   side.** The schema declares it but the typed parser rejects unknown
   step keys, so adding `timeout: "10s"` raises "unknown key 'timeout'".
   (Workaround: drop the `timeout:` line — done in this port.) Actually
   the common `timeout:` IS consumed at step level via `ConsumeCommonStepKeys`,
   so this might already work. Re-test if you care.

### Validator-side (JSON Schema)

6. **The embedded `pipeline.schema.json` is severely out of date.** It
   does NOT recognise:
   - `lang: ssisexpr` (only `lua` / `python` / `literal`)
   - Step types: `var.set`, `multicast`, `conditional_split`, `distinct`,
     `limit`, `pivot`, `unpivot`, `audit`, `xml.read`, `xlsx.read`,
     `xlsx.write`, `dotnet.task`, `dotnet.script`,
     `dotnet.pipelinecomponent`, `betl.gen_int64`, `betl.gen_strings`,
     `betl.count_rows`, `postgres.copy`, `postgres.exec`,
     `mssql.bulkinsert`
   - The `*.read` source convention (only `*.query`)

   Result: `betl-dotnet validate examples/full-coverage/pipeline.betl.yml`
   emits ~11k schema errors and exits 1, even though the typed parser
   accepts the file cleanly. The same is true of the existing
   conformance fixtures (`phase1/`, `phase2/union-multi/`,
   `phase3/sqlite/`, ...) — they all fail `validate` today.

   Until the schema is updated, the only way to typed-parse-validate
   a betl-dotnet fixture is to bypass `SchemaValidator` and call
   `PipelineLoader.Load(...)` directly.

### Runtime-side

7. **`var.set` query mode requires a single-column scalar result.** Tested
   working with `SELECT count(*) FROM ...`. Returning multiple columns
   throws at runtime (not validate time).

### Expression-language gaps (ssisexpr vs lua)

8. **No string-table indexing in ssisexpr.** Upstream uses
   `({"EU","NA","AP"})[(seq % 3) + 1]` — port required a `dotnet.script`
   step to do the array lookup. Workaroundable but verbose.

9. **No chained `or` over string literals in ssisexpr.** Upstream's
   tier classification `(row.on_hand or 0) < 50 and "low_stock" or ...`
   doesn't translate cleanly — the ternary `?:` works for two cases
   but not three with string outputs. Port uses two boolean derived
   columns + a `conditional_split` with `default_case:` instead.

10. **No MONTH() / DAY() / YEAR() in ssisexpr.** Upstream's
    `MONTH((DT_DBDATE) order_date)` -> ported as
    `(DT_I4) SUBSTRING(order_date, 6, 2)` against the `YYYY-MM-DD` text.

11. **Param expansion does NOT happen inside `expr:` strings.** Wanted
    to write `where: { lang: ssisexpr, expr: 'qty >= ${params.qty_floor}' }`
    but `${params.X}` is left literal, so the ssisexpr engine fails.
    Workaround: hard-code the value or use `condition:` (but see gap #2).

## Iterate locally

If you change the YAML, re-validate via the typed parser only (since
the JSON schema is broken — gap #6):

```powershell
# 1. Fast: bash one-liner through the typed-parse harness shipped in this
#    repo (creates a tiny dotnet project at first call).
# 2. Slow: run `betl-dotnet validate ...` and grep past schema noise:
betl-dotnet validate examples/full-coverage/pipeline.betl.yml 2>&1 |
    Select-String -NotMatch "^  -" -CaseSensitive
```
