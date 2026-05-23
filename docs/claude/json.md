# JSON: `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_OBJECT` / `JSON_ARRAY` / `JSON_PATH_EXISTS` / `ISJSON` / `OPENJSON`

Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions. JSON columns are plain `nvarchar(max)`.

`JSON_VALUE(json, path)` returns `nvarchar`. Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL. `strict $.foo` raises Msg 13608 on miss. NULL `json` or NULL path → NULL. JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`. Object/array matches → NULL in lax.

`JSON_QUERY(json, path)` returns `nvarchar` — complement of `JSON_VALUE`. Object/array match → raw JSON text via `JsonElement.GetRawText` (preserves the input's whitespace shape). Scalar match → NULL in lax, Msg 13624 in strict. Missing path → NULL in lax, Msg 13608 in strict. NULL `json` or NULL path → NULL. Two-arg form only (the 1-arg `JSON_QUERY(json)` shorthand for `JSON_QUERY(json, '$')` raises Msg 102 at parse). DACFx-emitted computed columns (WWI's `Application.People.OtherLanguages`, `Warehouse.StockItems.Tags`) always supply explicit paths. Pipes cleanly into `OPENJSON` for round-trip on extracted arrays.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar`. EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608). Bare `'$'` replaces the entire document. Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it. Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`).

`JSON_OBJECT([key : value [, ...]] [null_clause])` / `JSON_ARRAY([value [, ...]] [null_clause])` return `nvarchar(max)`. Probe-confirmed against SQL Server 2025 (2026-05-23). Default null clause is **ABSENT ON NULL** — NULL value-expressions are omitted; the explicit `NULL ON NULL` variant emits them as JSON `null`. The trailing keyword pair (`NULL ON NULL` / `ABSENT ON NULL`) is matched as `ReservedKeyword`s (`Null` + `On` + `Null` / `Absent` falls through `UnquotedString` since `ABSENT` isn't reserved). Empty argument list yields `{}` / `[]`. Duplicate keys preserved (no dedup, matching real SQL Server). NULL key raises **Msg 13638** at runtime; missing `:` separator, `=` instead of `:`, trailing comma, partial null-clause all raise Msg 102 at parse.

JSON_OBJECT's key parse needs the `:` separator to not collide with the `::` type-prefix postfix (hierarchyid / geography / geometry). Implementation: a `ParserContext.StopExpressionAtBareColon` flag, set transiently around the key parse, redirects the `Expression.Parse` postfix `:` case — single-colon rewinds and breaks out so the JSON_OBJECT body parser consumes the separator; double-colon still routes to `SpatialStaticCall` / `HierarchyIdStaticCall` unchanged. The flag is save/restored so a nested JSON_OBJECT inside another JSON_OBJECT's value position doesn't leak its key-parse state outward.

Value formatting matches real SQL Server byte-for-byte except float / real (documented quirk — simulator emits .NET `G15` / `G7`, real SQL Server emits `1.234e+000`). Specific mappings:
- `bit` → unquoted `true` / `false`
- integer / decimal / money — unquoted number
- `varbinary` / `binary` → base64-quoted (`"QUI="` for `0x4142`)
- `datetime` / `datetime2` / `smalldatetime` → quoted ISO with **T** separator (`"2025-01-15T12:34:56"`)
- `date` / `time` / `uniqueidentifier` → quoted default ISO / uppercase-hex
- other strings → JSON-escaped (`\"` `\\` `\b` `\f` `\n` `\r` `\t` `\uHHHH` for control chars; non-ASCII / `/` / `<` / `>` left literal)
- nested `JSON_OBJECT` / `JSON_ARRAY` / `JSON_QUERY` results — embedded **raw** (not re-quoted), via compile-time `JsonValueRender.ProducesJson(Expression)` detection that unwraps `Parenthesized`. Other strings — including `'{"x":1}'` literals — go through the quote-and-escape path, matching SQL Server's JSON-typed-input detection without needing an `SqlValue`-level marker bit.

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind. Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object. With WITH: each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`. `AS JSON` modifier → `NotSupportedException`. NULL/invalid JSON → zero rows under lax.

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`. Coercion via `SqlValue.CoerceTo`. Backed by `System.Text.Json`. JSON-path quoted-property escape `""` → literal `"`.

`JSON_PATH_EXISTS(json, path)` returns `int` (1 / 0 / NULL). Routes through the same `JsonPath.Walk` infrastructure as `JSON_VALUE` / `JSON_QUERY`: parses the path, walks the parsed `JsonDocument`, returns 1 if the path resolves to a node and 0 otherwise. NULL `json` or NULL `path` → NULL. Lax-mode invalid JSON → 0; strict-mode invalid JSON → Msg 13609.

`ISJSON(expression)` returns `int` (1 / 0 / NULL). Wraps `JsonDocument.Parse` in try/catch: NULL input → NULL, non-string input → 0 (real SQL Server raises Msg 8116 — the simulator's lax disposition is harmless for the CHECK-constraint use case), valid JSON object/array/scalar → 1, parse-fail → 0. The 2-arg shape (`VALUE | ARRAY | OBJECT | SCALAR` modifier) isn't modeled — DACFx-emitted CHECK constraints (`isjson([col])<>0`) only use the 1-arg form.

Not emitted by EF / not modeled: `FOR JSON PATH`/`AUTO`. Reachable only via raw SQL.
