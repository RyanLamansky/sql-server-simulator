# Claude Working Notes

Auto-loaded orientation. `README.md` is for humans.

## Identity

`SqlServerSimulator`: in-process .NET 10 SQL Server simulation. It's an **ADO.NET stand-in for `Microsoft.Data.SqlClient`** — consumers create a `Simulation`, get a `SimulatedDbConnection` via `CreateDbConnection()`, and use it with (for example) `Microsoft.EntityFrameworkCore.SqlServer` instead of going through SqlClient over the wire. The full ADO.NET concrete-pipeline chain (`SimulatedDb{Connection,Command,Parameter,ParameterCollection,DataReader,Transaction}` + `SimulatedSqlException` + the info-message family) is public with `new`-shadowed strongly-typed returns, mirroring `Microsoft.Data.SqlClient`'s shape so consumers can downcast and reach concrete properties identically. Public surface beyond that chain is intentionally minimal so internals stay free to refactor; `QualityTests.PublicApiWhitelist` is the authoritative list and fails the build on any unintended expansion — resist adding to it.

`SqlServerSimulator.EFCore` is a sibling package whose only public method is `UseSqlServerSimulator(DbContextOptionsBuilder, DbConnection)`. EF Core's SqlServer provider keeps emitting SQL-Server-flavored SQL; the adapter just registers an `IRelationalTypeMappingSourcePlugin` for the (CLR, store) pairs whose default mappings downcast to `SqlParameter` (since the simulator's connection isn't a `SqlConnection`).

## Operating goal

High-fidelity emulation. Authenticity over desirability — when SQL Server's behavior is quirky or lossy (CP1252 `?` replacement, ANSI trailing-space `=` padding, `LEN` excluding trailing spaces), mirror it. EF Core trusts the simulator end-to-end (`*.Tests.EFCore` is the regression oracle and must stay green). Beyond that floor, priority is broad SQL Server coverage weighted by popularity (user wins) and ease (thoroughness wins). The function-coverage backlog at [`docs/claude/function-coverage-todo.md`](docs/claude/function-coverage-todo.md) is sorted by that weighting and tracks category-completion milestones — read it before pitching a new built-in function.

## Feature-bundle workflow

1. **Probe.** Behavior questions get answered against the real SQL Server 2025 reference instance (connection details in user memory). Probe scaffolds live in `/tmp/<probe-name>/`; deleted after the bundle. Only graduated regression tests land in `*.Tests` / `*.Tests.EFCore`.
2. **Surface decisions.** Before writing code, surface 2–3 concrete design choices and recommend one each.
3. **Implement + test.** `*.Tests` exercises public API; `*.Tests.EFCore` validates the oracle. `*.Tests.Internal` only for things genuinely unreachable from public SQL.
4. **Update CLAUDE.md and `docs/claude/`.** Move bullets between What's-modeled / Not-modeled / Quirks as scope changes; deep-dive feature catalogs live under `docs/claude/`.
5. **Single-sentence commit.** Squashes capture end state. Don't run `git commit` — the user holds signing credentials.

Behavioral claims below were probed against a live SQL Server 2025 reference instance unless flagged otherwise.

## Build / test

```
dotnet build
dotnet test
```

Every `.csproj` sets `EnforceCodeStyleInBuild=true`, so `dotnet build` runs the IDE / SSS / MSTEST analyzer rules and fails on violations. No separate `dotnet format` pass is needed — running it costs seconds and catches nothing build doesn't already. CI matrix: Debug + Release. If `obj/` permission errors appear, the user's been building outside the dev container; `rm -rf obj/ bin/` clears them.

Full suite runs in ~3s; single-test filter (`--filter "FullyQualifiedName~Foo"`) under 100ms. Treat `dotnet test` as a verifier between micro-edits, not a checkpoint between major ones.

## Architecture — load-bearing patterns

Layout: `Storage/` (pages, types, row encoder/decoder, heap, constraints, lock manager + DMVs), `Parser/` (tokenizer, expressions, query planning + execution), `Simulation/` (per-statement-kind partials), `Schemas/` (`SchemaObject` hierarchy + alias/catalog-view/full-text/spatial/xml-schema-collection types), `Errors/` (exception factory partials), root (`Simulated*` ADO.NET front-door + `Simulation` / `Database` / `Schema` / supporting types).

### Storage
8KB heap pages. Rows encoded as bytes, navigated column-by-column without rehydrating. Every non-NULL variable-length column carries a 1-byte inline/pointer marker. LOB-eligible types (`varchar(MAX)` / `nvarchar(MAX)` / `varbinary(MAX)` / `text` / `ntext` / `image`) flow through a parallel chain of 8KB LOB pages. Bounded `varchar(N)` / `nvarchar(N)` / `varbinary(N)` start inline; the encoder pushes the largest off-row greedily until the row fits within 8060 bytes. Allocation tracking is a flat page list (no IAM/PFS).

### Type system
`SqlType` / `SqlValue` is the storage-layer pair. Three coercion paths: `SqlValue.Coerce` (runtime values), `SqlType.Promote` (static unification for CASE / set ops / COALESCE), `SqlType.PromoteForArithmetic(a, b, op)` (per-operator decimal/integer/money/float result type — the single source of truth for both `TwoSidedExpression.GetSqlType` and `DecimalArithmetic`; static/runtime parity required because the row encoder rejects type mismatches).

### Selection
`Selection.cs` + `Selection.Execution.cs` are a partial-class pair. `Parse → Selection`, `Execute → SimulatedSqlResultSet`. Correlated subqueries re-run the same plan per outer row via `outerResolver: Func<MultiPartName, SqlValue>?` (execute) and `outerTypeResolver: Func<MultiPartName, SqlType>?` (parse). Both walk arbitrary nesting depth via `ParserContext.OuterTypeResolver` + the runtime arg. **Derived tables in FROM are always deferred** (`FromSource.LateralPlan` is re-executed per outer row), matching SQL Server's "any FROM derived table can correlate" rule — required because outer references in WHERE/ON resolve through `Run`, not `GetSqlType`.

