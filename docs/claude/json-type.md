# `json` data type

SQL Server 2025's native `json`: declaration, the canonical text real hands back, the length of its binary form, the refusals of text that isn't a document, conversions, the refusals of a type with no ordering, how the JSON functions read and return it, the surfaces that take only it — `JSON_CONTAINS`, `JSON_VALUE … RETURNING`, the `modify` method and JSON indexes — and the catalog and wire surfaces.
The JSON functions themselves, and the advanced array accessors every reader shares, live in [`json.md`](json.md); this file owns only what the type changes.
Everything here was probed 2026-09-26 against SQL Server 2025, and the four json-only surfaces 2026-09-27; `JsonTypeTests`, `JsonContainsTests` and `JsonIndexTests` (Tests) and `JsonWireTests` (Tests.SqlClient) are the contract.

## Type and storage

`JsonSqlType` (`Storage/JsonType.cs`) is one singleton, `system_type_id` and `user_type_id` 244, always LOB (`max_length` -1), no collation.
A width in any form — `json(100)`, `json(max)` — is Msg 2716 in a declaration and Msg 291 in a CAST.
A value is its canonical text, stored as UTF-8.
Real stores a binary form instead and never exposes it (a conversion to `varbinary` is Msg 529), so its only observable is `DATALENGTH`, which `JsonDocumentText.BinaryLength` reproduces from the text by a size model fitted to well over a hundred probed documents; its XML doc carries the model.
The model's pieces that took probing: an integer is free in 30 bits and costs 9 bytes up to 64, a decimal costs its `decimal` storage length for the mantissa's digit count plus 4, string lengths are 7-bit varints, and property names go into one document-wide dictionary of distinct names (so `[{"a":1},{"a":2}]` is 11 bytes shorter than `[{"a":1},{"b":2}]`).

## Canonical text

Every path into the type — a literal, a variable, a parameter, `INSERT`, `UPDATE`, a `DEFAULT`, `ALTER COLUMN`, a unification arm — runs `JsonDocumentText.Canonicalize`:

- Whitespace (space, tab, line feed, carriage return — a form feed is refused) goes.
- A repeated property name is dropped with its value, the first occurrence staying, at every depth; the dropped value is still validated.
- **Property names are kept exactly as written**, escapes included, and compared as written: `{"\/":1,"/":2}` keeps both.
- String values are decoded and re-escaped: `\"`, `\\`, `\b \f \n \r \t`, any other control character as upper-case `\u00XX`, and everything else literal — `/`, DEL, non-ASCII, a decoded surrogate pair; an unpaired surrogate escape becomes U+FFFD.
- A plain number whose digits fit `decimal(38)` is kept as written, a negative zero losing its sign (`-0.0` → `0.0`).
- A number written with an exponent, or a plain one with more than 38 digits, is read as a double and written as `decimal(38, 10)`: the double's exact value rounded half away from zero, so `1e2` is `100.0000000000`, `1.5e-10` (a double just under 1.5e-10) is `0.0000000001` and `1e27` is `1000000000000000013287555072.0000000000`.
  Past `decimal(38, 10)`'s range it is Msg 1007 at class 16, state 5 for an exponent and state 3 for a long plain number.

Text that isn't a document is Msg 13609 state 9, positioned in **UTF-8 bytes** and naming a non-ASCII byte as a single-byte character, the vector reader's convention: only an object or an array is a document, a malformed token or string is named at its first byte, and running off the end names `.`.
The limits are Msg 13645 past 128 levels of nesting, Msg 13647 past 65535 members of one container, and Msg 13649 past 32768 distinct property names.

## Conversions and type pairs

