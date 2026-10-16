# Extended Events

The Extended Events engine's catalog, event sessions and their DDL, as SQL Server 2025 has them.
Everything here was probed 2026-10-16 against SQL Server 2025 (17.0.4065.4) on Linux unless it says otherwise.

## The engine's catalog

`sys.dm_xe_packages`, `sys.dm_xe_objects`, `sys.dm_xe_object_columns` and `sys.dm_xe_map_values` list real's own rows, byte for byte and in real's order: 18 packages, 3,864 objects, 32,264 object columns and 10,275 map values.
The rows live as tab-separated text under `src/SqlServerSimulator/ExtendedEvents/Metadata/`, one file per view, with each package and object referenced by its line number so a GUID is written once; the build zips the folder into the assembly (`ZipXeMetadata` in the project file), since the text is some 3 MB and the zip a sixth of that.
`XeMetadata` reads it once per process on first use.
A refreshed dump replaces the files wholesale: the format is in `XeMetadata`'s remarks.

`module_address` is real's load address of each module as the dump saw it, a stand-in like the other addresses the SQLOS views report.
All four views take the server-scope gate (Msg 300 then 297).

## Sessions

`ExtendedEventsServer` (one per `Simulation`, made on first use by `Simulation.ExtendedEventsFor`) holds the definitions and the running sessions.
A definition (`EventSessionDefinition`) is immutable, so `ALTER EVENT SESSION` builds a replacement and a statement refused partway leaves the session as it was, as real's does.

**Default sessions.**
`system_health` (65536, started) and `AlwaysOn_health` (65537, stopped) are real's own definitions, dumped from its catalog into `ExtendedEvents/DefaultSessions.tsv` under their own ids; their predicates are real's stored text, read back through the simulator's predicate parser — which renders each one unchanged, the round trip a regression test pins.
They parse once per process, the definitions being immutable.
`sys.dm_xe_sessions` also lists the engine's two internal sessions, `hkenginexesession` and the `sp_server_diagnostics session`, with their events, targets and attribute values (`ExtendedEvents/InternalSessions.tsv`); they have no definition and collect nothing.
A user session takes 65538 and on, the ids after the defaults'.

**Numbering.**
Session ids count from 65536 server-wide, are never reused, and one is spent by every `CREATE` that gets past its permission check — a duplicate name and a refused definition included.
Within a session one counter numbers every object, never going back: each event takes the next id and then each of its actions and set attributes one more, each target the next and its set attributes after it.
So after `ADD EVENT a (ACTION (x, y))` the next event is 4, and an event added after a drop takes a fresh id.

**Names.**
Events, actions and targets resolve case-insensitively, with or without their package and with a module GUID as a third part; an unqualified name two packages hold is Msg 25658, and an object real marks `private` (`xevents_heartbeat`, the `SecAudit` events, `router`) resolves to nothing (Msg 25623).
The catalog keeps the event's and target's name as the statement spelled it (`ERROR_REPORTED`), the action's as the engine does, and `ALTER … DROP EVENT` matches the stored spelling exactly — `DROP EVENT RPC_COMPLETED` misses an event added as `rpc_completed`.

**Grammar.**
`CREATE` takes a comma-separated `ADD EVENT` list, then optionally a comma-separated `ADD TARGET` list, then `WITH (…)`; `ALTER` takes one list of event changes or one of target changes — a list mixing `ADD` and `DROP` is Msg 156 at the second verb — then `WITH`, or `STATE = START | STOP` alone.
An event's parenthesized body is `SET`, `ACTION`, `WHERE`, each at most once and in that order.
A refusal real makes as the batch compiles stops the whole batch: a `#` session name (Msg 25701), a value the option grammar can't read (Msg 25703 states 2, 4, 7 — an unknown mode, a latency or size without its unit), an unknown option or a bad `ON` / `OFF` (Msg 102 state 30), an unknown unit (state 31), and every Msg 102 / 156.
Everything else is raised as the statement runs, in real's order: the session-level checks (transaction, `ON DATABASE`, permission, existence), the options one at a time as written (a repeat Msg 25702, a size or latency past its limit Msg 25708, a zero duration Msg 25703 state 6, any option but `STARTUP_STATE` on a running session Msg 25707), the memory checks (Msg 25632 under 195,993 bytes, Msg 25640 for an event size smaller than one buffer), then each event — its actions, attributes, predicate — and each target, then the definition-wide Msg 25635 and 25643.

