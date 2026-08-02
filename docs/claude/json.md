# JSON: `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_OBJECT` / `JSON_ARRAY` / `JSON_PATH_EXISTS` / `ISJSON` / `OPENJSON`

Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions.
JSON columns are plain `nvarchar(max)`.

`JSON_VALUE(json, path)` returns `nvarchar(4000)`.
Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL.
`strict $.foo` raises Msg 13608 on miss.
NULL `json` or NULL path → NULL.
A document that isn't JSON text raises Msg 13609 under either mode — see [Msg 13609](#msg-13609--the-document-isnt-json-text).
JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`.
Object/array matches → NULL in lax, **Msg 13623** State 2 in strict.
**A scalar string longer than 4000 chars → SQL NULL in lax** (probe-confirmed against SQL Server 2025: 4000 → value, 4001 → NULL); enforcing the cap also keeps the length-0 result within the bounded TDS length prefix, so a multi-KB extracted value can't overflow it.

`JSON_QUERY(json, path)` returns `nvarchar(max)` — complement of `JSON_VALUE`.
Object/array match → raw JSON text via `JsonElement.GetRawText` (preserves the input's whitespace shape).
Scalar match → NULL in lax, Msg 13624 State 2 in strict.
Missing path → NULL in lax, Msg 13608 in strict.
NULL `json` or NULL path → NULL.
The path is optional: `JSON_QUERY(json)` is shorthand for `JSON_QUERY(json, '$')` and hands back the whole document — the input's own text, so interior whitespace survives while the padding outside the document does not (`'  {"a" : 1}  '` → `{"a" : 1}`).
A root-level JSON scalar isn't JSON text at all, so it raises Msg 13609 rather than answering NULL; a third argument → **Msg 189** ("The json_query function requires 1 to 2 arguments.", against `JSON_VALUE`'s fixed-arity Msg 174).
DACFx-emitted computed columns (WWI's `Application.People.OtherLanguages`, `Warehouse.StockItems.Tags`) always supply explicit paths.
Pipes cleanly into `OPENJSON` for round-trip on extracted arrays.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar(max)`, and the result is **the input's own text with one span spliced** — see [Editing the source text](#json_modify-edits-the-source-text).
EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608, State 2).
Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it.
Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`).
Bare `'$'` — with or without a mode keyword — names the whole document, which leaves no slot to write into: **Msg 13619**, `Unsupported JSON path found in argument 2 of JSON_MODIFY.`
The `append` prefix (`'append $.arr'`, ahead of any `lax` / `strict` keyword, and the one segment-less form the function takes) adds an element to the array the path names; every other function reports Msg 13607 for it.

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
- other strings → JSON-escaped (`\"` `\\` `\b` `\f` `\n` `\r` `\t` `\uHHHH` for control chars; non-ASCII / `<` / `>` left literal, and `/` too — real escapes it as `\/` here, tracked in [`backlog.md`](backlog.md#fidelity-gaps-in-shipped-behavior); `JSON_MODIFY`'s substituted value does escape it, via `AppendJsonString`'s `escapeSolidus`)
- nested `JSON_OBJECT` / `JSON_ARRAY` / `JSON_QUERY` / `JSON_MODIFY` results — embedded **raw** (not re-quoted), via compile-time `JsonValueRender.ProducesJson(Expression)` detection that unwraps `Parenthesized`.
  Other strings — including `'{"x":1}'` literals — go through the quote-and-escape path, matching SQL Server's JSON-typed-input detection without needing an `SqlValue`-level marker bit.

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind.
Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object, unfolding the root one row per array element / object property.
With WITH: column paths are root-relative — an **array root yields one row per element** (paths relative to the element), an **object root yields a single row** (paths relative to the root).
Each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`.
A NULL document → zero rows; one that isn't JSON text → Msg 13609, State 4 or 3 — see [Msg 13609](#msg-13609--the-document-isnt-json-text).

`AS JSON` column modifier — accepted only on `nvarchar(max)` (any other declared type raises **Msg 13618** at parse).
Extracts the matched subtree via the shared `JsonSubtree.Extract` (the same rule backing `JSON_QUERY`): object/array → verbatim source text (whitespace and key order preserved, via `JsonElement.GetRawText`); JSON `null` → SQL NULL in both modes; any other (non-null) scalar → SQL NULL in lax, **Msg 13624** in strict; a missing path → SQL NULL in lax, **Msg 13608 State 6** in strict (the OPENJSON-context state, threaded through `JsonPath.Walk`'s `strictNotFoundState`; JSON_VALUE / JSON_QUERY report State 1 and JSON_MODIFY State 2).

OPENJSON WITH-clause types: `int`/`bigint`/`decimal(p,s)`/`float`/`bit`/`nvarchar(N|max)`/`varchar(N)`/`date`/`datetime2(N)`/`datetimeoffset(N)`/`uniqueidentifier`.
Coercion via `SqlValue.CoerceTo`.
Backed by `System.Text.Json`.
JSON-path quoted-property escape `""` → literal `"`.

