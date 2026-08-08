# Legacy LOB operations

Where `text` / `ntext` / `image` may and may not go, then the operations written for them: binary `SUBSTRING`, the text-pointer scalars `TEXTPTR` / `TEXTVALID`, and the `READTEXT` / `WRITETEXT` / `UPDATETEXT` statement trio they address rows for.
Probe-confirmed against SQL Server 2025.

Code: `Parser/Expressions/Substring.cs`, `Parser/Expressions/TextPointer.cs`, `Parser/Expressions/TextValid.cs`, `Simulation/Simulation.LegacyLobStatements.cs`, `Errors/SimulatedSqlException.LegacyLobErrors.cs`.

## Where the types can't go

The three types carry no comparison, which rules them out of every slot that orders, groups or dedups.
Real splits the rejection across five numbers, and the split is not the one the type list suggests — `xml` and the two spatial types are non-comparable in exactly the same slots, and only some of the messages distinguish them from the legacy trio.
All of it binds while compiling: probe-confirmed that an **empty** table raises every one of these.

| Slot | `text` / `ntext` / `image` | `xml` | `geography` / `geometry` |
|------|----------------------------|-------|--------------------------|
| `ORDER BY`, `GROUP BY` | **Msg 306** state 2 — `The text, ntext, and image data types cannot be compared or sorted, except when using IS NULL or LIKE operator.` | **Msg 305** — the same sentence for one type, capitalized `XML`, and exempting only `IS NULL` | **Msg 249** — `The type "geography" is not comparable. It cannot be used in the ORDER BY clause.`, the one message that names the clause |
| `DISTINCT` | **Msg 421** — `The text data type cannot be selected as DISTINCT because it is not comparable.` | Msg 421, same wording | Msg 421, same wording |
| `UNION` / `INTERSECT` / `EXCEPT` | **Msg 5335** — `The data type text cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.` | Msg 5335, same wording | Msg 5335, same wording |
| `MAX` / `MIN` | **Msg 8117** — `Operand data type text is invalid for max operator.` | Msg 8117 | **Msg 6210** `CLR type 'geography' is not fully comparable.`, and then the Msg 8117 |
| `COUNT` / `COUNT_BIG` | **Msg 8117** state 1 | accepted | accepted |
| `COUNT(DISTINCT …)` | Msg 8117 **state 2** | Msg 8117 state 2 | Msg 8117 state 2 |
| `=` / `<>` / `<` … | **Msg 402** against a string, **Msg 206** against anything else — see [`casting.md`](casting.md) | — | — |
| `LIKE` | accepted, in either slot | **Msg 8116** naming argument 1 or 2 `of like function` | Msg 8116, same shape |
| `UNION ALL`, `IS NULL`, `COUNT(*)` | accepted | accepted | accepted |

Three readings of that table are worth keeping:

- **DISTINCT and the deduping set operators make no family split.** One message each, naming the type, across all three families — which is why `SqlType.IsLob` (true for the legacy trio, `xml` and both spatial types) is exactly the predicate behind those two gates, while the sorting and grouping slots dispatch through the narrower `SqlType.IsLegacyLob`.
- **`COUNT` refuses the legacy trio and nothing else.** Counting never compares, so real has no comparability reason to refuse any of them; it refuses the deprecated three anyway and counts `xml` and spatial happily. A `DISTINCT` inside the parentheses does need the comparison, and then all three families raise — at state 2, a split `MAX(DISTINCT …)` doesn't make (it stays at state 1).
- **`MAX` / `MIN` over a spatial operand draws two errors.** Real leads the response with Msg 6210 and follows with the ordinary Msg 8117, so a client reading `Number` sees 6210; `Aggregator.MinMaxRejection` builds the pair through `SimulatedSqlException.Aggregate`.
- **`LIKE` is the legacy trio's alone.** Msg 306's own wording advertises it as an exemption, and it holds — a `text` / `ntext` / `image` subject or pattern matches normally, while `xml` and the spatial types are refused with the ordinary argument-type Msg 8116 rather than any of the comparability numbers. `LikeExpression.Bind` is the gate, so it binds while compiling like the rest of them.

The gates live in `Selection.Execution.cs` (`NotComparableInClause` for the sort and grouping slots, the DISTINCT loop above it), `Selection.Execution.SetOps.cs` and `Aggregator.Create`.
A `varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` column is comparable and takes none of this — the MAX forms are what the legacy trio is deprecated *in favour of*, and the whole table reads `accepted` for them.

For the separate rule that keeps these types out of a string function's transformed argument (Msg 8116, naming type, position and function), see [`scalars.md`](scalars.md#legacy-lob-arguments).

## Binary `SUBSTRING`

`SUBSTRING(x, start, length)` slices bytes when `x` is `varbinary` / `binary` / `image`, on the same window arithmetic the character form uses — only the unit changes.

