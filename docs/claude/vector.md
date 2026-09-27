# `vector` data type

SQL Server 2025's `vector(n [, float32])`: storage, the text form in both directions, conversions, the refusals of a type with no ordering, the four vector built-ins and the catalog surfaces.
Everything here was probed 2026-09-26 against SQL Server 2025; `VectorTests` (Tests) and `VectorWireTests` (Tests.SqlClient) are the contract.

## Type and storage

`VectorSqlType` (`Storage/VectorType.cs`) is one singleton per dimension count, 1 through 1998, `system_type_id` 165 (varbinary's) under `user_type_id` 255.
A value is its storage bytes: the 8-byte header SqlClient's vector wire format uses (`0xA9`, version 1, the count as `uint16`, base type 0, three zero bytes) and then the float32 elements, so `DATALENGTH(vector(3))` is 20 and `sys.columns.max_length` is `8 + 4n`.
The only base type is `float32`; the second argument is read by `TypeNameSynonyms.ReadSecondTypeArgument` at every type-spec site, which is what turns `float16` (a preview feature on real) into Msg 195.

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
  Their kernel (`VectorArguments.FusedSum`) is eight lanes with fused multiply-adds; dot and euclidean run four accumulator sets over successive blocks of eight and combine them pairwise, add the remaining whole blocks, add the partial block into the low lanes, and halve the lanes (i + 4, + 2, + 1).
  The cosine's cross product alone runs two sets and finishes by summing the lanes in order and multiply-adding the leftover elements; its two norms use the dot kernel.
  The structure came from cancellation probes (`M` and `−M` placed at every pair of positions among ones, so which sums absorb a 1 maps the reduction tree), and the variants were then fitted against 25 random-vector results from 5 to 1536 dimensions, every one of which matches.
- Dot is the negated sum (so two zero vectors give −0); a sum that reaches infinity is Msg 8115.
  Cosine is 1 when either vector is all zeros, and its similarity is clamped to [−1, 1] with NaN read as 1 — which is how two vectors whose norms overflow come out at distance 0.
- `VECTOR_NORM` answers in double: eight float32 lanes of magnitudes or float32 squares without fused multiplies, the lanes summed in order in double and the elements past the last full block added exactly.
  `VECTOR_NORMALIZE` divides each element by that norm narrowed to float32, so a norm past float32's range normalizes to zeros, as does a zero norm.

## Catalog surfaces

`sys.types` / `systypes` carry the vector row; `sys.columns` / `sys.all_columns` / `sys.parameters` fill `vector_dimensions`, `vector_base_type` (0) and `vector_base_type_desc` (`float32`).
`TYPE_ID('vector')` is NULL while `TYPE_ID('sys.vector')` is 255, `TYPE_NAME(165)` stays `varbinary`, `COLUMNPROPERTY` Precision is the storage length, `sp_help` reports the column as varbinary-like (`TrimTrailingBlanks` no, `FixedLenNullInSource` yes), `sp_columns` / `sp_columns_100` list no row for a vector column, and the describe surfaces report `vector(n)` with user type 255 and TDS type 245.

## Client surface

The simulator core doesn't reference SqlClient, so the in-process reader can't hand back `SqlVector<float>`: it surfaces the text form (`GetFieldType` string, `GetDataTypeName` `vector`, `GetValue` / `GetString` the text).
The TDS endpoint acknowledges no vector feature extension and sends a vector column the way real sends it to a client without vector support: `varchar(max)` collated `Latin1_General_100_BIN2_UTF8`, holding the text form.

## Divergences

- **A vector-aware client reads text.**
  Real sends SqlClient 6.1+ the binary vector type (`SqlVector<float>`); the endpoint sends every client the down-level `varchar(max)`, so a new client's `GetDataTypeName` is `varchar` where real's is `vector`.
- **The describe surfaces always answer as for a vector-aware client** (`vector(n)`, 165 / 255, TDS 245); to an old client real answers `varchar(max)` there too.
- **`vector(3, float16)` ends its statement.**
  Real reports Msg 195 and carries on with the batch (a following use of the variable is Msg 137); the simulator's Msg 195 ends the batch.
- **A bare `NULL` normalizes to `vector(1)`**: `VECTOR_NORMALIZE(NULL, …)` has no dimension count to take, and the simulator needs one.
- **`sys.parameters.vector_base_type`** stays NULL for a vector parameter (its sibling columns are filled).

## Not modeled yet

- Vector indexes (`CREATE VECTOR INDEX`, DiskANN) and `VECTOR_SEARCH`.
- The `float16` base type (a preview feature on real).
- A vector parameter sent by a vector-aware client over RPC, and vector columns in BACPAC import.
