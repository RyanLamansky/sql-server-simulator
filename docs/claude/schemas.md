# Schemas and OBJECT_ID

## Schemas (`CREATE SCHEMA` + schema-qualified resolution)
`CREATE SCHEMA <name>` adds an entry to `Database.Schemas`; subsequent two-part references (`SELECT * FROM audit.t`, `INSERT audit.t VALUES (…)`, every DML / DDL targeting a table) route through it.
Unqualified references fall back to `Database.DefaultSchemaName` (`"dbo"`), which every `Database` ships pre-populated with.
The 9 table-lookup sites (Selection FROM, Insert/Update/Delete/Merge targets, CREATE / DROP / TRUNCATE, SET IDENTITY_INSERT, IDENT_CURRENT, SELECT INTO) all share one parser (`BatchContext.ParseObjectName`) and one resolver pair (`BatchContext.TryResolveTable` for lookup, `BatchContext.TryResolveSchema` for CREATE-shape callsites that need the dict).
Every `Database` ships with three pre-populated schemas at conventional ids: `dbo=1`, `INFORMATION_SCHEMA=3`, `sys=4`.
User schemas allocate ids starting at 5 from `Database.AllocateSchemaId()` (a counter seeded so the next-allocated value is 5).
Probed against SQL Server 2025.

- **Duplicate `CREATE SCHEMA`** (case-insensitive) → **Msg 2714** (`"There is already an object named '<n>' in the database."` — same factory as duplicate CREATE TABLE; SQL Server shares the namespace).
- **Reserved schema names** (`dbo`, `sys`, `INFORMATION_SCHEMA`) → **Msg 2760** (`"The specified schema name \"<n>\" either does not exist or you do not have permission to use it."`).
  Wording is quirky for a CREATE (says "does not exist"), but probe-confirmed verbatim — real SQL Server resolves the principal first and these schemas tie to system principals.