- `start` ≤ 0 drops the leading `|start - 1|` bytes of the requested window: `SUBSTRING(0x0102030405060708090A, 0, 3)` is `0x0102` and `(-2, 5)` is the same two bytes.
- A window running past the end clamps to the remainder; a start past the end and a length of 0 both give an **empty** value, not NULL.
- A NULL source, start or length gives NULL.
- The window math runs in 64 bits, so `SUBSTRING(x, -2147483648, 2147483647)` is empty rather than an overflow.
- A `binary(N)` source is its padded N bytes, so `SUBSTRING(CAST(0x0102030405 AS binary(10)), 4, 4)` reads `0x04050000`.

The projected type follows the character rule with `varbinary` as the family: a constant length narrows to `varbinary(min(source width, length))` and a non-constant one leaves the source's width.
`image` has no declared width and behaves as 8000 — `SUBSTRING(<image>, 2, 3)` is `varbinary(3)` and a variable length is `varbinary(8000)`.
A `varbinary(max)` source stays `varbinary(max)` whatever the length argument is.
A width of 0 floors to 1, since SQL Server has no zero-width binary type.

The same constant-length narrowing reaches the *character* legacy LOBs: `SUBSTRING(<text>, 2, 3)` projects `varchar(3)` and `SUBSTRING(<ntext>, 2, 3)` projects `nvarchar(3)`, with a non-constant length landing on the family container (`varchar(8000)` / `nvarchar(4000)`).

### Negative length: Msg 536 while compiling, Msg 537 at run time

SQL Server settles a **constant** negative length while compiling and reports **Msg 536** naming the one function — state 8 for `SUBSTRING`, state 6 for `LEFT` and `RIGHT`.
A length that only turns negative at run time reports a different message per family: `LEFT` and `SUBSTRING` share **Msg 537** state 2 (`Invalid length parameter passed to the LEFT or SUBSTRING function.`) and `RIGHT` keeps **Msg 536** at state 2 with its own name capitalized (`Invalid length parameter passed to the RIGHT function.`).
The binary form takes the same split.

The simulator raises the constant case from the result-type resolution the three scalars share, so it fires over an empty rowset the way real's compile-time check does; real additionally aborts the whole batch there, where the simulator's is a statement error.

## Text pointers: `TEXTPTR` / `TEXTVALID`

- **`TEXTPTR(column)`** returns the 16-byte `varbinary` pointer of a base-table `text` / `ntext` / `image` column, or NULL when the cell is NULL.
  The argument must be a base-table column reference: a literal, CAST or computed expression raises **Msg 280** (`Only base table columns are allowed in the TEXTPTR function.`), and a column of any other type (`varchar(max)` included) raises **Msg 8116** (`Argument data type <t> is invalid for argument 1 of textptr function.`).
- **`TEXTVALID('table.column', text_ptr)`** returns `int` `1` when the pointer is valid for the named column, else `0`.
  A NULL pointer or name, bytes that aren't a simulator pointer, and a name whose column segment doesn't match the pointer's source column all return `0`.
  The name needs at least two dotted parts (a bare one-part name returns `0`, matching real).

### The pointer encoding

Real's pointer is an opaque handle into the LOB allocation structure that names a specific column and row.
The simulator has no such structure, so it derives the 16 bytes from what identifies the cell: a 4-byte signature, a 4-byte FNV-1a-32 hash of the case-folded column name, and an 8-byte FNV-1a-64 hash of the cell's own value.
The encoding is deterministic, so reading `TEXTPTR` twice off an unchanged cell yields the same bytes and two rows of one column yield different ones — both as on real.

A write through a pointer changes the value its bytes were derived from, so the statements keep a per-`HeapTable` cache from (column hash, value hash) to the row address they settled on (`HeapTable.TextPointerRows`).
That is what keeps one pointer driving the chunked idiom — a `WRITETEXT` followed by a run of appending `UPDATETEXT`s — which is the shape these statements exist for and which real supports because its pointer is physical.
An entry naming an address that no longer holds a live row is discarded and the scan re-runs; the map is dropped wholesale past 4096 entries.

## `READTEXT` / `WRITETEXT` / `UPDATETEXT`

```
READTEXT   table.column text_ptr offset size [HOLDLOCK]
WRITETEXT  table.column text_ptr [WITH LOG] { literal | @variable }
UPDATETEXT table.column text_ptr { NULL | insert_offset } { NULL | delete_length }
           [WITH LOG] [ { literal | @variable } | table.column text_ptr ]
```

The name is `[db.][schema.]table.column`, up to real's four-segment limit.
Every operand is a literal, a variable or the `NULL` keyword — nothing composite, so `WRITETEXT t.c @p 'a' + 'b'` is Msg 102 at the operator, as on real.

**Offsets and sizes count bytes for `text` and `image` and characters for `ntext`** — probe-confirmed (`READTEXT t.nt @p 0 3` over `N'Ünicode …'` reads `Üni`, and `UPDATETEXT t.nt @p 2 1 N'Ü'` replaces one character).
`text` is stored in a single-byte code page, so its byte offsets and character positions are the same number.

