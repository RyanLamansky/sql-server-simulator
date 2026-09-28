# Dynamic Data Masking

A column declared or altered `MASKED WITH (FUNCTION = '…')` reads through its masking function for any principal without `UNMASK`, while `dbo`, `db_owner`, `CONTROL` and an `UNMASK` grant see the stored value.
Everything below was probed 2026-09-27 against SQL Server 2025 unless it says otherwise.

The pieces: `MaskingFunction` (`Storage/`) parses, validates and applies one function; `HeapColumn.MaskingFunction` is the catalog truth; `DataMask` (`Parser/`) is the compile-time answer to "how does this output column mask, and which masked table columns does it read"; `DataMasking` applies those answers at a statement's output for the executing principal.

## DDL

`MASKED WITH ( FUNCTION = 'text' )` sits after the type, `COLLATE` and `SPARSE` and before every other column clause — after `NULL`, `DEFAULT`, `IDENTITY`, a key, `ROWGUIDCOL` or `GENERATED ALWAYS` it is Msg 102 near `masked`, and `COLLATE` or `SPARSE` after it is a syntax error too.
It is accepted in `CREATE TABLE`, `ALTER TABLE … ADD`, a `#temp` table, a table variable and `CREATE TYPE … AS TABLE`, on every type including `rowversion`, the CLR types, `sql_variant`, `xml`, `json` and a key or sparse column; a computed column takes no mask (Msg 102).
The function text may be an `N'…'` literal.

`ALTER TABLE … ALTER COLUMN c ADD MASKED WITH (…)` sets or replaces the mask and `DROP MASKED` clears it (Msg 16007 when there is none); both are metadata changes a rolled-back transaction reverses.
A computed column is Msg 4928, and a column a computed column reads is Msg 5074 then Msg 4922 state 9, whichever way; an index on the column doesn't matter.
`ALTER COLUMN c <type>` drops the mask — even to the same type — unless it restates one: `ALTER COLUMN c varchar(20) MASKED WITH (…) NULL`, the clause between `COLLATE` and the nullability.
`SELECT … INTO` never copies a mask.

### The function grammar

The text is read from its first `(` to its last `)`, and the checks run in this order:

| Case | Result |
|---|---|
| empty text | Msg 16002 |
| no `(` | Msg 16006 naming the whole text (`'default'`, `'default)'`) |
| no `)` after the `(` | Msg 16006 naming what precedes the `(` |
| anything after the last `)` | Msg 16004 naming what precedes the `(`, unrecognized or not (`'  Default ( ) '` reports `'  Default '`) |
| a name other than the five, case-insensitively, spaces included (`' default()'`, `'default ()'`) | Msg 16002 |
| a function the column's type doesn't take | Msg 16003, the function named in lowercase |
| the wrong argument count (whitespace alone is none) | Msg 16004 |
| a bad argument | Msg 16005 |

All six are raised at state 0.
Type legality: `default()` takes any type; `email()` and `partial()` the character types, `text` / `ntext` included, but not binary; `random()` the numeric types; `datetime()` the date and time types, `date` only with `Y` / `M` / `D` and `time` only with `h` / `m` / `s`.
`partial(prefix, "padding", suffix)` takes unsigned digit strings (leading zeros allowed, `-1` and `1.0` refused) and a double-quoted padding with `""` escaping a quote; a single-quoted padding is Msg 16005.
`random(low, high)` takes plain decimal literals — an optional leading minus, no exponent, no `+`, no quotes — converted to the column's type as an assignment would, so a decimal column rounds them to its scale and a bound the type can't hold is refused; `low > high` is refused, a `bit` takes only 0 and 1, and an integer column refuses a fraction.
`datetime("X")` takes one case-sensitive double-quoted part.

`sys.masked_columns.masking_function` is the canonical form: the lowercase name and each argument re-rendered, joined by `", "` — `partial(1, "a""b", 0)`, `random(1.56, 3.00)` for a `decimal(6,2)`, `random(1, 2.1235)` for `money` (rounded to four places, trailing zeros dropped), `random(1.5, 2.5)` for a float written `1.50, 2.500`, `-0` kept for a float.