**Predicates.**
`XePredicateSyntax` parses the grammar; `Bind` resolves it against the event as the statement runs.
The stored `predicate` text is real's rewrite: every name bracketed as written, numbers parenthesized (`(16)`, a numeric `(18446744073709551615.)`, a float `(1.5000000000000000e+000)`), strings and bytes as written, `!=` / `!<` / `!>` respelled, `like` in lower case, and parentheses only where precedence needs them — a right-nested `AND` / `OR` and an `OR` under an `AND` keep theirs, a `NOT`'s operand loses them unless it is a group.
`predicate_xml` nests left-deep, a leaf naming its comparator, its source (`<event … field=…>` or `<global …>`) and the converted constant.
An operator picks its comparator from the source's type: `*_int64` for a signed field, `*_uint64` for an unsigned one or a map (compared on its key, a string naming the key by its map value, case-insensitively), the case-insensitive `*_i_sql_*_string` pair in `sqlserver` for a string, and only `=` / `<>` for a boolean, GUID or bytes.
The Msg 25713 state says how a constant failed to convert — 23 for a string where a number belongs or a map value the map lacks, 13 for a number where a string belongs, 26 for a string where bytes belong, 15 for a fraction, 14 for a flag other than 0 or 1, 4 for a signed overflow, 9 (8 in a `SET`) for an unsigned one; a float rounds half away from zero and bytes read big-endian.

**Attributes.**
`sys.server_event_session_fields.value` keeps each `SET` constant with its literal's type — `0x10` stays `varbinary`, `'a.xel'` `varchar` — while `sys.dm_xe_session_object_columns` shows every customizable column of a running session's events and targets with its converted value or its default.
The target checks real makes are modeled: `event_file` needs a non-empty `filename`, `pair_matching` an `end_event` (Msg 25642), a `histogram` source must name an action, or an event field with `filtering_event_name` (Msg 25641).

**Running sessions.**
`STATE = START` makes a `RunningEventSession` with each target's state; an `ALTER` of a running session adds and drops events and targets live, a kept target keeping what it holds, and `DROP` stops it first.
Stopping a stopped session is Msg 25704 even for a session without permission, the state being checked ahead of the permission.
`sys.dm_xe_sessions` sizes the buffers as real does: three regular ones — two and a half per CPU under `PER_CPU` — each `MAX_MEMORY / count` rounded up to whole 64 KB pages less a 205-byte header, and two large ones of `MAX_EVENT_SIZE` when it is set; `buffer_policy` follows the retention mode and `flags` is 2049 (`flush_on_close`), 2057 with causality tracking.
An `event_file` target writes `name_0_<FILETIME>.xel` under real's Linux log directory, `/var/opt/mssql/log/`, for a relative name.

**Transactions and DDL triggers.**
Each statement inside a user transaction is Msg 574, naming it.
Each raises `CREATE_EVENT_SESSION` / `ALTER_EVENT_SESSION` / `DROP_EVENT_SESSION` (the `DDL_EVENT_SESSION_EVENTS` group) to a server trigger, whose `EVENTDATA()` carries the session as `ObjectName` with `ObjectType` `EVENT SESSION` and no database or user.

### Permissions

`CREATE` needs `CREATE ANY EVENT SESSION` (Msg 15247), `DROP` needs `DROP ANY EVENT SESSION` (Msg 15151 state 27; a missing session is state 26), and each `ALTER` clause its own granular permission from SQL Server 2022's split — `ENABLE` to start (Msg 15151 state 20), `DISABLE` to stop (19), `OPTION` for `WITH` (21), `ADD EVENT` (22), `DROP EVENT` (23), `ADD TARGET` (24), `DROP TARGET` (25); a missing session is state 28.
`ALTER ANY EVENT SESSION` covers them all, and `CONTROL SERVER` covers it.
None of them opens the views: `sys.server_event_sessions` and three of its companions list nothing without `VIEW SERVER PERFORMANCE STATE`, `sys.server_event_session_events` refuses with Msg 262 then 297 as a database-state DMV does, and the five `sys.dm_xe_session*` views with Msg 297 state 13 alone.

## Events

`XeEvents` and its siblings fire the events below, each with every data field real gives it in column order and the actions after them in the reverse of their written order, as real writes them (probed 2026-10-16 against SQL Server 2025):

