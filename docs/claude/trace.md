# SQL Trace

The server-side trace surface — `sp_trace_create`, `sp_trace_setevent`, `sp_trace_setfilter`, `sp_trace_setstatus`, `sp_trace_generateevent`, the `fn_trace_get*` functions, `sys.traces` and the trace catalog — as SQL Server 2025 has it.
Everything here was probed 2026-10-16 against SQL Server 2025 (17.0.4065.4) on Linux unless it says otherwise.
The default trace's own row and its empty file live in [`catalog-views.md`](catalog-views.md#server-health-views).

## The catalog

`sys.trace_categories`, `sys.trace_columns`, `sys.trace_events`, `sys.trace_subclass_values`, `sys.trace_event_bindings`, `sys.trace_xe_event_map` and `sys.trace_xe_action_map` list real's rows in real's order: 21 categories, 66 columns, 180 events, 6,279 subclass values, 4,316 bindings, 139 event mappings and 22 action mappings.
The rows live as tab-separated text under `src/SqlServerSimulator/Trace/Metadata/`, one file per view, zipped into the assembly like the Extended Events catalog (`ZipTraceMetadata`), and `TraceCatalog` reads them once per process.
`DefaultTraceEvents.tsv` beside them is the default trace's own event set, what `fn_trace_geteventinfo(1)` lists.
Real types the two `_map` views as tables in `sys`; here they register as views.
None of the views needs a permission.

## The procedures

Each is an extended procedure: arguments bind by position whatever name they're given, too many is Msg 8144 (which ends the batch), a missing required one Msg 201 (state 15 for the first parameter, 17 for a later one), and `sp_trace_create`'s `@traceid` not marked `OUTPUT` Msg 591 state 57 (which ends it too).
Each argument must be of exactly the parameter's type, or it is Msg 214 with return code 15 — state 4 naming `nvarchar(256)` / `nvarchar(128)`, state 5 for the rest: an `int` parameter takes neither `smallint` nor `bigint`, `@maxfilesize` only `bigint`, `@stoptime` `datetime`, `datetime2` or `datetimeoffset` (not a string, `date` or `smalldatetime`), `@on` any integer type, a string only `nvarchar` no longer than the length, and `DEFAULT` nothing.
The `NULL` keyword fits where the parameter takes NULL — `@tracefile`, `@maxfilesize`, `@stoptime`, `@filecount`, `@columnid`, a filter's `@value`, `@userinfo`, `@userdata` — and nowhere else, as a typed NULL of the wrong type doesn't.
Then `ALTER TRACE` (Msg 8189, state 6 for `sp_trace_create`, 7 `setstatus`, 8 `setevent`, 9 `setfilter`, 10 `generateevent`).
A refusal past binding raises its error and sets the return code real documents — unless a `TRY` will catch it, when the call never completes and the return code stays as it was.

- **`sp_trace_create`.**
  A stop time already past is Msg 19064 (14), checked before anything is answered, so `@traceid` stays as it was; then `@traceid` is set to 0.
  Options outside 1, 2, 4 and 8, or 1 or 8 with another, are Msg 19060 (10); `TRACE_PRODUCE_ROWSET` (1) alone, a black box (8) given a file, no file, a size of 0 or less, a file count without rollover or of 1 or less are Msg 19065 (15) — a file count of 0 with rollover means no limit.
  A black box with no file is Msg 19051 (1) after Msg 19097 naming `0x80070002(The system cannot find the file specified.)`: real's Linux server can't find its file.
  `.trc` is appended whatever the name ends in (`x.trc` makes `x.trc.trc`); a file that exists is Msg 19062 (12) after Msg 19097 naming `0x80070050(The file exists.)`, a directory that doesn't `0x80070005(Access is denied.)`; a relative name is taken as given.
  The trace takes the lowest id above 1 that no trace holds, stopped, with its file created empty; `sys.traces` lists it with two buffers of 1,024 KB, `max_files` 1 without rollover, the file count (0 for none) with it, and `file_position` in whole megabytes, 1 MB before anything is written.
- **`sp_trace_setevent`.**
  The default trace is Msg 19070 state 2 (20), a missing trace Msg 19059 (9), a running one Msg 19052 (2), an event id no event has Msg 19053 (3), a column id outside 1 to 66 Msg 19054 (4), and turning `SPID` off Msg 19061 (11).
  `fn_trace_geteventinfo` lists each event with `SPID` whether or not it was set, by event and then column; `EventClass` is taken and listed nowhere, and `ServerName` is the trace's: set on one event it lists on every event, cleared on one on none.
  A column needn't be bound to its event — it lists, and rows simply never carry it.
  Turning an event's last column off drops the event; a NULL column turns the event on with no columns, or off with all of them.