### Multi-source rows
`FromSource[]`; rows during enumeration are `byte[]?[]`, one slot per source, null = NULL-filled outer-join side (LEFT/RIGHT/FULL/OUTER APPLY). Column resolution is qualifier-aware via `FindSourceColumn` / `ResolveAcrossTuple`; ambiguous unqualified name → Msg 209.

### `MultiPartName`
Readonly struct, up to 4 inline slots (SQL Server's grammar limit). API: `Leaf`, `ImmediateQualifier` (null when unqualified — pair with `Collation.Baseline.Equals(name.ImmediateQualifier, "INSERTED")`, the equality folds null into `false`), `Count`, `ToString()`. 5th segment → Msg 4104.

### Exception factories
`SimulatedSqlException` constructor is private; each error case is an `internal static` factory in a topical partial (`TypeErrors`, `SchemaErrors`, `ConstraintErrors`, `ResolutionErrors`, `QueryErrors`, `SyntaxErrors`). The number lands in `Data["HelpLink.EvtID"]`. **Grep for an existing factory before adding a new one.**

### Expression evaluation
`Expression.Run(RuntimeContext runtime)` (runtime) and `Expression.GetSqlType(BatchContext batch, ...)` (static, for projection schema) must agree on result type — drift breaks union/CASE/coalesce schema. Both take a `BatchContext` so result types that depend on the active database (notably the collation of literal / CAST / function-result string types) stay in parity between the parse-time schema and the runtime value. `RuntimeContext` bundles `ResolveColumn` (per-row column lookup) and `Batch` (the executing `BatchContext`); expressions that need batch / session / database state read `runtime.Batch.*` directly. `BooleanExpression.Run` returns `bool?` (three-valued); WHERE/MERGE-ON exclude UNKNOWN, CHECK passes UNKNOWN. Aggregates: subclass `Aggregator` (`Add(SqlValue)` / `Result()`), register in `AggregateExpression`'s dispatch.

### Context layering
Six scopes, one home each. **Add new state to whichever class matches its true scope** — when in doubt, ask who outlives whom. The field roster on each class lives in the source XML docs; this section captures only the identity + load-bearing contracts.

- **`Simulation`** = server / instance. Holds `SystemHeapTables`, NEWSEQUENTIALID anchor, the `Databases` dict, `ServerCollationName` (string-typed `init`-only knob; defaults to `SQL_Latin1_General_CP1_CI_AS`; mirrors `model.collation` — install-time seed for every freshly-created `Database`, both the lazy `"simulated"` seed and bacpac imports without their own collation declaration; `init` reflects real SQL Server's immutability and Azure's outright block on changing it). Public surface (`Simulation` ctor + `CreateDbConnection()` + `ImportBacpac()` + `AddRemoteSimulation()` + `ServerCollationName`) is the entire external API.
- **`Database`** (internal) = one database in the instance. Holds `Schemas`, `CompatibilityLevel`, `CollationName`, `Principals`, `Permissions`, `ExtendedProperties`, `FullTextCatalogs`, `DdlTriggers`, the rowversion counter (`@@DBTS`), MVCC version store. `Simulation.Databases` starts empty; the first `CreateDbConnection()` call lazily seeds `Simulation.DefaultDatabaseName` (`"simulated"`) when no `ImportBacpac` has landed a database first. `USE <db>` switches the session to a different entry (Msg 911 on miss); 3-part names route reads across databases (`SELECT * FROM other.dbo.t` works), but cross-DB writes raise `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — issue `USE` first.
- **`Schema`** (internal) = one namespace inside a database. Holds the object dicts: `HeapTables`, `Functions`, `Views`, `Procedures`, `Sequences`, `Triggers` (DML — share the object namespace), and the separate type namespace `TableTypes` + `AliasTypes` + `XmlSchemaCollections`. Schema-qualified references (`audit.t`, `audit.fn`, `audit.proc`, `audit.seq`, …) route through `Database.Schemas["audit"]`; unqualified falls back to `Database.DefaultSchemaName` (`"dbo"`).
- **`SimulatedDbConnection`** = session. Holds `CurrentDatabase`, `CurrentTransaction`, `LastIdentity` (`SCOPE_IDENTITY` / `@@IDENTITY`), `LastStatementRowCount` (`@@ROWCOUNT`), `LastErrorNumber` (`@@ERROR`), `NestingLevel` (capped at 32), `TempTables` (per-session `#foo` dict, cleared on Dispose), `Spid` (≥51), `LockTimeoutMillis`, `SessionIsolationLevel`, `FiringTriggerIds`, `CurrentExecutingThreadId` (drives same-thread-deadlock detection).
- **`BatchContext`** (internal, `Parser/`) = one command execution. Owns the `ParserContext` (parse-time scratch). Holds batch-lifetime runtime state: `Variables`, `TableVariables` (`@t`), `CurrentUndoLog`, `CurrentTableVarUndoLog` (statement-only, disjoint from the tx-scoped log so `ROLLBACK TRAN` skips `@t`), `UdfFrame` / `ProcFrame` (non-null when this batch is a UDF / procedure body — gates value-form `RETURN`), `CurrentStatement`. Exposes the **resolver contract** that the parser depends on:
  - `TryResolveTable(MultiPartName)` — `#foo` → `Connection.TempTables` regardless of qualifier; `@t` → `TableVariables` (1-part only); else → named schema (`dbo` for unqualified); `SystemHeapTables` only as flat 1-part fallback.
  - `TryResolveFunction` — 2-/3-part only (Msg 195 on bare 1-part). `TryResolveProcedure` accepts 1-part. `TryResolveTableType` accepts 1-part with `dbo` fallback.
  - `ParseObjectName(context, acceptTableVariable=false)` — parses 1-4-segment dotted form, compresses empty middle segments. The `acceptTableVariable` opt-in routes `@t` to a 1-part leaf at DML/FROM sites and rejects it everywhere else (Msg 102 at ALTER TABLE / DROP TABLE / TRUNCATE / CREATE / SELECT INTO).
  - Threaded explicitly into every `Expression.Run(RuntimeContext runtime)` call via `runtime.Batch`.
  - UDF / procedure invocation allocates a child `BatchContext` via the body constructor: parameters pre-seed `Variables`, the matching frame is set, the captured body text is re-tokenized through a synthesized `SimulatedDbCommand`. UDF bodies discard yielded result sets; procedure bodies forward them through.
- **`StatementContext`** (internal, `Parser/`) = the dispatch loop's per-statement frame. Allocated once per batch and overwritten at the top of each iteration; holds `UtcNow` (the per-statement freeze the time scalars read).

**Don't stack misfit state into these buckets unthinkingly**: if no scope fits, introduce the missing one rather than squatting on a neighbor.

## Conventions that fail builds

- **SSS001**: non-public types may not have auto-properties or trivial wrappers over same-type fields. Expose the field directly: `public readonly T Foo = expr;` (or `static readonly` for static-singleton state). Overrides, abstracts, and interface implementations exempt.
- **SSS002**: a `readonly` field in a non-public-API type whose declared type is a strict supertype of its initializer should be declared as the concrete type. Public types, value-typed initializers (boxing), const fields, and uninitialized fields exempt.
- **SSS003**: `string.ToUpperInvariant()` / `ToLowerInvariant()` whose result is the *governing expression* of a `switch` allocates a temporary string. Use the `Span<char>` overload — `Span<char> buf = stackalloc char[s.Length]; s.AsSpan().ToUpperInvariant(buf)` — and switch on the resulting count.
- **SSS004**: two or more `if`/`else if` branches with conditions of the shape `<sameScrutinee> is <SameType> { <SameProperty>: ... }` should be a single `switch`. The `switch` form fuses isinst + ldfld; the if-chain repeats both per arm.
- **MSTEST0049**: async tests must thread `TestContext.CancellationToken`. Pattern: `public TestContext TestContext { get; set; } = null!;`.
- **MSTEST0037**: prefer `Assert.IsEmpty(values)` over `Assert.AreEqual(0, values.Count)`; typed asserts over generic.

## Style notes

- **No temporal words in code comments** — "currently", "now", "yet", "new" age badly.
- **No comments inside expression chains** — IDE0055 fails on comments in ternary chains or between `=>` and body. Restructure or hoist to XML doc.
- **Fields over auto-properties on non-public types** (SSS001 generalized).
- **AssemblyHooks**: each test project has `AssemblyHooks.cs` with a `static [TestClass] [AssemblyInitialize]` to warm shared initialization paths once before the parallel test run. Without it, the first batch of tests races to initialize hot shared state and serializes on contention. The analyzer-tests' Roslyn-cache warm-up is the most extreme case observed (~3x slowdown), but the pattern generalizes to any expensive first-touch shared resource.

## SimulatedSqlException vs NotSupportedException

- `SimulatedSqlException` for behavior matching SQL Server: invalid SQL, type mismatches, constraint violations, oversize columns, truncation. Mirrors number/class/state/message.
- `NotSupportedException` for valid SQL Server features the simulator hasn't built. Name the unmodeled feature.

## What's modeled

The `*.Tests` and `*.Tests.EFCore` suites are the authoritative behavior contract. Notes below cover only probe-confirmed quirks, deviations from SQL Server, and non-obvious implementation rules. Per-feature deep-dives live under `docs/claude/` (see [Feature reference](#feature-reference) for the trigger-phrased index); short cross-cutting sections stay inline below.

### JOINs / APPLY
INNER / bare JOIN / LEFT [OUTER] / RIGHT [OUTER] / FULL [OUTER] / CROSS / CROSS APPLY / OUTER APPLY. Multi-table chains compose left-to-right. ON-predicate UNKNOWN excludes. APPLY = lateral form (right side re-executed per outer row, no ON clause). Comma-separated FROM (ANSI-89 syntax — `FROM a, b WHERE a.id = b.id`) parses as a sequence of explicit-join chains spliced with `JoinKind.Cross` joins; each comma starts a fresh chain via the same `ParseExplicitJoinChain` helper the JOIN-keyword loop calls, so any explicit JOINs *within* a chain bind before the cross-splice.

`JoinDriver` is a fold over `joins[]`: leftmost rowset → wrap with each join's operator → final enumerator (`Selection.Execution.Joins.cs`). INNER / CROSS / LEFT / CROSS APPLY / OUTER APPLY stream one upstream tuple at a time; RIGHT / FULL materialize `sources[level].Rows` into a list and track a `matched[]` bitmap across the entire upstream iteration so unmatched right rows can be emitted (with all prior slots NULL-filled) after upstream is exhausted. **RIGHT / FULL with a derived-table right side** materialize the lateral plan once via the enclosing-scope `outerResolver` (not the joined-tuple resolver), so non-correlated and outer-correlated derived tables work; lateral correlation to the left side is rejected because the derived-table parse doesn't wire the left-source snapshot resolver — left-side references raise Msg 207 ("Invalid column name") at runtime when `Reference.Run` hits the null outer resolver. Real SQL Server raises Msg 4104 at bind time for the same shape; different code, same end state. EF Core 10's LINQ `LeftJoin` / `RightJoin` operators translate to LEFT / RIGHT JOIN respectively and route through this pipeline; .NET 10 LINQ doesn't expose a `FullJoin` operator, so FULL OUTER JOIN is reachable only via raw SQL.

### Subqueries
`EXISTS` / `NOT EXISTS` (multi-column inner allowed); `expr [NOT] IN (SELECT ...)` (single inner column, Msg 116); scalar `(SELECT col FROM ...)` (single column, single-row Msg 512 per outer row, empty → typed NULL); `expr <op> {ANY|SOME|ALL} (SELECT col FROM ...)` quantified comparison with all six operators (`=` `<>` `<` `<=` `>` `>=`) plus T-SQL synonyms `!=` `!<` `!>`, predicate-only (SELECT-list usage raises Msg 102 at the operator, matching real SQL Server's grammar restriction); SOME aliases ANY. Empty inner: ALL vacuously true, ANY vacuously false (both independent of LHS NULL); non-empty inner with NULL on either side of any per-row compare taints to UNKNOWN per three-valued logic. All forms work correlated and non-correlated, arbitrary nesting depth. `UNION` / `UNION ALL` / `INTERSECT` / `EXCEPT` are legal inside every subquery context — derived tables in FROM, EXISTS, IN, ANY/ALL inners, scalar `(SELECT ...)`, CTE bodies — because subquery parsers route through `Selection.Parse` → `ParseQueryExpression`, which already drives the set-op chain. EF Core 7+'s TPC inheritance emit shape (UNION ALL of selects from each concrete table wrapped in a derived table) ships end-to-end through this path.

### Constraints
- `CHECK`: inline single-column and table-level forms; Msg 547 per row on definitely-false predicate. Inline column-level CHECK predicates may only reference their owning column — peer references raise **Msg 8141** at CREATE TABLE (probe-confirmed verbatim wording). The walker is structural via `Expression.VisitColumnReferences` + `BooleanExpression.VisitOperandExpressions`; coverage is currently limited to common container subclasses (`Reference`, `Parenthesized`, `TwoSidedExpression`, `Cast`, `Length`) — peer refs buried in less-common containers (`DATEPART`, `SUBSTRING`, nested `CASE`, etc.) silently escape the CREATE-TABLE check and surface at INSERT instead. Table-level CHECK has no peer restriction.
- `PRIMARY KEY` / `UNIQUE`: linear scan (O(N) per insert); no B-tree.
- `FOREIGN KEY`: inline `col REFERENCES other(col)` + table-level `FOREIGN KEY (cols) REFERENCES other(cols)` + named `CONSTRAINT name`; all four referential actions on both `ON DELETE` / `ON UPDATE` (`NO ACTION` default / `CASCADE` / `SET NULL` / `SET DEFAULT`); enforcement at INSERT / UPDATE / DELETE / MERGE; cascade-cycle rejection at CREATE (Msg 1785); referenced columns must form PK or UNIQUE (Msg 1776); NULL in FK column skips check (including partial NULL in composite); DROP TABLE on referenced parent → Msg 3726; full `sys.foreign_keys` (22 cols) + `sys.foreign_key_columns` (6 cols); `sys.objects.type = 'F '`. See `docs/claude/foreign-keys.md`.

### Transactions
Three entry points share one per-connection undo log: implicit (statement-level atomicity), SqlClient API (`BeginTransaction()`/`Commit()`/`Rollback()`), SQL-text (`BEGIN`/`COMMIT`/`ROLLBACK`/`SAVE TRANSACTION`).

- **Statement-level atomicity**: a single mutation throwing mid-execution rolls back its partial writes. Multi-row INSERT failing on row 3 leaves zero rows.
- **Explicit txs**: `BEGIN TRAN` increments `TranCount`; only outermost `COMMIT` actually commits; `ROLLBACK` zeroes `TranCount` and walks the entire log. `SAVE TRAN <name>` + `ROLLBACK TRAN <name>` is the EF SaveChanges path inside an explicit tx. Parallel `BeginTransaction` → `InvalidOperationException`. `COMMIT`/`ROLLBACK` with no active tx → Msg 3902/3903.
- `@@TRANCOUNT` reads connection depth as int.
- **Identity counters and the database-scoped rowversion counter bypass the log** — both keep advancing through rollback. Orphaned LOB chains for rolled-back inserts also leak.
- **Temp-table CREATE/DROP participates in the log** via `TempTableCreation` / `TempTableRemoval` `UndoEntry` subtypes (rollback removes from / restores into the connection's `TempTables` dict). Regular CREATE/DROP TABLE is NOT logged — see the corresponding quirk.
- Locking + MVCC: full 8-mode matrix, row-X writers + row-mode readers per hints/iso, RR/SER/UPDLOCK/XLOCK/TABLOCK/HOLDLOCK/REPEATABLEREAD/NOLOCK/READPAST hints, escalation at 5000 row-locks, Msg 1205 deadlock detection, Msg 1222 timeouts, SNAPSHOT + RCSI with version chains + GC + DMVs. See [`docs/claude/locking.md`](docs/claude/locking.md).

### MERGE / OUTPUT
- `INSERT ... OUTPUT INSERTED.<col>` (single-row).
- `MERGE INTO target USING <source> ON … WHEN [NOT] MATCHED [BY TARGET|SOURCE] [AND …] THEN UPDATE|DELETE|INSERT … [OUTPUT $action, …]` — source = VALUES / SELECT / set-op / bare-table; multiple per-family AND-conditioned clauses; trailing `;` required. Msg 5324 / 8672 / 10713 / 10714 enforced. Triggers fire one INSERT → UPDATE → DELETE pass with combined affected rows. See [`docs/claude/dml.md`](docs/claude/dml.md).

### EF Core adapter coverage
`UseSqlServerSimulator(...)` covers seven SqlParameter-downcast pairs: `DateOnly→date`, `DateTime→date`, `DateTime→smalldatetime`, `TimeOnly→time(N)`, `TimeSpan→time(N)`, `decimal→money`, `decimal→smallmoney`. Without the adapter those mappings throw at SaveChanges. MAX-string family flows through plain `UseSqlServer`.

### `text` / `ntext` / `image` restrictions
Comparison (Msg 402), ORDER BY/DISTINCT (Msg 306), and aggregates (Msg 8117 from MAX/MIN) all enforced.

### `SimulatedDbDataReader`
Full `DbDataReader` contract. Typed accessors read `SqlValue` directly via the cursor's indexer and unwrap via `As*` (no boxing); NULL on a typed accessor → `SqlNullValueException` matching SqlClient. `GetDateTime` covers Date/DateTime/SmallDateTime/DateTime2 (Date surfaces at midnight, `Kind=Unspecified`); `GetDecimal` covers Decimal/Numeric/Money/SmallMoney; `GetFieldValue<T>` short-circuits EF's `DateOnly`-over-`Date` and `TimeOnly`-over-`Time`. `GetOrdinal(name)` is two-pass linear scan (case-sensitive then case-insensitive — SqlClient's documented precedence). `HasRows` is a sticky bit. `GetChar(int)` always raises `InvalidCastException` (matches SqlClient).

### Feature reference

Per-feature deep-dives live under `docs/claude/`. Each entry below is a trigger: read the linked file on demand when working in the matching area.

- **Built-in scalars** (math, date incl. DATETRUNC / DATE_BUCKET / SWITCHOFFSET / TODATETIMEOFFSET, current-time, `*FROMPARTS`, AT TIME ZONE, CONCAT, char-code, SOUNDEX / STR / TRANSLATE / STRING_ESCAPE / DIFFERENCE, CHOOSE / IIF, bit manipulation (BIT_COUNT / GET_BIT / SET_BIT / LEFT_SHIFT / RIGHT_SHIFT), CHECKSUM / BINARY_CHECKSUM, FORMAT, RAND, STRING_SPLIT, GENERATE_SERIES, COMPRESS/DECOMPRESS, session/server `@@`-constants + HOST_NAME / APP_NAME / GETANSINULL / ORIGINAL_DB_NAME) → [`scalars.md`](docs/claude/scalars.md).
- **`SqlType.Promote` / `PromoteForArithmetic` / decimal precision-scale / int↔string promotion** → [`arithmetic.md`](docs/claude/arithmetic.md).
- **`Cast` / coercion error paths** (CAST/CONVERT narrow targets, TRY_CAST/TRY_CONVERT swallow set, PARSE/TRY_PARSE culture-aware parsing) → [`casting.md`](docs/claude/casting.md).
- **`Selection`, aggregates, window functions, set ops, CASE, OFFSET/FETCH** → [`query.md`](docs/claude/query.md).
- **UPDATE / DELETE / INSERT…SELECT / SELECT…INTO / rowversion (incl. `@@DBTS` / `MIN_ACTIVE_ROWVERSION`) / identity helpers (`@@IDENTITY` / `SCOPE_IDENTITY` / `IDENT_CURRENT` / `IDENT_INCR` / `IDENT_SEED`) / `@@ROWCOUNT` / `ROWCOUNT_BIG` / OUTPUT / MERGE** → [`dml.md`](docs/claude/dml.md).
- **Variables, control flow (IF/WHILE/BREAK/CONTINUE/RETURN), TRY/CATCH+THROW+ERROR_*, `@@ERROR` / `@@TRANCOUNT` / `XACT_STATE`, PRINT, WAITFOR** → [`control-flow.md`](docs/claude/control-flow.md).
- **CTE shapes / recursive-CTE error handling** → [`ctes.md`](docs/claude/ctes.md).
- **JSON_VALUE / JSON_QUERY / JSON_MODIFY / JSON_OBJECT / JSON_ARRAY / JSON_PATH_EXISTS / ISJSON / OPENJSON** → [`json.md`](docs/claude/json.md).
- **Name resolution, schema lookup, CREATE / DROP / ALTER SCHEMA TRANSFER, `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` / `SCHEMA_ID` / `SCHEMA_NAME` / `DB_ID` / `DB_NAME`, cross-DB read routing** → [`schemas.md`](docs/claude/schemas.md).
- **System metadata surfaces** (sys.* / INFORMATION_SCHEMA.*, function-form lookups: `OBJECTPROPERTY` / `OBJECTPROPERTYEX` / `COLUMNPROPERTY` / `INDEXPROPERTY` / `INDEX_COL` / `INDEXKEY_PROPERTY` / `STATS_DATE` / `TYPEPROPERTY` / `SERVERPROPERTY` / `COL_LENGTH` / `COL_NAME` / `TYPE_NAME` / `TYPE_ID` / `PARSENAME`) → [`catalog-views.md`](docs/claude/catalog-views.md).
- **Scalar UDFs / TVFs / views / stored procs / dynamic SQL / `@@NESTLEVEL` / `@@PROCID`** → [`programmable.md`](docs/claude/programmable.md).
- **`#foo` / `##foo` routing, DROP TABLE, TRUNCATE TABLE** → [`temp-tables.md`](docs/claude/temp-tables.md).
- **`DECLARE @t TABLE`, table-variable DML, `OUTPUT … INTO`** → [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE TYPE … AS TABLE`, TVP params + `READONLY`, ADO.NET TVP** → [`table-valued-parameters.md`](docs/claude/table-valued-parameters.md).
- **`CREATE TYPE … FROM <builtin>` (scalar alias types / UDDTs), multi-part type-references** → [`alias-types.md`](docs/claude/alias-types.md).
- **`sp_addextendedproperty` / `fn_listextendedproperty` / `sys.extended_properties`** → [`extended-properties.md`](docs/claude/extended-properties.md).
- **`CREATE/ALTER/DROP SEQUENCE`, `NEXT VALUE FOR`, `sys.sequences`** → [`sequences.md`](docs/claude/sequences.md).
- **DML + DDL triggers** (`CREATE TRIGGER` incl. `ON DATABASE`, `INSERTED`/`DELETED`, `TRIGGER_NESTLEVEL`, `sys.triggers`) → [`triggers.md`](docs/claude/triggers.md).
- **`FOREIGN KEY` + referential actions, `sys.foreign_keys`** → [`foreign-keys.md`](docs/claude/foreign-keys.md).
- **`PERIOD FOR SYSTEM_TIME`, `FOR SYSTEM_TIME ALL/AS OF`, history sibling, `temporal_type`** → [`temporal-tables.md`](docs/claude/temporal-tables.md).
- **`ALTER TABLE` ADD/DROP/ALTER COLUMN + CONSTRAINT (incl. trust toggling)** → [`alter-table.md`](docs/claude/alter-table.md).
- **`CREATE INDEX` (UNIQUE / CLUSTERED / INCLUDE / WHERE filter), `sys.indexes`** → [`indexes.md`](docs/claude/indexes.md).
- **Table hints (`WITH (NOLOCK …)`) + statement `OPTION (…)` hints** → [`query-hints.md`](docs/claude/query-hints.md).
- **Locking, MVCC, SNAPSHOT/RCSI, deadlock/timeout, lock-related DMVs** → [`locking.md`](docs/claude/locking.md).
- **`hierarchyid` data type** (incl. deferred byte-identical CAST research notes) → [`hierarchyid.md`](docs/claude/hierarchyid.md).
- **`GRANT` / `REVOKE` / `DENY`, principal DDL, fixed-principal seed, principal scalars (`USER_ID` / `SUSER_ID` / `DATABASE_PRINCIPAL_ID` / `USER_NAME` / `SUSER_NAME` / `SUSER_SNAME` / `CURRENT_USER` / `SESSION_USER` / `SYSTEM_USER` / `ORIGINAL_LOGIN` / `HAS_PERMS_BY_NAME` / `IS_MEMBER` / `IS_ROLEMEMBER` / `IS_SRVROLEMEMBER`)** → [`permissions.md`](docs/claude/permissions.md).
- **`CREATE FULLTEXT CATALOG`/`INDEX`, `CONTAINS`/`FREETEXT` rejection** → [`full-text.md`](docs/claude/full-text.md).
- **`xml` data type, XML schema collections, XML method dispatch, XML indexes** → [`xml.md`](docs/claude/xml.md).
- **`geography` / `geometry` types, spatial methods, spatial indexes** → [`spatial.md`](docs/claude/spatial.md).
- **`ALTER DATABASE SET <option>` accept-list + database-level `COLLATE` clause** → [`database-options.md`](docs/claude/database-options.md).
- **Per-column / per-expression collation, coercibility precedence, Msg 468 / 457 cross-collation enforcement, recognized catalog, `#temp` collation inheritance** → [`collations.md`](docs/claude/collations.md).
- **New top-level statement parser or dispatch-loop separator rules** → [`grammar.md`](docs/claude/grammar.md) + [`control-flow.md`](docs/claude/control-flow.md).
- **BACPAC import** (`Simulation.ImportBacpac` instance method — multi-database via repeated calls, `BacpacImportOptions`, `ModelXmlReader` dispatcher, BCP wire format, `BacpacBuilder` test harness) → [`bacpac-loader.md`](docs/claude/bacpac-loader.md).
- **Linked servers** (`Simulation.AddRemoteSimulation`, `sp_addlinkedserver` / `sp_dropserver`, four-part-name FROM routing through the remote's ADO.NET pipeline, `sys.servers`) → [`linked-servers.md`](docs/claude/linked-servers.md).

## Not modeled

- **Key-range locks** — sole remaining phase 4+ deferral. See [`locking.md`](docs/claude/locking.md) for what does ship (full 8-mode matrix, SNAPSHOT/RCSI, DMVs, Msg 1205/1222/3952/3960).
- `BEGIN DISTRIBUTED TRANSACTION` raises `NotSupportedException` at dispatch. `BEGIN TRANSACTION <name> WITH MARK 'm'` raises **Msg 319** at parse (the parser doesn't accept `WITH` here); bare named transactions (`BEGIN TRAN t1`) ship.
- **`CREATE DATABASE`** / **`CREATE ASSEMBLY`** — Msg 102 at parse. Adding databases routes through `Simulation.ImportBacpac`; CLR assemblies aren't modeled at all.
- **Cross-database DML** (`INSERT`/`UPDATE`/`DELETE`/`MERGE` through a 3-part name targeting a different database) raises `NotSupportedException` via `BatchContext.RejectCrossDatabaseMutation` — trigger scope swapping, identity allocation routing, undo-log scoping, FK validation across DB boundaries pending. Issue `USE <db>` and reference via a 1- or 2-part name. Cross-database SELECT / JOIN / catalog-view reads (`SELECT * FROM other.dbo.t`, `dbA.sys.tables`) ship. **Four-part-name writes** (`linkedserver.db.schema.t` INSERT/UPDATE/DELETE/MERGE) also raise via the same factory — see linked-servers below. See [`schemas.md`](docs/claude/schemas.md).
- **Cross-server writes** through four-part names raise `NotSupportedException` (lock-manager + undo-log coordination across `Simulation` boundaries pending; parallels the `BEGIN DISTRIBUTED TRANSACTION` stance). **Four-part-name reads** ship through the remote `Simulation`'s full ADO.NET pipeline. See [`linked-servers.md`](docs/claude/linked-servers.md). **Catalog views through four-part names** (`srv.db.sys.tables`) currently fall through to Msg 208 — issue catalog queries against the remote `Simulation` directly.
- **`SET <option>` accept-list** (`Simulation.Set.cs`) covers XACT_ABORT, all ANSI/session-state toggles, `STATISTICS {IO|TIME|XML|PROFILE}`, value-taking options (`TEXTSIZE`/`DATEFIRST`/etc.) — all parse-and-discard. Unknown SET → Msg 195. `SET @v`, `IDENTITY_INSERT`, `NOCOUNT`, `LOCK_TIMEOUT`, `TRANSACTION ISOLATION LEVEL` carry semantic effect.
- **`ALTER DATABASE … SET` / `COLLATE` surface** — see [`database-options.md`](docs/claude/database-options.md). Most options parse-and-discard; `COMPATIBILITY_LEVEL`, `ALLOW_SNAPSHOT_ISOLATION`, `READ_COMMITTED_SNAPSHOT` are load-bearing.
- `RANGE BETWEEN <N> PRECEDING/FOLLOWING` numeric-offset — Msg 4194, matching real SQL Server's licensed-feature rejection. `ROWS` numeric-offset ships. Default frame with ORDER BY is `RANGE UNBOUNDED PRECEDING TO CURRENT ROW`; without it, whole partition. LAST_VALUE matches real SQL Server's default-frame semantic.
- Recursive-part feature restrictions (Msg 460 / 461 / 462 / 467 / 465) — silently accepted with possibly-incorrect semantics. Apps that exercise these hit rejection on real SQL Server too.
- `LEN(ntext)` raising Msg 8116; legacy `READTEXT` / `WRITETEXT` / `UPDATETEXT`.
- **MERGE gaps**: CTE-precedes-MERGE bare-name form (`WITH cte AS (…) MERGE … USING cte ON …`) and `MERGE INTO <updatable view>` both ship; the latter has full parity with the UPDATE / INSERT / DELETE-through-view shapes — visibility filter scopes the target row set, `WITH CHECK OPTION` enforced on inserts / updates (Msg 550), view-column rename / reorder translates through `View.BaseColumnOrdinals`, INSTEAD OF INSERT / UPDATE / DELETE triggers replace the heap-write path per action. `MERGE … OUTPUT` through a view raises `NotSupportedException` (matches existing pattern; view-column projection through `INSERTED` / `DELETED` deferred). Three related shapes initially flagged as gaps turned out to be invalid on real SQL Server too (Msg 156 at parse): `USING (WITH cte AS …)`, `MERGE` inside a CTE body, and multi-statement `WHEN` bodies.
- `UNIQUE` on a *non-persisted* computed column (PK/UNIQUE on `PERSISTED` ships). Msg 4936 determinism gate for PERSISTED computed columns also not enforced.
- Heap allocation tracking (flat page list, no IAM/PFS).
- **Table-variable named constraints / foreign keys** — Msg 102 (matches real SQL Server's grammar restriction inside `DECLARE @t TABLE`). Multi-variable DECLARE with a table variable, mixed scalar+table DECLARE, and `SET IDENTITY_INSERT @t ON` also reject. Column features (IDENTITY / UNIQUE / inline + table-level CHECK / computed / rowversion) all ship — see [`table-variables.md`](docs/claude/table-variables.md).
- **`CREATE SCHEMA AUTHORIZATION`** — `NotSupportedException` (no principal model on schemas). `DROP SCHEMA` + `ALTER SCHEMA TRANSFER` ship — see [`schemas.md`](docs/claude/schemas.md).
- **`CREATE SCHEMA <schema_element>` greedy form** — simulator dispatches trailing CREATE/GRANT as their own statements rather than as part of the same CREATE SCHEMA. Same end state for the common idiom; mismatched-grammar trailers raise.
- **`CREATE SCHEMA sys` / `INFORMATION_SCHEMA`** + **`CREATE TABLE sys.*` / `INFORMATION_SCHEMA.*`** — both raise Msg 2760, matching real SQL Server's permission-error framing. The schemas exist as catalog-view hosts.
- T-SQL `GOTO` / labels — `IF` / `WHILE` / `BREAK` / `CONTINUE` / `RETURN` ship; unconditional jumps don't.
- **Programmable-object top-level gaps**: CLR functions, logon triggers, INSTEAD OF UPDATE/DELETE on non-updatable views, JOIN-view single-base UPDATE/DELETE, OUTPUT through views, multi-source alias-form UPDATE/DELETE through views (Msg 4405). **Natively-compiled procedures** and **CLR procedures** (`CREATE PROCEDURE … AS EXTERNAL NAME …`) ship at parser-fidelity tier (`WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS …` + `BEGIN ATOMIC` dispatch the natively-compiled body; ATOMIC's transaction boundary falls through to session isolation. CLR-procedure bodies parse silently and `EXEC` is a no-op). See [`programmable.md`](docs/claude/programmable.md).
- **PRINT semantic gaps** — Msg 1046 subquery-in-operand not raised; non-string formatting uses `CoerceTo(varchar(8000))` instead of PRINT-specific style 0; 8000/4000-byte truncation not enforced. `InfoMessage` event surface itself ships (see `SimulatedDbConnection.InfoMessage`); these are the residual fidelity gaps in what each entry carries.
- **`ALTER TABLE` out-of-scope**: DROP PERIOD FOR SYSTEM_TIME, REBUILD, SWITCH PARTITION, `ALTER COLUMN ADD/DROP {PERSISTED|MASKED|ROWGUIDCOL|SPARSE}`, multi-constraint ADD in one statement. `ALTER COLUMN` of an IDENTITY column to a non-integer type raises **Msg 2749** (matches real, probe-confirmed wording); `ALTER COLUMN` of a `GENERATED ALWAYS AS ROW START/END` period column raises **Msg 13599**. Modeled shapes in [`alter-table.md`](docs/claude/alter-table.md).
- **`hierarchyid` / `geography` / `geometry` byte-identical CAST encoding** — currently simulator-native; cross-engine byte transfer deferred. See [`hierarchyid.md`](docs/claude/hierarchyid.md), [`spatial.md`](docs/claude/spatial.md).
- **Query hints gaps**: FROM-source `(unknown)` without alias falls through to Msg 102 (real raises Msg 207/321); `FORCESEEK(name(cols))` nested-form name validation isn't run. Surface in [`query-hints.md`](docs/claude/query-hints.md).

## Quirks (modeled, not byte-identical to SQL Server)

- `CHECKSUM_AGG`: order-independent XOR fold; semantic guarantee matches (same multiset → same checksum), bit pattern won't.
- `APPROX_COUNT_DISTINCT`: implemented as exact `COUNT(DISTINCT)`.
- `decimal` / `numeric`: backed by .NET `decimal`. Values requiring more than 28 significant digits aren't modeled (declarations up through `decimal(38, *)` accepted so storage byte-width matches).
- `float` text formatting: .NET `G15`/`G7` rather than SQL Server's `1e+015`-style scientific.
- Auto-generated constraint names: PK / UNIQUE shape `PK__<table8>__<16hex>` / `UQ__<table8>__<16hex>` (16-hex 64-bit FNV-1a); CK / FK / DF shape `CK__<table8>__[<col8>__]<8hex>` (8-hex 32-bit FNV-1a). Both are deterministic across runs, distinct from SQL Server's object-id-derived hex (so won't byte-match).
- **DELETE / UPDATE leak page space**: deleted (or relocated) row payload bytes stay in their original page until process exit; only the slot is tombstoned. Slot directory entries also never reused.
- **DELETE / UPDATE leak LOB chains**: orphaned LOB chains stay in `Heap.LobPages`. Other rows reference LOB pages by stable index, so list compaction would corrupt them.
- **`GetBytes`/`GetChars` materialize, don't stream**: each call decodes the full column value via `RowDecoder` and slices into the caller's buffer. Behavior matches per-call observation; the streaming-memory guarantee doesn't.
- **`SELECT INTO` string `+` reads as nullable**: real SQL Server projects `cs + 'x'` (both NOT NULL) as NOT NULL; the simulator can't statically distinguish string-concat from integer-add at projection-schema time (the dispatch happens runtime on operand types), so all `Add` results read as nullable. Conservative; no test reliance on string-`+`-non-null.
- **`SELECT INTO` from a CTE drops identity + nullability**: CTE bindings synthesize their wrapper `HeapColumn` entries with `nullable: true` and no identity, so the analyzer treats CTE sources as derived plans. Real SQL Server preserves both through simple single-source CTEs. Fix requires propagating column metadata through CTE bindings.
- **Temp-table DDL is transactional, regular-table DDL isn't**: `CREATE TABLE #foo` / `DROP TABLE #foo` inside `BEGIN TRAN` participate in the undo log (matching real SQL Server); the same statements on a regular table commit immediately regardless of an active transaction. Asymmetric, but no real workload depends on transactional regular-DDL (EF doesn't do schema changes through SaveChanges, and migrations run outside transactions on real SQL Server too).
- **Un-taken IF branches resolve names eagerly**: real SQL Server defers name resolution for un-taken branches, so `IF 1=0 SELECT bad_col FROM bad_table` runs silently. The simulator's parsers do name resolution inline with parsing, so un-taken branches that reference non-existent tables/columns still raise `Msg 208` / `Msg 207`. The common idioms (`IF NOT EXISTS (…) CREATE TABLE foo (…)`, `IF OBJECT_ID('foo','U') IS NOT NULL DROP TABLE foo`, `IF cond INSERT t VALUES (…)` against pre-existing `t`) work end-to-end because referenced names exist when the branch is skipped. State mutations inside the un-taken branch are correctly suppressed (skip-mode gate); the gap is name resolution only.
- **`IF` cond divide-by-zero**: real SQL Server surfaces `IF 1/0 = 0 …` as Msg 8134; the simulator surfaces the raw `DivideByZeroException` from .NET decimal arithmetic. Same pre-existing gap as documented for `TRY_CAST(1/0 AS INT)`.
- **`IF (1) select` paren-wrapped non-boolean cond — slight positional gap**: simulator raises Msg 4145 near `')'`; real SQL Server reports `'select'` (the post-paren token). Wording is correct (Msg 4145, non-boolean type), only the "near 'X'" suffix differs. Same gap applies to any `IF (value-expr) …` shape.
- **`REPLICATE` of a column-typed `varchar(MAX)` / `nvarchar(MAX)` truncates to 8000 bytes**: the simulator's runtime `SqlValue` doesn't carry the varchar / nvarchar declared length — both bounded `varchar(N)` and `varchar(MAX)` collapse to the length-agnostic singleton at the value level. `Replicate` captures the MAX-vs-bounded distinction at parse time via `Expression.GetSqlType`, which works for literal-only or CAST-target inputs (the common shape) but falls back to "treat as bounded" for column references because the parse-time outer-type resolver doesn't reach FROM-source column types. Threading the projection-time resolver through would lift this; EF's REPLICATE emissions don't hit the affected path.
- **`DATALENGTH` returns `int` for MAX-typed inputs**: real SQL Server returns `bigint` for `varchar(MAX)` / `nvarchar(MAX)` and the legacy LOB family. Pre-existing simulator divergence; the result still fits in int for any value the simulator can produce, but the declared column type doesn't widen.
- **Comma-FROM with an explicit JOIN whose ON references a pre-comma source silently succeeds** (e.g. `FROM a, b JOIN c ON c.id = a.id`): real SQL Server binds `b JOIN c ON …` as its own scope and raises Msg 4104 because `a` isn't visible there; the simulator doesn't do parse-time scope-checking on ON predicates (column refs resolve at runtime through `ResolveAcrossTuple` across all FROM sources in the tuple), so the query runs and returns the Cartesian-filtered rowset. The common shapes — basic `FROM a, b WHERE …`, multi-comma chains, comma + derived table, explicit JOIN followed by comma — all match real SQL Server byte-for-byte; only the rare back-reference-across-comma case diverges, and it diverges toward "more permissive" rather than wrong rowset.
- **`CREATE/ALTER PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA` inside a control-flow body raises Msg 111, not Msg 156**: the must-be-first-statement check is enforced at parse-time per probed wording (PROCEDURE merges CREATE/ALTER into one label; the others use separate `CREATE` / `ALTER` labels). Inside `IF` / `WHILE` / `BEGIN…END`, `BatchContext.BlockDepth > 0` triggers Msg 111; real SQL Server's parser surfaces Msg 156 ("Incorrect syntax near 'procedure'") at the same position. Same end state — the statement is rejected — different error code. Inner CommandText-equivalent contexts (procedure / function / trigger / dynamic-SQL bodies) get a fresh `BatchContext` and the flag resets, so a CREATE PROCEDURE as the first statement of a proc body succeeds (real SQL Server also raises Msg 156 here, a related minor divergence; no real application emits nested CREATE PROCEDUREs).
- **`GROUPING(expr)` / `GROUPING_ID(expr)` non-Reference arg always raises Msg 8161**: real SQL Server matches GROUP BY expressions by structural equality, so `GROUPING(a+1)` paired with `GROUP BY a+1` returns 0. The simulator only does leaf-name equality on `Reference` arguments — any wrapped form (arithmetic, function call, CAST) fails the match and raises Msg 8161. Right Msg, wrong row count; no real application emits the non-Reference form.
- **`STRING_SPLIT(..., ..., cast(@v as int))` — wrapped variable accepted**: the simulator's `enable_ordinal` const-only gate rejects bare `@v` (Msg 8748, matching real) but a Cast / Parenthesized wrapper around the variable slips past the gate. Real SQL Server rejects all variable-bearing shapes regardless of wrapping. No real-world emission hits this — the const-only restriction means `@v` ergonomics are weak in any form.
- **`OPENJSON` `WITH ... AS JSON`** on `nvarchar(max)` raises `NotSupportedException` even though real SQL Server accepts the column form there (sub-tree extraction not modeled). Non-`nvarchar(max)` columns with `AS JSON` raise **Msg 13618**, matching real.
- **`FOR SYSTEM_TIME` qualified-name format**: real SQL Server pads temp-table names in Msg 13544 with their internal allocation suffix (`#x____...___…000000000148`); the simulator emits the bare `tempdb.dbo.#x` form. Same Msg number / framing, less verbose name.