## Catalog

`sys.masked_columns` lists a table's and a table type's masked columns — `sys.columns`' row, then `definition` NULL, `uses_database_collation` and `is_persisted` 0, `masking_function`; `sys.columns.is_masked` is 1 for them, and `COLUMNPROPERTY(…, 'IsMasked')` answers.
A view's column is never masked in the catalog, though it masks when read.
`OBJECTPROPERTY(…, 'TableHasMaskedColumns')` is NULL on real and here, and `sp_help` / `sp_columns` / `INFORMATION_SCHEMA.COLUMNS` show nothing of the mask.
`masking_function` carries the catalog collation, so concatenating it with `name` is Msg 451, as on real.

## UNMASK

`GRANT` / `DENY` / `REVOKE UNMASK` at database scope, `ON SCHEMA::`, `ON` a table, and on a table's columns (`ON t(c)` or `UNMASK (c) ON t`); a view or any other object kind is Msg 4606, and the rows project through `sys.database_permissions` as type `UMSK`.
The ordinary checker answers: a column DENY masks that column under a database-wide GRANT, a role grant counts, `db_owner` and `CONTROL` on the table or database unmask, and `HAS_PERMS_BY_NAME(…, 'UNMASK')` reads it.
An output column reading several masked columns is unmasked only when every one is.
`UNMASK` is checked on the table a view or module reads, and ownership chaining doesn't unmask: a `dbo` procedure or view read by a restricted principal returns masked values.
A `#temp` table's creator reads it unmasked; a table variable masks for its reader and is unmasked by a database-scope grant (an assumption — a table variable has no object grant to probe).

## What a principal without UNMASK reads

The functions:
- `default()`: `xxxx` cut to a shorter declared length (`char(2)` → `xx`, `nchar(1)` → `x`), sent unpadded for a fixed-length type; the single byte `0x30` for `binary` / `varbinary` / `image`, whatever `binary(n)`'s length; zero; `1900-01-01 00:00:00` (at `+00:00` for `datetimeoffset`, `00:00:00` for `time`); the empty GUID; eight zero bytes for `rowversion`; `<masked />`; `{"masked":true}`; the byte `0x00` for `hierarchyid`, which nothing can decode.
  A `sql_variant` masks as its base type does, but a character payload is always the whole `xxxx`.
- `email()`: the first character, then `XXX@XXXX.com`, cut to the type's length; an empty string reads as `default()`.
- `partial(p, "pad", s)`: when the value is longer than `p + s` (trailing spaces and a `char`'s padding count), its first `p` characters, the padding and its last `s`; otherwise the padding alone; cut to the type's length.
- `random(low, high)`: a fresh value per row, an integer, a decimal at the column's scale, money at four places, a float anywhere between.
- `datetime("X")`: the part reset — the year to 2000, the month or day to 1, an hour, minute or second to 0 — with fractional seconds always dropped and a `datetimeoffset` keeping its clock time at `+00:00`.

NULL stays NULL, and a non-NULL result of an expression over a NULL masked value still masks (`ISNULL(masked_null, 'q')` reads `xxxx`).
How the function reaches an output column, and which clauses read the stored value instead, is `DataMask`'s XML doc.
Past a single query it rides the same seams: a derived table's, CTE's, view's, inline TVF's and `PIVOT`'s column carries its query's mask as `HeapColumn.DerivedMask`, a computed column over a masked column reads as `default()`, a FOR JSON / FOR XML subquery's whole document reads as `default()`, and `UNPIVOT` over a masked column masks both its value and its name column as `default()`.

The sinks, each applying its plan's masks for the executing principal:
- a SELECT's result set, a cursor's `FETCH`, a statement-level FOR JSON / untyped FOR XML document (its rows masked before serializing — a nested FOR JSON column inside it reads as a bare `xxxx`); a statement-level `FOR XML … TYPE` document reads as `<masked />` whole;
- `SELECT … INTO` and `INSERT … SELECT`, which store the masked values;
- `INSERT … VALUES ((SELECT masked …))`, which stores `default()` of the value's type whatever the column's function;
- `UPDATE … SET`, which stores the masked value of its expression at the target column's type — `SET s = s + '!'` overwrites the column with its own mask, `SET plain = LEFT(s, 2)` stores `xxxx` into a `varchar(20)` — and through a view masks each view column as its base column;
- `MERGE`'s `UPDATE SET` and `INSERT VALUES`, whose values for one target column meet as a `CASE`'s arms do: a bare source column stores its function, any other expression over one `default()`, and an unmasked value written by another action beside a masked one is masked too (`UPDATE SET s = s + src.x` with `INSERT VALUES (src.id, src.x)` stores `xxxx` both ways); its `OUTPUT` masks the source's columns as the source query projects them;
- `OUTPUT` `INSERTED` / `DELETED`, to the client and into an `INTO` target — through a view, each view column as the view reads it;
- `SELECT @v = …`, `SET @v = …` and `DECLARE @v = …`: through the column's function when the variable is declared as the value's own type, else `default()` of the variable's type (`varchar(40)` keeps `jXXX@XXXX.com`, `varchar(20)` reads `xxxx`).

A correlated subquery or an `APPLY` body projecting an enclosing query's masked column masks it as a direct reference would (`SELECT (SELECT t.s)` keeps `s`'s function, `CROSS APPLY (SELECT UPPER(t.s))` reads `default()`): `ParserContext.OuterMaskResolver` chains the enclosing scopes' masks beside their column types.