- **`sp_trace_setfilter`.**
  After the trace checks (Msg 19070 state 3 for the default trace), a NULL column clears every filter; a column real has no id for is Msg 19054 (4), save 67, which real types as `varbinary` first; the value must be of the column's type (`int`, `bigint`, `nvarchar(256)` for text and string columns, `datetime`, `uniqueidentifier`, `varbinary` for image columns); then a column real doesn't filter on (`EventClass`, `EventSequence`, `ServerName`) is Msg 19054 (4), a logical operator but 0 or 1 Msg 19057 (7), and a comparison operator the type doesn't take Msg 19056 (6): numbers and times take 0 to 5, strings `=`, `<>`, `LIKE` and `NOT LIKE`, a GUID `=` and `<>`, binary columns nothing, and NULL or an empty `LIKE` pattern only `=` and `<>`.
  A filter on a column whose filters another column's came between is Msg 19055 (5).
  `fn_trace_getfilterinfo` lists the filters in the order set, each value the variant real gives it — a string as `nvarchar` its own length — save a GUID, which reads back NULL.
- **`sp_trace_setstatus`.**
  1 starts a trace, 0 stops it, 2 closes a stopped one (a running one is Msg 19052 state 2, return code 2); starting or stopping a trace already so is a no-op, a status past 2 is taken and does nothing, and a negative one is Msg 19058 state 2 (8).
  Stopping or starting the default trace is Msg 19070 state 1 (20).
  A start writes `SQL Trace ID n was started by login "sa".` to the error log and a stop `SQL Trace stopped. Trace ID = 'n'. Login Name = 'sa'.`, under the caller's spid; a close writes nothing.
- **`sp_trace_generateevent @eventid, @userinfo, @userdata`** fires user-configurable event 82 to 91 (else Msg 19053, return code 3), `@userinfo` its `TextData`, `@userdata` its `BinaryData`.

Traces don't take part in transactions: a trace made inside one outlives its rollback.

## The functions

`fn_trace_getinfo(id)` answers five rows a trace — options, file, size limit, stop time, status — for every trace under 0, NULL or `DEFAULT`, the default one first; the file is an `nchar` its own length inside the variant.
`fn_trace_geteventinfo` and `fn_trace_getfilterinfo` answer nothing for an id no trace holds.
All three take the bare, `sys.` and legacy `::` forms, and refuse a call without `ALTER TRACE` with Msg 8189 (states 12, 13, 14); an empty argument list is Msg 313 state 3.

`fn_trace_gettable(file, number_files)` reads a user trace's file and the rollover files after it — all of them when `number_files` is NULL, `DEFAULT` or below 1 — in its 66-column shape.
A file real wrote holds, besides the events, rows real's reader synthesizes from its header: `65528` with the header block as `BinaryData`, `65534` at each start, `65533` at each stop, and `65527` opening a rollover file in place of the start row.
The header block is byte for byte real's: `FF FE`, its length 656, the product name, version 17.0.4065, the file options (2, 3 with rollover), the server name, then each event with its columns and the union of every event's columns with `EventClass`.
A trace never started has an empty file.

## Events