`JSON_PATH_EXISTS(json, path)` returns `int` (1 / 0 / NULL).
Routes through the same `JsonPath.Walk` infrastructure as `JSON_VALUE` / `JSON_QUERY`: parses the path, walks the parsed `JsonDocument`, returns 1 if the path resolves to a node and 0 otherwise.
NULL `json` or NULL `path` → NULL.
It is the one member of the family that never raises — see [Msg 13609](#msg-13609--the-document-isnt-json-text).

`ISJSON(expression)` returns `int` (1 / 0 / NULL).
NULL input → NULL; non-string input → 0 (real SQL Server raises Msg 8116 — the simulator's lax disposition is harmless for the CHECK-constraint use case); a well-formed JSON object or array with nothing but whitespace around it → 1; anything else → 0, root-level scalars (`'1'`, `'"abc"'`, `'true'`) and trailing text (`'{"a":1}extra'`) included.
It shares [the document scan](#msg-13609--the-document-isnt-json-text) with the rest of the family and reports what that scan objects to as 0 rather than raising.
The 2-arg shape (`VALUE | ARRAY | OBJECT | SCALAR` modifier) isn't modeled — DACFx-emitted CHECK constraints (`isjson([col])<>0`) only use the 1-arg form.

## `JSON_MODIFY` edits the source text

The result is the document argument as written with one span replaced, not a re-serialization of a parsed tree, so everything the edit didn't touch survives byte for byte: `JSON_MODIFY('  {"a" : 1}  ', '$.a', 2)` is `  {"a" : 2}  `, and writing a value back over itself is byte-identical.
`Parser/JsonEdit.cs` finds the span — a second walk over the raw text, distinct from the [Msg 13609 scan](#msg-13609--the-document-isnt-json-text) that validated it — reporting the leaf's value span plus the container coordinates an insert or a delete needs.
Four edits, each with its own splice point:

| edit | when | splice |
|---|---|---|
| replace | the path names a value | the value's own span |
| insert | the leaf's object lacks the key, value non-NULL | immediately before the object's `}`, `,"key":value` (no comma into an empty object) |
| delete | lax path, object member, NULL value | the member plus the comma **before** it, or — for the container's first member — the comma **after** it |
| append | an `append` path over an array | immediately before the array's `]`, `,value` (no comma into an empty array); onto a key the object lacks, the member is created holding `[value]`, a NULL value included |

Everything else leaves the document alone and hands the input straight back: a step that misses before the leaf (`'$.x.y'` over `{}`), a property path over an array or an index path over an object, an **array index at or past the end** (`'$[3]'` over `[1,2,3]` — appending is `append`'s job, not an out-of-range write's), a plain NULL value for a key the object lacks, and an `append` onto anything that isn't an array.
Under `strict` each of those is Msg 13608 State 2 instead — except the `append` onto a present-but-not-an-array value, which is **Msg 13621**, `Array cannot be found in the specified JSON path.`
`strict` also reads a NULL value as a value: it writes JSON `null` where lax would delete the key, which is also what an array element takes in either mode (`'$[1]'` over `[1,2,3]` leaves `[1,null,3]`).

The inserted text is canonical whatever spacing the document itself uses — SQL Server writes `,"b":2` into `{ "a" : 1 }`.
Values render through the shared `JsonValueRender`, with one difference from the JSON_* builders: a substituted string escapes `/` as `\/`.
A JSON-producing third argument (`JSON_QUERY` / `JSON_OBJECT` / `JSON_ARRAY` / a nested `JSON_MODIFY`, detected by the builders' compile-time `JsonValueRender.ProducesJson`) embeds **raw**, keeping its own spacing; every other string is quoted and escaped.
An inserted key comes from the path's own text, escaped the same way minus the solidus rule (`'$."café"'` → `"café"`).

## Duplicate property names — the reader stops at the first

A JSON object may name the same property twice, and SQL Server's reader takes the first one it meets: `JSON_VALUE('{"a":1,"a":2}', '$.a')` is `1`.
That first match binds even when it can't answer — `JSON_VALUE('{"a":{"z":1},"a":2}', '$.a')` is NULL rather than `2`, because the reader has already stopped.
`JSON_QUERY`, `JSON_PATH_EXISTS`, an `OPENJSON … WITH` column path and `JSON_MODIFY` all resolve the same way; `JSON_MODIFY` edits the leading namesake and leaves the trailing one standing (`'{"a":1,"a":2}'` + `'$.a'` = 9 → `{"a":9,"a":2}`), and an insert still lands at the closing brace past both.
`ISJSON` reports 1 — a repeated name is well-formed JSON text.

`OPENJSON`'s **default schema** is the exception, because it unfolds rather than resolving: every occurrence arrives as its own row, so `OPENJSON('{"a":1,"a":2,"b":3}')` yields three.

`JsonPath.TryStep` reads the first match by enumerating rather than through `JsonElement.TryGetProperty`, which hands back the *last*; `JSON_MODIFY` gets it for free from `JsonEdit`'s left-to-right text walk.

## Msg 13609 — the document isn't JSON text

A JSON function's document argument is read the way SQL Server's own reader reads it: left to right, stopping as soon as the path is settled.
Two rules fall out of that, neither of which `JsonDocument.Parse` applies on its own — **only an object or an array is JSON text** (a root-level scalar such as `1` or `"abc"` is malformed input), and text the reader never had to look at can't be a problem.
When the reader does meet something it can't read, that's **Msg 13609**: `JSON text is not properly formatted. Unexpected character '<c>' is found at position <n>.`
The position is a zero-based UTF-16 character index; running off the end of the text names the character `.` at the text's length.
A malformed *scalar token* is named at its first character rather than the character that spoiled it — `{"a":1x}` names `'1'` at 5, `{"a":01}` names `'0'`, and an unterminated string names its opening quote — because the reader takes the whole token before judging it.
The path's `lax` / `strict` prefix has no bearing on any of this: Msg 13609 comes before Msg 13608.
A NULL document is NULL, never an error.

`Parser/JsonText.cs` implements the scan.
It hands back the JSON text that read cleanly — the root value's own text, or, for a document that stopped partway, that prefix with its open containers closed — so a value read before the truncation still answers.
`JsonScan.OpenDepth` marks how deep that repair reaches and `JsonScan.CleanCut` whether anything more than the one separator behind the last complete value was dropped; `JsonPath.Walk` reads both to report, as a `JsonWalkResult`, how far the reader had to get:

| outcome | meaning | disposition |
|---|---|---|
| `Resolved` | the path reached a value the input itself closed | the answer, whatever is wrong further along |
| `Truncated` | the path reached a value only the repair closed | Msg 13609 — the reader ran out mid-answer |
| `Abandoned` | settled without reading as far as the problem | NULL, or Msg 13608 under `strict` |
| `Exhausted` | settling it took the reader to where the document stopped making sense | Msg 13609 |

What settles a path early is asking an object for an element or an array for a property: the container's opening bracket and first member decide it, and the reader stops there.
So `JSON_VALUE('{"a":1', '$[0]')` is NULL while `JSON_VALUE('{"a":1', '$.b')` raises at the end of the text.
A container with no member to start on settles nothing sooner than searching it would, which is why `JSON_VALUE('{x}', '$[0]')` raises where `JSON_VALUE('{"a":1}extra', '$[0]')` doesn't.
Every other way to miss — a property absent from an object, an index past an array's end, a step into a scalar — costs the reader the container it was searching, and then one step out of it, so the document's problem surfaces.

Per-function specifics:

- **`JSON_VALUE` / `JSON_QUERY`** report **State 1**.
  Both stop at the value the path names: `JSON_VALUE('{"a":1}extra', '$.a')` is `1`, and so is `JSON_VALUE('{"a":1', '$.a')` — the truncation is past the answer.
- **`JSON_MODIFY`** reports **State 7** and reproduces the whole document, so it has no path that lets it stop early: trailing text counts against it (`JSON_MODIFY('{"a":1}extra', '$.a', 2)` raises at `'e'`).
  A path that can't apply to what it finds is a no-op the reader settles early, and the input comes back verbatim however malformed the rest of it is (`JSON_MODIFY('[1,2', '$.a', 2)` → `[1,2`).
- **`OPENJSON`** reports **State 4** when the reader was inside the value it was after — always so for the one-argument form, whose value is the whole document — and **State 3** when it was still looking for it.
  It stops at the target's closing bracket, so `OPENJSON('{"a":1}extra')` unfolds without complaint, while `OPENJSON('{}extra', '$.a')` raises because the missing path took the reader past the root.
- **`JSON_PATH_EXISTS`** never raises: a document the scan objects to is 0, and so is a `strict`-mode miss that would be Msg 13608 anywhere else.
  Like `JSON_MODIFY` it answers for the whole document, so `JSON_PATH_EXISTS('{"a":1}extra', '$.a')` is 0 even though the path resolves.
- **`ISJSON`** applies both rules and reports them as 0.

The related strict-mode errors carry State bytes of their own: `JSON_VALUE`'s **Msg 13623** ("Scalar value cannot be found in the specified JSON path.") on an object or array match is State 2, and `JSON_QUERY`'s complementary **Msg 13624** on a scalar match is State 2 where an `OPENJSON … WITH (… AS JSON)` column's is State 1.

### Divergences

A statement that fails partway surfaces as the error alone: real streams the rows a truncated `OPENJSON` got through ahead of the error token, while the simulator's failed statement carries no rows (see [`data-reader.md`](data-reader.md)).
Msg 13607's wording is the simulator's own (`Unexpected character at position 0 in path '<path>'`) rather than real's, which names the offending character and its index and carries State 14.

## `FOR JSON` result serialization

The trailing `FOR JSON { PATH | AUTO } [, ROOT[('name')]] [, INCLUDE_NULL_VALUES] [, WITHOUT_ARRAY_WRAPPER]` clause on a SELECT serializes the whole result set to a single JSON string.
Parsed in `Selection.ParseOptionalForJson` (called from `ParseQueryExpression` in the slot `FOR XML` / `FOR BROWSE` occupy — after ORDER BY / OFFSET-FETCH, before OPTION); implemented in `Selection.ForJson.cs`.
A non-JSON `FOR` clause (`FOR XML` / `FOR BROWSE`) is left in place, restoring the cursor for the `FOR XML` parser that runs next and, failing that, the downstream Msg 102.
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
A set-operation result flattens to a single level named after the first branch's first source — the same rule FOR XML AUTO follows, described in [`xml.md`](xml.md#auto-nesting-shared-with-for-json-auto) — which in JSON means a flat object per row, since a lone level contributes no property name.

### Options

`ROOT('name')` wraps the output in `{"name": <output>}`; `ROOT` with no parens uses `"root"`; `ROOT('')` is a valid empty key.
`INCLUDE_NULL_VALUES` emits `"key":null` for NULL columns (the default omits them — the opposite of `JSON_OBJECT`'s `NULL ON NULL`).
`WITHOUT_ARRAY_WRAPPER` drops the `[ ]`; multiple rows become comma-separated objects with no wrapper (`{"id":1},{"id":2}` — intentionally not valid JSON, mirroring real).
`ROOT` combined with `WITHOUT_ARRAY_WRAPPER` raises **Msg 13620**.

### Where the clause may appear

Like `FOR XML`, the clause is refused on the SELECT an `INSERT … SELECT` or `SELECT … INTO` writes from — **Msg 13602**, `The FOR JSON clause is not allowed in a INSERT statement.` / `… in a SELECT INTO statement.` — while every nested position (scalar subquery, derived table, `SET @v = (SELECT … FOR JSON …)`) stays legal.
A variable-assigning `SELECT @v = … FOR JSON` instead reports **Msg 6819** state 3 with the *FOR XML* wording, which is real's own quirk (probe-confirmed); see [`xml.md`](xml.md#for-xml-on-a-select-that-doesnt-return-to-the-client).

A JSON property name is a quoted string, so an alias no XML name could carry (`[a b]`, `[1a]`) reaches the output as written — none of FOR XML's `_xHHHH_` escaping applies here.

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
