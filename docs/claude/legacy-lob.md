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
| `DISTINCT` | **Msg 421** — `The text data type cannot be selected as DISTINCT because it is not comparable.`, one per such column in select-list order | Msg 421, same wording | Msg 421, same wording |
| `UNION` / `INTERSECT` / `EXCEPT` | **Msg 5335** — `The data type text cannot be used as an operand to the UNION, INTERSECT or EXCEPT operators because it is not comparable.` | Msg 5335, same wording | Msg 5335, same wording |
| `MAX` / `MIN` | **Msg 8117** — `Operand data type text is invalid for max operator.` | Msg 8117 | **Msg 6210** `CLR type 'geography' is not fully comparable.`, and then the Msg 8117 |
| `COUNT` / `COUNT_BIG` | **Msg 8117** state 1 | accepted | accepted |
| `COUNT(DISTINCT …)` | Msg 8117 **state 2** | Msg 8117 state 2 | Msg 8117 state 2 |
| `=` / `<>` / `<` … | **Msg 402** against a string, a binary, another LOB, `xml`, or the `date` / `time` / `datetime2` family, **Msg 206** against anything else — see [`arithmetic.md`](arithmetic.md#type-pair-legality) | **Msg 305** against `xml`, Msg 402 / 206 against the rest by the same split | **Msg 403** naming the spatial operand |
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
A length that only turns negative at run time reports a different message per family: `LEFT` and `SUBSTRING` share **Msg 537** (`Invalid length parameter passed to the LEFT or SUBSTRING function.`) and `RIGHT` keeps **Msg 536** with its own name capitalized (`Invalid length parameter passed to the RIGHT function.`).
The state follows the source's bound type: 2 over a bounded ANSI string or binary, 3 (`RIGHT`: 4) over a bounded Unicode one, 3 (`RIGHT`: 5) over any MAX form, and for `SUBSTRING` 4 over `text` / `image` and 6 over `ntext` (probed 2026-09-25 against SQL Server 2025).
The binary form takes the same split.

The simulator raises the constant case from the result-type resolution the three scalars share, so it fires while the batch compiles, over an empty rowset and before any statement runs, the way real's check does.
It is a batch-level compile failure: no earlier statement in the batch runs and a `BEGIN TRY` in the same batch doesn't catch it, while the same text inside `EXEC('…')` is caught by the caller's `TRY`.

## Text pointers: `TEXTPTR` / `TEXTVALID`

- **`TEXTPTR(column)`** returns the 16-byte `varbinary` pointer of a base-table `text` / `ntext` / `image` column, or NULL when the cell is NULL.
  The argument must be a base-table column reference: a literal, CAST or computed expression raises **Msg 280** (`Only base table columns are allowed in the TEXTPTR function.`), and a column of any other type (`varchar(max)` included) raises **Msg 8116** (`Argument data type <t> is invalid for argument 1 of textptr function.`).
- **`TEXTVALID('table.column', text_ptr)`** returns `int` `1` when the pointer is valid for the named column, else `0`.
  A NULL pointer or name, bytes that aren't a simulator pointer, and a name whose column segment doesn't match the pointer's source column all return `0`.
  The name needs at least two dotted parts (a bare one-part name returns `0`, matching real).

### The pointer encoding

Real's pointer is an opaque handle into the LOB allocation structure that names a specific column and row.
The simulator names the same two things directly: the table's object id, a 4-byte FNV-1a-32 hash of the case-folded column name, and the row's stable heap address, page then slot.
The bytes won't match real's, and a pointer only ever resolves against the table and column it was read from.

The row's address reaches `TEXTPTR` through a row locator (see [`heap-storage.md`](heap-storage.md#row-addresses-reach-expressions-through-a-row-locator)), so the pointer follows the row rather than the value it holds.
Probed 2026-09-30 against SQL Server 2025, all modeled:

- Two rows of one column holding the **same value** get distinct pointers, and a write through one leaves the other row alone.
- An **ordinary `UPDATE`** of the cell — or of the row's key, or one growing the row past its slot — leaves a pointer read before it valid, reading the new value.
- A cell a write sets **NULL** (`UPDATE … SET c = NULL`, `WRITETEXT … NULL`) keeps its LOB root on real, so `TEXTPTR` still hands out a pointer to it, `TEXTVALID` answers 1, and `READTEXT` reads NULL; a cell that has never held a value has no pointer.
  The heap records such cells (`Heap.RootedNullLobCells`), dropping a row's entry when the row is deleted and the whole set on `TRUNCATE`.
- A **deleted** row's pointer is `TEXTVALID` 0 and Msg 7123 to the statements.
- `TEXTPTR` reads only a base table's column: through a view or a derived table it is Msg 280.
  Real raises it compiling the batch; the simulator raises it as the statement runs.

`TEXTVALID` resolves its `'[db.][schema.]table.column'` name and answers 1 only for a pointer naming a live row of that table's column; a name resolving to no table or no `text` / `ntext` / `image` column is 0, as on real.

## `READTEXT` / `WRITETEXT` / `UPDATETEXT`

```
READTEXT   table.column text_ptr offset size [HOLDLOCK]
WRITETEXT  [BULK] table.column text_ptr [TIMESTAMP = 0x…] [WITH LOG] { literal | @variable }
UPDATETEXT [BULK] table.column text_ptr { NULL | insert_offset } { NULL | delete_length }
           [WITH LOG] [ { literal | @variable } | table.column text_ptr ]
```

The `BULK` forms take no data in the statement — see [The bulk forms](#the-bulk-forms).

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
  Its `TIMESTAMP = 0x…` clause takes a binary literal of any length ahead of `WITH LOG` and is otherwise ignored (probed 2026-10-07 against SQL Server 2025); anything but `=` and a binary literal is a syntax error at it, and `UPDATETEXT` has no such clause, reading `TIMESTAMP` as its copy form's name and stopping at the `=`.
- **`UPDATETEXT`** splices: it deletes `delete_length` units at `insert_offset` and puts the inserted data there.
  A **NULL or negative** insert offset appends and a **NULL or negative** delete length runs to the end (both probe-confirmed — real reads a negative exactly as it reads NULL).
  Omitting the inserted data is a pure deletion.
  The copy form takes its inserted data from a second LOB cell named by its own `table.column` and pointer.
  `@@ROWCOUNT` is 0, as `WRITETEXT`'s (probed 2026-10-07 against SQL Server 2025).

`WITH LOG` parses and carries no further effect — the simulator has no recovery log to opt into, and the write is undo-logged for rollback either way.

### The bulk forms

Real's `WRITETEXT BULK` and `UPDATETEXT BULK` take their data from the stream that follows the batch, which only a raw TDS client sends — FreeTDS's `dbwritetext` sends `writetext bulk t.c 0x<ptr> timestamp = 0x<ts> [with log]` this way.
Probed 2026-10-07 against SQL Server 2025 with a raw TDS client, all modeled:

- The column, the pointer and its type are checked as the statement starts, with the errors the plain forms raise.
  Then the batch's response ends there, as though the batch had: the last DONE sent loses its more bit, or a bare DONE naming the batch kind goes out when none was sent.
- The session's next message is the data.
  A bulk-load packet (type 7) carries a 4-byte length and the bytes, in the column's own encoding — `text`'s code page, `ntext`'s UTF-16 with an odd trailing byte read as a last character's low byte, `image` verbatim.
  A length shorter than the bytes takes its prefix, and one longer than they are, or a packet too short to carry it, is **Msg 4002** at state 2.
  Written, the statement sends its DONE and the batch goes on.
- Any other request is not the data: **Msg 4022**, ending the batch, the request's own text unread and the batch's remaining output answering it instead.
  An attention ends the batch with Msg 3621 and a DONE carrying both the attention and the error bit; a transaction-manager request ends the session with Msg 4014, the transaction's rollback, Msg 3621 and Msg 596.
- `UPDATETEXT BULK` judges its offset and deletion length once the data have come, so Msg 7116 / 7135 follow the bulk packet.
- Data written in the statement — a literal, a variable, `NULL`, the copy form's source — is **Msg 185** as the batch compiles, for `UPDATETEXT BULK` too.

In process no bulk-load packet can follow, so the connection's next command meets Msg 4022, as SqlClient's next command would against real.
The suspended batch is parked on the connection (`SimulatedDbConnection.ParkedBulkText`) and resumed by whichever request comes next; the statement then runs a second time from its first token to write what arrived (see `SimulatedBulkTextRequest`).

### Errors abort as under `XACT_ABORT`

Probed 2026-10-07 against SQL Server 2025, with and without an open transaction, inside and outside `TRY`:

- **The column and the pointer's type are judged as the batch compiles**, for all three statements: none of the batch runs, no `TRY` catches it, and no transaction is touched.
  A column no pointer addresses is Msg 7125 (state 4); a pointer that can't hold `binary(16)` is Msg 7122 — anything but `binary`, `varbinary`, `char` or `varchar` of at least 16 (`max` fails too), a binary literal shorter than 16 bytes included — save that a write takes an integer, whose value is Msg 7125 at state 5 as it runs.
  A character literal or `NULL` as the pointer is a syntax error at it.
- **A pointer's value is its bytes cut to the first 16**, a character value's in its code page, so a short `varbinary` or one wider than 16 reaches Msg 7123 rendering what was read.
- **A `WRITETEXT` or `UPDATETEXT` error as it runs aborts as under `XACT_ABORT`** — the pointer's value (Msg 7123, 7133, the integer's 7125), the offset and length (Msg 7116, 7135), the copy form's source type (Msg 518, read after the source's pointer) and the bulk forms' data (Msg 4022, 4002): uncaught it ends the batch and rolls an open transaction back, the rollback's ENVCHANGE ahead of Msg 3621, which follows at line 1 whatever line the statement was on; caught it dooms the transaction, which the batch's end then rolls back with Msg 3998.
  The caught statement's DONE carries no count.
