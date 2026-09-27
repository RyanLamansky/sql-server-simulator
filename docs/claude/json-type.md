# `json` data type

SQL Server 2025's native `json`: declaration, the canonical text real hands back, the length of its binary form, the refusals of text that isn't a document, conversions, the refusals of a type with no ordering, how the JSON functions read and return it, and the catalog and wire surfaces.
The JSON functions themselves live in [`json.md`](json.md); this file owns only what the type changes.
Everything here was probed 2026-09-26 against SQL Server 2025; `JsonTypeTests` (Tests) and `JsonWireTests` (Tests.SqlClient) are the contract.

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
`DISTINCT` is Msg 421, a deduping set operator Msg 5335, `MIN` / `MAX` Msg 8117 state 1, and — unlike `xml` — `COUNT(j)` is Msg 8117 state 2 even without `DISTINCT`.
The string built-ins refuse it with Msg 8116 (state 1, `CHECKSUM` and `GREATEST` state 4), `CONCAT` with Msg 257 naming the string family it would convert to, and `SQL_VARIANT_PROPERTY` with Msg 206.
DDL refuses it as a key (Msg 1919 then 1750 for a constraint, Msg 1978 state 3 for an index or statistics), with a `COLLATE` (Msg 447 state 1) and as an alias type's base (Msg 13657); an included column, a clustered columnstore index, a `CHECK` over it and a `DEFAULT` for it are all accepted.
`ALTER COLUMN` from a string to `json` converts every row through the canonicalizer, and the other directions are refused by the assignment grid before any row is read.

## The JSON functions over a `json` document

Each function reads the canonical text as it reads any document, so what differs is typing and states:

- `JSON_QUERY` and `JSON_MODIFY` return `json` over a `json` document, `JSON_MODIFY` canonicalizing its edit (a `float` written as `1.5e0` lands as `1.5000000000`); `JSON_VALUE` returns the canonical number text.
- A strict-mode miss is Msg 13608 at state 5 from `JSON_VALUE` / `JSON_QUERY` / `JSON_MODIFY`, 7 from `OPENJSON`'s document path and 8 from an `OPENJSON … WITH` column path.
- `JSON_MODIFY`'s written value may not be `json` (Msg 8116, argument 3).
- `JSON_OBJECT`, `JSON_ARRAY`, `JSON_ARRAYAGG` and `JSON_OBJECTAGG` return `json` when a value they embed is `json` or a trailing `RETURNING json` asks for it, and canonicalize the result; any other `RETURNING` target is Msg 102 state 19 (`JsonNullClauseParser.ParseReturning`).
  A `json` value embeds as the document it is, in the builders and in `FOR JSON`; `FOR XML` writes its text.
- `OPENJSON … WITH` accepts `AS JSON` on a `json` column; a `json` column without it reads NULL from a `json` document and converts the scalar's text from any other.

## Catalog surfaces

`sys.types` / `systypes` carry the json row; `sys.columns` / `sys.parameters` report 244 / 244 / -1 / 0 / 0 with no collation; `TYPE_ID('json')` and `TYPE_NAME(244)` resolve unqualified; `COL_LENGTH` and `COLUMNPROPERTY` Precision are -1; `INFORMATION_SCHEMA.COLUMNS` reports `json` / -1 / -1; `sp_help` reports Length -1 (a parameter's Prec 0); `sp_columns` lists no row for a json column; the describe surfaces report `json`, 244, max length -1.

## Client surface

The in-process reader surfaces the canonical text (`GetFieldType` string, `GetDataTypeName` `json`).
The TDS endpoint acknowledges no json feature extension and sends a json column as `varchar(max)` collated `Latin1_General_100_BIN2_UTF8`, the form Microsoft's json data type page gives for a TDS 7.4 client without json support.

## Divergences

- **A json-aware client reads text.**
  Real sends SqlClient 6+ the native json type; the endpoint sends every client the down-level `varchar(max)`.
- **The describe surfaces always answer `json`**, as real does to a json-aware client.
- **Real refuses an `append` path beside another `JSON_MODIFY` over json in one select list** with Msg 13656 (`JSON data type cannot be used when its feature switch is off.`), though either call alone works; the simulator runs both.
- **The size model is fitted, not derived**: documents shaped unlike any probed one (very long property names, objects past 64 KB) may report a different `DATALENGTH`.

## Not modeled yet

- `JSON_CONTAINS` (real takes only a `json` document, refusing `varchar` / `nvarchar` with Msg 8116) and the `[*]` wildcard path it reads.
- The json `modify` method (`UPDATE t SET j.modify('$.a', 1)`), `CREATE JSON INDEX` and `sys.json_index_paths` rows.
- `JSON_VALUE … RETURNING <type>`.
- The native json TDS type a json-aware client negotiates, a json parameter sent by such a client over RPC, and json columns in BACPAC import.
- `sp_help 'json'` — `sp_help` answers no system type name.
