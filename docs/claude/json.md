# JSON: `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_OBJECT` / `JSON_ARRAY` / `JSON_PATH_EXISTS` / `ISJSON` / `OPENJSON`

Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions.
JSON columns are plain `nvarchar(max)`.

`JSON_VALUE(json, path)` returns `nvarchar(4000)`.
Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL.
`strict $.foo` raises Msg 13608 on miss.
NULL `json` or NULL path → NULL.
JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`.
Object/array matches → NULL in lax.
**A scalar string longer than 4000 chars → SQL NULL in lax** (probe-confirmed against SQL Server 2025: 4000 → value, 4001 → NULL); enforcing the cap also keeps the length-0 result within the bounded TDS length prefix, so a multi-KB extracted value can't overflow it.

`JSON_QUERY(json, path)` returns `nvarchar(max)` — complement of `JSON_VALUE`.
Object/array match → raw JSON text via `JsonElement.GetRawText` (preserves the input's whitespace shape).
Scalar match → NULL in lax, Msg 13624 in strict.
Missing path → NULL in lax, Msg 13608 in strict.
NULL `json` or NULL path → NULL.
Two-arg form only (the 1-arg `JSON_QUERY(json)` shorthand for `JSON_QUERY(json, '$')` raises Msg 102 at parse).
DACFx-emitted computed columns (WWI's `Application.People.OtherLanguages`, `Warehouse.StockItems.Tags`) always supply explicit paths.
Pipes cleanly into `OPENJSON` for round-trip on extracted arrays.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar(max)`.
EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608).
Bare `'$'` replaces the entire document.
Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it.
Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`).

`JSON_OBJECT([key : value [, ...]] [null_clause])` / `JSON_ARRAY([value [, ...]] [null_clause])` return `nvarchar(max)`.
Probe-confirmed against SQL Server 2025.
The default null clause is **builder-specific**: `JSON_OBJECT` defaults to **NULL ON NULL** (NULL values emit JSON `null`), while `JSON_ARRAY` defaults to **ABSENT ON NULL** (NULL elements omitted).
Microsoft documents the `JSON_OBJECT` default verbatim ("The default setting for this option is `NULL ON NULL`"); note it is the *opposite* of the `FOR JSON` clause, which omits NULL properties unless `INCLUDE_NULL_VALUES` is given — an earlier probe note had `JSON_OBJECT` wrong (claimed ABSENT) by conflating the two surfaces, fixed 2026-05-27.
The trailing keyword pair (`NULL ON NULL` / `ABSENT ON NULL`) is matched as `ReservedKeyword`s (`Null` + `On` + `Null` / `Absent` falls through `UnquotedString` since `ABSENT` isn't reserved).
Empty argument list yields `{}` / `[]`.
Duplicate keys preserved (no dedup, matching real SQL Server).
NULL key raises **Msg 13638** at runtime; missing `:` separator, `=` instead of `:`, trailing comma, partial null-clause all raise Msg 102 at parse.

`JSON_ARRAYAGG(value [ORDER BY ...] [null_clause])` / `JSON_OBJECTAGG(key : value [null_clause])` are the aggregate forms, both returning `nvarchar(max)`.
They reuse `JsonValueRender` for element/value formatting (including raw embedding of nested `JSON_OBJECT` / `JSON_ARRAY` / `JSON_QUERY`) and follow the scalar builders' null-clause defaults (`JSON_ARRAYAGG` → ABSENT ON NULL, `JSON_OBJECTAGG` → NULL ON NULL).
**Empty input (zero rows) → SQL NULL; a group with rows whose values are all absent → `[]` / `{}`** (the aggregators track row count independently of emitted fragments).
Grammar specifics, probe-confirmed: `JSON_ARRAYAGG`'s `ORDER BY` sits *inside* the parentheses (not `WITHIN GROUP`) and is mutually exclusive with `OVER` (the combination raises Msg 156); `JSON_OBJECTAGG` accepts neither an `ORDER BY` (Msg 156) nor the SQL-standard `key VALUE value` form (Msg 102), and raises **Msg 13638** on a NULL key.
Both support `OVER (...)` windows — `PARTITION BY`, running `ORDER BY`, and explicit `ROWS` frames all ride the standard aggregate-window path.
`JSON_OBJECTAGG`'s per-row key (which the generic value-only aggregator contract can't carry) is set via a `SetKey` side-channel before each `Add`, mirroring `STRING_AGG`'s separator handling; in the window executor it gets a dedicated walk (`ComputeJsonObjectAggWindow`) since the key isn't part of the pre-evaluated operand stream.
The aggregators build the closing `]` / `}` onto a snapshot rather than mutating the running buffer, so repeated `Result()` calls across sliding-window frames stay correct.
`DISTINCT` is not accepted by either.

All the `nvarchar(max)` JSON producers (`JSON_QUERY`, `JSON_MODIFY`, `JSON_OBJECT`, `JSON_ARRAY`, `JSON_ARRAYAGG`, `JSON_OBJECTAGG`) are typed `SqlType.NVarcharMax` at both `GetSqlType` and `Run` — not the length-0 `SqlType.NVarchar` "size from value" form.
This is load-bearing over the TDS wire: a length-0 result over 32,767 chars overflows the codec's bounded 2-byte length prefix, whereas a MAX result streams as PLP.
`JSON_VALUE` stays bounded (`nvarchar(4000)`) and is safe by its 4000-char cap.
See [`tds-endpoint.md`](tds-endpoint.md) for the wire mechanism.

JSON_OBJECT's key parse needs the `:` separator to not collide with the `::` type-prefix postfix (hierarchyid / geography / geometry).
Implementation: a `ParserContext.StopExpressionAtBareColon` flag, set transiently around the key parse, redirects the `Expression.Parse` postfix `:` case — single-colon rewinds and breaks out so the JSON_OBJECT body parser consumes the separator; double-colon still routes to `SpatialStaticCall` / `HierarchyIdStaticCall` unchanged.
The flag is save/restored so a nested JSON_OBJECT inside another JSON_OBJECT's value position doesn't leak its key-parse state outward.

Value formatting matches real SQL Server byte-for-byte except float / real (documented quirk — simulator emits .NET `G15` / `G7`, real SQL Server emits `1.234e+000`).
Specific mappings:
- `bit` → unquoted `true` / `false`
- integer / decimal / money — unquoted number
- `varbinary` / `binary` → base64-quoted (`"QUI="` for `0x4142`)
- `datetime` / `datetime2` / `smalldatetime` → quoted ISO with **T** separator (`"2025-01-15T12:34:56"`)
- `date` / `time` / `uniqueidentifier` → quoted default ISO / uppercase-hex
- other strings → JSON-escaped (`\"` `\\` `\b` `\f` `\n` `\r` `\t` `\uHHHH` for control chars; non-ASCII / `/` / `<` / `>` left literal)
- nested `JSON_OBJECT` / `JSON_ARRAY` / `JSON_QUERY` results — embedded **raw** (not re-quoted), via compile-time `JsonValueRender.ProducesJson(Expression)` detection that unwraps `Parenthesized`.
  Other strings — including `'{"x":1}'` literals — go through the quote-and-escape path, matching SQL Server's JSON-typed-input detection without needing an `SqlValue`-level marker bit.

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind.
Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object, unfolding the root one row per array element / object property.
With WITH: column paths are root-relative — an **array root yields one row per element** (paths relative to the element), an **object root yields a single row** (paths relative to the root).
Each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`.
NULL/invalid JSON → zero rows under lax.

`AS JSON` column modifier — accepted only on `nvarchar(max)` (any other declared type raises **Msg 13618** at parse).
Extracts the matched subtree via the shared `JsonSubtree.Extract` (the same rule backing `JSON_QUERY`): object/array → verbatim source text (whitespace and key order preserved, via `JsonElement.GetRawText`); JSON `null` → SQL NULL in both modes; any other (non-null) scalar → SQL NULL in lax, **Msg 13624** in strict; a missing path → SQL NULL in lax, **Msg 13608 State 6** in strict (the OPENJSON-context state, threaded through `JsonPath.Walk`'s `strictNotFoundState` — JSON_VALUE reports State 2, JSON_QUERY/JSON_MODIFY State 1).

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`.
Coercion via `SqlValue.CoerceTo`.
Backed by `System.Text.Json`.
JSON-path quoted-property escape `""` → literal `"`.

`JSON_PATH_EXISTS(json, path)` returns `int` (1 / 0 / NULL).
Routes through the same `JsonPath.Walk` infrastructure as `JSON_VALUE` / `JSON_QUERY`: parses the path, walks the parsed `JsonDocument`, returns 1 if the path resolves to a node and 0 otherwise.
NULL `json` or NULL `path` → NULL.
Lax-mode invalid JSON → 0; strict-mode invalid JSON → Msg 13609.

`ISJSON(expression)` returns `int` (1 / 0 / NULL).
Wraps `JsonDocument.Parse` in try/catch: NULL input → NULL, non-string input → 0 (real SQL Server raises Msg 8116 — the simulator's lax disposition is harmless for the CHECK-constraint use case), valid JSON object/array/scalar → 1, parse-fail → 0.
The 2-arg shape (`VALUE | ARRAY | OBJECT | SCALAR` modifier) isn't modeled — DACFx-emitted CHECK constraints (`isjson([col])<>0`) only use the 1-arg form.

Not emitted by EF / not modeled: `FOR JSON PATH`/`AUTO`.
Reachable only via raw SQL.
