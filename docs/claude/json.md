# JSON: `JSON_VALUE` / `JSON_MODIFY` / `OPENJSON`

Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions. JSON columns are plain `nvarchar(max)`.

`JSON_VALUE(json, path)` returns `nvarchar`. Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL. `strict $.foo` raises Msg 13608 on miss. NULL `json` or NULL path → NULL. JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`. Object/array matches → NULL in lax.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar`. EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608). Bare `'$'` replaces the entire document. Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it. Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`).

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind. Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object. With WITH: each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`. `AS JSON` modifier → `NotSupportedException`. NULL/invalid JSON → zero rows under lax.

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`. Coercion via `SqlValue.CoerceTo`. Backed by `System.Text.Json`. JSON-path quoted-property escape `""` → literal `"`.

Not emitted by EF / not modeled: `JSON_QUERY`, `ISJSON`, `FOR JSON PATH`/`AUTO`. Reachable only via raw SQL.
