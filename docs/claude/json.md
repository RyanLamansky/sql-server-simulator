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
The path is optional: `JSON_QUERY(json)` is shorthand for `JSON_QUERY(json, '$')` and hands back the whole document — the input's own text, so interior whitespace survives while the padding outside the document does not (`'  {"a" : 1}  '` → `{"a" : 1}`).
A root-level JSON scalar has nothing to extract, so it answers NULL like any other scalar match; a third argument → **Msg 189** ("The json_query function requires 1 to 2 arguments.", against `JSON_VALUE`'s fixed-arity Msg 174).
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
Microsoft documents the `JSON_OBJECT` default verbatim ("The default setting for this option is `NULL ON NULL`"); note it is the *opposite* of the `FOR JSON` clause, which omits NULL properties unless `INCLUDE_NULL_VALUES` is given — an earlier probe note had `JSON_OBJECT` wrong (claimed ABSENT) by conflating the two surfaces, fixed.
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

**Divergence — malformed input under lax paths.**
Real raises **Msg 13609** ("JSON text is not properly formatted. Unexpected character '<c>' is found at position <n>.") whenever the *input document* doesn't parse, regardless of the path's lax/strict prefix, and it counts a root-level JSON scalar (`'1'`, `'"abc"'`) as not parsing — only an object or array is JSON text to it.
The simulator swallows both to NULL (0 for `JSON_PATH_EXISTS`) under lax across `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_PATH_EXISTS`, and reserves Msg 13609 for the strict paths.
Closing it means the position-bearing message text as well as the root-shape rule; `ISJSON` already matches real (a root scalar → 0).

`ISJSON(expression)` returns `int` (1 / 0 / NULL).
Wraps `JsonDocument.Parse` in try/catch: NULL input → NULL, non-string input → 0 (real SQL Server raises Msg 8116 — the simulator's lax disposition is harmless for the CHECK-constraint use case), valid JSON object/array/scalar → 1, parse-fail → 0.
The 2-arg shape (`VALUE | ARRAY | OBJECT | SCALAR` modifier) isn't modeled — DACFx-emitted CHECK constraints (`isjson([col])<>0`) only use the 1-arg form.

## `FOR JSON` result serialization

The trailing `FOR JSON { PATH | AUTO } [, ROOT[('name')]] [, INCLUDE_NULL_VALUES] [, WITHOUT_ARRAY_WRAPPER]` clause on a SELECT serializes the whole result set to a single JSON string.
Parsed in `Selection.ParseOptionalForJson` (called from `ParseQueryExpression` in the slot `FOR XML` / `FOR BROWSE` occupy — after ORDER BY / OFFSET-FETCH, before OPTION); implemented in `Selection.ForJson.cs`.
A non-JSON `FOR` clause (`FOR XML` / `FOR BROWSE`) is left in place for the downstream Msg 102, so FOR XML stays unmodeled.
Not emitted by EF — reachable only via raw SQL.

The wrapper replaces the result schema with a single `nvarchar(max)` column named `JSON_F52E2B61-18A1-11d1-B105-00805F49916B` and yields **one row** carrying the whole string.
Real SQL Server chunks the string across multiple ~2033-char rows; the simulator returns it whole (consumers concatenate, and most read it whole) — a documented approximation.
An **empty input rowset yields zero output rows**, so a scalar subquery `(SELECT … FOR JSON …)` returns SQL NULL (probe-confirmed, matching real).
A `FOR JSON` Selection is marked (`Selection.ForJson`) so an enclosing `FOR JSON` serializer embeds its result as **raw JSON**, not a re-escaped string — the same role `JSON_QUERY` plays for the JSON_* builders.
The serializer is deterministic from the query, so it rides the plan cache.

### PATH mode (fully modeled)

Each row is a JSON object; each column is a key (its alias / name) in select order.
Dotted aliases nest to arbitrary depth (`x.id` / `x.a` → `{"x":{"id":…,"a":…}}`).
The nesting tree enforces SQL Server's contiguity rule: an object's properties must be consecutive in the select list — a duplicate leaf, a leaf name reused as an object prefix, or an object reopened after another object intervened all raise **Msg 13601** naming the offending column alias.
A column with no name / alias raises **Msg 13605**.
Rows are wrapped in `[ … ]` unless `WITHOUT_ARRAY_WRAPPER`.
A nested object whose leaves are all omitted (NULL under omit-NULL) is dropped entirely; the top-level per-row object always emits (an all-NULL row is `{}`).

### AUTO mode

Column names are literal keys (dots are **not** split — `[x.y]` → `{"x.y":…}`), and each FROM source becomes one nesting level: the first level's objects are the top-level array elements, every deeper level is an array-valued property keyed by the source's alias / written name.

```
select p.id, p.nm, c.cnm from pp p join cc c on c.pid = p.id for json auto
    → [{"id":1,"nm":"alpha","c":[{"cnm":"a1"},{"cnm":"a2"}]}]
```

The level model — which sources become levels, in what order, where a computed column lands, and how consecutive rows collapse — is shared with `FOR XML AUTO` and tabulated in [`xml.md`](xml.md#auto-nesting-shared-with-for-json-auto); `Parser/Selection.AutoNesting.cs` builds it for both.
JSON-specific corners: a NULL-filled outer-join side is `"c":[{}]` (an array holding one empty object), `INCLUDE_NULL_VALUES` reaches every level, `WITHOUT_ARRAY_WRAPPER` drops only the outermost array, and a SELECT with no FROM clause raises **Msg 13600**.
A set-operation result raises `NotSupportedException` (the same gap FOR XML AUTO has).

### Options

`ROOT('name')` wraps the output in `{"name": <output>}`; `ROOT` with no parens uses `"root"`; `ROOT('')` is a valid empty key.
`INCLUDE_NULL_VALUES` emits `"key":null` for NULL columns (the default omits them — the opposite of `JSON_OBJECT`'s `NULL ON NULL`).
`WITHOUT_ARRAY_WRAPPER` drops the `[ ]`; multiple rows become comma-separated objects with no wrapper (`{"id":1},{"id":2}` — intentionally not valid JSON, mirroring real).
`ROOT` combined with `WITHOUT_ARRAY_WRAPPER` raises **Msg 13620**.

### Value formatting (probed verbatim against SQL Server 2025)

FOR JSON's own formatter (`AppendForJsonValue`) — it diverges from the JSON_* builders' `JsonValueRender` in three probed ways, so it is **not** shared:

| type | JSON |
|---|---|
| int / bigint / smallint / tinyint | bare number (`5`) |
| decimal / numeric | bare, declared scale preserved (`1.50`) |
| money / smallmoney | bare, 4 decimals (`12.3400`) |
| float | scientific, 15 fraction digits, signed 3-digit exponent (`1.500000000000000e+000`) |
| real | scientific, 7 fraction digits, signed 3-digit exponent (`1.5000000e+000`) |
| bit | `true` / `false` |
| date | `"yyyy-MM-dd"` |
| datetime / smalldatetime | `"yyyy-MM-ddTHH:mm:ss[.fff]"` |
| datetime2 / time / datetimeoffset | ISO at declared precision, `datetimeoffset` keeps `+HH:mm` |
| uniqueidentifier | uppercase, quoted |
| binary / varbinary | base64, quoted (`0x0102FF` → `"AQL/"`) |
| sql_variant | formats its inner value |
| char / nchar / varchar / nvarchar / text / xml / other | quoted, JSON-escaped |

The date/time types **drop an all-zero fractional second** (`…T00:00:00`, not `…T00:00:00.000`) while keeping the interior/trailing zeros of a non-zero fraction (`.100`, not `.1`).
Unlike the JSON_* builders (which emit .NET `G15` / `G7` for float / real — a documented quirk), FOR JSON matches real's scientific notation exactly.

String escaping: `"` → `\"`, `\` → `\\`, **`/` → `\/`** (SQL Server escapes forward slash — the JSON_* builders do not), `\b` `\t` `\n` `\f` `\r`, other control chars < 0x20 → lowercase `\uXXXX`; chars ≥ 0x20 including non-ASCII stay verbatim.
Nested FOR JSON / `JSON_QUERY` / `JSON_OBJECT` / `JSON_ARRAY` columns embed as raw JSON (detected at compile time by `ColumnProducesRawJson`, unwrapping alias / parenthesis / scalar-subquery wrappers).