- **`READTEXT`** returns one row of one column carrying the read column's own name and type, so the session's `SET TEXTSIZE` caps it at the client boundary like any other LOB read.
  A size of **0** reads to the end of the value.
  Its offset and size take an unsigned integer or a variable, so a written sign is Msg 102; through a variable the two halves read differently — a **negative offset** is Msg 7116 at state 3 while a **negative size** reads to the end exactly as 0 does, and a **NULL offset** reads from the start.
  `HOLDLOCK` parses and asks for the SERIALIZABLE read a transaction already gives.
  `@@ROWCOUNT` is 1.
- **`WRITETEXT`** replaces the whole value; a NULL operand sets the cell NULL.
  `@@ROWCOUNT` is 0.
- **`UPDATETEXT`** splices: it deletes `delete_length` units at `insert_offset` and puts the inserted data there.
  A **NULL or negative** insert offset appends and a **NULL or negative** delete length runs to the end (both probe-confirmed — real reads a negative exactly as it reads NULL).
  Omitting the inserted data is a pure deletion.
  The copy form takes its inserted data from a second LOB cell named by its own `table.column` and pointer.
  `@@ROWCOUNT` is 1.

`WITH LOG` parses and carries no further effect — the simulator has no recovery log to opt into, and the write is undo-logged for rollback either way.

### What the statements are not

Probe-confirmed on real, and modeled:

- **No trigger fires.** An AFTER UPDATE trigger on the table stays silent for both writing forms.
- **No `rowversion` column advances.**
- Both writes participate in the enclosing transaction and roll back with it.

### Diagnostics

| Msg | State | Raised by |
| --- | --- | --- |
| 182 | 1 | A single-part name (`READTEXT tx …`) — `Table and column names must be supplied for the READTEXT or WRITETEXT utility.` |
| 208 / 207 | 1 | An unknown table / an unknown column in the `table.column` operand. |
| 7125 | 4 | A column no text pointer can address (anything but `text` / `ntext` / `image`) — `The text, ntext, or image pointer value conflicts with the column name specified.` |
| 7122 | 1 | A pointer operand narrower than `binary(16)` — `Invalid text, ntext, or image pointer type. Must be binary(16).` |
| 7123 | 1 | Bytes that carry no pointer identity, a pointer read from another column, or one whose row has since been deleted — `Invalid text, ntext, or image pointer value 0x….` |
| 7133 | 1 / 2 | A NULL pointer, which is what a cell that was never written hands back — `NULL textptr (text, ntext, or image pointer) passed to READ TEXT function.` at state 1, `WRITE TEXT` and `UPDATE TEXT` at state 2. |
| 7124 | 1 | `READTEXT`'s window running past the value — `The offset and length specified in the READTEXT statement is greater than the actual data length of 35.` |
| 7116 | 4 / 3 | An offset outside the value — `UPDATETEXT`'s insert offset past its end at state 4, `READTEXT`'s negative offset at state 3: `Offset 100 is not in the range of available LOB data.` |
| 7135 | 4 | `UPDATETEXT`'s deletion running past the value — `Deletion length 500 is not in the range of available text, ntext, or image data.` |
| 518 | 1 | `UPDATETEXT`'s copy form naming a source column of a different legacy LOB type — `Cannot convert data type ntext to text.` |
| 102 | 1 | A signed offset or size in `READTEXT`'s grammar, which takes an unsigned integer or a variable. |

Msg 7133 is what forces the classic initialization dance: a cell that has never been written has no pointer, so a `WRITETEXT` into it needs an ordinary `UPDATE t SET c = ''` first.

## Divergences

- **Two rows of one column holding the same value share a pointer** and resolve to the first of them, since the pointer's row half is a hash of the value.
  Real tells them apart.
- **A `WRITETEXT` of NULL leaves the simulator's cell without a pointer** (`TEXTPTR` reads NULL again), where real keeps handing one out — real's pointer reflects an allocated LOB root rather than a non-NULL value, so on real the initialization dance is needed only once per cell.
- **An ordinary `UPDATE` of the cell invalidates a pointer read before it** (Msg 7123 on next use) where real's stays valid and reads the new value.
  The cached binding covers the sequence the statements themselves write; an outside write moves the value the pointer names.
- A pointer wider than `binary(16)` reports Msg 7122 where real truncates to 16 bytes and reports Msg 7123.

## Not modeled yet

- **`WRITETEXT BULK` / `UPDATETEXT BULK`** raise `NotSupportedException`.
  Real's bulk form is a bulk-copy data stream rather than a statement and answers Msg 185 (`Data stream is invalid for WRITETEXT statement in bulk form.`) to a normal client.
- **Cross-session pointer reuse.** The binding cache lives on the table, so a pointer travels between connections of one `Simulation`; a pointer that outlives the value it names is Msg 7123 rather than real's physical-handle behavior.
- **`READTEXT` / `WRITETEXT` / `UPDATETEXT` through a view or a `#temp` table** resolve like any other `table.column` reference, so a view name reaches the view's own object rather than the base table's column and reports Msg 7125.
- Binary `CHARINDEX` — `CHARINDEX(<varbinary>, <image>)` is real's binary search and the simulator's Msg 8116 (see [`scalars.md`](scalars.md#divergences)).
- `SUBSTRING` over a `binary` / `varbinary` value under `SET ANSI_PADDING OFF` isn't distinguished; the simulator always reads the padded form.