A trace shares the seam Extended Events fires through: `XeEvents.Fire` delivers each firing to the running traces taking a class it maps onto (`TraceEvents`), the mapping real's `sys.trace_xe_event_map` lists, and every column is read off the event's own fields or the session it fired on (`sys.trace_xe_action_map`'s columns).
So a site that starts firing an Extended Event a trace class maps onto reaches traces without further work, as the object, lock-timeout, escalation and statistics events do.

| Class | Fired from | Columns beyond the session's |
| --- | --- | --- |
| 10 / 11 RPC:Completed / Starting | `rpc_completed` / `rpc_starting` | `TextData` the reconstructed call, `ObjectName`, and on completion `Duration`, `EndTime`, `Error` (0, or 1 for a request an error ended), `RowCounts` |
| 12 / 13 SQL:BatchCompleted / Starting | the batch pair | the batch text, and on completion as above |
| 14 / 15 Audit Login / Logout | `login` / `logout` | the options text with CRLF line ends, `EventSubClass` 1 or 2 (pooled), `Success`, `IntegerData` the packet size, `Type` 1; a logout's `Duration` since the login |
| 16 Attention | `attention` | `Duration`, `RequestID` |
| 17 ExistingConnection | `sp_trace_setstatus` starting the trace, once per open session — the starter's own included | the login columns, `Duration` since the login, `IntegerData` 8000 |
| 25 / 59 / 148 Lock:Deadlock / Deadlock Chain / Deadlock graph | the deadlock events | `Mode`, `Type` (the lock resource type), `OwnerID`, `ObjectID2` (the hobt), `IntegerData` the deadlock id; the chain from session 44, `EventSubClass` 101, `IsSystem`; the graph from session 2 as `sa`, its document in the older `deadlock-list` shape with CRLF line ends |
| 33 Exception, 162 User Error Message | `error_reported` | message, `Severity`, `State`, `Error`; `Exception` only for an error the engine raised, not `RAISERROR`, `THROW` or a message of severity 10 or less |
| 40 / 41 SQL:StmtStarting / Completed, 44 / 45 SP:StmtStarting / Completed | the statement events | `LineNumber`, `Offset` and `IntegerData2` the byte offsets, `NestLevel`, and on completion `RowCounts` and `IntegerData` the last row count; a module's `ObjectID`, `ObjectType` (two characters, the first in the low byte: `P ` is 8272), `ObjectName` and `SourceDatabaseID` |
| 42 / 43 SP:Starting / Completed | `module_start` / `module_end` | `TextData` the calling statement or the reconstructed RPC call, the module's columns, `NestLevel`; on completion `Duration` 0, as real reports it |
| 27 / 189 Lock:Timeout / Lock:Timeout (timeout > 0) | `lock_timeout` / `lock_timeout_greater_than_0` | `TextData` the resource as `sys.dm_tran_locks` describes it (a key's hash), `Duration`, `Mode`, `Type`, `ObjectID2` the rowset, `OwnerID`, `BigintData1` and `IntegerData2` 0 |
| 46 / 47 / 164 Object:Created / Deleted / Altered | the object events | `EventSubClass` the phase (0 `Begin`, 1 `Commit`, 2 `Rollback`), `ObjectID` (none for a database), `ObjectType`, `IndexID` (none for a table), `ObjectName` (none for a database), the database, `TransactionID`, and on a closing phase `BigintData1` its `Begin`'s `EventSequence` |
| 58 Auto Stats | `auto_stats`, a refresh's `Updated:` firing alone (a creation and the loads write no row) | `TextData` the statistics list, `ObjectID`, `IndexID`, `EventSubClass` the status, `IntegerData` the count, `Duration`, `Success` |
| 60 Lock:Escalation | `lock_escalation` | `TextData` the escalating statement's text, `ObjectID`, `ObjectID2` the rowset, `Mode`, `Type` 5, `IntegerData` the rowset's locks and `IntegerData2` the table's, `OwnerID`, `TransactionID` |
| 82–91 UserConfigurable | `user_event`, from `sp_trace_generateevent` | `TextData`, `BinaryData` |
| 137 Blocked process report | `blocked_process_report` | the report document, `Duration`, `ObjectID`, `IndexID`, `Mode`; the columns are unprobed |

The session columns are the database and its name, the host, client process, application, login and its SID (`0x01` for `sa`), `SPID`, `RequestID` 0, `XactSequence` 0, `SessionLoginName`, `GroupID` 2 and `ServerName`; `EventSequence` numbers every event any trace writes, server-wide, and a completed event's `StartTime` is its `EndTime` less its duration.
A row carries a column only when the trace chose it for the class and the class binds it.

**The default trace** runs from the simulation's start while `default trace enabled` is on, as real's does, and takes real's own events and columns (`DefaultTraceEvents.tsv`, 34 classes) — of which the simulator fires the object events, so its file holds every object created, altered or deleted, which `sp_Blitz` and SSMS's schema-changes report read off it.
A firing it alone takes keeps a compact row (`TraceEvents.ObjectRow`, captured with the session's columns as they stood) in `Trace.DefaultTraceLog`, with no Extended Events catalog read; its files are written from the kept rows only as `fn_trace_gettable` reads them — the header and start rows, then each event, rolling over at 20 MB to five files as real's do (`log_1.trc`, `log_2.trc` …) — so a server nobody reads the default trace of never loads the trace catalog.
`RECONFIGURE` turning the option off writes a stop row and keeps nothing more until it is on again, which writes a start row; `sys.traces` lists the default trace's path as last written and its event count.
An event a session or user trace also takes reaches the default trace with the row the trace classes make of it, the same `EventSequence` both.
The probed shapes are real's own default trace over the same statements (2026-10-16): `create table t (a int primary key, b int)` writes the key's index `Begin`, the table's `Begin` and `Commit`, the index's `Commit`, each `Commit`'s `BigintData1` its `Begin`'s sequence.

**Filters** read left to right, each joining what came before by its own logical operator, the first's ignored — `TextData LIKE 'a%'` then `OR DatabaseID = 9` takes either.
A column the event lacks passes every filter but `<> NULL`; string comparisons are case-insensitive, `=` ignores trailing spaces, and `LIKE` is the simulator's matcher with real's wildcards.

**Files.**
A file outgrowing `@maxfilesize` rolls over to `name_1.trc`, `name_2.trc` and on, dropping the oldest past the file count, and `sys.traces.path` names the newest; without rollover the trace stops and closes with `Trace ID 'n' was stopped because of an error. Cause: Stopping the trace because the current trace file is full and the rollover option is not specified.. Restart the trace after correcting the problem.` in the error log.
A trace past its stop time stops and closes at its next event, writing its stop row and the stop line.
A trace's files are the simulator's own form, kept in memory on the server's trace state (`SqlTraceServer.Files`) — real's tools can't read them, and they never reach the host's disk — and the server-file surfaces (`xp_fileexist`, `xp_dirtree`) see them.

**Cost.**
A firing site asks one flag the trace state publishes (`Simulation.TraceTaken`, null while no trace runs) beside the Extended Events one, so without a trace nothing changes; a workload of 4,000 client batches and a 40,000-statement loop ran as fast with no trace, and with a trace running that takes only a class nothing fires, as before the trace surface, and a trace taking `SQL:StmtCompleted` behind a filter refusing every statement cost about what an Extended Events session taking `sql_statement_completed` behind a refusing predicate does (measured 2026-10-16, Release build, median of six runs in a shared container).
The default trace, running from the start, costs a statement that changes no object nothing — a warm workload of 4,000 client batches and a 40,000-statement loop ran within 2% of the same build before it — and a DDL-heavy one (1,500 rounds of `CREATE TABLE` with a key, `CREATE INDEX`, `ALTER TABLE`, `INSERT` and `DROP TABLE`) about as fast warm and 5% slower in a cold process (measured 2026-10-16, Release build, median of six runs in a shared container).

### Divergences

- The default trace keeps its last 400,000 events, about what real's five files hold of the object events, and writes its files afresh from them at each read, so a rollover file's name follows the kept events rather than the server's uptime; `XactSequence` reads 0 where real's numbers each transaction, an autocommit statement's rows carry a `TransactionID` only when something else drew the statement one (drawing it would move the ids the session's later transactions take), and the system sessions' own object events (a statistic real's background tasks create) aren't recorded.
- `Lock:Escalation`'s `TextData` is the statement as written where real's is its simply parameterized form (`(@1 int)UPDATE [big] set [b] = @1`), without its `Offset` and `LineNumber`, and none at all for a statement the compile walk didn't read whole.
- A file's size counts each row at the simulator's estimate of real's encoding — a six-byte header and per column six bytes and its data — so a rollover lands near, not at, the event real's does.
- `Exception` fires once for a compile error real raises twice (its compile and its recompile), and after the statement's `SQL:StmtStarting` where real's constant folding raises it before.
- `Reads`, `Writes` and `CPU` read 0, `TransactionID` is the session's open transaction rather than real's per-event one, the batch and RPC events' `RowCounts` and the login options' `BinaryData` read zero as their Extended Events do, and an RPC's `BinaryData` (its TDS data stream) is left out.
- A statement run by `sp_executesql` reports `SQL:Stmt*` where real reports `SP:Stmt*` under the procedure's `PQ` object, as the Extended Events do.
- The two messages a login sends report `GroupID` 2 where real's report 1, and the packet size is the Extended Events' 4096 rather than the client's.
- The deadlock graph keeps the `xml_deadlock_report` document's execution stacks and resources in the older shape; real's trace version also carries native `stackFrames`.

### Not modeled yet

- The default trace's other classes — the error log, the audit events, autogrowth, `DBCC` and the hash and sort warnings: the simulator fires none of them.
- Every event class the simulator fires no Extended Event for — scans, lock acquisition and release, showplans, the audit classes, recompiles — takes, lists and never fires.
- Rowset traces (`TRACE_PRODUCE_ROWSET`, `sp_trace_getdata`), which real refuses through `sp_trace_create` as well, and `SHUTDOWN_ON_ERROR`'s shutdown, which no file error here reaches.