Only the character strings convert to `json` (`text` / `ntext`, binaries, `xml`, `sql_variant` and every other class are Msg 529), and `json` converts to the character strings alone, explicitly — assigning it to a string is Msg 257, to anything but `vector` Msg 206.
A string converts implicitly the other way, so `json` outranks every string in a unification: `COALESCE(<json>, N'…')` is `json`, and the string arm is validated at run time.
A bounded string target too short for the canonical text is Msg 13639, never a truncation; `char(n)` / `nchar(n)` pad as usual.
`TRY_CAST` / `TRY_CONVERT` absorb Msg 13609 and 13639 but not Msg 1007 or 13645.
`vector` converts both ways, implicitly too: a vector writes its `%.7e` text, which the number rule turns into `decimal(38, 10)` elements, and a document reads as a vector with an object refused as Msg 13670 state 20 (`Key-Value Not Supported`) and a count mismatch as Msg 42204 at state 2 where text reports state 4.

The json row and column of the pair grids (`SqlType.PairRules.cs`) were probed against one member of each class in both orders, adding a `J` cell for Msg 13636.
Comparing two json values, `IN`, and comparing json with a bare `NULL` are Msg 13636 state 1; a sort or grouping slot (ORDER BY, GROUP BY, a window's PARTITION BY or ORDER BY) is state 2; `IS NULL` is the one comparison that works.
Against a string the comparison is Msg 402, the ordinary incompatible-operator error.
`DISTINCT` is Msg 421, a deduping set operator Msg 5335, `MIN` / `MAX` Msg 8117 state 1, and `COUNT(DISTINCT j)` Msg 8117 state 2, while `COUNT(j)` counts (probed 2026-10-02 against SQL Server 2025).
The string built-ins refuse it with Msg 8116 (state 1, `CHECKSUM` and `GREATEST` state 4), `CONCAT` with Msg 257 naming the string family it would convert to, and `SQL_VARIANT_PROPERTY` with Msg 206.
DDL refuses it as a key (Msg 1919 then 1750 for a constraint, Msg 1978 state 3 for an index or statistics), with a `COLLATE` (Msg 447 state 1) and as an alias type's base (Msg 13657); an included column, a clustered columnstore index, a `CHECK` over it and a `DEFAULT` for it are all accepted.
`ALTER COLUMN` from a string to `json` converts every row through the canonicalizer, and the other directions are refused by the assignment grid before any row is read.

## The JSON functions over a `json` document

Each function reads the canonical text as it reads any document, so what differs is typing and states:

- `JSON_QUERY` and `JSON_MODIFY` return `json` over a `json` document, `JSON_MODIFY` canonicalizing its edit (a `float` written as `1.5e0` lands as `1.5000000000`); `JSON_VALUE` returns the canonical number text.
- A strict-mode miss is Msg 13608 at state 5 from `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY`, 7 from `OPENJSON`'s document path and 8 from an `OPENJSON … WITH` column path.
- `JSON_MODIFY` writes a `json` value into a `json` document as the document it is, and refuses one over text (Msg 8116, argument 3; probed 2026-10-02 against SQL Server 2025).
- `JSON_OBJECT`, `JSON_ARRAY`, `JSON_ARRAYAGG` and `JSON_OBJECTAGG` return `json` when a value they embed is `json` or a trailing `RETURNING json` asks for it, and canonicalize the result; any other `RETURNING` target is Msg 102 state 19 (`JsonNullClauseParser.ParseReturning`).
  A `json` value embeds as the document it is, in the builders and in `FOR JSON`; `FOR XML` writes its text.
- `OPENJSON … WITH` accepts `AS JSON` on a `json` column; a `json` column without it reads NULL from a `json` document and converts the scalar's text from any other.

## `JSON_CONTAINS`

`JSON_CONTAINS(json, value [, path [, mode]])` (`Parser/Expressions/JsonContains.cs`) is `int`: 1 when a scalar the path selects equals the value, 0 when the path selects values and none does, NULL when the document is NULL or the path finds nothing.
The path takes the [advanced array accessors](json.md#advanced-array-accessors), and without one it is `$[*]` — the root array's elements, NULL over a root object; `strict` changes nothing.
Only a scalar can match, and the value's SQL type decides which: a number equals a JSON number of the same value (`1.0` matches `1`, and `1e2` stored as `100.0000000000` matches `100`), `bit` only `true` / `false`, a string only a JSON string — never across (`'1'` against `1`, `1` against `true`).
A JSON string is read as `varchar` in the database's collation, so a character its code page lacks becomes its best fit or `?` whatever the value's type (`N'ア'` fails to match `"ア"` where `N'?'` matches), and is compared under the value's collation with `=`'s trailing-space padding.
Mode 0 (and NULL) is `=`; mode 1 is `LIKE`, which only a string value reads, with no trailing-space slack; any other mode is Msg 13692, checked per row once the document is non-NULL.
A NULL value is 0, never a match for JSON `null`.

Every argument's type binds while compiling (Msg 8116): the document must be `json` (a bare `NULL` passes); the value an integer, `decimal` / `numeric`, `bit` or a non-LOB character string — `float` and `real`, money, the date/time types, binaries, `json` itself and a bare `NULL` are refused; the path a character string, a bare or evaluated NULL reporting "argument 2 of JSON_CONTAINS" at State 8 as the sibling path functions do; the mode `int` exactly.
Two to four arguments, else Msg 189.

## `JSON_VALUE … RETURNING`

Over a `json` document `JSON_VALUE(json, path RETURNING type)` returns the named type — the integer types, `bit`, `decimal` / `numeric`, `float` / `real`, the four character types with a length (in the database's collation), `date`, `time`, `datetime2`, `datetimeoffset`.
Any other type is Msg 102 State 29 near its name (`sys.vector` for `vector`, an alias type included), a character type without a length and any `RETURNING` over a text document Msg 102 near `RETURNING`.
The scalar converts as real's does: a string read as `varchar` in the database's collation (so `"ア"` is `?` even into `nvarchar`), a whole number as `int` or `bigint` and any other as `decimal`, `true` / `false` as `bit` — or as their own text into a character type.
Under lax a failed conversion, an overflow or a string longer than a bounded character target is NULL; under `strict` it is the conversion's own error, and the length Msg 8152 State 34; a conversion real never allows (a number or `bit` into `date`) is Msg 529 either way.

## The `modify` method

`UPDATE … SET col.modify(path, value)`, the same clause in a `MERGE`'s `UPDATE`, and `SET @var.modify(path, value)` rewrite the column or variable through `JSON_MODIFY`'s edit (`JsonModify.ParseMethod`), so every path rule, `append` and the advanced-accessor refusals carry over.
What differs: the method name matches without case and takes a `json` value (embedded as the document it is), a NULL receiver is Msg 5302 — which, as xml's does, ends the batch and rolls the transaction back as under `SET XACT_ABORT ON` — and its refusals name `modify` (Msg 313 / 8144 State 101 for the argument count, 8116 for a type).
It is the whole assignment: a second assignment to the column is Msg 264, a qualified `t.col.modify` or an operator after it Msg 102, and outside an assignment a json variable's method call is Msg 258 and a json column's Msg 4121.
A method call on a column of any other type but `xml` is Msg 258 naming the type.

## JSON indexes

`CREATE JSON INDEX name ON table (col) [FOR ('path' [, …])] [WITH (…)]` (`Simulation/Simulation.JsonIndex.cs`, stored as `Schemas/JsonIndex`) needs a clustered primary key of fewer than 32 columns (Msg 13672) and a `json` column (Msg 13680), refuses a temp table (Msg 13675) and a second JSON index on the column (Msg 13681), and takes ids from 1216000, one past the table's highest.
The paths are string literals kept as written (`$` without `FOR`), each well-formed (Msg 13607); a path using `[*]` is Msg 13683 State 2, any other advanced accessor State 3, and two paths where one is the other or leads to it — names compared without case, the mode keyword ignored — State 1.
`WITH` takes `FILLFACTOR` (Msg 129 outside 1-100), `PAD_INDEX`, `ALLOW_ROW_LOCKS`, `ALLOW_PAGE_LOCKS`, `OPTIMIZE_FOR_ARRAY_SEARCH`, `DATA_COMPRESSION`, `MAXDOP` and `DROP_EXISTING` (Msg 13685 when no JSON index of that name is on the column); another index option is Msg 153 State 35 and an unknown one Msg 155 then 153.
`sys.indexes` lists it as type 9 `JSON`, `sys.index_columns`, `INDEXPROPERTY` and `INDEX_COL` see its column, and `sys.json_indexes` / `sys.json_index_paths` carry its options and paths.
`ALTER INDEX … DISABLE` / `REBUILD` / `REORGANIZE`, `sp_rename` and `DROP INDEX … ON` work — `SET` and the resumable forms are Msg 13688, a `PARTITION = n` on `REBUILD` / `REORGANIZE` Msg 7731 state 5, `REBUILD` with `ONLINE` / `IGNORE_DUP_KEY` / `STATISTICS_INCREMENTAL` / `SORT_IN_TEMPDB` / `STATISTICS_NORECOMPUTE` / `XML_COMPRESSION` turned on Msg 153 (states 37 / 36 / 40 / 42 / 43 / 45, the option named in capitals except the two lower-case ones), `REORGANIZE` of a disabled one Msg 1973 and `COMPRESS_ALL_ROW_GROUPS = ON` Msg 35375 (probed 2026-09-30 against SQL Server 2025) — the `table.index` form of the drop is Msg 3766; the index blocks `DROP COLUMN` / `ALTER COLUMN` (Msg 5074, ahead of the type grid) and dropping the primary key (Msg 3767); a name shared with any other index on the table is Msg 1913.

## Catalog surfaces

`sys.types` / `systypes` carry the json row; `sys.columns` / `sys.parameters` report 244 / 244 / -1 / 0 / 0 with no collation; `TYPE_ID('json')` and `TYPE_NAME(244)` resolve unqualified; `COL_LENGTH` and `COLUMNPROPERTY` Precision are -1; `INFORMATION_SCHEMA.COLUMNS` reports `json` / -1 / -1; `sp_help` reports Length -1 (a parameter's Prec 0); `sp_columns` lists no row for a json column; the describe surfaces report `json`, 244, max length -1.

## Client surface

The in-process reader surfaces the canonical text (`GetFieldType` string, `GetDataTypeName` `json`).
Over the TDS endpoint a client that negotiates json support (SqlClient 6+) receives the native json type, `GetDataTypeName` `json`; one that doesn't receives `varchar(max)` collated `Latin1_General_100_BIN2_UTF8`, the form Microsoft's json data type page gives for a TDS 7.4 client without json support — see [`tds-endpoint.md`](tds-endpoint.md).

## Divergences

- **The describe surfaces always answer `json`**, as real does to a json-aware client.
- **Real refuses an `append` path beside another `JSON_MODIFY` over json in one select list** with Msg 13656 (`JSON data type cannot be used when its feature switch is off.`), though either call alone works, and the same Msg 13656 (State 8) meets a `SET @j.modify('append …', …)` that follows a `SELECT @j = …` in its batch (probed 2026-09-27); the simulator runs them.
- **The size model is fitted, not derived**: documents shaped unlike any probed one (very long property names, objects past 64 KB) may report a different `DATALENGTH`.
- **A JSON index accelerates nothing**: it is catalog metadata, and reads never consult it.
- **A deferred statement's Msg 8116 / 313 / 8144 from the `modify` method ends only its statement**, where real ends the batch.

## Not modeled yet

- A JSON index's internal table (`json_index_<object_id>_<index_id>` in `sys.objects` and `sys.internal_tables`) and the internal index `sys.indexes` lists beside it.
- json columns in BACPAC import.