- **`READTEXT`'s run-time errors** (Msg 7123, 7124, 7133, 7116) end only their statement and leave a transaction committable.

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
| 7122 | 1 | A pointer operand whose type can't hold `binary(16)`, judged as the batch compiles — `Invalid text, ntext, or image pointer type. Must be binary(16).` |
| 7123 | 1 | Bytes that carry no pointer identity, a pointer read from another column, or one whose row has since been deleted — `Invalid text, ntext, or image pointer value 0x….` |
| 7133 | 1 / 2 | A NULL pointer, which is what a cell that was never written hands back — `NULL textptr (text, ntext, or image pointer) passed to READ TEXT function.` at state 1, `WRITE TEXT` and `UPDATE TEXT` at state 2. |
| 7124 | 1 | `READTEXT`'s window running past the value — `The offset and length specified in the READTEXT statement is greater than the actual data length of 35.` |
| 7116 | 4 / 3 | An offset outside the value — `UPDATETEXT`'s insert offset past its end at state 4, `READTEXT`'s negative offset at state 3: `Offset 100 is not in the range of available LOB data.` |
| 7135 | 4 | `UPDATETEXT`'s deletion running past the value — `Deletion length 500 is not in the range of available text, ntext, or image data.` |
| 518 | 1 | `UPDATETEXT`'s copy form naming a source column of a different legacy LOB type — `Cannot convert data type ntext to text.` |
| 102 | 1 | A signed offset or size in `READTEXT`'s grammar, which takes an unsigned integer or a variable. |

