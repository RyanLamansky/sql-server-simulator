# JSON: `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_OBJECT` / `JSON_ARRAY` / `JSON_PATH_EXISTS` / `ISJSON` / `OPENJSON`

Unlocks EF's owned-types-as-JSON (`OwnsOne(...).ToJson()`) and primitive-collection emissions.
Every function here reads text documents; SQL Server 2025's native `json` type, what it changes about these functions' result types and states, and the functions that take only it (`JSON_CONTAINS`, `JSON_VALUE … RETURNING`, the `modify` method) are in [`json-type.md`](json-type.md).

`JSON_VALUE(json, path)` returns `nvarchar(4000)` in the document's collation and coercibility, the database's over `json` (probed 2026-10-02 against SQL Server 2025).
Lax mode (default and EF's only emitted form): missing path / non-scalar match → SQL NULL.
`strict $.foo` raises Msg 13608 on miss.
NULL `json` → NULL; a NULL path is refused — see [Argument types](#argument-types).
A document that isn't JSON text raises Msg 13609 under either mode — see [Msg 13609](#msg-13609--the-document-isnt-json-text).
JSON booleans render as lowercase `'true'`/`'false'`; numbers as raw text via `JsonElement.GetRawText`.
Object/array matches → NULL in lax, **Msg 13623** State 2 in strict.
**A scalar string longer than 4000 chars** is SQL NULL in lax mode over a MAX document (probe-confirmed against SQL Server 2025: 4000 → value, 4001 → NULL) and Msg 13625 under `strict`, and is cut to its first 4000 characters over a bounded one in either mode (2026-09-23, 2026-10-02); either way the result stays within the bounded TDS length prefix, so a multi-KB extracted value can't overflow it.

`JSON_QUERY(json, path)` returns `nvarchar(max)` over a MAX string document and `nvarchar(4000)` over any other text, a literal included, either in the document's collation (probed 2026-09-28 and 2026-10-02 against SQL Server 2025) — complement of `JSON_VALUE`; the bound type is what its rows carry (`JsonQuery.resultType`).
A bounded document is read as that `nvarchar(4000)`, so text past its 4000th character is gone before the reader starts: a 3,999-character string value already runs off the end (Msg 13609 at its opening quote), while a path settled ahead of the cut still answers.
Object/array match → raw JSON text via `JsonElement.GetRawText` (preserves the input's whitespace shape).
Scalar match → NULL in lax, Msg 13624 in strict — State 1 over a MAX document, 2 over any other.
Missing path → NULL in lax, Msg 13608 in strict.
SQL Server 2025's trailing `WITH ARRAY WRAPPER` (any case; `CONDITIONAL`, `UNCONDITIONAL`, `WITHOUT` and a bare `WITH WRAPPER` are Msg 102) gathers every value the path selects, scalars included, into one array — see [Advanced array accessors](#advanced-array-accessors).
NULL `json` → NULL.
The path is optional: `JSON_QUERY(json)` is shorthand for `JSON_QUERY(json, '$')` and hands back the whole document — the input's own text, so interior whitespace survives while the padding outside the document does not (`'  {"a" : 1}  '` → `{"a" : 1}`).
A root-level JSON scalar isn't JSON text at all, so it raises Msg 13609 rather than answering NULL; a third argument → **Msg 189** ("The json_query function requires 1 to 2 arguments.", against `JSON_VALUE`'s fixed-arity Msg 174).
DACFx-emitted computed columns (WWI's `Application.People.OtherLanguages`, `Warehouse.StockItems.Tags`) always supply explicit paths.
Pipes cleanly into `OPENJSON` for round-trip on extracted arrays.

`JSON_MODIFY(json, path, newValue)` returns `nvarchar(max)`, and the result is **the input's own text with one span spliced** — see [Editing the source text](#json_modify-edits-the-source-text).
EF emits `'strict $.City'`-shape paths from owned-as-JSON partial updates (missing leaf → Msg 13608, State 2).
Lax existing-key + NULL value removes the key; lax missing key + non-NULL value adds it.
Numeric/boolean `newValue` stays JSON-typed (`{"n":42}` not `{"n":"42"}`), and a `real` is written as the `float` it converts to, at float's sixteen digits (probed 2026-10-02).
Bare `'$'` — with or without a mode keyword — names the whole document, which leaves no slot to write into: **Msg 13619**, `Unsupported JSON path found in argument 2 of JSON_MODIFY.`
The `append` prefix (`'append $.arr'`, ahead of any `lax` / `strict` keyword, and the one segment-less form the function takes) adds an element to the array the path names; every other function reports Msg 13607 for it.

`JSON_OBJECT([key : value [, ...]] [null_clause])` / `JSON_ARRAY([value [, ...]] [null_clause])` return `nvarchar(max)`.
Probe-confirmed against SQL Server 2025.
The default null clause is **builder-specific**: `JSON_OBJECT` defaults to **NULL ON NULL** (NULL values emit JSON `null`), while `JSON_ARRAY` defaults to **ABSENT ON NULL** (NULL elements omitted).
Microsoft documents the `JSON_OBJECT` default verbatim ("The default setting for this option is `NULL ON NULL`"); note it is the *opposite* of the `FOR JSON` clause, which omits NULL properties unless `INCLUDE_NULL_VALUES` is given — an earlier probe note had `JSON_OBJECT` wrong (claimed ABSENT) by conflating the two surfaces, fixed.
The trailing keyword pair (`NULL ON NULL` / `ABSENT ON NULL`) is matched as `ReservedKeyword`s (`Null` + `On` + `Null` / `Absent` falls through `UnquotedString` since `ABSENT` isn't reserved).
Empty argument list yields `{}` / `[]`.
Duplicate keys preserved (no dedup, matching real SQL Server).
NULL key raises **Msg 13638** at runtime; missing `:` separator, trailing comma, partial null-clause all raise Msg 102 at parse, and `'a' = 1` in place of a pair names the token after the value.
A key is written the way its value would be, quoted when that isn't a string already — a `float` key is its scientific form, a `bit` `"true"`, a binary its base64 (probed 2026-10-02).
A CLR-typed key or value (`hierarchyid`, the spatial types, a CLR user-defined type) is **Msg 13666** while binding, `json_object and json_objectagg does not support …` at State 2 for both object builders, `json_array` State 3 and `json_arrayagg` State 1.

`JSON_ARRAYAGG(value [ORDER BY ...] [null_clause])` / `JSON_OBJECTAGG(key : value [null_clause])` are the aggregate forms, both returning `nvarchar(max)`.
They reuse `JsonValueRender` for element/value formatting (including raw embedding of JSON text) and follow the scalar builders' null-clause defaults (`JSON_ARRAYAGG` → ABSENT ON NULL, `JSON_OBJECTAGG` → NULL ON NULL).
**Empty input (zero rows) → SQL NULL; a group with rows whose values are all absent → `[]` / `{}`** (the aggregators track row count independently of emitted fragments).
Grammar specifics, probe-confirmed: `JSON_ARRAYAGG`'s `ORDER BY` sits *inside* the parentheses and is mutually exclusive with `OVER` (the combination raises Msg 156); its commas don't count as arguments.
A `WITHIN GROUP (ORDER BY …)` after `JSON_ARRAYAGG` is accepted and orders nothing — the rows keep their arrival order — while after an in-parentheses `ORDER BY`, or after `JSON_OBJECTAGG`, the word is Msg 102 (probed 2026-10-02).
`JSON_OBJECTAGG` accepts neither an `ORDER BY` (Msg 156) nor the SQL-standard `key VALUE value` form (Msg 102), refuses a comma list with Msg 174 naming itself in capitals, and raises **Msg 13638** on a NULL key.
Both support `OVER (...)` windows — `PARTITION BY`, running `ORDER BY`, and explicit `ROWS` frames all ride the standard aggregate-window path.
`JSON_OBJECTAGG`'s per-row key (which the generic value-only aggregator contract can't carry) is set via a `SetKey` side-channel before each `Add`, mirroring `STRING_AGG`'s separator handling; in the window executor it gets a dedicated walk (`ComputeJsonObjectAggWindow`) since the key isn't part of the pre-evaluated operand stream.
The aggregators build the closing `]` / `}` onto a snapshot rather than mutating the running buffer, so repeated `Result()` calls across sliding-window frames stay correct.
`DISTINCT` is not accepted by either.

All the `nvarchar(max)` JSON producers (`JSON_QUERY` over a MAX document, `JSON_MODIFY`, `JSON_OBJECT`, `JSON_ARRAY`, `JSON_ARRAYAGG`, `JSON_OBJECTAGG`) are typed `SqlType.JsonTextMax` — `nvarchar(max)` carrying the JSON text mark — at both `GetSqlType` and `Run`, not the length-0 `SqlType.NVarchar` "size from value" form, except where a `json` input or `RETURNING json` makes them `json` (see [`json-type.md`](json-type.md#the-json-functions-over-a-json-document)).
This is load-bearing over the TDS wire: a length-0 result over 32,767 chars overflows the codec's bounded 2-byte length prefix, whereas a MAX result streams as PLP.
`JSON_VALUE` stays bounded (`nvarchar(4000)`) and is safe by its 4000-char cap.
See [`tds-endpoint.md`](tds-endpoint.md) for the wire mechanism.

JSON_OBJECT's key parse needs the `:` separator to not collide with the `::` type-prefix postfix (hierarchyid / geography / geometry).
Implementation: a `ParserContext.StopExpressionAtBareColon` flag, set transiently around the key parse, redirects the `Expression.Parse` postfix `:` case — single-colon rewinds and breaks out so the JSON_OBJECT body parser consumes the separator; double-colon still routes to `SpatialStaticCall` / `HierarchyIdStaticCall` unchanged.
The flag is save/restored so a nested JSON_OBJECT inside another JSON_OBJECT's value position doesn't leak its key-parse state outward.

Value formatting matches real SQL Server byte-for-byte, float / real included: every JSON producer writes CONVERT style 126 (source-precision scientific — sixteen significant digits for `float`, eight for `real`), where a styleless conversion to a string type writes style 0's compact form (see [`casting.md`](casting.md)).
Specific mappings:
- `bit` → unquoted `true` / `false`
- integer / decimal / money — unquoted number, written as the SQL value carries it rather than formatted from the declared type, so an exact numeric's trailing zeros are whatever its type declares: `JSON_ARRAY(CAST(1 AS numeric(10, 2)))` is `[1.00]`, `JSON_OBJECT('a': CAST(1.5 AS numeric(10, 4)))` is `{"a":1.5000}`, `JSON_MODIFY('{"a":0}', '$.a', CAST(1 AS numeric(10, 2)))` is `{"a":1.00}`, and every `money` / `smallmoney` value shows its fixed scale of 4.
  That falls out of the [declared-scale stamp](arithmetic.md#the-value-carries-the-declared-scale) rather than living here, so a conversion, arithmetic, an aggregate and a column read all agree.
- `varbinary` / `binary` / `image` / `rowversion` → base64-quoted (`"QUI="` for `0x4142`)
- `sql_variant` → the value it holds, written as that type is (`1`, `"2024-01-01"`)
- `datetime` / `datetime2` / `smalldatetime` → quoted ISO with **T** separator (`"2025-01-15T12:34:56"`)
- `date` / `time` / `uniqueidentifier` → quoted default ISO / uppercase-hex
- other strings → JSON-escaped (`\"` `\\` `\b` `\f` `\n` `\r` `\t` `\uHHHH` for control chars, and **`/` → `\/`**; non-ASCII / `<` / `>` left literal).
  The solidus escape reaches the keys too (`JSON_OBJECT('k/1': 'v')` is `{"k\/1":"v"}`), and every JSON producer writes it — the two builders, both aggregates, `JSON_MODIFY`'s substituted value and `FOR JSON`.
  The exceptions are the two strings that aren't a rendered *value*: `REGEXP_MATCHES`' `substring_matches` column and the property name `JSON_MODIFY` takes from its path's own text, both of which leave `/` literal (all probe-confirmed).
- **JSON text** — embedded **raw** (not re-quoted).
  Over text, `JSON_QUERY`, `JSON_MODIFY`, the builders, their aggregates and a `FOR JSON` with its array wrapper return `nvarchar` carrying a mark (`NVarcharSqlType.jsonText`), and the mark travels with the type: through a derived table, a view, a CTE, a scalar subquery, `ISNULL` / `NULLIF` (the check's type), `COALESCE` and `CASE` (the unified type), and a set operation's bounded column — so `COALESCE(<nvarchar column>, JSON_QUERY(…))` embeds the column's value raw too, as real does (probed 2026-10-02 against SQL Server 2025).
  An arm real settles while compiling takes its own type instead, so `COALESCE(N'[0]', JSON_QUERY(…))` and `IIF(1 = 1, N'x', JSON_QUERY(…))` embed quoted and `CASE WHEN 1 = 1 THEN JSON_QUERY(…) ELSE N'x' END` raw (probed 2026-10-06; see [`query.md`](query.md#boolean--set-ops--projection--case)).
  It is lost by `CAST` / `CONVERT`, an operator, a MAX set-operation column (two `JSON_ARRAY` arms `UNION ALL`ed embed quoted), a stored column (`SELECT INTO`), a variable, and `FOR JSON … WITHOUT_ARRAY_WRAPPER`, whose document is a plain string.
  Other strings — including `'{"x":1}'` literals — go through the quote-and-escape path.

`OPENJSON(json [, doc_path]) [WITH (col TYPE [path] [AS JSON], …)]` — rowset-returning, structurally a new FromSource kind.
The default schema is `key nvarchar(4000)` NOT NULL in `Latin1_General_BIN2` — so it compares and sorts binary, and a longer name is cut to 4000 characters — `value nvarchar(max)` in the document's collation and `type tinyint` NOT NULL, and no OPENJSON column is updatable (probed 2026-09-28 and 2026-10-02 against SQL Server 2025).
A document of any other type is read as the `nvarchar` it converts to (`OPENJSON(1)` is Msg 13609 at `'1'`, a binary its bytes as UTF-16), and a NULL document path — a literal while binding, a variable when it runs — is Msg 8116 State 9 naming `OPENJSON`.
A `WITH` list may name a column twice (`WITH (id int, id int)`): the name reads the first, and `SELECT *` returns both, the later one held under a name no reference spells (`Selection.ShadowedColumnName`).
A `COLLATE` on a non-string `WITH` column is Msg 447 state 1 followed by the informational Msg 2724 state 14 naming the type as written (`float(10)` is `float`), an unknown collation name is judged first (Msg 448), and a type written with two arguments takes no `COLLATE` (Msg 156) (all probed 2026-10-06 against SQL Server 2025).
Without WITH: default schema `(key nvarchar, value nvarchar, type int)` — type codes 0=null/1=string/2=number/3=bool/4=array/5=object, unfolding the root one row per array element / object property.
With WITH: column paths are root-relative — an **array root yields one row per element** (paths relative to the element), an **object root yields a single row** (paths relative to the root).
Each column extracts via `$.<col-name>` (default) or explicit `'$path'`; primitive collections use `'$'`.
A NULL document → zero rows; one that isn't JSON text → Msg 13609, State 4 or 3 — see [Msg 13609](#msg-13609--the-document-isnt-json-text).
A document path that misses, or lands on a value that isn't an object or array (JSON `null` included), opens no rows in lax mode; in strict mode the miss is **Msg 13608 State 3** and the scalar **Msg 13611**, State 1 for the default schema and 2 with `WITH` (probed 2026-09-24 against SQL Server 2025).

`AS JSON` column modifier — accepted only on `nvarchar(max)` (any other declared type raises **Msg 13618** at parse).
Extracts the matched subtree via the shared `JsonSubtree.Extract` (the same rule backing `JSON_QUERY`): object/array → verbatim source text (whitespace and key order preserved, via `JsonElement.GetRawText`); JSON `null` → SQL NULL in both modes; any other (non-null) scalar → SQL NULL in lax, **Msg 13624** in strict; a missing path → SQL NULL in lax, **Msg 13608 State 6** in strict (the OPENJSON-context state, threaded through `JsonPath.Walk`'s `strictNotFoundState`; JSON_VALUE / JSON_QUERY report State 1 and JSON_MODIFY State 2).

A `WITH` column reads its scalar's text — `true` / `false` as the words — and converts it as a string would, with real's twists (probed 2026-10-02 against SQL Server 2025):

- A character column takes the text cut to its length, never an error, `strict` included; a character type written without a length is one character long, and takes the document's collation unless the column has a `COLLATE` of its own (a non-string type with one is Msg 447).
- A binary column reads base64: text that isn't is Msg 13612, more bytes than a bounded column holds Msg 13613 (State 1, or 2 for `rowversion`, which reports even what isn't base64 that way), and `binary(n)` pads.
- An object or array is no scalar: NULL, or Msg 13624 State 1 under `strict`.
- A `decimal` column's conversion failure names `decimal` where `CAST`'s says `numeric`.
- `text` / `ntext` / `image` / `sql_variant` are Msg 13614 and the CLR types Msg 13616, while the clause parses.
- A column without a path reads its own name as a quoted member, escaped as a JSON string would be, so `[a b]` and `[c"d]` both resolve.

`JSON_PATH_EXISTS(json, path)` returns `int` (1 / 0 / NULL).
Routes through the same `JsonPath.Walk` infrastructure as `JSON_VALUE` / `JSON_QUERY`: parses the path, walks the parsed `JsonDocument`, returns 1 if the path resolves to a node and 0 otherwise.
NULL `json` → NULL.
It is the one member of the family that never raises — see [Msg 13609](#msg-13609--the-document-isnt-json-text).

`ISJSON(expression)` returns `int` (1 / 0 / NULL).
NULL input → NULL; a well-formed JSON object or array with nothing but whitespace around it → 1; anything else → 0, root-level scalars (`'1'`, `'"abc"'`, `'true'`) and trailing text (`'{"a":1}extra'`) included.
It shares [the document scan](#msg-13609--the-document-isnt-json-text) with the rest of the family and reports what that scan objects to as 0 rather than raising.
The second argument narrows or widens the kind asked for: `VALUE` takes any JSON value, `SCALAR` a string or number but none of `true` / `false` / `null`, `ARRAY` / `OBJECT` their container; another word is Msg 155 and a non-word (`'scalar'`) Msg 1023 (probed 2026-09-26 against SQL Server 2025, `JsonText.RootKind`).


## Argument types

`JSON_VALUE`, `JSON_QUERY`, `JSON_MODIFY`, `JSON_PATH_EXISTS` and `ISJSON` read the document, and the four path functions the path, as text or — the document only — as `json`: any other type — `text` / `ntext` and `xml` included — is Msg 8116 while compiling, so an empty rowset raises it too (probed 2026-09-25 against SQL Server 2025).
A path is never NULL.
A bare `NULL` literal is refused while compiling, `json_value` / `json_modify` at state 1 in lower case and `JSON_QUERY` / `JSON_PATH_EXISTS` at state 8 in capitals; a path that *evaluates* to NULL — a typed NULL, a variable, a column, a `NULLIF` — is refused at runtime by all four at state 8 in capitals, whatever the document holds, a NULL document included.
## The path grammar

`['append'] ['lax' | 'strict'] '$' segment*`, where a segment is `.<name>` / `."<quoted name>"` / `[<index>]`.
One parser (`Parser/JsonPath.cs`) serves every function, so a malformed path reports identically from `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY` / `JSON_PATH_EXISTS` and an `OPENJSON … WITH` column.
Only `JSON_MODIFY` reads the `append` prefix; elsewhere it is Msg 13607 State 14 at its `a`.

**Whitespace** separates the grammar's tokens and may sit between any two of them — around a keyword, either side of the `$`, either side of a `.`, inside an index's brackets, and trailing the path — so `'  lax  $ . a [ 0 ] '` resolves.
It is space, tab, line feed, form feed and carriage return; vertical tab and the non-breaking space are not whitespace here.
A keyword needs no whitespace behind it (`lax$.a` parses) but does need the word to end there, so `laxx$.a` is malformed.
An unquoted name is ASCII letters, digits and `_`, starting with a letter or `_` — `$.é` is State 22 at the `é` — and the keywords `append`, `lax` and `strict` are lower case only (`LAX $.a` is State 22 at the `L`; `strict append` State 14 at the `a`).
A quoted name is a JSON string literal: `\"`, `\\`, `\/`, `\b \f \n \r \t` and `\uXXXX` escape, any other escape is State 17 at the character behind the backslash, a `\u` short of four hex digits State 17 at the fourth digit's position, and a doubled `""` closes the name and leaves a stray quote (State 14).
More than 128 steps is Msg 13606 State 4, raised as the 129th is read (all probed 2026-10-02 against SQL Server 2025).

Names compare in one form on the path and the document alike (`JsonPath.NameForm`): a `\uXXXX` escape decoded, every other escape kept as the two characters written.
So `$."a\u0041"` finds `{"aA":1}` and `{"a\u0041":1}`, `$."a\/b"` finds `{"a\/b":1}` but not `{"a/b":1}`, and `$."a\nb"` doesn't find `{"a\u000ab":1}`.
`JSON_MODIFY` writes an inserted key in that form, verbatim: `$."a\/b"` inserts `"a\/b"`, `$."a/b"` inserts `"a/b"`.
An index reads up to eleven digits and tops out at `uint`'s ceiling.

**Msg 13607** — `JSON path is not properly formatted. Unexpected character '<c>' is found at position <n>.` — names the character the parser stopped on and its zero-based index, with `.` at the path's length standing in for running off the end (the same placeholder [Msg 13609](#msg-13609--the-document-isnt-json-text) uses).
The State byte names what the parser was reading, and one rule cuts across it: the grammar's own punctuation (`$` `"` `[` `]` `.`) and the digits report **14** wherever they turn up out of place, whatever the position expected.

| where the parser stopped | State | example |
|---|---|---|
| the `$`, or the `.` / `[` / end that follows a segment, or the name behind a `.` | 22 | `'xyz'` → `'x'` at 0, `'$.a b'` → `'b'` at 4 |
| behind a quoted name — any character at all | 14 | `'$."a"x'` → `'x'` at 5 |
| the end of the path | 14 | `'$.'` → `'.'` at 2 |
| inside `[`, before the digits | 21 | `'$[a]'` → `'a'` at 2 |
| inside `[`, past the digits | 15 | `'$[1x]'` → `'x'` at 3 |
| an index above `uint`'s ceiling | 16 | `'$[4294967296]'` → `'6'` at 11 |
| a quoted name the path never closed | 20 | `'$."a'` → `'.'` at 4 |

Every row is probed verbatim against SQL Server 2025, as is the punctuation rule (`'$.a$b'` → `'$'` at 3 State 14 where `'$.a-b'` → `'-'` at 3 State 22).

### Advanced array accessors

SQL Server 2025 widens a bracket to `[*]`, a range `[n to m]`, `last` in place of any number, and a comma list of those (`[last, 0]`, `[0 to 1, 2]` — any order, repeats allowed), and adds the `.*` member wildcard; `JsonPath.Select` evaluates such a path to a set of values (probed 2026-09-27 against SQL Server 2025).
The shared parser takes them everywhere, and what each reader does with a set differs:

- **Found or not.**
  A path is found when every step named something in at least one value — `[*]` or `.*` over an empty container counts, a range reaching past the end counts for the part inside it, a range that `last` turns backwards selects nothing without missing.
  `[5 to 7]` over three elements, a wildcard over the wrong kind of value and a missing property are misses; `strict` also reads a partial reach (`[0 to 5]` over one element) as a miss.
- **`JSON_VALUE` / `JSON_QUERY`** answer as a plain path would when exactly one value is selected, NULL for several — under `strict` Msg 13623 / 13624 State 2 over `json`, Msg 13608 State 2 over text — and a miss as any miss.
- **`JSON_QUERY … WITH ARRAY WRAPPER`** is the reader built for sets: `[]` for a found-but-empty selection, NULL (Msg 13608, State 5 over `json` and 2 over text, under `strict`) for a miss.
  Over text a container keeps its spacing and a string is re-escaped the way the builders write one (`\/` included); over `json` each value is its canonical text.
- **`JSON_PATH_EXISTS`** is 1 exactly when the path is found.
- **`JSON_MODIFY`** over text takes none of them (Msg 13660 State 4, bar the two text-wide refusals below); over `json`, `[*]`, `.*` and a range with two different ends are Msg 13660 State 5, a list of several items leaves the document alone (Msg 13608 under `strict`), and `[last]` writes into the **first** element of a non-empty array — real's own reading, reproduced.
- **`OPENJSON`**: a wildcard document path is Msg 13665 State 5 over `json` and Msg 13660 State 2 (`OpenJson with default schema …`) over text; a `WITH` column's wildcard is Msg 13665 State 3 over `json`.
  Otherwise a document path opens the one value it selects (nothing for several), and a column path reads the first.
- Over a **text** document every reader refuses `last` (Msg 13660 State 2) and a list (State 5) before anything else, once the document is known to be non-NULL.
- A range written high-to-low in plain numbers is Msg 13660 State 1 wherever the path parses.

The grammar's own states extend the table above: after `[` a word that isn't a lower-case `last` reports 21, `to` must be lower case with whitespace before it (a word there reports 21 after whitespace and 15 without), a second `to` reports 14, whatever follows `[*` or `.*` reports 21 and 14 respectively, and `,` and `*` join the punctuation that reports 14 wherever it turns up out of place.

Divergences: over a malformed text document the selection runs on what read cleanly and raises Msg 13609 only when it found nothing there, an approximation of the early-stopping reader the plain paths model exactly; and a range ending at exactly `4294967295`, which real reports as a corrupted json value (Msg 13643), selects as any other.

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
Values render through the shared `JsonValueRender`, so a substituted string carries the same escaping the JSON_* builders write, `/` → `\/` included.
A third argument of JSON text (the JSON text mark under value formatting above) embeds **raw**, keeping its own spacing; every other string is quoted and escaped.
An inserted key is the path's name as written (see [the path grammar](#the-path-grammar)), so `'$."café"'` → `"café"` and `'$."a/b"'` → `"a/b"`.

The written value's **type** is gated, and real binds the rule while compiling (a refused type reports over an empty rowset).
Accepted: the string family bar `text` / `ntext` / `xml` / the spatial types, the integer family, `decimal` / `numeric`, `float`, `real` and `bit` — plus an untyped `NULL` literal, which types as `int` and so leaves the delete-a-member form open.
Everything else is **Msg 8116**, `Argument data type <type> is invalid for argument 3 of json_modify function.` — `money` / `smallmoney`, every date/time type, `uniqueidentifier`, `binary` / `varbinary` / `image`, `text` / `ntext`, `xml`, `sql_variant`, `hierarchyid` and the spatial types, a *typed* NULL of any of them included (all probe-confirmed).

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
A value nested inside 129 containers — a 130th container, or a scalar inside the 129th — is **Msg 13606** State 1 instead, raised as lazily as Msg 13609 is, by `ISJSON` and `JSON_PATH_EXISTS` too (probed 2026-10-02 against SQL Server 2025).
An escaped surrogate with no partner reads as that lone UTF-16 unit.
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

## `FOR JSON` result serialization

The trailing `FOR JSON { PATH | AUTO } [, ROOT[('name')]] [, INCLUDE_NULL_VALUES] [, WITHOUT_ARRAY_WRAPPER]` clause on a SELECT serializes the whole result set to a single JSON string.
Parsed in `Selection.ParseOptionalForJson` (called from `ParseQueryExpression` in the slot `FOR XML` / `FOR BROWSE` occupy — after ORDER BY / OFFSET-FETCH, before OPTION); implemented in `Selection.ForJson.cs`.
A non-JSON `FOR` clause (`FOR XML` / `FOR BROWSE`) is left in place, restoring the cursor for the `FOR XML` parser that runs next and, failing that, the downstream Msg 102.
Not emitted by EF — reachable only via raw SQL.

The wrapper replaces the result schema with a single `nvarchar(max)` column named `JSON_F52E2B61-18A1-11d1-B105-00805F49916B` and yields **one row** carrying the whole string — which is what a subquery, a view body or an inline function body reads.
A SELECT statement's *own* FOR JSON or untyped FOR XML instead **streams** the document to the client, as real does (probed 2026-09-26 against SQL Server 2025): rows of exactly 2033 UTF-16 units, split with no regard for a surrogate pair, and a row count — the DONE token's and `@@ROWCOUNT` — of the rows the clause *serialized*, not the rows it sent.
`Selection.AsStatementResult` wraps the parsed statement for that, applied by the SELECT dispatch and the FMTONLY path (so `sp_describe_first_result_set` too); the serializers record their input count in `StatementContext.ForClauseSourceRows` as they finish.
An **empty input rowset yields zero output rows**, so a scalar subquery `(SELECT … FOR JSON …)` returns SQL NULL (probe-confirmed, matching real).
A `FOR JSON` document is JSON text, which an enclosing `FOR JSON` or JSON builder embeds as **raw JSON** — except under `WITHOUT_ARRAY_WRAPPER`, whose document is a plain string that embeds quoted (probed 2026-10-02 against SQL Server 2025).
Read as a derived table without a column list, the document's column is unnamed — Msg 8155, as for `FOR XML`.
The serializer is deterministic from the query, so it rides the plan cache.

### PATH mode (fully modeled)

Each row is a JSON object; each column is a key (its alias / name) in select order.
Dotted aliases nest to arbitrary depth (`x.id` / `x.a` → `{"x":{"id":…,"a":…}}`).
The nesting tree enforces SQL Server's contiguity rule: an object's properties must be consecutive in the select list — a duplicate leaf, a leaf name reused as an object prefix, or an object reopened after another object intervened all raise **Msg 13601** naming the offending column alias.
A column with no name / alias raises **Msg 13605**, and one whose alias has an empty step — starting or ending with `.`, or holding `..` — **Msg 13603**.
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

`ROOT('name')` wraps the output in `{"name": <output>}`; `ROOT` with no parens uses `"root"` in PATH mode and the first level's name in AUTO (`{"t":[…]}`, probed 2026-10-02); `ROOT('')` is a valid empty key.
Each option is written at most once; a repeat, a mode other than PATH / AUTO, `ELEMENTS` or a parenthesis after the mode is Msg 102 near the `JSON` keyword itself.
`INCLUDE_NULL_VALUES` emits `"key":null` for NULL columns (the default omits them — the opposite of `JSON_OBJECT`'s `NULL ON NULL`).
`WITHOUT_ARRAY_WRAPPER` drops the `[ ]`; multiple rows become comma-separated objects with no wrapper (`{"id":1},{"id":2}` — intentionally not valid JSON, mirroring real).
`ROOT` combined with `WITHOUT_ARRAY_WRAPPER` raises **Msg 13620**.

### Where the clause may appear

Like `FOR XML`, the clause is refused on the SELECT an `INSERT … SELECT` or `SELECT … INTO` writes from — **Msg 13602**, `The FOR JSON clause is not allowed in a INSERT statement.` / `… in a SELECT INTO statement.` — while every nested position (scalar subquery, derived table, `SET @v = (SELECT … FOR JSON …)`) stays legal.
A variable-assigning `SELECT @v = … FOR JSON` instead reports **Msg 6819** state 3 with the *FOR XML* wording, which is real's own quirk (probe-confirmed); see [`xml.md`](xml.md#for-xml-on-a-select-that-doesnt-return-to-the-client).

A JSON property name is a quoted string, so an alias no XML name could carry (`[a b]`, `[1a]`) reaches the output as written — none of FOR XML's `_xHHHH_` escaping applies here.

### Value formatting (probed verbatim against SQL Server 2025)

FOR JSON's formatter (`AppendForJsonValue`); the JSON_* builders' `JsonValueRender` writes every row the same way, sharing the date/time renderers:

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
| datetime2 / time / datetimeoffset | ISO at declared precision, `datetimeoffset` keeps `+HH:mm`, or `Z` for a zero offset |
| uniqueidentifier | uppercase, quoted |
| binary / varbinary / image / rowversion | base64, quoted (`0x0102FF` → `"AQL/"`) |
| hierarchyid | its string, quoted |
| geometry / geography / CLR user-defined type | refused while binding, **Msg 13604** |
| sql_variant | formats its inner value |
| char / nchar / varchar / nvarchar / text / xml / other | quoted, JSON-escaped |

A `datetime` writes its milliseconds rounded from the three-hundredths it stores (`.997`, not `.996`).
The date/time types **drop an all-zero fractional second** (`…T00:00:00`, not `…T00:00:00.000`) while keeping the interior/trailing zeros of a non-zero fraction (`.100`, not `.1`), in FOR JSON and the builders alike (probed 2026-09-26 against SQL Server 2025).
FOR JSON and the JSON_* builders share one renderer and match real's scientific notation exactly.

String escaping: `"` → `\"`, `\` → `\\`, **`/` → `\/`**, `\b` `\t` `\n` `\f` `\r`, other control chars < 0x20 → lowercase `\uXXXX`; chars ≥ 0x20 including non-ASCII stay verbatim — the same set the JSON_* builders write.
Nested FOR JSON / `JSON_QUERY` / `JSON_OBJECT` / `JSON_ARRAY` columns embed as raw JSON (detected at compile time by `ColumnProducesRawJson`, unwrapping alias / parenthesis / scalar-subquery wrappers).
