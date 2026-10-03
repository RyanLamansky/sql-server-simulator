# `vector` data type

SQL Server 2025's `vector(n [, float32 | float16])`: storage, the text form in both directions, conversions, the refusals of a type with no ordering, the four vector built-ins, the catalog surfaces, and the preview surface — the `float16` base type, vector indexes and `VECTOR_SEARCH`.
The type and its functions were probed 2026-09-26 against SQL Server 2025, the preview surface 2026-09-29; `VectorTests` / `VectorSearchTests` (Tests) and `VectorWireTests` (Tests.SqlClient) are the contract.

## Type and storage

`VectorSqlType` (`Storage/VectorType.cs`) is one singleton per dimension count and base type — 1 through 1998 for float32 — `system_type_id` 165 (varbinary's) under `user_type_id` 255.
A value is its storage bytes: the 8-byte header SqlClient's vector wire format uses (`0xA9`, version 1, the count as `uint16`, base type 0, three zero bytes) and then the float32 elements, so `DATALENGTH(vector(3))` is 20 and `sys.columns.max_length` is `8 + 4n`.
The second argument is read by `TypeNameSynonyms.ReadSecondTypeArgument` at every type-spec site: `float32`, or `float16` in a database whose `PREVIEW_FEATURES` scoped configuration is on (see [the float16 base type](#the-float16-base-type)); anything else, `float16` without the switch included, is Msg 195 and ends the batch.

## Text form

Writing: each element as C's `%.7e` with a three-digit exponent, comma-separated, no spaces, negative zero as positive (`[1.0000000e+000,-5.0000001e-002]`).
Reading: `VectorSqlType.Parse` walks the text's **UTF-8 bytes**, which is why a Msg 13609 position counts bytes and names the offending byte as a single-byte character (`é` is reported as `Ã` at its first byte).
The reader's order of refusal is its own and matters:

- A bare token (number or literal) runs to whitespace or punctuation; a malformed one is Msg 13609 at its **first** byte (`[1.5x]` names the `1`).
- A non-number value is Msg 13670 as soon as it is read — `null`, a string, `true` / `false` (whose phrases differ in capitalization) — so `["a", 1e39]` is the string error and `[1e39, "a"]` the range error.
- A nested container is judged by its first member: `[[` runs off the end (13609), `[[]` is the empty-array error, anything else is `Malformed JSON`; objects likewise (`{}` has its own misspelled phrase).
- A root-level number is `Malformed JSON`; the dimension count (Msg 42204 state 4) is checked only after a complete array with nothing but whitespace after it.
- An element is parsed as a double and narrowed; only a narrowing to infinity is Msg 42241, so `3.4028235e38` is accepted and `1e-50` reads as 0.

## Conversions and type pairs

Only the character strings and `json` convert, both ways, implicitly and explicitly (`text` / `ntext`, binaries and `sql_variant` are Msg 529); the `json` pair is described in [`json-type.md`](json-type.md#conversions-and-type-pairs).
A bounded target too short for the whole text is Msg 42211, never a truncation, and `TRY_CAST` / `TRY_CONVERT` absorb that and every text-reading error (`Cast.IsVectorConversionFailure`).
The vector row and column of the pair grids (`SqlType.PairRules.cs`) were probed against one member of each class in both orders; the grid gained an `o` cell (Msg 8117 naming the right operand) for it.
Two vectors of different counts are Msg 42204 state 1 in a unification or assignment; a set of CASE / COALESCE arms names the settled arm's count first, where `ISNULL` names the replacement's.
A vector unifies with a character string as a `varbinary` of its storage length would, so `COALESCE(<vector(3)>, '[1,2,3]')` is `varchar(20)` and fails at run time with Msg 42211 — real's own trap, mirrored.

`SqlType.IsIncomparable` (the LOB types plus vector) is the predicate behind the DISTINCT, set-operator, sort, grouping, hash-key, MIN / MAX, CHECKSUM and GREATEST gates; the per-slot errors (42213 in a sort or grouping slot, 421, 5335, 8117) are real's.
The string built-ins refuse a vector with Msg 8116 at the state real gives each — 1 for most, 6 for `REPLACE` / `TRIM` / `HASHBYTES` / `LIKE` / `SQL_VARIANT_PROPERTY`, 9 for `CONCAT`, 4 for `CHECKSUM` (naming `vector(n)`) and `GREATEST`, and 1 then 6 for `STRING_AGG`.
DDL refuses a vector as a key (1919 + 1750 for a constraint, 1978 for an index or statistics), in a CHECK (1760 + 1750), under a DEFAULT (1752 + 1750, NULL included), with a COLLATE (447) and as an alias type's base (42212); an included column and a clustered columnstore index accept it.

## Functions

`Parser/Expressions/VectorFunctions.cs`.
All four are nondeterministic to real (a persisted computed column over one is Msg 4936); a NULL argument answers NULL, except that a bare `NULL` vector is refused by `VECTORPROPERTY` as `varbinary` at state 36.
Metric, norm and property names match case-insensitively with trailing spaces set aside (a `char(10)` `'dot'` works, a leading space doesn't).

The results are **bit-exact** to real's, which took reverse-engineering its SIMD kernels, because a float32 sum's last bits depend on the order it adds in:

- The distances work in float32 and widen the result.
  Their kernel (`VectorArguments.FusedSum`) is eight lanes with fused multiply-adds: a main loop takes four blocks of eight at a time into accumulator sets, the sets combine pairwise, the remaining whole blocks add into the combined lanes, the partial block into the low lanes, and the lanes halve (i + 4, + 2, + 1).
  Dot and euclidean give each block of the four its own set; the cosine sums its cross product and both norms with the four blocks alternating between two sets.
  The structure came from cancellation probes (`M` and `−M` placed at every pair of positions among ones, so which sums absorb a 1 maps the reduction tree).
  The cosine's variant was refitted after a 25-vector fit proved wrong for about one pair in six: against 700 random pairs of 1 to 1998 dimensions (probed 2026-09-29), every cosine and dot result matches.
- Dot is the negated sum (so two zero vectors give −0); a sum that reaches infinity is Msg 8115.
  Cosine is 1 when either vector is all zeros, and its similarity is clamped to [−1, 1] with NaN read as 1 — which is how two vectors whose norms overflow come out at distance 0.
- `VECTOR_NORM` answers in double: eight float32 lanes of magnitudes or float32 squares without fused multiplies, the lanes summed in order in double and the elements past the last full block added exactly.
  `VECTOR_NORMALIZE` divides each element by that norm narrowed to float32, so a norm past float32's range normalizes to zeros, as does a zero norm.

## Catalog surfaces

`sys.types` / `systypes` carry the vector row; `sys.columns` / `sys.all_columns` / `sys.parameters` fill `vector_dimensions`, `vector_base_type` (0, or 1 for float16) and `vector_base_type_desc` (`float32` / `float16`).
`TYPE_ID('vector')` is NULL while `TYPE_ID('sys.vector')` is 255, `TYPE_NAME(165)` stays `varbinary`, `COLUMNPROPERTY` Precision is the storage length, `sp_help` reports the column as varbinary-like (`TrimTrailingBlanks` no, `FixedLenNullInSource` yes), `sp_columns` / `sp_columns_100` list no row for a vector column, and the describe surfaces report `vector(n)` with user type 255 and TDS type 245.

## Client surface

The simulator core doesn't reference SqlClient, so the in-process reader can't hand back `SqlVector<float>`: it surfaces the text form (`GetFieldType` string, `GetDataTypeName` `vector`, `GetValue` / `GetString` the text).
Over the TDS endpoint a client that negotiates vector support (SqlClient 6.1+) receives a float32 vector as the native type and reads `SqlVector<float>`; a float16 vector, and any vector sent to a client that didn't negotiate, travels as `varchar(max)` collated `Latin1_General_100_BIN2_UTF8` holding the text form, as real sends it (captured 2026-10-02 through SqlClient 7.0.2) — see [`tds-endpoint.md`](tds-endpoint.md).

## The float16 base type

`vector(n, float16)` needs `PREVIEW_FEATURES` on in the database compiling the type spec.
A value is the same 8-byte header with base type 1 and two bytes per element, so `DATALENGTH` and `max_length` are `8 + 2n`, and a declaration may reach 3996 dimensions (Msg 2717 at state 5 naming a column, 2 elsewhere).
Text reads as for float32 — each element parsed as a double and narrowed to float32 — then rounds to half precision, ties to even: `0.1` stores `9.9975586e-002`, `2049` stores `2048`, `1e-5` the nearest subnormal, and `1e-8` zero.
An element past float32's range is Msg 42241 state 1 and one past half precision's (65520 and up) state 2, both naming `float16`.
The text form prints each element as a float32 would print it.

The two base types never meet:

- `CAST`, an assignment, `ALTER COLUMN` or a unification between them is Msg 42238 while the batch compiles; an assignment converts the source to the target's base type, and unified arms settle on float32, so the message reads *from float16 to float32* whichever arm comes first.
- `VECTOR_DISTANCE` over one of each is Msg 42243 as the statement runs, which ends the batch; `VECTOR_SEARCH` with a query vector of the other base type raises the same.
- `VECTOR_NORM` and `VECTOR_NORMALIZE` refuse a float16 vector outright, Msg 42246 while compiling; `VECTORPROPERTY(v, 'BaseType')` answers `float16`.

Distances widen each element to float32 and run the float32 kernels, which is bit-exact to real's (probed on nine-dimension pairs across all three metrics).
Every describe surface reports a float16 column as the `varchar(max)` `Latin1_General_100_BIN2_UTF8` text it is sent as, even to a vector-aware client, and `FOR JSON` embeds either base type as the array its text is.

## Vector indexes

`CREATE VECTOR INDEX name ON table (col) WITH (METRIC = 'cosine' | 'euclidean' | 'dot' [, TYPE = 'DiskANN'] [, MAXDOP = n]) [ON filegroup]` (`Simulation.VectorIndex.cs`) records a `Schemas.VectorIndex` on the table and builds nothing: the search reads the table.
Without `PREVIEW_FEATURES` the statement is Msg 343 compiling the batch.

- **Grammar.** The `WITH` list is required (Msg 102 at the column list's `)`), and `METRIC` in it (Msg 153 state 7); a metric or type real doesn't know, or one written unquoted, is Msg 102 at the value, trailing spaces and case aside.
  `MAXDOP` takes 0 to 32767 (Msg 304 echoing the value as written).
  Any relational index option is Msg 155 state 6, an unknown one Msg 155 (and Msg 153 when given a number).
- **Refusals running, in real's order.** Inside a user transaction Msg 574 state 31, and a SET option an indexed view would refuse Msg 1934 naming `CREATE VECTOR INDEX`; then a temp table (Msg 42220), a missing table (Msg 1088), a missing column (Msg 1911), a column that isn't a vector (Msg 42215), a name the table already uses (Msg 1913 state 203), a second vector index on the column (Msg 42230), and a table without a clustered primary key on one `int` column (Msg 42217 — state 1 without one, 2 when its column has another type).
  An empty table and NULL vectors are accepted.
- **Build output.** Success sends Msg 8625, class 0 — real's build runs a query with a join hint — and raises no `CREATE_INDEX` DDL event, where `DROP INDEX` raises `DROP_INDEX`.
- **Catalog.** `sys.indexes` lists it at type 8 `VECTOR` from `index_id` 1152000, every option at its default; `sys.index_columns` lists the column with no key ordinal; `sys.vector_indexes` adds `vector_index_type` (`DiskANN`), `distance_metric` (upper case) and `build_parameters`.
  `sp_helpindex`, `sp_help`, `sys.stats` and `sys.partitions` leave it out, and `INDEXPROPERTY` answers as for an XML index.
- **`build_parameters`** is `{"StartId":"…", "L":"48", "M":"8", "R":"48"}`; the three bounds are constant at every dimension count probed.
  `StartId` is the key of the vector nearest the mean of the table's vectors by the index's metric, visiting rows in key order with the second ahead of the first and only a strictly nearer one displacing the held one — the rule that reproduces which of several equally near rows real names — or `0` for a table without vectors; over a float16 column it is simply the lowest key.
  The rule matched every probed table, generated ones of up to 1000 rows and ties of every shape among them.

While the index exists the table is **read-only**, as real's DiskANN build of this release leaves it:

- Any statement writing it — `INSERT`, `UPDATE`, `DELETE`, `MERGE`, a write through a view — is Msg 42231, raised where real's optimizer meets the statement and ending the batch; `TRY` catches it inside dynamic SQL.
  A batch without DDL is optimized before it runs, so even a write in an untaken branch refuses the whole batch; a batch holding any `CREATE`, `ALTER` or `DROP` is optimized statement by statement, so its untaken writes pass and a taken one fails when it runs (`BatchContext.DeferredOptimizerError`).
  A module body is optimized when it runs, so creating one that writes the table succeeds.
- A `DELETE` whose foreign keys would cascade (or set NULL or a default) into such a table is Msg 42231 state 3 naming the child; `TRUNCATE TABLE` is Msg 42232.
- `ALTER INDEX` in any form reaching it is Msg 42250; dropping or altering the column is Msg 5074 then 4922, dropping the primary key Msg 3768 then 3727, `sp_rename` Msg 290 after its caution, and `DROP INDEX table.index` Msg 3766 state 1 while compiling.
  `DROP INDEX name ON table` removes it, and the table takes writes again.

## `VECTOR_SEARCH`

`VECTOR_SEARCH(TABLE = t [AS a], COLUMN = col, SIMILAR_TO = q, METRIC = 'm', TOP_N = n) [AS s]` (`Parser/Selection.VectorSearch.cs`) is the source form of the search.
This release takes exactly those five arguments in that order; the `SELECT TOP (n) WITH APPROXIMATE` form Microsoft's documentation describes for later index versions is a syntax error here, as is a table hint after the source.

- **Two members.** Real exposes the table's columns under the table's alias (or name) and `distance` under the function's alias, `distance` first in a `SELECT *`; the simulator builds exactly that pair — a rowset of `(distance, key)` joined on the clustered key to an ordinary source over the table — as a parenthesized join group, so JOIN, APPLY, outer joins, ON scope and the exposed-name collisions (Msg 1011 / 1012 / 1013) follow the ordinary rules.
  The key column is hidden from a star.
  `s.id` is Msg 207, and `distance` is `float NOT NULL`.
- **Arguments.** `TABLE` names a table (Msg 208 state 240 when missing, Msg 42217 state 4 for a view, Msg 102 for a table variable).
  `COLUMN` is one bare name (Msg 207 state 20 when missing, Msg 42226 when not a vector).
  `SIMILAR_TO` is a variable, a literal or a column of an enclosing scope or an APPLY's left side — never the searched table's own — anything else Msg 102, and a non-vector type Msg 8116 naming `vector_distance`.
  `METRIC` is a string literal; the table must carry a vector index on the column with that metric, else Msg 42227, the metric as written.
  `TOP_N` is an `int` literal, a variable or an outer column: another literal is Msg 1060, a sign or an expression Msg 102, a NULL count Msg 1014 as the statement runs.
  A missing `TOP_N` is Msg 102 naming `TOP_N`, and a query vector of another dimension count Msg 42204 state 3 as the statement runs.
  Without `PREVIEW_FEATURES` the call is Msg 156 at `TABLE`, and the positional form is Msg 208 naming `vector_search`.
- **Result.** The nearest `TOP_N` non-NULL vectors, in distance order with ties in key order; each `distance` equals `VECTOR_DISTANCE` over the same pair, bit for bit.
  A `WHERE` filters the rows the search returned, so it can return fewer than `TOP_N`, as real's post-filtering does.

**Real's search is approximate; the simulator's is exact.**
Probed 2026-09-29 on 8-dimension tables of generated vectors, 20 to 50 queries each at `TOP_N` 10:

| Rows | euclidean | cosine | dot |
|---:|---|---|---|
| 50 | exact | exact | exact |
| 200 | exact | 2 of 500 rows missed | 7 of 500 missed |
| 300 | exact | exact | several misses |
| 1000 | exact | 1 of 500 missed | 20 of 500 missed |
| 2000 | 1 of 500 missed | exact | 67 of 500 missed |
| 5000 | 10 of 500 missed (`TOP_N` 100: 16 of 5000; 1000: 2 of 50000) | | |

A miss is a row of the exact nearest set real didn't return, a farther one taking its place.
Duplicated vectors are the other gap: of several identical vectors real can leave some unreachable in its graph at any size — four equal rows among seven returned only three — where the simulator returns them all.

## Divergences

- **The describe surfaces always answer as for a vector-aware client** (`vector(n)`, 165 / 255, TDS 245); to an old client real answers `varchar(max)` there too.
- **`VECTOR_SEARCH` answers exactly** where real's DiskANN graph approximates, above — and a statement with more than one binder error can repeat real's leading one (a sibling reference in its arguments is Msg 4104 three times on real, once here).
- **Real ends the session** for an inline function whose body is a `VECTOR_SEARCH` (Msg 596 and a stack dump, probed 2026-09-29); the simulator runs it.
- **The internal tables** a vector index owns on real (`vector_index_Graph_Edge_table_…`, `vector_index_quantization_table_…`, type `IT` in `sys.objects` and `sys.internal_tables`) aren't listed.
- **A second float16 refusal in one batch** — `VECTOR_NORM` and `VECTOR_NORMALIZE` both over float16 vectors — reports both where real stops at the first.
- **A bare `NULL` normalizes to `vector(1)`**: `VECTOR_NORMALIZE(NULL, …)` has no dimension count to take, and the simulator needs one.
- **`sys.parameters.vector_base_type`** stays NULL for a vector parameter (its sibling columns are filled).
- **Real's kernel depends on the CPU, the simulator's doesn't.**
  The bit-exact kernels above are the ones real runs on a CPU with FMA (probed on a Ryzen 7800X3D); on an Ivy Bridge 3770K without FMA, real's `VECTOR_DISTANCE` and `VECTOR_NORM` answer differently in the last bits (probed 2026-10-03 against SQL Server 2025 RTM-GDR 17.0.1135.8, a different build from the reference's CU7, so a version change isn't fully ruled out).
  Its dot kernel there fits 33 of 36 probed dimension counts as four SSE lanes with the multiply and the add rounded separately, two accumulator sets combined lane-wise, a leftover block of four into the combined lanes, the last one to three elements in sequence into lane 0, and lanes halved into each other; the three misses (15, 63 and 999 dimensions) are each one ulp off, all with three leftover elements after a main-loop pass.
  The simulator computes the FMA kernels on every CPU — its fused multiply-add is exact in software where the instruction is missing — so it answers as real does on current hardware wherever it runs.

## Not modeled yet

- A vector parameter sent by a vector-aware client over RPC, and vector columns in BACPAC import.
- The later vector-index version Microsoft documents — writable tables, `TOP (n) WITH APPROXIMATE`, iterative filtering, `FORCE_ANN_ONLY`, `sys.dm_db_vector_indexes` — which this release of SQL Server 2025 doesn't take.
- A cascading `UPDATE` into a vector-indexed table, which writes it here; a cascading `DELETE` is refused as real's is.
