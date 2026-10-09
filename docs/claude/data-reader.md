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
The statement produces about 40,000 wire bytes before the reader reads anything — the packet the client is reading and the four `SessionRequest.BytesAheadOfClient` grants after it — then three packets more each time the reader reads into a third packet (the third, the sixth, the ninth …), and otherwise waits, `suspended` on `ASYNC_NETWORK_IO` in `sys.dm_exec_requests` and `sp_who`, holding what its position needs (`ResultStream`).
Probed 2026-10-08 and 2026-10-09 against SQL Server 2025 over a MARS connection, which is what the in-process connection is: a `REPEATABLE READ` reader two rows into a result held the key locks of 20 rows of 2,007 wire bytes, 79 of 507 and 4,438 of 9 — each about 40,000 bytes produced — and a reader of the 2,007-byte rows held 32 from its tenth row, 44 from its twenty-first, 116 a hundred rows in and 319 three hundred in, of 107-byte rows 373 until it read into the third packet, then 596, then 820 from its 380th; the window here matches each to within a row, the client's position being the packet the row it reads begins in.
Over the TDS endpoint outside MARS the run-ahead is whatever the connection's socket buffers take on top of that window, as on real, whose reader held 2,119 such keys there; over MARS it is the client's four-packet SMP window, the statement producing a packet past what the writer has buffered.
What the suspended statement holds, per isolation level and table hint, is in [`locking.md`](locking.md#a-reader-suspended-mid-result).

What streams is a statement-level `SELECT` of a command the in-process reader (`ExecuteReader`) or the TDS endpoint runs (`SimulatedDbCommand.StreamsResultRows`), whose outcomes reach that consumer as they are produced — one at the batch's level or inside a block, a `TRY`, an `IF` or a `WHILE` there, or in the body of a procedure or dynamic batch the batch calls with its outcomes streaming (`EXEC p`, `EXEC (…)`, `sp_executesql`; `BatchContext.StreamsResultRows`), or in a procedure an RPC calls (`CommandType.StoredProcedure`, in process and over TDS) — and a cached plan's replay alike; and a DML statement's `OUTPUT` rows in the same places ([below](#a-dml-statements-output-rows)).
Its plan doesn't matter: a sort, an aggregate or a join's build reads its input before its first row goes out, as real's does, and its output then streams, the statement holding its object lock until its last row (probed: a `READ COMMITTED` reader of a sort holds `OBJECT IS` while the sorted rows go out).
A result whose rows all fit the first window is produced whole before it is sent, exactly as before, which keeps the small-result path free of the machinery.
A statement's own `FOR JSON` or untyped `FOR XML` document streams its chunks as the source rows serialize (`Selection.documentPieces`), every serializer writing its text a row at a time, so a reader a chunk into a `FOR JSON PATH` over 2,000 rows of 2,000 characters holds eleven key locks under `REPEATABLE READ`, as real's does (probed 2026-10-09 against SQL Server 2025: eleven, twenty-nine twenty chunks in, ten for `FOR XML PATH`, as here); its count stays the rows serialized, settled as its last chunk goes.
A `FOR XML … TYPE` value is one value, produced whole on both engines.
A procedure or dynamic batch an `EXECUTE … WITH RESULT SETS` calls streams too, its `SELECT`s converting their rows to the declared types as they produce them ([`programmable.md`](programmable.md#execute--with-result-sets)), and so does a trigger's result set, from inside the statement that fired it ([below](#a-triggers-rows)).
`ExecuteScalar` and `ExecuteNonQuery`, a function's body, a procedure an `INSERT … EXEC` calls, an assignment `SELECT` and `SELECT … INTO` produce their results whole.
A procedure's call holds no lock on the procedure once its body has compiled, as real's doesn't, so another session drops it while the reader is suspended inside it ([`locking.md`](locking.md#acquisition-sites)).

The statement is suspended inside the batch's outcome stream: the dispatch loop yields the result set, then an internal `SimulatedRowsProduced` marker after each window, and runs on only when the consumer advances the stream again — so every row is produced where a request's state, its cancellation scope, its transaction membership and the engine's culture are already in place.
A consumer of a top-level outcome stream that enables streaming reads markers from its row cursor's pull; any other loop over outcomes passes over them.
The statement ends with its last row: its `@@ROWCOUNT`, a row's error (the rows before it out first, as before), its locks, its Query Store capture and its `STATISTICS` messages settle there, and the batch's next statement runs only after.
While it waits it reports no executing thread, so the same-thread deadlock check and the abandoned-session sweep treat it as idle, and its `CommandTimeout` stops counting: SqlClient counts only the time a call spends waiting on the server, so each read that resumes the statement has the whole timeout to itself.
It keeps its LOB epoch and statement snapshot (see [`heap-storage.md`](heap-storage.md#a-freed-lob-chain-waits-for-the-statements-that-could-read-it)), its request's own while another request of the session runs ([below](#in-process-mars-overlapping-readers)).
A scan that reaches a row another request or session inserted, updated or deleted ahead of it while it waited reads the table as it stands then: once its statement has waited on its client and the heap has changed, a scan in key order reads on from the key it stopped at (`BatchContext.ScanInKeyOrder`, `OrderedScanRest`, and a `NOLOCK` scan's `ClusteredScan.Rows`), so a key inserted ahead is read in its place, an `ORDER BY` the clustered key satisfies staying ordered, and a key deleted ahead isn't (probed 2026-10-08 against SQL Server 2025 at every locking level, another session's writes and another MARS request's alike); a versioned read keeps its snapshot.
A write by another request of the statement's own transaction is the exception: real versions it, and the statement reads every row such a write reached as it found it ([below](#a-request-of-the-readers-own-transaction)).

How the statement ends early:
- **The reader moves on or closes** — `NextResult`, `Close`, `Dispose` — and the rest of the rows are produced and discarded, as SqlClient reads past them: `@@ROWCOUNT` after a closed reader counts every row, and each row is still locked and checked on the way.
- **Another request of the session redefines an object the statement holds, in its transaction**: the change is refused with Msg 3970, which dooms the transaction, so the statement reads its rows to the end and its batch ends with Msg 3998 ([below](#a-request-of-the-readers-own-transaction)); every row write in the transaction leaves it where it stood, and a request working in another transaction runs beside it.
- **The connection closes**: it closes its readers first, each running the rest of its batch to its end as a reader's `Close` does, as SqlClient's close does (probed 2026-10-09 against SQL Server 2025: a batch two rows into a 2,000-row result ran its later statements, a transaction it began and committed included); the reader's next `Read` throws SqlClient's closed-reader `InvalidOperationException`.
- **The connection is reclaimed abandoned**: the statement is abandoned and gives back what it held.
- **A `KILL`** ends a session whose statement waits on its client at once, as real does: in process the reader's next `Read` raises the broken-connection Msg 0, over TDS the connection drops under it (probed 2026-10-08 against SQL Server 2025).
  A statement running when the kill lands meets its cancellation at its next safe point, as before.
- **A cancel or `CommandTimeout`** reaches the statement as the read that resumes it runs, as Msg 0 or Msg -2 from that `Read`.

Measured 2026-10-08 in process against the build that produced every result whole (Release, one case per process, medians of five to seven interleaved processes): reading a result two rows in, the managed heap held 97.5 MB where it held 176.2 MB over 20,000 rows of `char(2000)`, and 97.7 MB against 129.9 MB over 200,000 narrow rows — the buffered result gone; draining them took 93.6 ms against 155.3 ms and 38.8 ms against 102.9 ms, a filtered 200k-row read 17.8 ms against 23.2 ms and a sorted one 180.5 ms against 195.8 ms, the results no longer outliving a collection whole.
Results that fit the first window are unchanged within noise (a parameterized point `SELECT` 3.83 µs against 3.82 µs, 10 rows 4.41 against 4.50, 100 rows 22.2 against 22.5, 1,000 rows 198 against 204) at about 110 more bytes a call, and the sqllogictest replays too (43.6 / 45.3 s against 45.2 / 45.2 s, 56.5 / 54.9 s against 58.1 / 54.7 s).
Measured 2026-10-08 again once requests interleaved mid-statement, against the build before (Release, medians of five interleaved processes): a point `SELECT` 4.36 against 4.34 µs at 64 more bytes, an `INSERT` 17.2 against 17.6 µs and an `UPDATE` 20.2 against 19.9 µs at about 50 more, and a steady 200,000-row drain 24.1 against 23.3 ms (20,000 rows of `char(2000)` 90.2 against 87.4 ms), the sqllogictest replays 47.4 / 56.9 s against 47.3 / 58.0 s.
Over a TDS MARS connection, the first of 20,000 rows of `char(2000)` arrived in 95 ms where it took 771 ms, the client holding 1.5 MB where it held 65 MB, and draining them took 1,021 ms against 490 ms, each four packets now waiting on the client's window acknowledgment; a 20,000-row `UPDATE … OUTPUT` holds its undo log until its last row, 81 MB against 40 MB at its second row.
Measured 2026-10-09 again once `WITH RESULT SETS` and trigger rows streamed and the window refilled three packets at a time, against the build before (Release, medians of five interleaved processes): a point `SELECT` 4.75 against 4.66 µs at 55 more bytes, 100 rows 40.3 against 40.4 µs, an `UPDATE` 9.49 against 9.49 µs, a small `EXEC … WITH RESULT SETS` 19.6 against 19.8 µs, draining 200,000 narrow rows 42.0 against 42.6 ms and 20,000 rows of `char(2000)` 101.9 against 103.8 ms, the heap two rows into those 95.2 against 95.0 MB, and the sqllogictest replays 43.3 / 53.1 s against 43.4 / 54.0 s.

### Rows before a wait go out while the statement waits

While another session holds a lock that could keep the statement waiting (`LockManager.BlockingHolds` past its own owner's), the statement produces its rows on a thread of its own and its client reads the ones it has meanwhile, as real's server produces ahead of its client (`ResultStream.StartBackground`): its first window is its first packet, which is what `ExecuteReader` waits for, and the rest it produces in the background as the client reads.
When that production begins a lock wait, its client reads every row that begins inside a packet the statement filled before the wait — the rest of the packet it was filling never goes out — and the next `Read` waits on the statement, its `CommandTimeout` counting from there (`ResultStream.AwaitBackground`).
Probed 2026-10-09 against SQL Server 2025, MARS or not: with 2,007-byte rows and a writer holding row 3, 5, 9, 12, 30 or 100 the client read 0, 4, 8, 8, 28 and 96 rows before its timeout — `ExecuteReader` itself timing out at row 3 — and with 107-byte rows and row 60, 100, 151 or 301 held, 0, 75, 150 and 299; both engines read those counts in process and over TDS, where the endpoint's own framing holds the unfilled packet back.
A cancel ends the statement where its client stands, so the held-back rows never arrive; a `LOCK_TIMEOUT`, `NOWAIT` or other error of the statement's own sends every row before it first, as on real.
Uncontended, a production can't wait, so it runs on the client's own call as before; every other entry into the session's engine — another command, an API transaction request, a reader advancing past the result, `Close` — waits the background production out first (`SimulatedDbConnection.JoinBackgroundProduction`), which also bounds the wait by the statement's `CommandTimeout`.
The production thread takes the request's executing thread and culture as its own while it runs, so the same-thread deadlock check and the abandoned-session sweep read it as the request's.

### A request of the reader's own transaction

Real versions a write another request makes in the transaction a suspended statement works in — the transaction the API or an earlier batch began, which both work in — so the statement goes on reading the rows as its transaction left them when it began, holding the locks its position holds and taking the rest as it reaches them, while another session's committed write ahead of it reads as written (probed 2026-10-09 against SQL Server 2025 at `READ COMMITTED`, `REPEATABLE READ`, `SERIALIZABLE`, `READ UNCOMMITTED` and under `NOLOCK`, scans, ranges and descending reads alike: a row the other request updated ahead reads as before, one it deleted is read, one it inserted isn't, a key moved either way reads at its old key, the transaction's write from before the statement reads as written, the reader's twenty `KEY S` stayed twenty, and another session's update and insert ahead went in and were read; and at `SNAPSHOT` and under `READ_COMMITTED_SNAPSHOT` the same).
The simulator does the same (`Storage.OwnWriteImages`): a statement waiting on its client inside a transaction has the transaction's undo log note each visible row write from then on, the row's image before the write — or its absence, for an insert — keyed by address, a rollback to a save point forgetting what it took back, and while the statement produces rows (`SimulatedDbConnection.ActiveOwnWriteImages`) every read path takes a noted row as noted: the heap walk, the key-order and fenced scans, the seeks and ordered seeks, the `SERIALIZABLE` key walk, `NOLOCK`'s scan and a snapshot read's scan and seeks read the image at the address, and an order they read again after a wait puts a noted row back under its old key (`BatchContext.WithOwnWrites`).
A request of the transaction redefining an object the suspended statement holds — `ALTER`, `TRUNCATE`, `DROP`, an index build, whatever the statement's level, under `NOLOCK` too — is refused with **Msg 3970**, which ends its batch and dooms the transaction as an error under `XACT_ABORT` does: every request until the reader's batch ends is Msg 3989, and the reader reads on to its end, its batch ending with Msg 3998 (probed 2026-10-09 against SQL Server 2025; `SimulatedDbConnection.RefuseDefinitionBesideSuspendedRead`).
A redefinition of an object the statement doesn't hold goes ahead beside it, and so does `UPDATE STATISTICS` of one it does.

### A trigger's rows

A trigger's result set goes out as its client reads it, from inside the statement that fired it, which waits mid-way meanwhile, as real's does (probed 2026-10-09 against SQL Server 2025 with a reader two rows into a trigger's 2,000 rows of `char(2000)`: the firing update held its row's X and the trigger's read its position, under `REPEATABLE READ` its twenty keys, the trigger's statements after its `SELECT` hadn't run, and the request read `suspended` on `ASYNC_NETWORK_IO` with the command `SELECT`).
A DML statement of a streaming command whose database has a DML trigger runs on a thread of its own (`StatementCoroutine`), which a trigger body's outcomes are handed out of as the body produces them — its messages and counts ahead of its rows as before, and its `SELECT` streaming as one at the batch's level does — the two threads taking turns, so only one runs at a time; the statement's thread reports no executing thread while it waits, and the request holds the session, so another command waits and is refused with Msg 3980, as for a DML statement's `OUTPUT` rows below.
Closing the reader or its connection runs the rest to its end and commits it, as on real; a row's error rolls the firing statement back with the rows before it out first; a cancel or `KILL` ends it, rolled back.
A reader an application drops without closing it is collected, since the statement's thread names it only weakly, and the thread then ends the statement as a closed connection's reclamation would.
A trigger fired by a trigger body's statement — or by one of a procedure or dynamic batch the body calls with its outcomes going straight out — sends through the same thread, its rows streaming as the outer trigger's do, and so does a trigger on another database's table a three- or four-part name writes (`Simulation.MayFireCrossDatabaseTrigger`; probed 2026-10-09 against SQL Server 2025: a reader two rows into a nested trigger's rows held its twenty keys under `REPEATABLE READ`, the inner body's statements after its `SELECT` and the outer body's after its `INSERT` not yet run); a statement with `STATISTICS IO` or `TIME` on runs whole.
What a trigger body's statements write stays locked until the firing statement ends, as its transaction's writes do on real ([`locking.md`](locking.md#lock-owner--lock-scope-model)).
Only a statement naming a table or view a trigger is attached to — or one the look-ahead can't name — takes the thread (`Simulation.MayFireTrigger`), the threads kept idle between statements for reuse: measured 2026-10-09 in process (Release, medians of five interleaved processes against the build before), an `INSERT` into a table with a trigger through `ExecuteReader` took 19.5 µs against 15.6, the handoff's cost, while the same statement through `ExecuteNonQuery` and every statement of a table without one were unchanged within noise; a thread created per statement had cost 275 µs.

### A DML statement's `OUTPUT` rows

A DML statement whose client `OUTPUT` rows reach such a consumer writes each row as it sends it, as real's pipeline does: its executor hands its rows back unwritten (`SimulatedSqlResultSet.WritesAsItSends`), and each row's read, lock, write and checks run as the row is produced (`Simulation.SendAsWritten`), inside the statement's atomic scope whichever thread asks for it.
Its first window is written before the result set goes out and the rest as the client reads, its ending — its commit or its place in the transaction, its statement-scoped locks, its `@@ROWCOUNT`, the count a reader adds to `RecordsAffected` — held until its last row (`ResultStream.PendingWrite`).
Probed 2026-10-09 against SQL Server 2025 over MARS, a reader two rows into 2,000 rows of 2,010 bytes: an `UPDATE`, a `DELETE`, an `INSERT … SELECT` and a `MERGE` each held the X of 20 rows and a `NOLOCK` reader counted 20 written; another session read and wrote a row ahead, its write then read as written when the statement reached it, a key it inserted ahead was written and one it deleted ahead passed over; a cancel rolled the statement back, an API transaction around it left open, and a reader closing wrote the rest.
The window measures the rows as the wire carries them (`EncodedValueRowMeasure`): real's held 4,437 rows of 9 bytes, 2,853 of 13, 193 of 210 and 79 of 507, each within a few rows here.
A row's error ends the statement where it is met, rolled back, the rows before it sent first however few — `INSERT t OUTPUT inserted.a VALUES (2), (1), (3)` over a key 1 sends the 2 row, then Msg 2627 — and a `TRY` frame catching it leaves the statement counting 0; a row another session holds stops the write where its walk meets it, the 99 rows before it going out ahead of the lock timeout.
A command not reading its rows as they come (`ExecuteNonQuery`), and an `OUTPUT` statement in a trigger body, write every row first, as before.

When real writes the rows is its plan's to say, and the simulator follows it shape by shape (`Simulation.WriteOrder`, probed 2026-10-09 against SQL Server 2025):

- **As read** — a single-table `UPDATE` setting no key or index column, a `DELETE`, an `INSERT`, a joined write to another table, a `MERGE`: each row read, locked and written as it goes out, the rows ahead untouched, a scan reading on from the key it stopped at once the statement has waited and another session changed the table (`Simulation.TargetRowsAsTheyStand`).
  A `DELETE` from a table with no nonclustered index sends each row before it deletes it, so the row a window ends on is held but not yet gone — 20 held and 19 gone, a heap's too, 20 gone with a nonclustered index — a row a foreign key's child references refused before it is sent.
- **Read first** — an `UPDATE` setting a nonclustered index's key or included column, a statement holding a subquery, a self-join: every qualifying row read first and held in U, each converted to X as it is written (real held U on the 1,980 rows ahead and X on 20, a `NOLOCK` reader counting 20 written).
  A joined write reads its join whole before its first write, holding the rows ahead in U only where real's spool does.
- **Whole first** — an `UPDATE` of a clustered or unique key, a cascading foreign key's parent, a system-versioned target, an indexed view over the target, a write through a view or to a linked server: every row written before the first goes out, its ending held as the rows go out.

A `MERGE` matches its source and computes every action before writing the first, holding its matched rows in U, and an `INSERT` reads its source whole first — real's spool for a source reading its own target.
Measured 2026-10-09 in process against the build that wrote every row first (Release, medians of five interleaved processes, steady state): a statement without `OUTPUT` unchanged within noise (a point `INSERT` 6.58 against 6.60 µs, a point `UPDATE` 4.63 against 4.58, a 1,000-row `INSERT … SELECT` 1,259 against 1,273 µs); a one-row `INSERT … OUTPUT` 6.49 against 6.16 µs at 335 more bytes, an `UPDATE … OUTPUT` of one row 5.05 against 4.74 µs at 728 more, EF Core's four-row `MERGE` 18.5 against 18.9 µs; draining 200,000 `UPDATE … OUTPUT` rows 326 against 389 ms, `DELETE … OUTPUT` 140 against 232 ms, `INSERT … SELECT … OUTPUT` 418 against 458 ms, and 20,000 rows of `char(2000)` 254 against 333 ms, the heap two rows into those 49.5 MB against 134.2 MB, the undo log holding the rows written; the sqllogictest replays 43 / 43 s against 42 / 44 s and 54 / 56 s against 54 / 54 s.

### Divergences

- **Another request of the session waits for a statement producing its rows in the background** — one under contention, waiting on a lock — before it runs, where real runs it beside the waiting statement; the wait ends with the statement's own lock wait, or with its `CommandTimeout`, which the waiting request's call arms.
- **A request refused with Msg 3970 lets its transaction go at once**, rolling back what it held, where real keeps the doomed transaction's locks — the refused change's Sch-M among them — until the reader's batch ends it.
- **A snapshot read takes a row another request of its transaction rewrote as the row stood before that write**, which is its snapshot's own unless another session committed a change to the row after the statement began and before the rewrite.
- **Under contention a result larger than one packet streams**, its first window being that packet, so a reader's batch runs ahead past it only once the reader has read its last row ([In-process MARS](#in-process-mars-overlapping-readers)), where real's runs ahead past a result its 32 KB still holds.
- **A DML statement's write order departs from real's plan at its margins**, real choosing by cost (probed 2026-10-09 against SQL Server 2025):
  - an `UPDATE` of a nonclustered index's column scanning the clustered index under an `INDEX(1)` hint writes as read on real, read first here;
  - a large `INSERT … SELECT` or `MERGE` real sorts by the clustered key sends its rows in key order, a duplicate key surfacing at its sorted place (1,499 of 2,000 rows ahead of the error, where source order sends 1,999 here); 500 narrow rows real sends unsorted;
  - a large insert into a table with a foreign key real writes whole and checks with a hash join, sending every other row before the violation (199 of 200, 59 of 60), where here the rows before the failing one go out, as real's nested-loop plan sends them for 40 rows or a `VALUES` list of 100;
  - an `UPDATE` of the clustered key real writes in two passes, every old key gone before the first new one goes in as the rows go out, and a system-versioned target's history rows go out with the rows while its current rows are written whole first, where both write everything first here;
  - a subquery reading only another table reads the target whole first here, where real spools only for one reading the target.
- **A DML statement's walk reads ahead of real's** in a few shapes: an `INSERT`'s source is read whole before its first write, where real reads one that isn't its own target as it writes, holding its position's S, so another session's change to a source row ahead is read as written there; a `MERGE` matches and computes every action first, holding its matched rows in U where real holds nothing ahead; a seek chooses its rows as the statement begins, so a key another session inserts into the sought range ahead isn't written; and a `SERIALIZABLE` write takes its whole fence as it begins, where real's range locks grow with the rows written.

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
