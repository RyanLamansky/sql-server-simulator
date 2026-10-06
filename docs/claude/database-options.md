# `ALTER DATABASE` SET-option surface

Closed accept-list parser (`RecognizedDatabaseOptions` in `Simulation.Alter.cs`) covering every database-scope toggle SqlPackage emits from a bacpac's `SqlDatabaseOptions` element.
Most options are recorded without behavior — see [Recorded switches](#recorded-switches) — and only the nine "load-bearing" toggles (`COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `TRUSTWORTHY`, `DB_CHAINING`, `READ_ONLY` / `READ_WRITE`, `CHANGE_TRACKING`, `MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT`) drive actual behavior.
`RECOVERY` is tracked without driving anything — the simulator has no transaction log, but `sys.databases.recovery_model` / `recovery_model_desc` report it, and a bacpac carries the source database's value, so an imported database describes itself the way the original did.
Real ships `master` / `tempdb` / `msdb` SIMPLE and `model` FULL, which every new user database inherits (probe-confirmed).

## Target database

`ALTER DATABASE <name>` lands on **that** database, not the session's — `CURRENT` names the session's (`ResolveAlterDatabaseTarget`).
So a per-database flag is settable from anywhere, which is what lets one batch stage two databases' versioning options.
A name the `Simulation` doesn't host raises **Msg 5011** sev 14 state 5 (`User does not have permission to alter database '<n>', the database does not exist, or the database is not in a state that allows access checks.`), followed by Msg 5069 (`ALTER DATABASE statement failed.`), as are the other failures an `ALTER DATABASE` meets while running (`SimulatedSqlException.FollowedByAlterDatabaseFailed`).
The rest of the statement is read in skip mode before the refusal, so its `SET …` tail isn't left behind to run as a statement of its own; a `COLLATE` over a missing name is **Msg 911** instead (probed 2026-09-24 against SQL Server 2025).
One `SET` takes a comma-separated list of options and one trailing termination clause (`WITH NO_WAIT` / `ROLLBACK …`), which every option accepts but `ALLOW_SNAPSHOT_ISOLATION` (**Msg 5083**, and nothing in the list applies); the statement is read in skip mode first, since that refusal depends on its end.
The name also governs the `COLLATE` clause below.
Besides `SET`, `COLLATE` and `MODIFY NAME`, the statement takes `ADD FILEGROUP` / `REMOVE FILEGROUP`, which maintain the catalog's filegroups ([`partitioning.md`](partitioning.md#filegroups)), and the file forms — see [Files and filegroups](#files-and-filegroups).
Only `SET` follows a refusal with Msg 5069: the `COLLATE`, `MODIFY`, `ADD` and `REMOVE` forms name a missing database with Msg 911 instead, and the filegroup forms refuse a read-only one with a plain Msg 3906 (probed 2026-09-27).

## Recognized options by value shape

**`OnOff`** (`SET <name> {ON | OFF}`):
- `ANSI_NULL_DEFAULT` / `ANSI_NULLS` / `ANSI_PADDING` / `ANSI_WARNINGS` / `ARITHABORT` / `CONCAT_NULL_YIELDS_NULL` / `NUMERIC_ROUNDABORT` / `QUOTED_IDENTIFIER` / `TORN_PAGE_DETECTION` / `TEMPORAL_HISTORY_RETENTION` / `AUTO_CLOSE` / `AUTO_SHRINK` / `AUTO_CREATE_STATISTICS` (whose `ON` takes an optional `(INCREMENTAL = ON | OFF)`) / `AUTO_UPDATE_STATISTICS` / `AUTO_UPDATE_STATISTICS_ASYNC` / `CURSOR_CLOSE_ON_COMMIT` / `DATE_CORRELATION_OPTIMIZATION`

**`EnumIdent`** (`SET <name> <bareIdent>`):
- `RECOVERY`: `FULL` / `BULK_LOGGED` / `SIMPLE`
- `PAGE_VERIFY`: `CHECKSUM` / `TORN_PAGE_DETECTION` / `NONE`
- `CURSOR_DEFAULT`: `GLOBAL` / `LOCAL`
- `PARAMETERIZATION`: `SIMPLE` / `FORCED`

**`EqualsOnOff`** (`SET <name> = {ON | OFF}` — `=` required per probe):
- `ACCELERATED_DATABASE_RECOVERY`
- `OPTIMIZED_LOCKING`

**`IntegerWithUnit`** (`SET <name> = N SECONDS|MINUTES` — unit required per probe):
- `TARGET_RECOVERY_TIME`

**`Broker`** (a bare switch, at most one per `SET` list — a second is Msg 5062 naming it, raised as the batch compiles; probed 2026-09-30 against SQL Server 2025):
- `ENABLE_BROKER` / `DISABLE_BROKER` / `NEW_BROKER` / `ERROR_BROKER_CONVERSATIONS`

**`AccessMode`** (bare state, no `=`, with an optional termination clause): `SET {SINGLE_USER | MULTI_USER | RESTRICTED_USER} [WITH ROLLBACK IMMEDIATE | WITH ROLLBACK AFTER n [SECONDS] | WITH NO_WAIT]`.
`READ_ONLY` / `READ_WRITE` take the same shape and the same termination clause but are load-bearing — see [Read-only databases](#read-only-databases).
The state is recorded for the catalog and the termination clause discarded — the simulator has no connection-count access model, so it never actually restricts, and `WITH ROLLBACK …` never evicts.
Load-bearing for `DROP DATABASE`: every ORM/app test-teardown runs `SET SINGLE_USER WITH ROLLBACK IMMEDIATE` immediately before the drop (Django/mssql-django).
Parsed explicitly (`ConsumeAccessModeTail`) rather than scanned to a boundary, because `ROLLBACK` is itself a statement-starting keyword — only `WITH`/`ROLLBACK` tokenize as keywords, `IMMEDIATE`/`AFTER`/`SECONDS`/`NO_WAIT` are matched by text.

**`QueryStore`** is not in this list — it is load-bearing, and the only ALTER DATABASE option with a sub-grammar of its own.
See [Query Store](#query-store).

## Recorded switches

The `OnOff` options, `PAGE_VERIFY` (and its legacy `TORN_PAGE_DETECTION` spelling), `CURSOR_DEFAULT`, `PARAMETERIZATION`, the access mode, `TARGET_RECOVERY_TIME` and the broker switches are recorded on the database (`Database.Switches` / `PageVerify` / `UserAccess` / `TargetRecoveryTimeSeconds` / `BrokerEnabled`) without driving anything, and reported by `sys.databases`' option columns and `DATABASEPROPERTYEX` (probed 2026-09-26 against SQL Server 2025).
Every database, system or user, starts from the same defaults: automatic statistics creation and update and temporal history retention on, page verification `CHECKSUM`, `MULTI_USER`, a target recovery time of 60 seconds, Service Broker enabled, everything else off — save what the system databases ship with (`master` a recovery time of 0, `master` and `model` the broker off, `master` and `msdb` snapshot isolation allowed; probed 2026-09-30).
`TORN_PAGE_DETECTION OFF` clears torn-page detection alone: a `CHECKSUM` database stays `CHECKSUM` (probed 2026-09-30).
Turning `AUTO_CREATE_STATISTICS` off takes its incremental mode with it.

## Load-bearing options (behavior wired)

These dispatch to dedicated helpers rather than falling into the parse-and-discard accept-list:

- **`COMPATIBILITY_LEVEL`** — stored on `Database.CompatibilityLevel`.
- **`ALLOW_SNAPSHOT_ISOLATION`** — toggles `Database.AllowSnapshotIsolation`; required for `SET TRANSACTION ISOLATION LEVEL SNAPSHOT`.
  See [`locking.md`](locking.md).
- **`READ_COMMITTED_SNAPSHOT`** — toggles `Database.ReadCommittedSnapshot`; switches RCSI behavior for the default READ COMMITTED isolation level.
  See [`locking.md`](locking.md).
- **`RECURSIVE_TRIGGERS`** — toggles `Database.RecursiveTriggers`; lets an AFTER trigger's own DML re-fire that trigger, and surfaces as `sys.databases.is_recursive_triggers_on`.
  See [`triggers.md`](triggers.md#nesting-and-recursion-options).
- **`TRUSTWORTHY`** — toggles `Database.Trustworthy`; lets a database-scoped identity established here (an `EXECUTE AS USER` frame, a module's `WITH EXECUTE AS <user>` frame, an activated application role) reach another database, where its own login then answers.
  Surfaces as `sys.databases.is_trustworthy_on`.
  See [`permissions.md`](permissions.md#cross-database-references).
- **`DB_CHAINING`** — toggles `Database.CrossDatabaseChaining`; an ownership chain crosses the database boundary only when *both* databases have it on.
  Surfaces as `sys.databases.is_db_chaining_on`.
  See [`permissions.md`](permissions.md#cross-database-references).
- **`READ_ONLY` / `READ_WRITE`** — toggles `Database.IsReadOnly`, which refuses every write to that database.
  See [Read-only databases](#read-only-databases).
- **`CHANGE_TRACKING`** — `= ON [( … )]` / `= OFF` / `( … )` sets `Database.ChangeTracking`, which `ALTER TABLE … ENABLE CHANGE_TRACKING` requires; it must stand alone in its `SET` list.
  See [`change-tracking.md`](change-tracking.md).
- **`MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT`** — recorded on `Database.Switches` like the others, and read where a READ COMMITTED transaction reaches a memory-optimized table, which it lets read at snapshot rather than refusing (Msg 41368).
  See [`memory-optimized.md`](memory-optimized.md#isolation).

Both cross-database toggles take the bare `ON` / `OFF` shape (`SET TRUSTWORTHY = ON` is Msg 102, probe-confirmed), and each refuses a set of system databases whatever the value asked for:

| Statement | Refused on | Error |
|---|---|---|
| `SET TRUSTWORTHY` | `model` / `tempdb` | **Msg 15309** class 16 state 1 — `Cannot alter the trustworthy state of the model or tempdb databases.` |
| `SET DB_CHAINING` | `master` / `model` / `tempdb` | **Msg 5600** class 16 state 2 — `The Cross Database Chaining option cannot be set to the specified value on the specified database.` |

`msdb` is the one system database real lets either flag move on.
The **shipped defaults** match real (probe-confirmed): `master` / `tempdb` chained, `msdb` chained *and* trustworthy, `model` and every user database neither.
Neither flag is inherited from `model` — a new database starts with both off, which real enforces structurally by refusing to set them on `model` at all.

## Query Store

`SET QUERY_STORE = ON [( … )] | = OFF | ( … ) | CLEAR [ALL]`, parsed by `ParseQueryStoreTail` into `Database.QueryStore` (a `QueryStoreOptions`).
A store in READ_WRITE records statements into `Database.QueryStoreData` as they complete (`Simulation/Simulation.QueryStore.cs`), the `sp_query_store_*` procedures act on what it holds (`Simulation.QueryStoreProcedures.cs`), and the `sys.query_store_*` views project it — see [`catalog-views.md`](catalog-views.md#query-store).

**A fresh database's store is on in READ_WRITE**, the state SQL Server 2025 inherits from `model`, with capture mode AUTO, size-based cleanup AUTO, wait-stats capture ON, and the bigint knobs at 900 / 60 / 1000 / 30 / 200.
`master` and `tempdb` are seeded OFF and refuse the option outright; `msdb` is seeded OFF and accepts it.

### Configuration

Sub-options, all probe-confirmed 2026-08-08:

| Sub-option | Values | Catalog column |
|---|---|---|
| `OPERATION_MODE` | `READ_WRITE` / `READ_ONLY` | `desired_state` (+ `actual_state`) |
| `CLEANUP_POLICY = ( STALE_QUERY_THRESHOLD_DAYS = N )` | integer | `stale_query_threshold_days` |
| `DATA_FLUSH_INTERVAL_SECONDS` | integer, at least 60 | `flush_interval_seconds` |
| `MAX_STORAGE_SIZE_MB` | integer | `max_storage_size_mb` |
| `INTERVAL_LENGTH_MINUTES` | 1, 5, 10, 15, 30, 60 or 1440 | `interval_length_minutes` |
| `SIZE_BASED_CLEANUP_MODE` | `AUTO` / `OFF` | `size_based_cleanup_mode` |
| `QUERY_CAPTURE_MODE` | `ALL` / `AUTO` / `NONE` / `CUSTOM` | `query_capture_mode` |
| `MAX_PLANS_PER_QUERY` | integer | `max_plans_per_query` |
| `WAIT_STATS_CAPTURE_MODE` | `ON` / `OFF` | `wait_stats_capture_mode` |
| `QUERY_CAPTURE_POLICY = ( … )` | `STALE_CAPTURE_POLICY_THRESHOLD = N {DAYS\|HOURS}` (an hour to seven days), `EXECUTION_COUNT`, `TOTAL_COMPILE_CPU_TIME_MS`, `TOTAL_EXECUTION_CPU_TIME_MS` (each at least 1) | the four `capture_policy_*` |

Behaviors worth knowing before touching this:

- **Every value survives `= OFF`.** Real reports the last-configured sub-options on a disabled store and restores them on re-enable, so the state is one more retained field rather than a reset.
- **An `= ON` carrying only unrelated sub-options still enables**, at READ_WRITE — the sub-option block never means "configure without turning on".
  Which is why the bacpac loader can't emit one statement per property; see [Bacpac loader context](#bacpac-loader-context).
- **A bare block, `SET QUERY_STORE ( … )`, configures without turning on**: an OFF store stays OFF, while its `OPERATION_MODE` moves an enabled one (probed 2026-09-29).
- **The four `capture_policy_*` columns are masked, not cleared, by the capture mode**: projected NULL unless the mode is CUSTOM, and the values behind them survive a trip through another mode.
  A first switch to CUSTOM with no policy block reports real's 30 / 1000 / 100 / 24.
- **`STALE_CAPTURE_POLICY_THRESHOLD`'s unit is mandatory** and normalizes to the hours the column reports (`DAY`/`DAYS` ×24, `HOUR`/`HOURS` ×1); a bare integer and an unrecognized unit are both Msg 102 *at the entry name*.
- **`CLEAR` / `CLEAR ALL`** forget everything captured — the id counters restart at 1 too — and touch neither the state nor the configuration; an OFF store clears as well.
- **The whole tail parses into a copy and swaps in at the end**, so a block that raises partway through leaves the configuration standing.
- **A value check fails the batch's compile**, so none of the batch runs — the statements ahead of the `ALTER` included (probed 2026-09-29).
  Real's checks are patchy, and the accepted values are as load-bearing as the refused ones: `MAX_STORAGE_SIZE_MB = 0`, `STALE_QUERY_THRESHOLD_DAYS = 0` and `MAX_PLANS_PER_QUERY = 0` are stored.

Rejections, all real's own:

| Statement | Error |
|---|---|
| any QUERY_STORE form on `master` / `tempdb` — `= OFF` and `CLEAR` included, all worded as being about enabling | **Msg 12438** class 16 state 1 — `Cannot perform action because Query Store cannot be enabled on system database <name>.` followed by Msg 5069 |
| `DATA_FLUSH_INTERVAL_SECONDS` below 60 | **Msg 153** class 15 state 5, naming `flush_interval_seconds` |
| `INTERVAL_LENGTH_MINUTES` outside its seven values | **Msg 153** class 16 state 6, naming `interval_length_minutes` |
| `EXECUTION_COUNT` / `TOTAL_COMPILE_CPU_TIME_MS` / `TOTAL_EXECUTION_CPU_TIME_MS` of 0 | **Msg 12452** class 15, state 2 for the last and 1 for the others |
| `STALE_CAPTURE_POLICY_THRESHOLD` outside an hour to seven days | **Msg 12453** class 16 state 1 |
| a sub-option written twice in one block | **Msg 12401** class 15 state 2, ahead of the second value's own check |
| two `QUERY_STORE` clauses in one `SET` list | **Msg 12417** class 16 state 1 |
| a value past `int` range, or signed | **Msg 102** at the number or the sign |
| an unrecognized sub-option name, at either nesting level | **Msg 102** at the name |
| `OPERATION_MODE = OFF`, `SIZE_BASED_CLEANUP_MODE = ON` | **Msg 156** — the value is a reserved keyword where the grammar wants an identifier |
| `WAIT_STATS_CAPTURE_MODE = AUTO`, `OPERATION_MODE = BOGUS` | **Msg 102** |
| a sub-option block after `= OFF`; `STALE_QUERY_THRESHOLD_DAYS` outside its `CLEANUP_POLICY` wrapper | **Msg 102** |

### Capture

Probed 2026-09-29 against SQL Server 2025, with `sp_query_store_flush_db` and a few seconds' wait before each read, since real registers a new query asynchronously.

- **What a store records.**
  A query reading a table (or calling a user function, the one FROM-less query real keeps), every `INSERT` / `UPDATE` / `DELETE` / `MERGE` (a table variable's included), `SELECT … INTO`, a `DECLARE CURSOR`, and a `SET`, `DECLARE`, `RETURN` or `IF` / `WHILE` condition whose expression reads a table — the condition as `if <condition>`, each time it is evaluated.
  A procedure's, trigger's and dynamic batch's statements record for themselves, and a replayed plan-cache hit records like a parsed statement.
  So do the statements of a function body the optimizer doesn't inline — every multi-statement table-valued function's, and a scalar function's below compatibility level 150 or when `ModuleInlining` says it isn't inlineable — under the function's `object_id` (`BatchContext.CapturesQueryStore`); an inlined body is part of its caller's statement (probed 2026-09-30).
  Nothing else does: `SELECT 1`, `SELECT @x + 1`, a bare `SET` or condition, `EXEC`, DDL, and a statement whose compile failed (a syntax error, a missing object or column).
- **Capture modes.**
  ALL records a query's first execution; AUTO its 30th within a day, or the one that takes its CPU past 100 ms (a query run 31 times reports 2 executions, one run 42 times 13); CUSTOM the same by its own policy; NONE captures nothing new but keeps counting the queries it holds.
  The uncaptured tally is bounded at 4,096 queries, past which it starts over.
- **States.**
  READ_ONLY and OFF record nothing and keep what is held, which the views still read.
- **The stored text** is the statement as written, from its first token to its last — no separator or trailing comment, except that a `MERGE` keeps its terminating `;`.
  A statement real simply parameterizes is stored in real's parameterized form, `(@1 tinyint)SELECT * FROM [t] WHERE [a]=@1`, whose rendering rules sit on `Parser/SimpleParameterization.cs`; `<>`, `!=`, `NOT`, `TOP` and a catalog view keep a statement as written.
  A statement reading variables or parameters is prefixed with their declarations, spelled as declared — `(@X int,@s varchar(10))select …` — except a condition or `RETURN`, stored bare.
  They come in the order real's binder meets them (`QueryStoreShape.VariablesInBinderOrder`): a query's FROM clause and joins, WHERE, GROUP BY and HAVING, then its select list, ORDER BY, TOP or FETCH, OFFSET, and last the variables its select list assigns, each set-operation branch in turn; an `UPDATE` or `DELETE`'s FROM, WHERE and TOP ahead of its SET list; a `SET` or `DECLARE`'s expression ahead of its target; anything else, and whatever a parenthesis holds, as written (probed 2026-10-06 against SQL Server 2025).
  `query_parameterization_type` is 2 for the parameterized form, 1 for a parameterized command or `sp_executesql` whose text declares its parameters (only those it reads), 0 otherwise; a module body is never parameterized.
- **What makes a query distinct**: its stored text, context settings, containing module (`object_id`) and parameterization type, and for a statement reading a table variable its batch — such a query carries a `batch_sql_handle`.
- **Context settings.**
  `set_options` carries real's plan-attribute bits for the session's options (0xFB for a SqlClient session, 0x10FB with `ARITHABORT`), with the language, `DATEFORMAT` (1 for mdy, 2 for dmy) and `DATEFIRST`; `default_schema_id` is the schema a one-part object name resolved through, -2 when the statement names none that way, and -2 always in a module.
- **Handles.** `statement_sql_handle` is the type byte 9, a zero, and the MD5 of the stored text's UTF-16 bytes, zero-padded to 44 — real's own derivation, so the bytes match.
- **Runtime statistics** are kept per plan, interval and execution type (0 regular, 3 aborted by an attention, 4 ended by a run-time error).
  Duration and CPU are measured in microseconds, CPU less what the statement spent in `WAITFOR` or blocked on a lock; logical reads are counted as `STATISTICS IO` counts them ([`session-options.md`](session-options.md#statistics-io)); `rowcount` is the rows returned or affected.
- **Intervals** are `INTERVAL_LENGTH_MINUTES` long and aligned to that length from midnight UTC; the latest runs to its end, so a changed length takes effect at the next.
- **Lock waits** are the one wait category recorded (`Lock`, 3), per plan, interval and execution type, unless `WAIT_STATS_CAPTURE_MODE = OFF`.

**Cost.** A database whose store is OFF pays two field reads per statement.
A store in AUTO — every user database's default — times each candidate statement, counts its reads, and reads each distinct text's shape once (from the parser's own tokens when it still holds them), which measured about 3% on the index sqllogictest replay (60.9 / 59.7 / 61.3 s against 58.0 / 59.8 / 58.6 s with the change absent, measured 2026-09-29); a text's shape analysis is the bulk of it, which is why the tokens the parser collected — kept even once the token memo is full — are read back rather than the text tokenized again.

### Procedures

All eleven of real's, typed `X` in `sys.all_objects` as real's are, each reporting its errors at line 1 of itself.

| Procedure | Behavior |
|---|---|
| `sp_query_store_flush_db` | nothing to flush, the store being visible as soon as a statement completes; Msg 8144 state 51 for an argument |
| `sp_query_store_force_plan @query_id, @plan_id [, @disable_optimized_plan_forcing] [, @force_plan_scope]` | marks the plan forced (`MANUAL`) and records a forcing location; quiet when already forced |
| `sp_query_store_unforce_plan @query_id, @plan_id [, @force_plan_scope]` | clears both; quiet when not forced |
| `sp_query_store_remove_query @query_id` | removes the query, its plans and statistics, its hints, and its text once unshared |
| `sp_query_store_remove_plan @plan_id` | removes the plan and its statistics, leaving the query |
| `sp_query_store_reset_exec_stats @plan_id` | removes the plan's runtime and wait statistics |
| `sp_query_store_set_hints @query_id, @query_hints [, …]` | replaces the query's hints under a new `query_hint_id`, checking the clause in real's order: an argument not opening `OPTION (`, or a hint no query hint begins with, is Msg 102 at the word; `OPTIMIZE FOR` a variable list is Msg 12455 state 2, then `USE PLAN` and `TABLE HINT` Msg 12455 state 1 naming them; then the clause's own refusals as a statement's `OPTION` clause meets them (Msg 1042, 310, 10715) |
| `sp_query_store_clear_hints @query_id [, …]` | removes the query's hints, quietly when there are none |
| `sp_query_store_consistency_check` | Msg 12427 state 3 while the store is on; quiet when OFF |
| `sp_query_store_clear_message_queues` | nothing to clear; Msg 8144 state 51 for an argument |
| `sp_query_store_remove_plan_feedback @feature_id [, @plan_id]` | Msg 12469 for a plan (checked first), Msg 12467 for a feature outside 1 to 4, Msg 12468 for 1 to 3, success for 4; Msg 201 state 62 without the feature, Msg 214 state 56 for NULL, Msg 8144 state 120 for a third argument |

A query id the store doesn't hold is Msg 12402 class 11 — state 1 from `remove_query`, 2 from `force_plan` / `unforce_plan`, 5 from `set_hints`, 6 from `clear_hints` — and a plan id Msg 12403 class 11 (state 1 from `remove_plan`, 2 from `reset_exec_stats`); a plan that isn't the query's is Msg 12406.
While the store is OFF, forcing and unforcing are Msg 12405 state 4, `set_hints` state 6 and `clear_hints` state 7, ahead of the id checks; the remove and reset procedures work on an OFF store.
A missing id is Msg 313 and a NULL one Msg 214, both state 51.

A query's hints apply the next time a statement that is the query compiles — its identity read off its text as its capture reads it (`Simulation.QueryStoreHintFor`) — so setting or clearing them retires every cached plan (probed 2026-10-06 against SQL Server 2025).
The hint's `MAXRECURSION` replaces the statement's own (a 50-deep recursive CTE under `OPTION (MAXRECURSION 10)` is Msg 530), its join hints and `FORCE ORDER` steer the plan as an `OPTION` clause's do, without a Msg 8625, and its `RECOMPILE` compiles the statement every run.
Join hints that leave no plan don't fail the query: it compiles without the hint, and `sys.query_store_query_hints` records `last_query_hint_failure_reason` 8622, `NO_PLAN`, and counts the failure.
Forced plans are recorded, not applied: the simulator has one plan per query and no optimizer one could steer.

### Divergences

- **Registration is immediate.** Real's views show a new query seconds after its execution, and in probes irregularly missed the first statements against a table created moments before; the simulator records every eligible execution as it completes.
- **Hashes and batch handles are the simulator's own.** `query_hash` and `query_plan_hash` share real's property that texts differing only in literals, case or spacing hash alike, but not its bytes; `last_compile_batch_sql_handle` and `batch_sql_handle` are the simulator's `sql_handle` shape ([`catalog-views.md`](catalog-views.md)).
- **Compile figures are 0** and `count_compiles` 1: the simulator compiles a statement as it runs it.
- **Figures the simulator doesn't have** read constant: DOP 1, no memory grant, no physical, CLR, log or tempdb use, no logical writes.
- **A module statement's context settings** are the session's, where real gave some functions and procedures a context-settings row of their own; its offsets are into the module's `CREATE` text, as real's (`BatchContext.ModuleBodyOffset`).
- **`sp_query_store_reset_exec_stats`' Msg 12403** names the plan and database asked about, where real's prints uninitialized numbers.
- **A `DECLARE CURSOR`** records no rows, where real counts the rows its cursor fetched.

### Not modeled yet

- Size-based and stale-query cleanup, and `MAX_PLANS_PER_QUERY` (real enforced a `MAX_STORAGE_SIZE_MB` of 0 neither immediately nor by turning read-only in probes) — not chased: cleanup follows real's store size and clock, and the simulator keeps one plan per query for the cap to bite on.
- The AUTO / CUSTOM compile-CPU threshold, forced parameterization, plan feedback and query variants, the internal statistics queries real records, and an operator tree in `query_plan`.
- A forced plan steers real's optimizer, which the simulator doesn't have — not chased.
  So do a hint's `MAXDOP`, `FAST`, grant percentages and the rest that have no effect the simulator models.

## Read-only databases

`ALTER DATABASE <name> SET { READ_ONLY | READ_WRITE }` moves `Database.IsReadOnly`, projected by `sys.databases.is_read_only` and by `DATABASEPROPERTYEX(name, 'Updateability')` (`READ_ONLY` / `READ_WRITE`).
Every write to a read-only database is **Msg 3906** class 16 — `Failed to update database "<n>" because the database is read-only.` — the identical wording for DML and DDL, probe-confirmed against SQL Server 2025 (2026-08-04, the states and the catalog-writing statements re-probed 2026-08-08).
The state is **1** everywhere but `ALTER TABLE`, whose every sub-action (ADD / DROP / ALTER COLUMN, ADD / DROP CONSTRAINT, CHECK / NOCHECK, REBUILD) reports **12**, and `UPDATE STATISTICS`, which reports **13** (probed 2026-09-25).
The error names the database that *would have been written*, so a three-part write out of another session database reports the target's name, the same rule the rowversion counter and trigger dispatch follow.

**The check happens where the write happens**, which is what reproduces real's laziness.
An `UPDATE` or `DELETE` matching no row, an `INSERT … SELECT` producing none, and a `MERGE` whose actions all decline complete quietly; an `INSERT … VALUES`, a `TRUNCATE` of an already-empty table, and every DDL statement raise.
Writes to a table belonging to no database — a `#temp` table, a `##global` table, a table variable, a table-valued parameter — are unaffected however the session's own database is set, matching real's separate `tempdb`; `SELECT … INTO #t` reading a read-only table is legal, while `SELECT … INTO <permanent>` is not.

Enforced at two kinds of seam: the per-row DML writes (INSERT / UPDATE / DELETE / MERGE / bulk load, keyed on `HeapTable.OwningDatabase`), and the DDL statements' own target resolution — the module `CREATE` / `ALTER` family through `ResolveModuleSchema`, plus `CREATE TABLE`, `SELECT … INTO`, `TRUNCATE`, `ALTER TABLE`, `CREATE INDEX`, `ALTER SEQUENCE`, the `CREATE` / `DROP` pairs for sequences, types and synonyms, and every `DROP` of a table, view, procedure, function, sequence, type or trigger.

The catalog-writing statements carry it too: `GRANT` / `REVOKE` / `DENY`, `sp_rename` (object, column and index forms), `sp_addextendedproperty` and its update / drop siblings, `ALTER SCHEMA … TRANSFER`, `CREATE SCHEMA`, `CREATE` / `ALTER` / `DROP INDEX`, `CREATE STATISTICS`, `CREATE` / `DROP ASSEMBLY`, `DROP SCHEMA`, `UPDATE STATISTICS`, and the database-scoped principal DDL (`CREATE` / `DROP USER`, `CREATE` / `DROP ROLE`, `ALTER ROLE … ADD | DROP MEMBER` and its `sp_addrolemember` / `sp_droprolemember` spelling, the application-role trio).
Drawing a sequence value is a write too: `NEXT VALUE FOR` refuses when it actually draws, so a query whose `WHERE` admits no row completes (probed 2026-09-25).
Login and server-role DDL don't: those write `master`, which can never be read-only.

**Where the refusal sits relative to name resolution differs per statement**, and real's order is what each gate follows (all probe-confirmed):

| Statement | Refused before or after resolution |
|---|---|
| `GRANT` / `REVOKE` / `DENY` | before — a missing object *or* principal still reports Msg 3906 |
| `ALTER SCHEMA … TRANSFER` | before — a missing object still reports Msg 3906 |
| `CREATE USER` / `CREATE ROLE` | before — an existing name still reports Msg 3906 |
| `DROP ASSEMBLY` | before — a name no assembly holds still reports Msg 3906 |
| `DROP SCHEMA` | before everything — a missing, protected or `IF EXISTS` schema still reports Msg 3906 |
| `UPDATE STATISTICS` | after the *table* (a missing one is Msg 2706), before the statistic names |
| `ALTER INDEX` | after the *table* (a missing one is Msg 1088), before the index |
| `DROP INDEX` | after both — a missing table or index reports its own Msg 3701 |
| `sp_rename` | after the target resolves (Msg 15225 / 15248 otherwise), and after its Msg 15477 caution |
| `sp_addextendedproperty` | after the target resolves (Msg 15135 otherwise) |
| `ALTER TABLE` | after the table resolves (Msg 4902 otherwise) |
| every `DROP` of an object | after — real checks existence before the access mode |

The **full-text** statements report the subsystem's own **Msg 7690** instead (`Full-text operation failed because database is read only.`), at severity 16 and a state per statement: `CREATE FULLTEXT CATALOG` 100, `DROP FULLTEXT CATALOG` 102, `CREATE FULLTEXT INDEX` 103, `DROP FULLTEXT INDEX` 105.
`CREATE SCHEMA` raises Msg 3906 and then real's own trailing Msg 2759, of which the simulator reports the 3906.

`master` and `tempdb` **pin** the option and raise **Msg 5058** class 16 for either value asked for — `Option '<READ_ONLY|READ_WRITE>' cannot be set in database '<n>'.` — at their own states, **5** for `master` and **4** for `tempdb`.
`model` and `msdb` both accept it.

`COMPATIBILITY_LEVEL` is the one `SET` option a read-only database itself refuses (Msg 3906, followed by Msg 5069).
`ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT`, `RECURSIVE_TRIGGERS`, `ANSI_NULLS`, `RECOVERY` — and `READ_WRITE` itself — all move freely on one, probe-confirmed by reading the flags back.

**A bacpac import lands writable.**
DacFx omits the access mode from `SqlDatabaseOptions` even when exporting a read-only database (verified against the WideWorldImporters and AdventureWorks models, neither of which carries the property), and an `IsReadOnly` property is deliberately not translated: the element is read in phase 1, before the schema and data load, so a `READ_ONLY` set there would refuse the rest of its own import.
Carrying it wants a post-load hook.

### Not modeled yet

- The database-level `OFFLINE` / `EMERGENCY` / `RESTRICTED_USER` states real also refuses writes in stay parse-and-discard.

## `COLLATE` clause

`ALTER DATABASE name COLLATE <name>` — separate top-level grammar, not under `SET`.
Validates against `Collation.Recognized` (12 entries — see [`collations.md`](collations.md)).
Stores on `Database.CollationName`.
An unrecognized *collation* name raises `NotSupportedException` rather than silently accepting — silent acceptance would mean the bacpac loader silently mis-loads collation-sensitive data on a non-default-collation model.
(An unrecognized `SET` **option** name is a different path and never reaches an accept-list: the grammar has no production for it, so it is Msg 102 at the name.)

`sys.databases.collation_name` and `DATABASEPROPERTYEX(db, 'Collation')` surface the declared name.

Per-column declarations, the postfix `expr COLLATE name` operator, coercibility resolution, Msg 468 / 457 cross-collation enforcement, and `#temp` collation inheritance are documented in [`collations.md`](collations.md).

## `IsFullTextEnabled`

Not handled here — emitted by SqlPackage as `EXEC sp_fulltext_database 'enable|disable'`, a system sproc the simulator doesn't model.
See [`full-text.md`](full-text.md) for the broader full-text deferral.

## Error paths

All raise Msg 102 — matching probed real SQL Server wording:
- `SET RECOVERY = FULL` (EnumIdent options reject `=`)
- `SET ACCELERATED_DATABASE_RECOVERY ON` (EqualsOnOff options require `=`)
- `SET TARGET_RECOVERY_TIME = 60` (IntegerWithUnit options require the unit)

## Files and filegroups

Each database carries its files as catalog state, `Database.Files` (`DatabaseFile`) — no physical file stands behind one — and every file surface reads that list: `sys.database_files`, `sys.master_files`, `FILE_ID` / `FILE_NAME` / `FILEPROPERTY`, `sp_helpfile`, and the database sizes `sp_helpdb` / `sp_databases` / `sp_spaceused` report ([`catalog-views.md`](catalog-views.md)).
All of it was probed 2026-09-27 against SQL Server 2025.

**Sizes.**
A size, ceiling or growth is written in `KB`, `MB` (the default), `GB` or `TB` and kept in 8 KB pages, rounded up to a whole 64 KB extent (`21001KB` is the 2632 pages `21000KB` is); a growth may be a percentage instead.
Anything past `int` pages is Msg 1842, and like every grammar refusal here — an option the form doesn't take, a repeated one, an unknown unit (Msg 153 naming it as written, `percent` for `%`), a missing `NAME` or `FILENAME` (Msg 1036) — it is raised compiling the batch, so nothing in the batch runs.
Since every row lands in the primary data file (placement isn't recorded), that file reports the larger of its declared size and the pages its data needs, so a file never reports less than it holds.

**`CREATE DATABASE … ON [PRIMARY] <spec>, … [, FILEGROUP name [CONTAINS …] [DEFAULT] <spec>, …] [LOG ON <spec>, …]`.**
The first data file is file 1 on `PRIMARY`, floored at `model`'s 8 MB (a smaller or missing size reads 1024 pages, and the floor lifts a ceiling it passes); the first log file is file 2, then the other data files and the other log files take 3, 4, … in order.
Without `LOG ON` the database gets `<db>_log`; `LOG ON` without data files is Msg 188.
A filegroup declared twice or named `PRIMARY` is Msg 5035, a repeated logical name Msg 1828; a file under 512 KB (the primary data file excepted), a ceiling under its size or a growth past its ceiling, or a path another file holds, is followed by Msg 1802.
A database made without a file list takes the default paths, so after `MODIFY NAME` the old name can't be reused until the renamed database goes — its files keep their names and paths, as real's do.

**`ADD [LOG] FILE <spec>, … [TO FILEGROUP name]`.**
A file takes the lowest free `file_id` from 3, so a removed file's id comes back; without `TO FILEGROUP` it joins `PRIMARY` even when another filegroup is the default.
A log file naming `PRIMARY` ignores it, and any other filegroup is Msg 5087.
The list is all-or-nothing, and a file's creation refusal (Msg 5174 / 5103 / 5169) is followed by Msg 5009; a taken path is Msg 5009 and then Msg 5170.

**`MODIFY FILE <spec>`** takes one specification and changes nothing unless everything passes.
The size must grow (Msg 5039 otherwise), and a size past the ceiling lifts the ceiling; a ceiling under the current size is Msg 5040, and one under the current growth Msg 5169 state 3 — checked against the growth the file has, even when the statement sets a new one.
`NEWNAME` refuses any name a file holds, the file's own in another case included (Msg 1828); `FILENAME` refuses a path another file holds (Msg 12106) and reports the class-0 Msg 5018, which real follows by using the path at the next restart.
`OFFLINE` on a log file or a `PRIMARY` file is Msg 5077.

**`REMOVE FILE`** refuses the primary data and log files (Msg 5020), a read-only filegroup's file (Msg 5055), and the default filegroup's only file (Msg 5031).

**`MODIFY FILEGROUP name { DEFAULT | READ_ONLY | READ_WRITE | AUTOGROW_ALL_FILES | AUTOGROW_SINGLE_FILE | NAME = new }`** (`READONLY` / `READWRITE` spell the same) sets the flags `sys.filegroups`, `sys.data_spaces`, `FILEGROUPPROPERTY` and `FILEPROPERTY(…, 'IsReadOnly')` report.
A property is refused on a filegroup without files (Msg 5050), `PRIMARY` can't be made read-only or read-write (Msg 5047) or renamed (Msg 5012), and a property the filegroup already has is Msg 5045; a rename onto the filegroup's own name in any case succeeds.
A read-only filegroup's files refuse `ADD` / `MODIFY FILE` (Msg 5048).

**A read-only database** refuses the file forms with Msg 5004 (state 1 adding, 3 removing, 4 modifying) and the filegroup forms with a plain Msg 3906.
All of these refusals end only their statement.

**A `CONTAINS MEMORY_OPTIMIZED_DATA` filegroup** is recorded with its files as containers, the type-2 `FILESTREAM` rows from file id 65537 that memory-optimized tables need — see [`memory-optimized.md`](memory-optimized.md#the-filegroup-and-its-containers).

### Divergences

- What a filegroup holds is its tables' and indexes' placement ([`partitioning.md`](partitioning.md#filegroup-placement)), not pages kept per file: `REMOVE FILE` counts any row on the file's filegroup as the file's.
- A secondary file's `SpaceUsed` is a constant, where real's varies between fresh files ([`scalars.md`](scalars.md#filepropertyfile_name-property)).
- A path is taken only when another file in the `Simulation` holds it; real checks the disk, so a stray file there refuses it too.
- `MODIFY FILE … FILENAME` changes the reported path at once and never fails a later restart, which real's does when nothing is at the new path.

### Not modeled yet

- `MODIFY FILE … OFFLINE` on a secondary data file raises `NotSupportedException`.
- A `CONTAINS FILESTREAM` filegroup is registered as a rows filegroup, and its files aren't recorded.
- A file's contents: rows aren't kept per file, so `DBCC SHRINKFILE` ([`heap-storage.md`](heap-storage.md)) doesn't move the declared sizes.
- `ALTER DATABASE … MODIFY FILE` on a system database follows the user-database rules unprobed.

## Scoped configuration

`ALTER DATABASE SCOPED CONFIGURATION [FOR SECONDARY] SET <option> = <value>` and `… CLEAR PROCEDURE_CACHE [plan_handle]` act on the session's database, whose `DatabaseScopedConfiguration` holds a primary and a secondary value for every option `sys.database_scoped_configurations` lists; the option table in `DatabaseScopedConfiguration.cs` carries each option's id, value grammar, default and primary-only refusal.
Probed 2026-09-27 against SQL Server 2025.

- **Values.** The bit options take `ON` / `OFF`; `MAXDOP` an integer 0–32767 (Msg 12108), `PAUSED_RESUMABLE_INDEX_ABORT_DURATION_MINUTES` 0–71582 (Msg 12121), `FULLTEXT_INDEX_VERSION` 1 or 2 (Msg 31207); the `ELEVATE_*` pair `OFF` / `WHEN_SUPPORTED` / `FAIL_UNSUPPORTED`; `LEDGER_DIGEST_STORAGE_ENDPOINT` a string or `OFF`.
  A fractional or past-`int` number is Msg 1080, a quoted or bracketed value or name a syntax error, and an unknown option name Msg 102 at it.
- **`PRIMARY`** is the secondary value that means "as the primary", taken by the bit options and `MAXDOP` only; as a primary value it is Msg 12109.
  `FOR SECONDARY` on one of six primary-only options is Msg 12110, naming `GLOBAL_TEMPORARY_TABLE_AUTO_DROP` as `DISABLE_GLOBAL_TEMP_TABLE_AUTODROP` and the minutes option as `AUTO_ABORT_PAUSED_INDEX`.
  Real sets the *primary* `FULLTEXT_INDEX_VERSION` from a `FOR SECONDARY` set, and ignores a `FOR SECONDARY` ledger endpoint `OFF`; both are mirrored.
- **Refusals by phase.** The value and replica refusals above are raised compiling the batch.
  Running, a caller without `ALTER` on the database gets Msg 15247 state 13, a read-only database Msg 3906 for `SET` (`CLEAR` still succeeds), a plan handle Msg 12117 (the plan cache hands none out), a ledger endpoint Msg 12136 unless it is an `https://…blob.core.windows.net` URL and then Msg 37531, there being no credential to reach it, and the Synapse-only `DW_COMPATIBILITY_LEVEL` a class-16 Msg 102; each ends the batch, and a `TRY` catches it.
  Inside a user transaction the statement is Msg 226 state 7, which acts as under `XACT_ABORT` ([`transactions.md`](transactions.md)).
- **What a value drives.** `PREVIEW_FEATURES` admits SQL Server 2025's preview vector surface — the `float16` base type, `CREATE VECTOR INDEX` and `VECTOR_SEARCH` ([`vector.md`](vector.md)) — read from the database compiling the statement.
  `VERBOSE_TRUNCATION_WARNINGS` is the other one that changes behavior: with it on, a compatibility level of 150 or more selects the verbose Msg 2628 for string truncation over Msg 8152, and trace flag 460 selects it whatever the option and level say.
  `CLEAR PROCEDURE_CACHE` drops the plans cached for the session's database ([`plan-cache.md`](plan-cache.md#clearing-dbcc-freeproccache)).
- **Where values come from.** A new database copies `model`'s configuration, as real's does; the four system databases don't list `PREVIEW_FEATURES`.
- Every success raises the `ALTER_DATABASE_SCOPED_CONFIGURATION` DDL event, whose `EVENTDATA()` carries no `ObjectName` / `ObjectType`.

### Divergences

- The permission is `ALTER` on the database (which `db_owner` holds); real's own `ALTER ANY DATABASE SCOPED CONFIGURATION` isn't separately grantable.
- The ledger endpoint's Msg 12136 states split on the host as probed once each (a foreign host state 2, blob storage in another case state 3).

### Not modeled yet

- Every option but `VERBOSE_TRUNCATION_WARNINGS` and `PREVIEW_FEATURES` is recorded without effect — `IDENTITY_CACHE = OFF`, `GLOBAL_TEMPORARY_TABLE_AUTO_DROP = OFF` and the rest change nothing the simulator does.

## Bacpac loader context

`EmitDatabaseOptions` in `ModelXmlReader.cs` translates each `SqlDatabaseOptions` property to its `ALTER DATABASE … SET …` form.
Options that fall outside the accept-list (e.g. unrecognized future toggles) record on `BacpacLoadResult.Warnings` and the load continues — graceful degradation per the load-best-effort contract.
See [`bacpac-loader.md`](bacpac-loader.md).

The `QueryStore*` properties are the one family that can't go property by property, for two reasons that only show up together:

- Every sub-option's statement is an `= ON (…)`, which also enables the store, and DacFx writes `QueryStoreDesiredState` **before** the sub-options — so a model declaring an off store with configured sub-options would re-enable itself.
  They are harvested across the loop and emitted as one `= ON (…)` block followed, when the model asked for it, by a `= OFF`.
  That pair is what a sqlpackage import of the same bacpac produces, disabled-store configuration included.
- **An omitted property takes DacFx's model default, which is not always a fresh database's.**
  Probed 2026-08-08 by exporting a database at each setting: DacFx writes a property only when it differs from its schema default, and those defaults are the SQL Server 2016 ones — `QueryStoreCaptureMode` **ALL** and `QueryStoreMaxStorageSize` **100**, against 2025's AUTO / 1000.
  The other seven agree with a fresh database (`DesiredState` READ_WRITE, 900 / 60 / 200 / 30 / AUTO / ON), so only those two are seeded.
  The reference AdventureWorks omits both and its sqlpackage import reports ALL / 100, which is what the seeding reproduces.

Property encodings: `QueryStoreDesiredState` and `QueryStoreCaptureMode` carry the catalog's own codes (0–3 and 1–4), the rest their catalog units.
`QueryStoreSizeBasedCleanupMode` and `QueryStoreWaitStatisticsCaptureMode` are named by the DacFx schema but that export wrote neither at any value, so their translations are unreached insurance.