A scalar UDF's call reads as `default()` of its return type wherever a query projects it — a select list, `SET @v =`, `SELECT @v =`, `UPDATE … SET`, `INSERT … SELECT` — when its result reads a masked column, but a `DECLARE @v = dbo.f()` initializer and a `WHERE` read the stored value.
Inside the body nothing masks: `SELECT @v = s` assigns the stored value and a comparison on it sees it.
Real settles the result's mask by data flow, inlineable or not: a variable assigned from a masked column carries it until reassigned from something that doesn't, a `CASE WHEN` or `IF` over it passes nothing on, and every `RETURN` counts whichever branch it sits in — so `IF @x = 1 SELECT @v = s … RETURN @v` masks even when the branch isn't taken.
`Simulation.ScalarFunctionReturnMask` walks the body once without running and keeps the answer per schema version.

A conversion error raised computing a masked output column's value, for a principal who reads it masked, hides its value and type names: Msg 245 reads `Conversion failed when converting the ****** value '******' to data type ******.`, and Msg 220, 232 and 248 redact the same way; Msg 8114, 8115, 235 and 241 quote no value and keep their text.
A `WHERE`, an `ORDER BY` or a `CASE WHEN` condition reads the stored value, and its error quotes it.
The projection's `CAST`, `CONVERT` and operator nodes carry the column's mask (`ErrorMask`) for this.

A plan compiles its masks once — `Selection.ColumnMasks`, null for any query reading no masked column — and every sink tests that array before anything else, so an unmasked query pays one null test.
The projection walk that fills it is skipped outright until the simulation's first mask is declared (`Simulation.DeclaresDataMasks`).

## Divergences

- Only a `CAST`, a `CONVERT` or an operator redacts its error; a value-quoting error another function raises over a masked argument keeps its text here, unprobed on real (`ABS(s)` raises Msg 8114, which quotes nothing).
- A cursor's `FETCH` and a masked `char` / `binary` value written to storage are padded to the type's length, as a stored value is, where a SELECT sends it unpadded.

## Not modeled yet

- **A derived table in `FROM` correlating to an enclosing query** reads the outer column unmasked: only the select list and an `APPLY` body chain the outer masks.
- **An inline TVF's column masks** are settled when it is created, so a mask its base table gains or loses later reaches it only when it is re-created or altered.
- **Masking a `geography`, `geometry` or `vector` value** raises `NotSupportedException`: real sends a single `0x00` byte its own client can't read back, and the simulator's values of those types are parsed, so there is no such value to send.