- `error_reported` for every error a statement raises — from `StatementLifecycle.RouteError`, once per error however many frames it unwinds through, `is_intercepted` when a `TRY` frame anywhere on the session will catch it — and for every numbered message as it is queued (`PendingMessageQueue`); `PRINT`'s unnumbered message fires none, and a message sent as class 0 reports the severity 10 it was raised at.
  Category and destination are the `SERVER` / `USER` pair every probed statement error reports.
- `sql_batch_starting` and `sql_batch_completed` around a command sent as a batch, `rpc_starting` and `rpc_completed` around a parameterized command (SqlClient's `sp_executesql`) or a procedure call, with the statement real reconstructs (`exec sp_executesql N'…',N'@p int',@p=1`, `exec dbo.p @a=7`).
  `result` is `Error` only for a request an error ended — a statement error the batch went on past is `OK`, as on real.
- `sql_statement_starting` / `_completed` around each statement of a batch, and `sp_statement_starting` / `_completed` around each of a module body's — procedure, trigger, function — with `source_database_id`, `object_id`, `object_type`, `nest_level` and an empty `object_name` (`XeStatementEvents`, from `StatementLifecycle.Enter` and `Leave`, the plan cache's replays included).
  The offsets are bytes into the batch or the module's whole definition, the line the definition's; a query — `SELECT`, a DML statement, `EXEC`, `WITH`, and a `CREATE`, `ALTER` or `DROP` (as real's trace reports the same statements, probed 2026-10-16) — ends at its own last character, anything else at the character before the next statement, its terminator and white space included (`set nocount on` takes the line break after it, `set @i += 1;` its `; `), or -1 when it runs to the batch's end.
  `row_count` is the statement's own, an `EXEC` of a procedure the sum of its body's; `last_row_count` stays at the last count a query, `SET @v` or initializing `DECLARE` set, which a session option or `PRINT` leaves standing.
  A block, `TRY` / `CATCH`, a label and a `DECLARE` initializing nothing fire none.
- `module_start` / `module_end` around a procedure's body, with the calling statement's line and offsets, the two-character type code (`P `), the name, an empty statement, and at the end its duration and the rows its statements counted.
- `login` as a session opens — in-process, over TDS and on a pooled reset (`is_cached`) — after the `error_reported` real fires for the login's Msg 5701 (state 2) and 5703; `options_text`, when collected, lists the session's settings in real's words.
  `logout` as it ends, its duration since the login.
- `existing_connection` into the session starting, alone, for every other open session, by that session's own fields (`client_app_name`, `transaction_count`, `started_event_session_name`…) — a predicate on its actions reads the starter.
- `attention` as a command timeout or a cancel ends a request (`SimulatedDbCommand.CancellationException`).
- `wait_info` (its `End`) and `wait_completed` as a lock wait or a `WAITFOR` ends, typed `LCK_M_*` / `WAITFOR` with the duration in milliseconds, granted (`wait_result` 0) or timed out (258); `system_health` keeps a lock wait past thirty seconds.
- `blocked_process_report`: a lock wait past each multiple of `sp_configure 'blocked process threshold (s)'` reports itself (`BlockedProcessReport`, from the wait's own slices while a session takes the event) — the blocked process as a deadlock graph gives one, the blocking one with its status (`suspended` while it waits or sleeps in `WAITFOR`, `running`, else `sleeping`), wait, real `trancount` and session attributes, each stack as bare frames (line, offsets, handle).
- `user_event` from `sp_trace_generateevent`, its `event_id`, `user_info` and `user_data` the procedure's arguments.

Each event the simulator fires is an `XeEventKind`, and the server publishes which of them a running session takes (`ExtendedEventsServer.Taken`) with its subscriptions, so a firing site nothing subscribes to costs one array read — before any statement made the server's Extended Events state, whether a session that starts with the server lists the event (`XeEventKinds.TakenAtStartup`, read off the resource without loading the catalog).
An `error_reported` is first judged against `system_health`'s own predicate, so the state is made only for an error it would keep; the login's two messages and a wait of fifteen seconds or less, which its predicates can't take, skip even that.
A hot workload of 44,000 small statements (client batches and a `WHILE` loop) ran in the same time before and after the statement events with nothing subscribed and with `system_health` alone running, and about 1% slower with a session taking `sql_statement_completed` behind a predicate refusing every statement (measured 2026-10-16, Release build, median of six runs).
A running SQL Trace taking a class an event maps onto is published beside them (`Simulation.TraceTaken`, null while no trace runs), so `XeEvents.Takes` answers for both and `XeEvents.Fire` delivers to the traces first ([`trace.md`](trace.md#events)).
The actions read the session (`session_id`, `client_app_name`, `client_hostname`, `client_pid`, `database_id` / `database_name`, the principal names, `transaction_id`, `sql_text` — the batch's text, none for an RPC as on real) and the session's own counters (`event_sequence`, `package0.counter`); one the simulator keeps no state for reads as its type's zero.
A predicate evaluates over the same values, its string comparators case-insensitive and its `like` the simulator's `LIKE` matcher.

## Deadlock reports

A deadlock the lock manager finds (see [`locking.md`](locking.md#cycle-detection)) is captured under its gate while every session in the cycle waits — when something reports deadlocks: trace flag 1204 or 1222 on server-wide, or a running session taking `xml_deadlock_report`, `lock_deadlock` or `lock_deadlock_chain`, which `system_health` always does (`Simulation.ReportsDeadlocks`).
The victim reports it (`DeadlockReport`) as its wait ends, before its Msg 1205 rolls anything back, so every lock of the cycle is still held and the lock views read it as it was found.
Probed 2026-10-16 against SQL Server 2025 over key, RID, page and object deadlocks of two and three sessions, inside procedures and out:

- **The order.** `lock_deadlock_chain` once per process, then `lock_deadlock` for the victim, then `xml_deadlock_report`, all at one timestamp.
  The processes run from the victim through the session waiting on it, and on against the wait-for order, so the last process holds what the victim waits for; resource *k* is the one process *k* waits on, owned by the process before it.
- **The graph.** `<deadlock>` holds `victim-list`, `process-list` and `resource-list` in real's layout to the white space — one-space indentation, `<stackFrames>` present and empty, a frame's text on its own line before four spaces and `</frame>`, the input buffer before three, apostrophes escaped as `&apos;`.
  A process carries real's thirty-odd attributes in real's order: the wait resource as `sys.dm_exec_requests` spells it, the wait time, the transaction (`user_transaction`, its `BEGIN TRAN` name, or the statement's verb for an autocommit one) and when it began, the requested mode, `priority` the session's `DEADLOCK_PRIORITY` and `taskpriority` its negation, `trancount` the transaction count with one more for a write statement's own (an autocommit write reports 2, an autocommit read 0), the batch times, the client, the login, `isolationlevel` as `read committed (2)`, the database and `lockTimeout` as an unsigned 32-bit value.
- **The execution stack** comes from the session's running batch (`SimulatedDbConnection.RunningBatch`, each module body's `BatchContext.CallerBatch` the batch that called it), innermost first.
  A module's frame names it `db.schema.name` with its statement's line in the `CREATE`, offsets into the definition and the module's handle; the batch that called it gives its own statement under its legacy `0x01` handle; the batch statement running innermost writes `unknown` under its `0x02` handle, behind a frame of its own for the prepared text a simple parameterization runs it as.
  A frame's text stops one character short of its statement, real's own slip, and a start offset of 0 or an end of -1 is left out.
- **The resources** are `keylock` (`hobtid`, `indexname`), `ridlock` and `pagelock` (`fileid`, `pageid`), `objectlock` (`lockPartition`, `objid`, `subresource`) and `applicationlock`, each with `dbid`, `objectname` as `db.schema.table`, a stand-in `id` and the holder's `mode`, read off the holder's own row of `sys.dm_tran_locks` (`LockDmvs.EnumerateDmTranLocks`).
- **`lock_deadlock_chain` / `lock_deadlock`** carry the waited resource as the lock manager keys it — a key's hobt in `resource_0` and its hash's bytes little-endian in `resource_1`'s high half and `resource_2`, a row's page and file, a page's number and file, an object's id in `resource_0` and `object_id` — the mode the process asked for, its transaction and the server's deadlock count as `deadlock_id`; `lock_deadlock` adds the victim's wait as `duration`.
- **Trace flags.** With 1204 on server-wide, real writes its wait-for graph under `spid44s` — `Deadlock encountered .... Printing deadlock information`, then per resource a `Node:n` with its wait resource, the holder's grant (mode padded to nine), statement type, line and input buffer, and the request waiting on it — and the victim's owner line; with 1222, the graph as indented `name=value` lines under `spid3s`.
  A session-scope `DBCC TRACEON (1222)` writes nothing.
- The lock monitor fires `lock_deadlock_chain` and `xml_deadlock_report`, so their `session_id` action reads its session, 44 on the probed instance; `lock_deadlock` fires on the victim's.

### Divergences

- `blocked_process_report` fires from the waiter, as soon as its wait passes each multiple of the threshold; real's lock monitor looks every few seconds, so its reports trail the threshold (a five-second threshold's wait reported at about 6 and 11 seconds).
  `object_id` / `index_id` are the lock manager's words for the resource (a key's hobt in two parts), stand-ins where the hobt isn't real-shaped, and the blocking process of a sleeping session is unprobed: it reports `sleeping` with no frames.
- The victim is the simulator's — the lowest `DEADLOCK_PRIORITY`, the requester on a tie — where real weighs the log each transaction would roll back; the graph follows whichever it chose.
- Addresses (`process…`, `XDES`, `lock…`, the 1204 owners and proxies), `logused`, `schedulerid`, `kpid`, the `clientoption` words (sqlcmd's) and the `sqlhandle` bytes stand in for real's, and `<stackFrames>` holds none of real's native frames.
- A deadlock between two requests of one MARS session (Msg 1205 on its own thread) reports nothing.

## Targets

- `ring_buffer` keeps the newest `max_events_limit` events (1,000 by default), its `target_data` the `RingBufferTarget` document real's has.
- `event_counter` counts each event under its package, every package listed in `sys.dm_xe_packages`' order.
- `histogram` buckets by an action (`source_type = 1`) or by a field of `filtering_event_name` (`0`), slots in descending count.
- `pair_matching` keeps each `begin_event` as an orphan until an `end_event` pairs with it.
- `event_file` writes `name_0_<FILETIME>.xel` under `/var/opt/mssql/log/` for a relative name, one file per run of the session.
  The simulator keeps its own form of the file in memory, on the simulation's Extended Events state, so it outlives the session and its drop, and `sys.fn_xe_file_target_read_file(path, mdpath, initial_file_name, initial_offset)` reads it back: every file the path matches (`*` any run of characters, a relative path under the log directory), each event in the file's own shorter form — no `<type>` elements — with the buffer offset real reports.
  A NULL path is Msg 25718 naming `(null)`, an initial file without its offset (or the reverse) Msg 25719, and a path matching nothing reads nothing.

### Not modeled yet

- Every other event in the catalog: a session takes it, and it never fires.
  Among the commonly captured ones: `lock_acquired` / `lock_released` / `lock_timeout` / `lock_escalation`, `object_created` / `_altered` / `_deleted` (real fires each twice, its `Begin` and `Commit` phase) and `database_created` / `_dropped`, `auto_stats`, `query_post_execution_showplan`, and `wait_info_external`.
- An `IF` or `WHILE` condition's own statement events (real reports `if 1=1` as a statement, a loop's condition once per evaluation), `module_start` / `module_end` for a trigger or function, `wait_info`'s `Begin`, `wait_completed`'s `wait_resource` text (empty here), and `attention` for a TDS attention, which goes no way through the command.
- The live event stream (`event_stream`, `sys.fn_MSxe_read_event_stream`), and a file target on a host disk: the event file never leaves the simulation.
- `cpu_time` and the read and write counts read zero, as do the batch events' `row_count`; `duration` is the measured time.

### Divergences

- The running-session views list each session's events and actions newest first and its targets asynchronous first, then file targets, then synchronous ones, which matches every probed session's targets and the user sessions' events; real lists `system_health`'s events in an internal hash order the simulator doesn't reproduce.
- Addresses are stand-ins numbered from the session's start, `create_time` the simulation's start for the sessions that start with it, and the counters real's engine keeps (`buffer_processed_count`, `total_bytes_generated`, `event_fire_count` and the like) read zero.
- `event_counter` lists a package's events in the order the session added them, where real follows an internal hash; `pair_matching` pairs each end with the oldest waiting begin and drops an end no begin waits for, where real also keeps such an end as an orphan and honors the matching columns; and an event file's offsets step by a default session's buffer size whatever the session's own.
