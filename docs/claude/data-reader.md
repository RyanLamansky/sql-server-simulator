# `SimulatedDbDataReader` client surface

Full `DbDataReader` contract.
A command runs only on an open connection: `ExecuteReader` / `ExecuteNonQuery` / `ExecuteScalar` on one never opened, closed, or closed by an error that ended its session throw SqlClient's `InvalidOperationException`, worded as it words it and naming the method.
Typed accessors read `SqlValue` via the cursor indexer, unwrap via `As*` (no boxing); NULL → `SqlNullValueException` (SqlClient parity).

- `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date at midnight, `Kind=Unspecified`).
- A **`datetime` rounds to whole milliseconds at the ADO.NET boundary** (`DateTimeSqlType.RoundToClientMilliseconds`, in `GetDateTime` + `SqlValue.ToObject` — the latter covers `GetValue`/`GetFieldValue`/output-param writeback) matching SqlClient's `.000`/`.003`/`.007`; the engine keeps full 1/300-second resolution internally, so only the client surface rounds.
  (The TDS endpoint transfers the full internal resolution and lets real SqlClient do the same client-side rounding — see [`tds-endpoint.md`](tds-endpoint.md).)
- `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` / `TimeOnly`-over-`Time`.
- A **`decimal` wider than a .NET `decimal` sheds or raises at the boundary**, reproducing SqlClient's own rule rather than the server's: `GetDecimal` / `GetValue` / `GetFieldValue<decimal>` / `ExecuteScalar` drop trailing *fractional* zeros until the value fits and hand back what's left (`CAST(1 AS decimal(38, 30))` arrives at scale 28, `CAST('8000000000000000000000000000.0' AS decimal(38, 1))` at scale 0), and raise `OverflowException("Conversion overflows.")` when nothing can be shed (`CAST('79228162514264337593543950336' AS decimal(38, 0))`, one past `decimal.MaxValue`).
  `GetFieldType` / `GetDataTypeName` answer `System.Decimal` / `decimal` whatever the width.
  `SqlDecimal` — the accessor real SqlClient offers for full 38-digit fidelity — is not part of the simulator's public surface; the TDS endpoint carries the full width to real SqlClient, where `GetSqlDecimal` reads it (see [`tds-endpoint.md`](tds-endpoint.md)).
- `GetOrdinal` two-pass linear (case-sensitive then -insensitive, SqlClient precedence).
  `HasRows` sticky.
  `GetChar(int)` always raises `InvalidCastException`.

## The row form the reader reads

A result set holds its rows in one of two forms and the reader's cursor follows whichever it is.
A **FROM-bearing SELECT projects `SqlValue[]` rows** — the projection computed the values — and those travel to the client as they are (`ValueArrayCursor`, whose indexer is an array read).
The niche producers that genuinely emit encoded rows — a set operation, a view or TVF body, `OPENJSON` / `OPENXML`, the catalog procedures, a DML `OUTPUT` clause — keep the `byte[]` form and the reader decodes each accessed cell (`SqlValueCursor`).
Both forms are lazy per cell, so a client that reads two columns of thirty pays for two either way.

The dispatch loop settles the form once, at the statement boundary: `SimulatedSqlResultSet.MaterializeRows` drains the row sequence into a list **without converting it**, which is what statement atomicity and `@@ROWCOUNT` need and all they need — and a result larger than what real gets ahead of its client streams in the same form instead ([below](#rows-go-out-as-the-reader-reads-them)).
Materializing through `RowBytes` instead would encode every projected row into a page image the cursor decodes straight back, cell by cell.
That round trip measures at 35-43% of the statement's total allocation across every shape measured (730 B/row on a 228k-row `SELECT *` of `Sales.InvoiceLines`).
It bought one thing: the page image is *compact*, so a buffered result holds ~32 bytes per cell alive while the reader is open rather than the record's own width — which matters where a result is still buffered whole (see [`backlog.md`](backlog.md#complex-query-execution--perf-residuals)); a streamed one holds a window.

**The one thing the page image did that the values don't is narrow.**
`varchar` / `char` / `text` encode through their collation's ANSI code page, whose encoder fallback is `?`, so a character the page can't carry is lost on the way to bytes — SQL Server's own lossy narrowing, and the client sees `?` for `SELECT CAST(N'水' AS varchar(10))` on both engines.
`RowEncoder.NarrowingColumns` marks the columns that can suffer it (those three families, plus a `sql_variant` holding one) and `RowEncoder.StorageForm` applies the type's own `Encode`/`Decode` pair to such a cell — exact by construction rather than by per-type reasoning.
Every other family's value factory already normalizes at construction (`FromTime` quantizes to the declared precision, `FromDecimal` re-tags to the declared scale, `FromChar` pads by byte count, `FromDateTime` quantizes to 1/300 s), so their storage round trip is the identity and they are never flagged; neither is an ASCII payload of a flagged column, since every ANSI code page the simulator stores through is ASCII-transparent.

## Rows go out as the reader reads them

A `SELECT` larger than what real gets ahead of its client is produced as the client reads it, as real sends a result set, rather than run whole before its first row goes out.
The statement keeps about 40,000 wire bytes ahead of the reader — the packet the client is reading and the four `SessionRequest.BytesAheadOfClient` grants after it — producing more as the reader reads into each next packet, and otherwise waits, `suspended` on `ASYNC_NETWORK_IO` in `sys.dm_exec_requests` and `sp_who`, holding what its position needs (`ResultStream`).
Probed 2026-10-08 against SQL Server 2025 over a MARS connection, which is what the in-process connection is: a `REPEATABLE READ` reader two rows into a result held the key locks of 20 rows of 2,007 wire bytes, 79 of 507 and 4,438 of 9 — each about 40,000 bytes produced — and a reader 2, 50, 100 and 300 rows into the 2,007-byte rows had produced 20, 68, 116 and 318, refilling as it read; the window here gives 20, 68, 120 and 319.
Over the TDS endpoint outside MARS the run-ahead is whatever the connection's socket buffers take on top of that window, as on real, whose reader held 2,119 such keys there; over MARS it is the client's four-packet SMP window, the statement producing a packet past what the writer has buffered.
What the suspended statement holds, per isolation level and table hint, is in [`locking.md`](locking.md#a-reader-suspended-mid-result).

What streams is a statement-level `SELECT` of a command the in-process reader (`ExecuteReader`) or the TDS endpoint runs (`SimulatedDbCommand.StreamsResultRows`), whose outcomes reach that consumer as they are produced — one at the batch's level or inside a block, a `TRY`, an `IF` or a `WHILE` there, or in the body of a procedure or dynamic batch the batch calls with its outcomes streaming (`EXEC p`, `EXEC (…)`, `sp_executesql`; `BatchContext.StreamsResultRows`), or in a procedure an RPC calls (`CommandType.StoredProcedure`, in process and over TDS) — and a cached plan's replay alike; and a DML statement's `OUTPUT` rows in the same places ([below](#a-dml-statements-output-rows)).
Its plan doesn't matter: a sort, an aggregate or a join's build reads its input before its first row goes out, as real's does, and its output then streams, the statement holding its object lock until its last row (probed: a `READ COMMITTED` reader of a sort holds `OBJECT IS` while the sorted rows go out).
A result whose rows all fit the first window is produced whole before it is sent, exactly as before, which keeps the small-result path free of the machinery.
`ExecuteScalar` and `ExecuteNonQuery`, a function's or trigger's body, a procedure an `INSERT … EXEC` or `WITH RESULT SETS` calls, `FOR XML` / `FOR JSON`, an assignment `SELECT` and `SELECT … INTO` produce their results whole.
A procedure's call holds no lock on the procedure once its body has compiled, as real's doesn't, so another session drops it while the reader is suspended inside it ([`locking.md`](locking.md#acquisition-sites)).

The statement is suspended inside the batch's outcome stream: the dispatch loop yields the result set, then an internal `SimulatedRowsProduced` marker after each window, and runs on only when the consumer advances the stream again — so every row is produced where a request's state, its cancellation scope, its transaction membership and the engine's culture are already in place.
A consumer of a top-level outcome stream that enables streaming reads markers from its row cursor's pull; any other loop over outcomes passes over them.
The statement ends with its last row: its `@@ROWCOUNT`, a row's error (the rows before it out first, as before), its locks, its Query Store capture and its `STATISTICS` messages settle there, and the batch's next statement runs only after.
While it waits it reports no executing thread, so the same-thread deadlock check and the abandoned-session sweep treat it as idle, and its `CommandTimeout` stops counting: SqlClient counts only the time a call spends waiting on the server, so each read that resumes the statement has the whole timeout to itself.
It keeps its LOB epoch and statement snapshot (see [`heap-storage.md`](heap-storage.md#a-freed-lob-chain-waits-for-the-statements-that-could-read-it)), its request's own while another request of the session runs ([below](#in-process-mars-overlapping-readers)).
A scan that reaches a row another request or session inserted, updated or deleted ahead of it while it waited reads the table as it stands then: once its statement has waited on its client and the heap has changed, a scan in key order reads on from the key it stopped at (`BatchContext.ScanInKeyOrder`, `OrderedScanRest`), so a key inserted ahead is read in its place, an `ORDER BY` the clustered key satisfies staying ordered, and a key deleted ahead isn't (probed 2026-10-08 against SQL Server 2025 at every locking level, another session's writes and another MARS request's alike); a versioned read keeps its snapshot.

How the statement ends early:
- **The reader moves on or closes** — `NextResult`, `Close`, `Dispose` — and the rest of the rows are produced and discarded, as SqlClient reads past them: `@@ROWCOUNT` after a closed reader counts every row, and each row is still locked and checked on the way.
- **Another request of the session writes in its transaction** — the transaction the API or an earlier batch began, which both work in — and the suspended statement first runs to its end, its rest kept for its reader (`SimulatedDbConnection.FinishReadsBeforeWrite`), as real versions such a write so the reader reads none of it ([below](#divergences)); a request reading in it, or working in another transaction, runs beside it.
- **The connection closes or is reclaimed abandoned**: the statement is abandoned and gives back what it held; the reader's next `Read` throws SqlClient's closed-reader `InvalidOperationException`.
- **A `KILL`** ends a session whose statement waits on its client at once, as real does: in process the reader's next `Read` raises the broken-connection Msg 0, over TDS the connection drops under it (probed 2026-10-08 against SQL Server 2025).
  A statement running when the kill lands meets its cancellation at its next safe point, as before.
- **A cancel or `CommandTimeout`** reaches the statement as the read that resumes it runs, as Msg 0 or Msg -2 from that `Read`.

Measured 2026-10-08 in process against the build that produced every result whole (Release, one case per process, medians of five to seven interleaved processes): reading a result two rows in, the managed heap held 97.5 MB where it held 176.2 MB over 20,000 rows of `char(2000)`, and 97.7 MB against 129.9 MB over 200,000 narrow rows — the buffered result gone; draining them took 93.6 ms against 155.3 ms and 38.8 ms against 102.9 ms, a filtered 200k-row read 17.8 ms against 23.2 ms and a sorted one 180.5 ms against 195.8 ms, the results no longer outliving a collection whole.
Results that fit the first window are unchanged within noise (a parameterized point `SELECT` 3.83 µs against 3.82 µs, 10 rows 4.41 against 4.50, 100 rows 22.2 against 22.5, 1,000 rows 198 against 204) at about 110 more bytes a call, and the sqllogictest replays too (43.6 / 45.3 s against 45.2 / 45.2 s, 56.5 / 54.9 s against 58.1 / 54.7 s).
Measured 2026-10-08 again once requests interleaved mid-statement, against the build before (Release, medians of five interleaved processes): a point `SELECT` 4.36 against 4.34 µs at 64 more bytes, an `INSERT` 17.2 against 17.6 µs and an `UPDATE` 20.2 against 19.9 µs at about 50 more, and a steady 200,000-row drain 24.1 against 23.3 ms (20,000 rows of `char(2000)` 90.2 against 87.4 ms), the sqllogictest replays 47.4 / 56.9 s against 47.3 / 58.0 s.
Over a TDS MARS connection, the first of 20,000 rows of `char(2000)` arrived in 95 ms where it took 771 ms, the client holding 1.5 MB where it held 65 MB, and draining them took 1,021 ms against 490 ms, each four packets now waiting on the client's window acknowledgment; a 20,000-row `UPDATE … OUTPUT` holds its undo log until its last row, 81 MB against 40 MB at its second row.

### A DML statement's `OUTPUT` rows

A DML statement whose `OUTPUT` rows go to the client past the first window sends them as the client reads them too, and its ending — its autocommit or its place in the transaction, its statement-scoped locks, its `@@ROWCOUNT` — waits for its last row (`ResultStream.PendingWrite`).
Real can't suspend such a statement, so it writes as it sends, while every other request of the session is refused with Msg 3980, and a cancel mid-way rolls the statement back (probed 2026-10-05 against SQL Server 2025: an `UPDATE … OUTPUT` of 2,000 rows cancelled ten rows in left none changed; read to its end or closed undisturbed, all changed).
Here the statement writes every row first and holds its ending while the rows go out: a cancel or `CommandTimeout` meeting it then — the next `Read`, a TDS attention — rolls it back, a reader closing reads the rest and commits it, and the session is held until its last row is out.

### Divergences

- **A lock wait inside the run-ahead window holds up the call that ran the window** — `ExecuteReader`, or the `Read` that pulled it — where real's client reads the packets sent before the blocked row while the server waits.
  Probed 2026-10-09 against SQL Server 2025 with 2,007-byte rows, four to a packet, and a writer holding row 9, 30 or 100: real's client read 8, 28 and 96 rows — every row in a packet completed before the blocked one — then timed out on its next `Read`, MARS or not; here an in-process reader reads 0, 29 and 99 (none while the row is inside the first window, which `ExecuteReader` produces), and a TDS client outside MARS 76 of 99.
  Under a `LOCK_TIMEOUT` the error lands after the rows before the row, as on real; a wait for a writer to commit can't be answered from the reader's own thread meanwhile.
  Closing it means the rows already produced reaching the consumer while the producer waits, which the producer, running on the reader's own thread inside the scan's iterators, can't hand back from: production would have to move to a thread of its own once a wait begins, and every per-thread piece of a request's state — its culture, its date and language settings, the executing thread the same-thread deadlock check reads — with it; over TDS the endpoint could instead send the window's completed packets just before the wait.
- **A write by another request in the reader's own transaction runs the suspended statement to its end first**, where real versions the write and leaves the reader where it stood: the reader reads exactly what real's does, but takes the locks of every row it has left, which real takes as it reaches them.
  Probed 2026-10-09 against SQL Server 2025 at `READ COMMITTED`, `REPEATABLE READ` and `SERIALIZABLE`, either direction: a row the other request updated ahead reads as before, one it deleted is read, one it inserted isn't, a key it moved reads at its old key, while a write of the transaction's own from before the statement and another session's committed write ahead read as written — and the reader kept nineteen or twenty key locks, where here it takes them all, so another session's write of a row ahead waits.
  Closing it means hiding from the statement exactly the writes its transaction made since it began, which the transaction's undo log past the statement's start names, the first entry per address giving the image to read instead; a scan that reads the table afresh after a wait (`ScanInKeyOrder`, `OrderedScanRest`) would also have to put the deleted and moved rows back in key order.
- **A DML statement's `OUTPUT` rows go out after it wrote them all**, where real writes as it sends: another session meets the X of a row real hasn't reached yet, a `NOLOCK` reader counts every change at once, and a lock wait the statement meets holds up `ExecuteReader` before its first row where real's client reads the rows before it.

## `GetDataTypeName` answers SqlClient, not the server

This is a *client* surface, so it reports the name of the CLR-facing type a TDS token maps to rather than the name the server declares.
Two of the simulator's own type names never reach a SqlClient consumer as written, and are mapped here — probed over SqlClient 7.0.2 against SQL Server 2025 (2026-08-05) through `GetDataTypeName`, `GetSchemaTable`'s `DataTypeName` / `ProviderType` / `ProviderSpecificDataType`, and `GetProviderSpecificFieldType`, all of which agree:

- **`numeric` reads `decimal`.** The server genuinely distinguishes the two — `sys.dm_exec_describe_first_result_set` reports `numeric(2,1)` for `SELECT 1.0`, `numeric(9,2)` for `CAST(1.0 AS numeric(9,2))` and for a `numeric`-declared column, against `decimal(9,2)` for the `decimal` spellings — and the distinction still rides the wire, where mssql-jdbc's `getColumnTypeName` reads it off the NUMERICN (`0x6C`) / DECIMALN (`0x6A`) COLMETADATA token the listener writes.
  SqlClient collapses both to `SqlDbType.Decimal`, whose type name is `decimal`, so an in-process consumer must see the collapsed name.
  `SimulatedQueryResult.ColumnReportsNumeric` keeps carrying the split for the listener; `Tests.Internal`'s `DecimalTypeNameTests` is where it's pinned, since no public ADO.NET surface can observe it.
- **`sysname` reads `nvarchar`.** It is an alias for `nvarchar(128)` and TDS carries the base type, so every producer of one reports `nvarchar` on real: a `sysname` column, the catalog views' `name` columns, the `TYPE_NAME` / `SCHEMA_NAME` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` / `DB_NAME` / `USER_NAME` / `SUSER_NAME` / `SUSER_SNAME` / `COL_NAME` / `FILE_NAME` / `FILEGROUP_NAME` / `INDEX_COL` family, `CURRENT_USER` / `SESSION_USER` / `SYSTEM_USER` / `USER`, `CAST(… AS sysname)` and a `sysname` variable.
  `sysname` likewise carries the **national** string family into a promotion the way `nvarchar` does, so `TYPE_NAME(56) + ''` types as `nvarchar(129)` rather than falling to `varchar` (`SqlType.Promote`'s two `national` predicates).

Everything else in the type matrix already matched: the whole numeric / character / binary / date-time family, `xml`, `sql_variant`, `hierarchyid`, `rowversion` (reported `timestamp`), and an alias type, which reports its base type's name on both.

## `GetSchemaTable` answers SqlClient's table

`GetSchemaTable` (`ResultSchemaTable.Build`) returns SqlClient's 31-column schema table — `ColumnSize`, `NumericPrecision` / `NumericScale` (255 where a type has none), `ProviderType` as `SqlDbType`, `DataType` and `ProviderSpecificDataType` (the `System.Data.SqlTypes` type, a CLR type resolved by its assembly-qualified name as SqlClient resolves it), `IsIdentity` / `IsAutoIncrement` / `IsReadOnly` from the column-character flags the endpoint writes to COLMETADATA, `IsLong` for the MAX and legacy LOB types — which is what `DataTable.Load` and `DataAdapter` read; without it both threw `NotSupportedException`.
`SchemaTableDualReadTests` holds every cell equal to SqlClient's own table for the same result read over the TDS endpoint, which is how the per-type values were settled.
`ExecuteReader(CommandBehavior.KeyInfo)` turns `NO_BROWSETABLE` on for the command and back off when the reader closes, as SqlClient's `SET NO_BROWSETABLE ON` / `OFF` wrapper does, so the table then carries the base table and column and the key / hidden / expression / alias flags (see [`tds-endpoint.md`](tds-endpoint.md#browse-mode-commandbehaviorkeyinfo)).

## An `xml` value reads through `SqlXml`

SqlClient hands an `xml` column over as the text `SqlXml.Value` writes — its reader-to-writer round trip spells an empty element `<a />` and single-spaces attributes — from `GetValue`, `GetString`, `GetChars`, `GetFieldValue<string>` and `ExecuteScalar` alike (confirmed 2026-09-25 against SqlClient 6.1 over SQL Server 2025), so the reader and `SimulatedDbCommand.ExecuteScalar` pass an `xml` value through the same round trip; the stored text, which `CAST(x AS nvarchar(max))` reads, keeps the server's own `<a/>`.
The TDS endpoint sends the stored text and lets the connecting client do its own round trip.

## `RecordsAffected`

Rows the batch's statements **changed**, summed — never rows a SELECT returned.
The number is the same one `ExecuteNonQuery` reports for the same batch, in every shape probed against SQL Server 2025 (2026-08-03): DDL, each DML verb, SELECT, `SELECT INTO`, MERGE, `OUTPUT`-to-client DML, mixed batches, procedures, `SET NOCOUNT`.
`-1` means no statement contributed a count.

What contributes:

- **DML contributes its rows-affected** — `INSERT` / `UPDATE` / `DELETE` / `MERGE`, a `WHILE` body's every iteration, a `SELECT … INTO` (it writes rows), and a statement whose `OUTPUT` clause returns rows to the client (tabular, but its count is still a rows-affected count).
  A statement matching nothing contributes `0`, which is distinct from `-1`.
- **A SELECT contributes nothing**, however many rows it returned — including the assignment-only `SELECT @x = col FROM t`, which reads rows without returning them, and a cursor `FETCH`.
- **DDL, `SET`, `DECLARE`, `PRINT` and an un-taken branch contribute nothing.**
- **`SET NOCOUNT ON` suppresses the contribution** of every statement that runs while it is on, whatever the kind — the count is recorded per statement rather than read when the client consumes the outcome, because a procedure body's `SET NOCOUNT` reverts at the body's exit, before the caller pulls what the body produced.

`SET NOCOUNT`'s **scope** decides how far the suppression reaches, and real's is narrower than "the session" in four cases (probe-confirmed by running a counting statement on the same connection afterward):

| where `SET NOCOUNT ON` runs | reaches the next command? |
|---|---|
| a plain batch (no parameters) | yes — session state, and it outlives the batch |
| a command carrying parameters | no — SqlClient sends one as `sp_executesql`, whose SET options revert with the scope |
| `EXEC('…')` / `sp_executesql` | no |
| a procedure body | no |
| a trigger body | no — and the firing statement keeps its own count |

The simulator restores the flag at each of those scope exits, next to the `TEXTSIZE` / `QUOTED_IDENTIFIER` restores already there; the in-process front door treats a parameterized command as the ad-hoc scope SqlClient turns it into, which is what EF Core's modification batches (they open with `SET NOCOUNT ON`) depend on.
The wider SET-option set is not scoped that way for a parameterized command — only `NOCOUNT` is.

The value accumulates as the reader is advanced and is final once it is closed: a statement ahead of the current result set has already contributed, one behind it has not yet, and `Close` / `Dispose` runs the rest of the batch and folds in what it counted (`Close` is overridden for exactly that reason — `DbDataReader`'s base `Close` is a no-op).

The wire renderer answers the same question with the two DONE-token fields real uses, both captured off SQL Server 2025's wire.
`CurCmd` names the kind of statement that produced the token and is what a client keys on to leave a SELECT's count out of the sum — real tags a plain SELECT and a cursor `FETCH` `0x00C1`, and `SELECT INTO` / `INSERT` / `DELETE` / `UPDATE` / `MERGE` their own kinds (`0x00C2` / `0x00C3` / `0x00C4` / `0x00C5` / `0x0117`).
`DONE_COUNT` says whether there is a count at all, and NOCOUNT clears the flag while leaving the row count in the token.
The simulator classifies SELECT and leaves every other kind `0`; see [`tds-endpoint.md`](tds-endpoint.md).

### Divergences

- **Mid-stream timing after a result set is exhausted.**
  Real's client reads tokens ahead to the next result-set boundary, so once `Read` has returned false the counts of the *following* non-row-returning statements are already in; the simulator folds them on the `NextResult` that steps over them.
  A batch's final value agrees, and a single-statement batch is unaffected.
- **A DML statement's `OUTPUT` count lands early.**
  A DML statement produces its `OUTPUT` rows whole before sending them, so `RecordsAffected` reports an `INSERT … OUTPUT`'s count the moment the reader parks on it; real learns it from the DONE that follows the rows, and reads `-1` until they are drained.
- **A trigger's own DML doesn't contribute.**
  Real counts the writes a trigger body performs into the firing statement's total (an INSERT firing a trigger that writes two rows reports 3); the simulator reports the firing statement's own count alone.
  Both front doors agree with each other — the counts never reach the outcome stream.

## Batch-error surfacing (positional)

The reader consumes the unified continue-on-error outcome stream (see [`control-flow.md`](control-flow.md)), so a mid-batch statement error is a `SimulatedErrorOutcome` in the stream rather than a throw.
`AdvanceToNextResult` (the constructor's and `NextResult`'s shared step) skips pure `SimulatedNonQuery` outcomes and stops on either a `SimulatedQueryResult` or a `SimulatedErrorOutcome`, mirroring SqlClient's positional error model:

- **A SELECT whose rows fail at run time** has already sent its COLMETADATA and the rows before the failing one on real, and sends the error before the result set's DONE (probed 2026-09-25 against SQL Server 2025).
  The statement keeps the rows its materialization produced and yields them as a result set marked `SimulatedSqlResultSet.EndedByError` — an empty one carrying the plan's metadata when the first row failed, or the plan itself — and the error follows it; `Read` after the last row throws that error, and the TDS endpoint writes it ahead of the result set's DONE.
  Inside a `TRY` the partial result set still goes out ahead of the `CATCH` output, as real sends it, so `ExecuteScalar` over `BEGIN TRY SELECT CAST('x' AS int) END TRY BEGIN CATCH SELECT 2 END CATCH` is NULL on both.
  Its caught error never reaches the client, so the result set is marked `ErrorCaught` too: it reports a count of 0 whatever rows it sent, and the next error in the stream — a `THROW` in the `CATCH`, say — is a later statement's rather than one `Read` raises (probed 2026-09-26).
  A FROM-less SELECT, whose values are computed while it parses, keeps a value's error for the plan to raise when it runs, past its row limit and WHERE.
  Rows buffered for an ORDER BY are projected before sorting, so none precede the error there where real may deliver the rows it sorted ahead of the failing one.
- **Any other failed statement** — DML, DDL, and a SELECT refused before its first row (a missing table, a permission) — sent no result-set envelope, so the error throws *eagerly* on the advance onto it: `ExecuteReader` (the constructor's advance) or `NextResult` throws rather than a later `Read`, as SqlClient does against real (probed 2026-09-25: `SELECT 1; SELECT * FROM missing` reads the 1 and `NextResult` throws Msg 208).
  This is what lets EF Core's no-OUTPUT modification batches — which never call `Read` — observe a failed write.

`ExecuteNonQuery` / `ExecuteScalar` bypass this positional model: they drain the whole outcome stream and aggregate every error into one `SimulatedSqlException` thrown at completion (`ExecuteScalar` returns the first result set's first value only when the batch had no error).
Which informational messages ride along in an exception rather than firing as events is in [`errors.md`](errors.md#the-message-stream).

**Dispose = drain**: closing the reader produces and discards the rest of the result it was parked on, then executes the batch's remaining statements (side effects persist) and swallows their errors — a disposed reader never throws.

## In-process MARS (overlapping readers)

The in-process `SimulatedDbConnection` is a MARS connection: a second command — or a second open reader — while a reader is live works, as EF Core's lazy loading needs (iterate a parent query, touch a navigation per row), and its connection string says so until one is set (`MultipleActiveResultSets=True`), which is what tells EF Core to take no savepoint in a user transaction.
Another command runs beside a statement suspended on a reader's client ([above](#rows-go-out-as-the-reader-reads-them)), as real runs another MARS request beside one suspended mid-statement, the suspended statement's position — its locks, statement snapshot, LOB epoch, `CommandTimeout`, `STATISTICS IO` count and session settings — parked with its request (`SimulatedDbConnection.ParkRequest`) and taken back as its reader reads on.
Real separates a session's requests by the transaction each works in, so the suspended statement's locks meet another request's statements as another session's would: a request that begins while another of its session's is unfinished holds its autocommit statements' locks, and a transaction it begins, under a token of its own (`SessionRequest.LockOwner`), while requests working in the same transaction share it.
A write of the session's own then waits on its suspended reader's row or key until a timeout ends the wait, as real's mostly does — real's deadlock monitor ended 5 of 27 such waits with Msg 1205 within five seconds, whatever the lock, and the rest ran to their `CommandTimeout` (probed 2026-10-05 and 2026-10-08 against SQL Server 2025); an `ALTER TABLE` or `TRUNCATE` waits on its table lock, while `CREATE INDEX` shares it (probed 2026-10-05).
A cursor's scroll locks are the session's (`SimulatedDbConnection.SessionScope`), so a positioned update meets its cursor's lock as its own whichever request runs it.

Real's MARS rules apply, with each command a request (`SessionRequest`) that real tracks two ways — see [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets) for the probed rules:
- **Running** until its batch's outcome stream has ended.
  A reader's batch runs ahead of it as real's does ahead of its client, as far as its output fits about 32 KB (`SessionRequest.BytesAheadOfClient`, `SimulatedDbDataReader.RunAhead`): past a small result set as soon as the reader is positioned on it, past a large one once the reader has read its last row, and past a statement's error once the reader has raised it — a statement inside a block, a `TRY`, an `IF`, a `WHILE` or a procedure or dynamic-SQL call as much as one at the batch's level, since those stream their statements' outcomes.
  What a statement run ahead reports still waits for the advance onto it — its count reaches `RecordsAffected`, its error throws and its messages fire there.
  A rollback of the transaction a running reader works in leaves its batch doomed — `XACT_STATE()` -1, a write Msg 3930 — ending with Msg 3998, and every command until then is Msg 3989; a reader parked on a DML statement's `OUTPUT` rows larger than that is Msg 3980 for every other command, after the four seconds it waits first (see [`tds-endpoint.md`](tds-endpoint.md#mars-multiple-active-result-sets)) — a shorter `CommandTimeout` ending it with Msg -2 instead.
- **Outstanding** until the reader has read past its end — `Read` false on the batch's last result set, `NextResult` false, or `Close` — however small the result.
  `BeginTransaction` with another reader outstanding is Msg 3988; a commit or save point, through the API or by SQL text, with one outstanding in the transaction is Msg 3981; and a transaction a batch begins and leaves open while another is outstanding rolls back with Msg 3997.
  `ExecuteNonQuery` and `ExecuteScalar` read their batch whole, so never stay outstanding.

A transaction-manager stand-in — `BeginTransaction`, an API `Commit` or `Save` — reports its refusal at line 1, as real reports a transaction-manager request's.

Each command works on its own copy of the session's per-request state, as a wire request does — its settings, `@@ROWCOUNT`, `@@ERROR`, identity values, cancellation scope and a transaction its batch began — so a reader's later statements read their own whatever another command did between, and a command run meanwhile starts from what the last finished one left (`SimulatedDbConnection.ResumeRequest`, swapping only once commands overlap).
A command takes the lowest request id from 2 that no unread reader holds, as SqlClient numbers its MARS sessions, and `sys.dm_exec_requests` lists a reader whose batch is still running as suspended on `ASYNC_NETWORK_IO`.
`Cancel` targets the command's own request, so a reader cancelled while it reads a SELECT runs none of the statements after it, however many commands ran meanwhile.

**Not modeled yet** (beside the wire's, which apply here too):
- A transaction a batch began and left open is rolled back with Msg 3997 only when another reader was outstanding as its batch ended, where a MARS batch's always rolls back.
- `ChangeDatabase` changes the database of a reader whose batch is still running, where SqlClient sends it as a request of its own, which leaves the reader's copy alone.

## Divergence

**`GetBytes` / `GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer.
Per-call observation matches SqlClient; the streaming-memory guarantee doesn't.

**A non-fitting `decimal` always raises `OverflowException`.**
SqlClient answers a minority of them with `System.Data.SqlTypes.SqlTypeException("Invalid numeric precision/scale.")` instead — the split is value-dependent even between operands whose precision, scale and byte length are identical (`123456789012345678901234567890.0` raises the `SqlTypeException` where `111111111111111111111111111111.0` raises the `OverflowException`), and it comes from `SqlDecimal.ConvertToPrecScale` receiving a negative scale inside `SqlBuffer.get_Decimal`.
That is a SqlClient internal rather than a server behavior, so the one exception type is what the reader raises.