Msg 7133 is what forces the classic initialization dance: a cell that has never been written has no pointer, so a `WRITETEXT` into it needs an ordinary `UPDATE t SET c = ''` first.

## Divergences

- **The pointer's bytes are the simulator's own**, so Msg 7123 renders different hex than real's for the same statement.
- **A cell's LOB root is forgotten when a rolled-back delete restores its row**, so a cell a write had set NULL reads no pointer after that rollback where real's still has one.

## Not modeled yet

- **A bulk form inside a block, a `TRY`, an `IF` or a `WHILE`, a module body or dynamic SQL, or on a MARS session** raises `NotSupportedException`.
  Those statements send their outcomes with the statement enclosing them, which has nowhere to suspend; real suspends there too, and a `TRY` around the statement catches Msg 4022 and 4002 (probed 2026-10-07 against SQL Server 2025).
- **An in-process transaction call while a bulk form waits** — `BeginTransaction`, `Commit`, `Rollback` — runs, where SqlClient's transaction-manager request would end the session with Msg 4014.
- **`TEXTPTR` in a joined `UPDATE` / `DELETE`, a write through a join view, or a `MERGE`** raises `NotSupportedException`: those statements' row resolvers don't carry a row locator.
  A single-target `UPDATE` / `DELETE` and every read path do.
- **`READTEXT` / `WRITETEXT` / `UPDATETEXT` through a view or a `#temp` table** resolve like any other `table.column` reference, so a view name reaches the view's own object rather than the base table's column and reports Msg 7125.
- Binary `CHARINDEX` — `CHARINDEX(<varbinary>, <image>)` is real's binary search and the simulator's Msg 8116 (see [`scalars.md`](scalars.md#divergences)).
- `SUBSTRING` over a `binary` / `varbinary` value under `SET ANSI_PADDING OFF` isn't distinguished; the simulator always reads the padded form.