- **Three-part `db.schema.t`** routes the db segment through `Simulation.Databases` (case-insensitive).
  Missing database surfaces as Msg 208 / 3701 / 4701 per callsite (same as a missing table); existing-but-other-database resolves cross-DB for reads (SELECT / JOIN / catalog views) and for writes (see [Cross-database writes](#cross-database-writes) below).
  **Four-part `server.db.schema.t`** always returns false (linked-server names aren't modeled — real SQL Server raises Msg 7202 for unknown server; the simulator surfaces Msg 208 instead).
  Empty middle segment (`db..table`) is substituted with `dbo` at parse time so `db..table` resolves identically to `db.dbo.table` — real SQL Server uses the login's default schema; the simulator has no per-login schema and routes everything through `dbo`.
  This makes cross-database short-form queries (`SELECT * FROM sales..Customer`) land in the correct database; temp-table forms (`tempdb..#foo`) still work because `TryResolveTable`'s `IsLocalTempName` check operates on the leaf regardless of qualifier.
- **`CREATE TABLE schema.t`** where `schema` doesn't exist → **Msg 2760** (target schema for the create must already exist).
  Distinct from FROM / INSERT / UPDATE / DELETE / MERGE / DROP / TRUNCATE access which use 208 / 3701 / 4701 respectively.
- **`AUTHORIZATION owner`** and the embedded `<schema_element>` list (CREATE TABLE / VIEW / GRANT nested inside CREATE SCHEMA) aren't modeled — `AUTHORIZATION` raises `NotSupportedException`; trailing statement-starting tokens (CREATE / SELECT / INSERT / etc.) parse as their own statements in the same batch (deviates from real SQL Server's strict greedy-consume but reaches the same end state for the common idiom).
- **"First in batch" is enforced** — `CREATE SCHEMA` after any dispatched statement raises **Msg 111 state 14** (`'CREATE SCHEMA' must be the first statement in a query batch.`), matching real; with no `GO`, a batch is one `CommandText`.
- **`sys` and `INFORMATION_SCHEMA` host catalog views** (sys.schemas / sys.tables / sys.objects / sys.columns / INFORMATION_SCHEMA.TABLES / .COLUMNS / .SCHEMATA — see [`catalog-views.md`](catalog-views.md)).
  Adding a user table via `CREATE TABLE sys.foo (…)` raises `NotSupportedException` ("Cannot CREATE TABLE in the built-in 'sys' schema"); same rejection for `INFORMATION_SCHEMA`.
  Both `Schema` entries exist in `Database.Schemas` to carry their conventional ids and to be reachable from `sys.schemas`, but their `HeapTables` dicts stay empty — catalog views live in a separate `Simulation.CatalogViews` registry.
- **Error wording**: Msg 208 wraps the qualified name in single quotes (`Invalid object name 'badschema.t'.`); Msg 3701 (DROP) does the same; Msg 4701 (TRUNCATE) carries only the leaf (probe-confirmed asymmetric — distinct error path).

## `USE <db>`
`USE <name>` (bare or bracketed) switches the connection's `CurrentDatabase` to the named database.
Routes through `Simulation.Use.cs` / `ParseUseStatement`.
**Not transactional** (probe-confirmed: `BEGIN TRAN; USE other; ROLLBACK` leaves the session pointed at `other`).
Skip-mode (inside an un-taken `IF` / `WHILE`) suppresses the switch.

- **Missing database** → **Msg 911** (`"Database '<n>' does not exist. Make sure that the name is entered correctly."` — Class 16 State 1, probe-confirmed verbatim against SQL Server 2025).
  The dispatch loop's mid-batch exception abort prevents subsequent statements from running, matching real-server behavior.
- **`USE @var`** / **`USE (paren)`** → **Msg 102** via `GetNextRequired<Name>()`'s type-mismatch (probe-confirmed: real SQL Server rejects both).
- **Switching is one of two ways to mutate another database** — after `USE [other]`, an unqualified `INSERT t VALUES (…)` writes to `other.dbo.t`; the three-part name reaches it without the switch.

## Cross-database writes

`INSERT` / `UPDATE` / `DELETE` / `MERGE` / `SELECT … INTO` and the table DDL (`CREATE` / `ALTER` / `DROP` / `TRUNCATE TABLE`) all route through a three-part name to the named database — as does a write through a synonym whose base is three-part, and the `db..t` short form.
The session's own database is unaffected: `DB_NAME()` in the mutating statement still reads the session's, `@@ROWCOUNT` and `SCOPE_IDENTITY()` / `@@IDENTITY` flow back to the caller, and one transaction spans both databases (`ROLLBACK` and `ROLLBACK TRAN <savepoint>` undo the other database's write, since undo entries reference their `Heap` directly).
Every shape here is probe-confirmed against SQL Server 2025.

What follows the *target* rather than the session, all keyed off `HeapTable.OwningDatabase` (stamped when the table enters a `Schema.HeapTables` dict) via `BatchContext.DatabaseFor`:

- **The rowversion counter.**
  A cross-database INSERT / UPDATE of a `rowversion` column advances the target's `@@DBTS` and leaves the session's where it was — probed both directions.
  Rollback doesn't give the stamp back, matching the identity / rowversion log-bypass rule ([`transactions.md`](transactions.md)).
- **Trigger dispatch.**
  Matching triggers are found in the target's schemas, and each body runs with the connection's current database switched to the target's for its duration (restored in a `finally`, invisible to the firing batch).
  So `DB_NAME()` inside the body reads the target — probe-confirmed — and the body's unqualified writes land there; reaching back to the firing session's database is itself a three-part write.
- **Object-id allocation** for a table created by a three-part `CREATE TABLE` / `SELECT … INTO`, so its `sys.tables` row carries an id from the database it lives in.
- **The version store.**
  Capture is gated on the target's `ALLOW_SNAPSHOT_ISOLATION` / `READ_COMMITTED_SNAPSHOT`, and so is a *reader's* versioning: whether a read is versioned at all follows the table's own database, so a session in a non-RCSI database reading a three-part name into an RCSI one reads versioned and the reverse blocks (probe-confirmed both directions), and a SNAPSHOT session's Msg 3952 names the target.
  The commit-id sequence itself is **instance-wide** rather than per database, mirroring real's server-scoped transaction sequence number — see [`locking.md`](locking.md#commit-xid-allocator).
- **Permission resolution.**
  A reference through a three-part name is checked against the *login's user in the target database* — see [`permissions.md`](permissions.md#cross-database-references).

Constraint enforcement, identity allocation, indexes and the seek cache all hang off the `HeapTable` and need no routing.
Locks likewise: the `LockManager` is per-`Simulation` and its resources hang off the table, so a session holding locks in two databases at once already worked.

**Not modeled yet**

- **Four-part writes to a linked server** stay rejected by `BatchContext.RejectCrossServerMutation` — the remote's lock manager and undo log are its own, and that's the [`linked-servers.md`](linked-servers.md) gap, not this one.
- **The database name in Msg 515 / 547 constraint messages** is still the literal `Simulation.DefaultDatabaseName`, so a violation in another database names `simulated` where real names the target (a pre-existing hardcode, unrelated to which database the write came from).
- **`CREATE VIEW` / `PROCEDURE` / `FUNCTION` / `TRIGGER` with a db prefix** — real raises Msg 166 (`does not allow specifying the database name as a prefix`); the simulator doesn't enforce that yet.

## DROP SCHEMA
`DROP SCHEMA [IF EXISTS] <name>` removes an entry from `Database.Schemas`.
Routes through `Simulation.Drop.cs` alongside DROP TABLE / VIEW / FUNCTION / PROCEDURE / SEQUENCE / TRIGGER / TYPE — the dispatcher's switch on `DropTargetKind` adds a `Schema` arm.

- **Reserved schemas** (`dbo`, `sys`, `INFORMATION_SCHEMA`) → **Msg 15150** (`"Cannot drop the schema '<n>'."`) — probe-confirmed.
  Real SQL Server rejects these even when empty; `dbo` is also unconditionally rejected.
  Reuses `IsReservedSchemaName` from CreateSchema.
- **Missing schema, no `IF EXISTS`** → **Msg 15151** (`"Cannot drop the schema '<n>', because it does not exist or you do not have permission."`).
  With `IF EXISTS`, the miss is silent (same shape as the temp-table / function / procedure variants).
- **Non-empty schema** → **Msg 3729** (`"Cannot drop schema '<n>' because it is being referenced by object '<obj>'."`).
  Real SQL Server names the first dependent object the engine encounters in its dependency walk — often a PK / UNIQUE / CHECK constraint name rather than the table itself.
  The simulator's `FirstSchemaResident` walks `Schema.SchemaObjects()` first (heap tables / views / functions / procedures / sequences / triggers) and falls through to `Schema.TableTypes` (which occupies the separate type namespace); it names the table (or first-found object) rather than a constraint, since auto-named constraints aren't tracked as standalone `SchemaObject`s.
  Same Msg / wording prefix as the probe; the specific object-name suffix is a minor fidelity gap.

## Synonyms (`CREATE SYNONYM` / `DROP SYNONYM`)

`CREATE SYNONYM [schema.]name FOR base_object` (`Simulation.Synonym.cs`) stores a name indirection in the schema's `Synonyms` dict (a `Synonym` = a `SchemaObject` carrying the base `MultiPartName` as written).
`SYNONYM` isn't a reserved keyword — the CREATE / DROP dispatchers match it as an identifier (`Name … Equals("SYNONYM")`), like `CREATE SERVER ROLE`.
The base object is **not** resolved at creation: real binds it lazily, so a synonym over a missing — or cross-database, or not-yet-created — base creates successfully and fails at first use.

### Resolution

`BatchContext.TryRedirectThroughSynonym` is the shared redirect step behind `TryResolveTable` / `TryResolveView` / `TryResolveFunction`; each recurses on the base name so a schema-qualified (or 3-part cross-database) base routes.
`ExpandSynonym` rewrites a name to its base ahead of resolution at the two EXEC entry points, and `RejectCrossServerMutation` expands before testing the server segment so a write through a synonym is gated on the base's server, not the synonym's.
Reference sites that ship, all probe-confirmed against real: FROM-source table / view, INSERT / UPDATE / DELETE / MERGE targets, `EXEC syn` (procedure base), `SELECT dbo.syn(1)` (scalar-function base), `FROM dbo.syn(1)` (TVF base), and cross-database bases for both reads and writes.

- **Missing base at first use** → **Msg 5313** ("Synonym '<n>' refers to an invalid object."), the name rendered as written at the use site.
  State 1 when the base names nothing, State 224 when it names an object the reference can't use (a procedure or sequence in a FROM clause).
  Raised from `BatchContext.UnresolvableObjectName`, which the FROM and DML sites throw in place of the plain Msg 208.
  The EXEC path is the exception: it expands the synonym first, so a missing base is **Msg 2812** naming the *base* (real's wording — the synonym name never appears).
- **Synonym chain** (a base that is itself a synonym) → **Msg 470** at first use, not at creation: `The synonym "<outer>" referenced synonym "<inner>". Synonym chaining is not allowed.`
- **`NEXT VALUE FOR syn`** where the base is a sequence → **Msg 11726** ("Object '<n>' is not a sequence object.").
  Real refuses the indirection here even though `CREATE SYNONYM … FOR <sequence>` succeeds; the simulator matches.

### Catalog surface

Synonyms enroll in `Schema.SchemaObjects()`, so they take an `ObjectId` / `CreateDate` / `ModifyDate` and project everywhere that walk feeds: `sys.objects` / `sys.all_objects` / `sysobjects` (type `SN`, type_desc `SYNONYM`, `parent_object_id` 0), `OBJECT_NAME` / `OBJECT_SCHEMA_NAME`, `GRANT`'s securable resolution, and `DROP SCHEMA`'s non-empty check.
`sys.synonyms` projects the 13-column real shape (`BuiltInResources.CoreObjects.cs`), with `principal_id` NULL, the three `is_*` flags 0, and `base_object_name` the bracket-quoted base **as written** — `[t]` / `[dbo].[t]` / `[otherdb].[dbo].[t]` for the 1-, 2-, and 3-part forms.

- **`OBJECT_ID('syn')`** returns the synonym's own id and never follows the base: the `'SN'` filter matches, every other filter (`'U'`, `'P'`, …) is NULL — probe-confirmed.
- **`OBJECTPROPERTYEX(id, 'BaseType')`** reports the *base object's* type code (`'U '` / `'P '` / `'FN'`), NULL when the base doesn't resolve.
  `OBJECTPROPERTY(id, 'IsSynonym')` is NULL on real (not a property name) and stays NULL here; `IsTable` / `IsView` report 0.

### Name collisions and DROP

- **Either direction collides** → **Msg 2714** (`ThereIsAlreadyAnObject`): a `CREATE SYNONYM` over any existing object, and a `CREATE TABLE` / `CREATE VIEW` / `CREATE PROCEDURE` / `CREATE SEQUENCE` / `SELECT … INTO` over an existing synonym.
  Both directions run through `Schema.HasNameInSharedNamespace`.
  (Real varies the State per statement — 8 for CREATE SYNONYM / SEQUENCE, 6 for CREATE TABLE / SELECT INTO, 3 for CREATE VIEW / PROCEDURE — and qualifies the name in some of them; the simulator's single factory reports State 6 with the leaf name.)
- **DROP of the wrong kind** → **Msg 3705** (`CannotUseDropWithObjectKind`): `DROP TABLE syn` says "because 'syn' is a synonym. Use DROP SYNONYM.", `DROP SYNONYM t` says "is a table. Use DROP TABLE."
  The check is kind-general (probe-confirmed wording per kind: table / view / procedure / function / table valued function / sequence / trigger / synonym), so `DROP TABLE` over a view or sequence raises it too.
  `IF EXISTS` doesn't suppress it — the object exists, it's just the wrong kind.
- **DROP SYNONYM [IF EXISTS] name** removes the entry; a missing target without `IF EXISTS` → **Msg 3701 State 5** (`CannotDropSynonymDoesNotExist`, "Cannot drop the synonym '<n>', because it does not exist or you do not have permission.").

### Permissions

A synonym is a grantable securable in its own right: `GRANT SELECT ON syn` and `GRANT EXECUTE ON syn` both land (real accepts either family, since the base's kind isn't consulted at grant time), and the grant records against the synonym's own `object_id`.

**It is also the securable that gets checked.**
A reference written through a synonym is enforced against the synonym and never walks through to the base — a grant on the base alone does not admit it (the Msg 229 names the synonym), and a DENY on the base does not block it.
The redirect carries that provenance: `FromSource.ViaSynonym` for query sources, the written `MultiPartName` for DML / EXEC targets, both funnelling through `PermissionEnforcement.SecurableFor`.
A synonym takes no column list at all (Msg 1020), so every check through one is object-grain → [`permissions.md`](permissions.md#reference-provenance-synonyms).

### Divergences

- **A grant on the synonym isn't honored at use.** The DML / FROM permission gate checks the resolved *base* object, so a principal holding `SELECT` on the synonym but not on the base gets Msg 229 where real reads through.
  Closing it means carrying resolution provenance out of the redirect (`TryResolveTable` returns the base table with no record of the synonym it came through).
- `base_object_name` expands an omitted middle segment: `FOR tempdb..t` stores `[tempdb].[dbo].[t]` where real keeps `[tempdb]..[t]`, because `MultiPartName` compresses empty segments at parse.
- Msg 2714's State / name qualification per statement kind, as above.

## ALTER SCHEMA TRANSFER
`ALTER SCHEMA <dest> TRANSFER [(OBJECT|TYPE)::] <source>.<obj>` moves a single object from one schema to another.
Routes through `Simulation.Alter.cs`'s `TryParseAlterSchemaTransfer`.
The `Object` and `Type` class prefixes parse via two adjacent `:` operators (the tokenizer accepts `:` as a single-char operator, so the `::` separator decomposes into two tokens for the prefix grammar).
Default class is `OBJECT` (the bare form with no prefix).

- **OBJECT class** walks the shared object-name namespace dicts on the source `Schema`: `HeapTables` → `Views` → `Functions` → `Procedures` → `Sequences` → `Synonyms`, first-hit wins.
  A transferred synonym keeps its stored base name verbatim (probe-confirmed: `base_object_name` still reads `[dbo].[t]` after the move).
  Triggers fail-fast with **Msg 15347** (`"Cannot transfer an object that is owned by a parent object."`) — triggers belong to their parent's schema and follow the parent automatically.
  Found-object collision check uses `Schema.HasNameInSharedNamespace` against the destination; collision → **Msg 15530** (`"The object with name \"<n>\" already exists."`).
  On success, the object's `SchemaId` updates to the destination's id, its `Schema` reference reseats (every concrete `SchemaObject` derivative except `HeapTable` carries a per-instance `Schema` field), and any attached DML triggers (matching `Trigger.Parent` to the moved table / view) co-migrate into the destination's `Triggers` dict via `ReseatAttachedTriggers`.
- **TYPE class** targets the parallel `Schema.TableTypes` dict (user-defined table types occupy a separate namespace).
  Same collision / found semantics, distinct factory wording (`CannotFindType` / `ObjectAlreadyExistsInDestination`).
- **Missing destination schema** → **Msg 15151** alter-schema variant (`"Cannot alter the schema '<n>', …"`).
- **Missing source object** → **Msg 15151** find-object variant (`"Cannot find the object '<leaf>', …"`); the qualifier doesn't echo into the message (probe-confirmed).
- **Same-schema transfer** (source schema = destination schema) is a silent no-op — probe-confirmed against real SQL Server.
- **An object a `WITH SCHEMABINDING` module references** → **Msg 15348** (`"Cannot transfer a schemabound object."` — real's unspaced "schemabound", matched verbatim).
  Only the referenced side is pinned: transferring the schema-bound module itself succeeds (probe-confirmed).
  See [`programmable.md`](programmable.md#schema-binding-with-schemabinding).
- **SchemaId mutability**: `SchemaObject.SchemaId` is a settable `int` (not `readonly`) so the TRANSFER path can reseat objects.
  Every derived type with a separate `Schema` field (`View`, `Procedure`, `UserDefinedFunction`, `Sequence`, `TableType`, `Trigger`) likewise dropped `readonly`.
  Apps that read `SchemaId` between transfers see the updated value.
- **Sch-M acquisition**: every actual move (same-schema fast-path excluded) calls `batch.AcquireStatementLock(obj.SchemaLock, LockMode.SchemaModification)` on the moving object before mutating the source / destination dicts.
  Statement-scoped — matches the idiom every other DDL site uses (`CREATE` / `ALTER` / `DROP` / `TRUNCATE`).
  Same-schema transfers skip the lock acquisition along with the mutation.

**Deferred**: `ALTER SCHEMA … TRANSFER` with the `XML SCHEMA COLLECTION::` / `PARTITION FUNCTION::` / other niche class prefixes (the simulator only models OBJECT and TYPE); `ALTER AUTHORIZATION` (no principal model); `DROP SCHEMA` cascade-mode (real SQL Server's ANSI extension; not in the standard T-SQL grammar).

## Object identifiers + `OBJECT_ID()`
Every `HeapTable` carries a stable per-database `int ObjectId` assigned at CREATE time from `Database.AllocateObjectId()` (a `Database`-scoped `Interlocked.Increment` counter seeded at 100).
DROP-then-recreate yields a fresh id, matching real SQL Server (probe-confirmed — counter never reuses values).
The counter bypasses transaction rollback: a rolled-back CREATE TABLE still consumed an id, matching the identity-counter rule.
System tables (`SystemHeapTables`) carry a sentinel `ObjectId = -1` — they're process-shared, sit outside per-DB id space, and aren't reachable through `OBJECT_ID()` anyway.
Backs `OBJECT_ID()` plus `sys.tables` / `sys.objects` / `sys.columns.object_id`.

**`OBJECT_ID(name [, type])`** scalar (`Parser/Expressions/ObjectId.cs`): returns the `int` ObjectId of the named object, or NULL when not found / wrong type / malformed name.
The name is a runtime string parsed as a 1–3-part dotted identifier with bracket-quoting (`'[dbo].[foo]'`, `'dbo.foo'`, `'simulated.dbo.foo'` all resolve identically); 4-segment names return NULL (linked-server form unmodeled).
The type filter is case-insensitive but whitespace-sensitive — `'U'` and `'u'` match user tables; `' U '`, `'XX'`, `''` all → NULL; other documented codes (`V`/`P`/`F`/`FN`/...) → NULL until those features land.
A NULL on any argument propagates NULL.

- **Runtime-evaluated arguments**: `DECLARE @n nvarchar(100) = 'foo'; SELECT OBJECT_ID(@n)` works — both args are full `Expression`s.
- **Unqualified function names resolve against the default schema.** `BatchContext.TryResolveFunction` takes 2-/3-part names only, because a bare `f()` at a *call* site is Msg 195 on real; `OBJECT_ID` is a name lookup rather than a call, and real returns the id for `OBJECT_ID('f')`, so the 1-part form is qualified with `dbo` before the resolver is asked.
  Tables, procedures and views already resolved unqualified — this was specific to the function namespace, and it silently broke the common `OBJECTPROPERTY(OBJECT_ID('f'), …)` idiom, which reported NULL rather than the property.
- **Temp-table divergence**: `OBJECT_ID('#foo')` resolves the session's `#foo` directly because `BatchContext.TryResolveTable` routes `#` leaves to the connection's temp dict regardless of qualifier.
  Real SQL Server requires the explicit `tempdb..#foo` three-part form (since unqualified resolution targets the current DB, not tempdb).
  The simulator's existing temp-routing simplification carries through; `OBJECT_ID('tempdb..#foo')` also works (probe-confirmed real behavior).
- **Bracket-handling fidelity gap**: the runtime-string name parser strips bracket pairs at segment level (`'[dbo].[foo]'` → `dbo`+`foo`) and decodes `]]` → `]` inside brackets — but bracketed segments containing a literal `.` (`'[a.b].[c]'`, the literal-dot case) don't parse correctly (split on `.` happens before bracket-aware tokenization).
  Rare in practice; revisit if a real app hits it.
- **Arity**: too-few-args (`OBJECT_ID()`) currently surfaces as Msg 102 (the inner Parse failure path) rather than Msg 174 — same pattern as other built-ins; the simulator doesn't enforce min-args.
  Too-many-args raises Msg 174 verbatim.

## The system databases + database ids

Every `Simulation` seeds all four SQL Server system databases — `master`, `tempdb`, `model`, `msdb` — at construction (before any `ImportBacpac` or `CreateDbConnection`), so `USE <systemdb>` (rather than Msg 911), three-part `master.sys.*` reads, `master.dbo.<proc>` calls (e.g. `xp_msver`), and SSMS's connect-time `has_dbaccess` / `msdb.dbo.*` probes all resolve without an import.
All four are excluded from the initial-database fallback in `SimulatedDbConnection.ResolveInitialDatabase` (via the `Simulation.SystemDatabaseNames` set), so a fresh connection with no user database still lazily seeds and lands on `simulated`; a fresh connection with imported databases picks the alphabetically-first *user* database.
The `#temp` routing is unaffected — `#foo` lives in the connection's `TempTables`, never in the seeded `tempdb` `Database`.

Database ids: the four system databases carry a **fixed reserved map (master = 1, tempdb = 2, model = 3, msdb = 4)**; every user database carries a **stored `Database.Id`** assigned at registration — the smallest free id ≥ 5, so a dropped id is reused before a higher one is minted (matching real SQL Server; probed dropped-id-12 → next-create reclaims 12).
Every user-database entry point routes through `Simulation.RegisterUserDatabase` / `RegisterUserDatabaseLocked` (the id allocator): `CREATE DATABASE`, `Simulation.ImportBacpac`, and the lazy `simulated` seed in `ResolveInitialDatabase`.
The single sources of truth are `Simulation.SystemDatabaseIds` (the system name↔id map + seed list) and `DbId.DatabasesWithIds(simulation)` (which projects `(db, db.Id)` ordered by id), consumed by `DB_ID` / `DB_NAME`, `OBJECT_NAME(id, db_id)`, the `sys.databases.database_id` column, and `DBCC SHRINKDATABASE`'s numeric-id form — keep them consistent by routing every id lookup through `DatabasesWithIds`.

## `CREATE DATABASE` / `DROP DATABASE`

`CREATE DATABASE <name> [COLLATE <collation>] [<file / option clauses>]` (`Simulation.CreateDatabase.cs`): the name is a single identifier (bare or bracketed, so `[app b-2]` with spaces works); `COLLATE` sets the new database's collation (default = the server collation, mirroring `model.collation`); every remaining clause (`ON (…)`, `LOG ON (…)`, `WITH …`, `CONTAINMENT = …`, `FOR ATTACH`, …) is parse-and-discarded — no physical-file model.
A duplicate name raises **Msg 1801**.
The database registers with the smallest-free id (above) and is immediately usable via `USE`.

`DROP DATABASE [IF EXISTS] <name> [, …]` (`Simulation.Drop.cs`): a system database raises **Msg 3708**; the **executing** session sitting in the target (`USE foo; DROP DATABASE foo`) raises **Msg 3702**; a missing database raises **Msg 3701** unless `IF EXISTS` (then a silent no-op).
Removing the database frees its id for reuse.
Divergence from real: real also blocks *other* active sessions on the target (Msg 3702), but the teardown idiom apps run first — `ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK IMMEDIATE` (parse-and-discarded, [`database-options.md`](database-options.md)) — evicts those on a real server; the simulator has no eviction model, so it treats other sessions as already evicted and blocks only the executing one, matching the idiom's intent.
This is what lets an ORM's unmodified test runner (Django/mssql-django) create → migrate → run → drop its `test_*` database against the simulator with no configuration override.

**`msdb.dbo.syspolicy_system_health_state`** is seeded as an empty object (six columns: `health_state_id bigint`, `policy_id int`, `last_run_date datetime`, `target_query_expression_with_id nvarchar(400)`, `target_query_expression nvarchar(max)`, `result bit`, probe-confirmed) so SSMS's server-level Policy Health feature — which reads `has_dbaccess('msdb')` and then `select … from msdb.dbo.syspolicy_system_health_state` at connect — renders cleanly instead of raising a permission error.
It's modeled as a real **VIEW** (`sys.objects.type_desc` = `VIEW`, matching the reference) whose body is a `WHERE 1 = 0` filter yielding zero rows; it's constructed directly on msdb's `dbo` schema at Simulation construction (no `CREATE VIEW` DDL, so no connection is materialized and `simulated` isn't seeded prematurely), and exists only in msdb.

Two sibling msdb Policy objects are seeded the same way (directly on msdb's `dbo` schema at construction, so no connection is materialized), because SSMS's Object-Explorer database-node preamble reads all three on the enumeration connection — a Msg 195/208 on any of them aborted the tree build before the Databases folder enumerated (it showed empty with no surfaced error):

- **`msdb.dbo.syspolicy_configuration`** — a **VIEW** projecting four probe-confirmed rows: `Enabled` = `1`, `HistoryRetentionInDays` = `0`, `LogOnSuccess` = `0`, `PurgeHistoryJobGuid` = a binary GUID.
  Two columns: `name` + `current_value`.
  SSMS's PolicyStore setup reads `(SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE name = '…')` for the three integer rows and casts each to `bit` / `int`.
  On the real server `current_value` is `sql_variant` (int base for the three named rows, `binary` for the GUID); the simulator doesn't model sql_variant, and a single column can't hold both an int and a binary GUID, so **`current_value` is surfaced as `nvarchar`** — the integer rows stay CAST-compatible with the `bit` / `int` targets SSMS applies, and the GUID row (never cast by SSMS) carries the hex text.
  Values copied verbatim from the reference.
- **`msdb.dbo.fn_syspolicy_is_automation_enabled()`** — a scalar **FUNCTION** returning `bit` `1` (probe-confirmed; consistent with `syspolicy_configuration`'s `Enabled = 1`).
  Constructed directly as a `ScalarFunction` with body `return cast(1 as bit)`.
  Callable three-part as `msdb.dbo.fn_syspolicy_is_automation_enabled()` from any current database (function resolution routes 3-part names cross-DB).
  SSMS's PolicyHealth query is `case when 1 = msdb.dbo.fn_syspolicy_is_automation_enabled() and exists (select * from msdb.dbo.syspolicy_system_health_state where target_query_expression_with_id like 'Server%') then 1 else 0 end`; since the health-state view is empty the result is `0` regardless, but the function must resolve without error.

## Database name / id scalars

**`DB_ID([name])`** / **`DB_NAME([id])`** (`Parser/Expressions/DatabaseScalarFunctions.cs`): round-trip the connection's view of `Simulation.Databases` by name and id via `DbId.DatabasesWithIds` (master = 1, tempdb = 2, model = 3, msdb = 4, user databases from 5).
Zero-arg `DB_ID()` returns the current database's id, zero-arg `DB_NAME()` returns its name.
Unknown name / out-of-range id → NULL.
NULL arg → NULL.
Result types: `DB_ID` → `smallint`; `DB_NAME` → `sysname`.

**`HAS_DBACCESS('name')`** (same file): int — **accessibility-aware, not existence-based**.
`1` for an accessible hosted database (master / tempdb / msdb and every user database — the simulator has no per-login access model, so hosted ⇒ accessible), `0` for `model` (the restricted template database, inaccessible even to a normal login — probe-confirmed), NULL for unknown / empty / NULL names (case-insensitive lookup; missing argument → Msg 174).
So `model` is seeded and resolves through `DB_ID` / `sys.databases` yet `has_dbaccess('model')` reports `0` — the "exists but inaccessible" split.
SSMS calls `has_dbaccess('msdb')` at connect to gate its Policy Health / Agent features; the seeded msdb answers `1` and the feature renders.
`DB_ID` resolves all four system databases; `has_dbaccess` reflects accessibility.

## Three-part-name reach for metadata scalars

Probe-confirmed against SQL Server 2025:
- **`OBJECT_ID('db.schema.tbl')`** — the name argument's 3-part form routes to the named database through `TryResolveSchema → Simulation.Databases`.
  Bracketed (`'[db].[schema].[tbl]'`) and `db..tbl` shorthand (substituting `dbo` for the empty middle) both work; missing database returns NULL silently.
- **`OBJECT_NAME(id, db_id)`** — second arg routes by id (see the OBJECT_NAME entry above).
- **`SCHEMA_ID(name)` / `SCHEMA_NAME(id)`** are strictly leaf-name / current-DB-scoped — real SQL Server returns NULL for any multi-part input.
  No 3-part-name reach exists.
- Built-in scalars **cannot** be invoked through a 2- or 3-part call-site name (`claude.sys.OBJECT_ID('...')` raises "cannot find user-defined function" on real SQL Server).
  The simulator inherits this naturally from its 1-part-only built-in dispatch.

## Id → name scalars

**`SCHEMA_NAME([id])`** (`Parser/Expressions/SchemaName.cs`): the `int → name` inverse of `SCHEMA_ID`.
With an int `schema_id` argument, walks `Database.Schemas.Values` for the matching `Schema.SchemaId` and returns its `Name`.
No-arg returns `Database.DefaultSchemaName` (`"dbo"`) — matches real SQL Server's "default schema for the current user" behavior (single-principal simulator).
NULL arg / missing id / negative id → NULL.
Result type: `sysname` (nvarchar(128)).

**`OBJECT_NAME(object_id [, database_id])`** (`Parser/Expressions/ObjectName.cs`): walks every `Schema.SchemaObjects()` (the shared object-name namespace: heap tables / views / functions / procedures / sequences / triggers) plus `Schema.TableTypes` and returns the matching object's leaf `Name`.
The optional `database_id` argument is **load-bearing** (probe-confirmed against SQL Server 2025): without it, the walk is scoped to `BatchContext.CurrentDatabase`; with it, the walk is scoped to the database at that id under `DbId.DatabasesWithIds` (master = 1, user databases from 5 — same scheme as `DB_ID` / `DB_NAME`).
Different DBs allocate their own `object_id` namespaces, so the second arg disambiguates id collisions across databases.
NULL `object_id` / missing id → NULL; NULL `database_id` / out-of-range `database_id` → NULL.
Result type: `sysname`.

**`OBJECT_SCHEMA_NAME(object_id [, database_id])`** (`Parser/Expressions/ObjectSchemaName.cs`): same lookup walk as `OBJECT_NAME`; returns the owning `Schema.Name` instead of the object leaf.
Same NULL / ignored-db_id semantics.
Result type: `sysname`.

- **TableType / sys.objects gap**: `OBJECT_NAME` and `OBJECT_SCHEMA_NAME` both resolve table-type objects through `Schema.TableTypes` directly, so `SELECT OBJECT_NAME(type_table_object_id) FROM sys.table_types` works.
  `sys.objects` itself doesn't currently surface TT-rows (separate namespace per the `SchemaObjects()` enumerator's design), so the same lookup via `sys.objects WHERE type = 'TT'` returns empty.
  Closing the gap is a `BuiltInResources` change, not a scalar change.
