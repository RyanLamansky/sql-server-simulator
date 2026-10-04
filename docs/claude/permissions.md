# Permissions: identity, enforcement, and the writer surface

`GRANT` / `REVOKE` / `DENY` plus the principal DDL surface (`CREATE USER` / `CREATE ROLE` / `ALTER ROLE` / `DROP USER` / `DROP ROLE`, and the server-scope `CREATE LOGIN` / `ALTER LOGIN` / `DROP LOGIN`) + catalog views.
**Permissions are enforced**: a non-dbo session's SELECT / INSERT / UPDATE / DELETE / EXECUTE and every modeled CREATE / ALTER / DROP statement are checked at execution time against its effective principal, with role closure, fixed roles, DENY-beats-GRANT, covering permissions, and ownership chaining.
**Session identity is real**: a per-connection principal (original login + database user + impersonation stack) drives the identity scalars, `EXECUTE AS` / `REVERT`, module `WITH EXECUTE AS`, connection-string / TDS authentication, and the per-database identity a cross-database reference or a `USE` resolves through.
A session that never authenticates and never runs `EXECUTE AS` is the `sa` login — what `SYSTEM_USER` / `SUSER_SNAME()` / `ORIGINAL_LOGIN()` report, and a `sysadmin` to `IS_SRVROLEMEMBER`, as a default real connection is — mapped to `dbo` in every database, and **dbo bypasses every check** — the enforcement layer short-circuits on `SessionSecurityContext.EffectiveIsDbo` before any allocation, so existing (dbo) consumers see byte-identical behavior.
(The one `dbo` that doesn't bypass everything is a database-scoped frame — `EXECUTE AS USER = 'dbo'`, or a module's `WITH EXECUTE AS OWNER` / `SELF` that resolves to `dbo` — whose privilege stops at the database boundary — see [Cross-database references](#cross-database-references).)
Logins are enforced as connection credentials at both front doors (TDS endpoint — see [`tds-endpoint.md`](tds-endpoint.md) — and in-process `User ID=` connection strings), and the server permissions they hold gate the server-scope statements and reach into every database — see [Server permissions and the fixed server roles](#server-permissions-and-the-fixed-server-roles).

## Storage

**`DatabasePrincipal`** (`src/SqlServerSimulator/DatabasePrincipal.cs`) carries:
- `principal_id` (int)
- `name` (string)
- `type_code` (char): `S` = SQL_USER, `R` = DATABASE_ROLE
- `type_desc` (string): `SQL_USER` / `DATABASE_ROLE`
- `is_fixed_role` (bool)
- `create_date` / `modify_date`
- `LoginName` (string?) — the mapped server login from `CREATE USER … FOR LOGIN` (null otherwise); drives login → database-user resolution at connect.
- `SecurityIdentifierString` (string?) — the deterministic `S-1-9-3-…` SID a `CREATE USER … WITHOUT LOGIN` user reports through `SYSTEM_USER` / Msg 916 (FNV-derived from the name).
- `EffectiveLoginIdentity` — the `SYSTEM_USER` value while impersonating this user (login ?? SID ?? name).
- `DefaultSchemaName` (string?) — an **application role's** declared `DEFAULT_SCHEMA` (`dbo` unless it said otherwise); null for every other principal, which the catalog view then fills in per real's own rules (see below).
- `PasswordHash` (byte[]?) — an application role's password, in the same legacy `0x0200` single-pass format `ServerLogin` uses (never persisted, so PBKDF2 hardening would only bill activation).

**`DatabasePermission`** (`src/SqlServerSimulator/DatabasePermission.cs`) carries class + major_id + minor_id + grantee/grantor ids + a `Permission` enum + a `PermissionState` enum (Grant / GrantWithGrantOption / Deny / Revoke, projecting the `G`/`W`/`D`/`R` state codes).
Canonical rows draw their `permission_name` and 4-char `type` code from `PermissionCatalog` at projection; off-catalog names (`Permission.Other`) carry their raw text on `PermissionName` and are never matched by a permission check.
`PermissionChecker` compares the enum throughout (closure walk, DENY precedence, covering/scope walk, read/write/DDL fixed-role virtual grants) — no permission-name string comparison remains on any check path; `HAS_PERMS_BY_NAME` / GRANT parsing resolve the incoming name to the enum once at the boundary via `Permission.Resolve` (a zero-alloc span switch, a `PermissionCatalog` static extension member).
The catalog surfaces per-enum lookups as extension members (`permission.CanonicalName` / `.CanonicalTypeCode` / `.Category` / `.Covering(class)`, `state.Code` / `.Description`); row-shaped concerns live on `DatabasePermission` itself (`IsFor` securable+permission identity, `DisplayName` / `DisplayTypeCode` projection).

Both live on `Database`:
- `Database.Principals` — `ConcurrentDictionary<string, DatabasePrincipal>` keyed by name
- `Database.Permissions` — `List<DatabasePermission>` for grants / denies
- `Database.RoleMembers` — `List<(int RoleId, int MemberId)>`

**Pre-seeded fixed principals** at `Database` construction, matching real SQL Server's `sys.database_principals` ids (probe-confirmed):

| id | name | type | is_fixed_role |
|---|---|---|---|
| 0 | `public` | `R` | true |
| 1 | `dbo` | `S` | false |
| 2 | `guest` | `S` | false |
| 3 | `INFORMATION_SCHEMA` | `S` | false |
| 4 | `sys` | `S` | false |

Application roles carry `type` `A` / `type_desc` `APPLICATION_ROLE` — see [Application roles](#application-roles).

Plus the nine fixed database roles at their real ids (`Database.FixedDatabaseRoles`; 16388 is deliberately absent, matching real): 16384 `db_owner`, 16385 `db_accessadmin`, 16386 `db_securityadmin`, 16387 `db_ddladmin`, 16389 `db_backupoperator`, 16390 `db_datareader`, 16391 `db_datawriter`, 16392 `db_denydatareader`, 16393 `db_denydatawriter` — all `type R`, `is_fixed_role`, owned by dbo (owning_principal_id 1, so the DacFx cdc-filter predicate keeps working).
User principals start at 5 via `Database.AllocatePrincipalId`.

## Session principal & impersonation

`SessionSecurityContext` (`src/SqlServerSimulator/SessionSecurityContext.cs`) lives on `SimulatedDbConnection.Security` (session scope).
It carries the original login name, a base `SecurityPrincipalFrame` (database-principal id + name + login), and an impersonation stack.
`Effective` is the top frame (or the base); `EffectiveIsDbo` (principal id == 1) is the bypass every same-database enforcement gate short-circuits on.
Each frame also records whether its identity is `IsDatabaseScoped`, which is what makes a reference *across* a boundary ask `PermissionEnforcement.Bypasses` instead — see [Cross-database references](#cross-database-references).
An unauthenticated in-process connection uses `CreateDefault()` — dbo as login, database user, and original login everywhere — so existing consumers see byte-identical identity output.

**Identity scalars read the effective frame**: `CURRENT_USER` / `SESSION_USER` / `USER` / `USER_NAME()` / `USER_ID()` / `DATABASE_PRINCIPAL_ID()` → the effective database user; `SYSTEM_USER` / `SUSER_SNAME()` / `SUSER_NAME()` → the effective login (or the WITHOUT-LOGIN SID string), and with an argument the `sys.server_principals` row carrying that id or sid; `ORIGINAL_LOGIN()` → the session's original login.

**`EXECUTE AS` / `REVERT`** (`Simulation/Simulation.ExecuteAs.cs`, dispatched by peeking the `AS` after `EXEC`/`EXECUTE`; `REVERT` is its own statement).
- `EXECUTE AS USER = 'x'` pushes x's database-principal frame; a missing / non-user target raises Msg 15517.
  **`dbo` is an ordinary target**: a session holding IMPERSONATE on it — a sysadmin / `dbo` session, a `db_owner` member, or an explicit `GRANT IMPERSONATE ON USER::dbo` grantee — impersonates it successfully, and only a principal holding none of that gets Msg 15517 (severity 16 state 1, naming `dbo`).
  Probe-confirmed against SQL Server 2025 on two instances, which is what retires the earlier always-raises claim: the probe that produced it read a principal without the permission.
  The pushed frame is database-scoped like every other `EXECUTE AS USER` one, so it narrows even an `sa` session — a cross-database reference out of a non-`TRUSTWORTHY` database raises Msg 916 (probe-confirmed).
  `SYSTEM_USER` reports the *database owner's* login while impersonating (`sa` on both probed instances), as it does under a module frame that resolves to `dbo`; that login is also what the frame answers as in another database (probed 2026-09-27 against SQL Server 2025).
- `EXECUTE AS LOGIN = 'l'` maps l to its database user in the current DB (Msg 15406 on a missing login).
- `REVERT` pops one frame; a stray REVERT at the base is a silent no-op.
  Each frame carries an `ExecuteAsGuard` — the database it was pushed in, `WITH NO REVERT`, the `WITH COOKIE INTO @c` value, and the module that pushed it — and a `REVERT` that breaks one ends the batch: another database is Msg 15199, a no-revert frame Msg 15196, a missing or wrong cookie Msg 15591, and a module's own frame can't be reverted from outside it (probed 2026-10-04 against SQL Server 2025).
  A cookie variable of the wrong type is Msg 15533 at compile.
- `EXECUTE AS USER` / `LOGIN` takes a variable target; a `sys` / `INFORMATION_SCHEMA` target is Msg 15517, and a target holding no `CONNECT` in the database is Msg 916 state 4 (naming `public` for `guest`).
- `SETUSER 'user' [WITH NORESET]` impersonates the user through the `EXECUTE AS USER` path, and a bare `SETUSER` drops every impersonation frame.
- `USE` under `EXECUTE AS LOGIN` rebinds the impersonation frame to the login's user in the target database, falling back to `guest` where it has `CONNECT`.
- Nested `EXECUTE AS USER` by a non-dbo principal needs IMPERSONATE on the target at class 4, answered by the ordinary `PermissionChecker.IsGranted` walk — so an explicit grant, a role that holds one, `CONTROL` on the principal, and `db_owner` membership all admit it, and a DENY binds first.
- Nested `EXECUTE AS LOGIN` gates at **server** scope instead: `IMPERSONATE ON LOGIN::<target>` (class 101) or the server-wide `IMPERSONATE ANY LOGIN` (class 100), with a class-101 DENY overriding the blanket grant and `CONTROL ON LOGIN::` covering IMPERSONATE.
  A refusal reports the same Msg 15406 as a missing login — real leaks no distinction (probe-confirmed).
  See [`ON LOGIN::` securables](#on-login-securables).
- Module `WITH EXECUTE AS {CALLER | SELF | OWNER | 'user'}` is captured (on `Procedure.ExecuteAsClause` / `UserDefinedFunction.ExecuteAsClause` / `Trigger.ExecuteAsClause`) and pushed/popped around the body via the shared `PushModuleExecuteAsFrame` — procedures (`InvokeProcedure`), scalar UDFs / TVFs (`InvokeScalarFunction`), and triggers (`InvokeTrigger`) all honor it at runtime (OWNER → the module's effective owner — see [Ownership](#ownership) — SELF → the principal that ran the CREATE or the last ALTER, CALLER → no-op, a named user → that principal).
  SELF's principal is what `execute_as_principal_id` records, and an ownership change leaves it alone; the frame is that principal's, so a broken chain or dynamic SQL in the body checks the creator's rights (probed 2026-09-27 against SQL Server 2025).
  A user a module runs as — through SELF or by name — can't be dropped: Msg 15136, raised after every ownership refusal.
  The clause also resolves to a principal id at CREATE, stored on `SchemaObject.ExecuteAsPrincipalId` and projected by `sys.sql_modules.execute_as_principal_id` — see [`catalog-views.md`](catalog-views.md#execute_as_principal_id) for real's encoding.
  A scalar UDF's own `EXECUTE` permission is checked at the invocation seam (once per statement, memoized on `BatchContext.ExecuteCheckedFunctionIds`), covering the SET / IF operand contexts the query read-source sink doesn't reach.

**Authentication.** A login validates against `Simulation.Logins` at TDS connect and at in-process `Open()` when the connection string carries `User ID=`, then maps to a database user via `Simulation.TryMapLoginToDatabaseUser`.
The mapping is faithful (probe-confirmed against SQL Server 2025, PROBE_NOTES_HARDENING bundle 1) — resolution order for an authenticated login `l` in database `D`:
1. **Empty login registry ⇒ open dev mode.** When `Simulation.Logins.IsEmpty` the front doors accept any credentials and the session is **dbo** in every `D` — the honest "no authentication configured ⇒ open" default and the back-compat invariant the whole no-login test corpus rides on. The strict path below engages only once the registry is non-empty.
2. A **sysadmin-member login** (`sa`, or any login added to the `sysadmin` fixed server role) → **dbo** in every `D`, overriding any `FOR LOGIN` mapping (the dbo effective principal then bypasses every check, including explicit DENY).
   The login that owns `D` is its `dbo` too.
3. An explicit `CREATE USER … FOR LOGIN l` user in `D` → **that (restricted) user**.
4. A login holding **`CONNECT ANY DATABASE`** (which `CONTROL SERVER` covers) → a user of the login's own name at **principal id 0**, which `USER_ID()` and `DATABASE_PRINCIPAL_ID()` report and whose grants are `public`'s — in `master` too, ahead of `guest` (probed 2026-09-29 against SQL Server 2025).
5. **`guest` where accessible** — `master` / `tempdb` / `msdb` (aligned with `HAS_DBACCESS`; not `model`, not user databases) → the **guest** principal (id 2, a genuinely restricted principal whose effective rights flow through the normal checker: CONNECT + anything granted to `guest` / `public`).
6. Otherwise **refuse** — the login cannot open `D`: at connect, the Msg 4060 shape (`Cannot open database "<D>" requested by the login. The login failed.`, on the wire followed by Msg 18456 `Login failed for user '<l>'.`, then the connection closes); the session never opens on `D`.
There is **no permissive dbo fallback** for an authenticated login once the registry is non-empty — an unmapped login lands on `guest` where accessible or is refused, matching real SQL Server.
The unauthenticated in-process path (`CreateDbConnection()` with no `User ID=`) stays **dbo** always — the trusted in-process front door EF Core rides.
The same mapping answers `USE` / `ChangeDatabase` mid-session — see [Cross-database references](#cross-database-references).

## Parser

`Simulation/Simulation.GrantRevokeDeny.cs` + `Simulation/Simulation.PrincipalDdl.cs`.

### GRANT / REVOKE / DENY

```
GRANT <perm_list> [ON <securable>] TO <principal_list> [WITH GRANT OPTION] [AS <grantor>]
REVOKE [GRANT OPTION FOR] <perm_list> [ON <securable>] FROM <principal_list> [CASCADE] [AS <grantor>]
DENY <perm_list> [ON <securable>] TO <principal_list> [AS <grantor>]
```

- Permission list eats word sequences ending at comma / `ON` / `TO` / `AS` / `WITH`.
  A sequence of bare identifiers fuses into one permission name (e.g. `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` → single permission).
- `ON` clause resolves to a real (class, major_id): a bare or `OBJECT::<name>` name → class 1 + the object's id (and its schema id, for the covering-scope walk); `SCHEMA::<name>` → class 3 + schema id; `USER::<name>` → class 4 + principal id (the IMPERSONATE gate); no `ON` clause / `DATABASE::<name>` → class 0.
  `TYPE::<name>` → class 6 (`user_type_id`, alias and table types alike), `XML SCHEMA COLLECTION::<name>` → class 10 (`xml_collection_id`), `FULLTEXT CATALOG::<name>` → class 23 (`fulltext_catalog_id`, unqualified only); each takes `CONTROL`, `REFERENCES`, `TAKE OWNERSHIP` and `VIEW DEFINITION`, plus `EXECUTE` on a type and an XML schema collection and `ALTER` on an XML schema collection and a full-text catalog — any other permission is Msg 102 near its name at line 0, a missing securable Msg 15151 naming the leaf (the catalog as written, a `sys.` type as an object), and a dropped securable takes its rows with it (probed 2026-09-29 against SQL Server 2025).
  `CONTROL` on one of them is the alternative the securable's DROP accepts beside schema `ALTER` (or `ALTER ANY FULLTEXT CATALOG`); the other permissions are stored and projected but enforced nowhere yet — not `REFERENCES` on a type a column uses, which real refuses with Msg 15247, and not a `DENY` of any of them.
  `SERVER::<name>` and `LOGIN::<name>` route out of the database entirely, to `Simulation.ServerPermissions` — see [Server roles + server-scope permissions](#server-roles--server-scope-permissions-simulationsimulationserverrolescs).
  An unknown securable raises the Msg 15151 object-variant (`Cannot find the object '<name>', because it does not exist or you do not have permission.`), and a `DATABASE::` naming another database **Msg 4610** (probed 2026-09-28 against SQL Server 2025).
  A permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF, anything but UPDATE among the four DML permissions on a sequence) raises **Msg 4606**.
- Grantee names accept either `Name` or `ReservedKeyword` raw text (so `public` works without special-casing).
- The stored row's grantor follows the authority the statement used (probed 2026-10-04 against SQL Server 2025): a grantor holding the securable through ownership, `CONTROL`, `db_securityadmin` (or `db_accessadmin`, for `CONNECT`) or dbo records the **securable's owner**, and one holding only a `W` row of the exact permission records **itself**.
  `AS <grantor>` names a role the session belongs to, `db_securityadmin`'s reach, or a principal it may impersonate, else the grantor is a missing user.
  A `REVOKE` removes only the rows the recorded grantor matches, so a revoke by a principal other than the owner-proxied grantor leaves the row standing, as real does.
- Grantees are validated before anything changes: a missing one is Msg 15151's user wording, a fixed database role Msg 4617, and `sa` / `dbo` / `sys` / `INFORMATION_SCHEMA` / self the Msg 4624 notice (state 2, state 3 for the securable's owner).
- `GRANT ALL` expands to the permissions real's deprecated `ALL` names for the securable's class and sends the Msg 4628 class-0 deprecation notice; `DENY … CASCADE` denies and removes the grantee's delegations, and a `DENY` over a grantable row without it is Msg 4611.
- A permission name the class doesn't take (checked against `sys.fn_builtin_permissions` through `PermissionGraph.IsPermissionOf`) is Msg 102 near the name at line 0; `RECEIVE` on a non-queue object Msg 4606; an `INFORMATION_SCHEMA` view or a `sys.sp_*` procedure outside `master` Msg 4629; a three-part object in another database Msg 4610.
- A **column list** after a permission name — `GRANT SELECT (a, b) ON t TO u`, `DENY SELECT (c) ON t TO u`, `GRANT UPDATE (b) ON t TO u`, `REFERENCES (col)` — stores **one row per column** at `minor_id` = the column's 1-based ordinal (`sys.columns.column_id`); an unknown column raises **Msg 4615** (`Invalid column name '<col>'.`).
  The list may sit after the permission (`SELECT (a, b) ON t`) or after the object name (`SELECT ON t (a, b)`); the two placements can't combine (**Msg 1019**), and a list on a non-object scope — or on a **synonym**, which is entity-level — raises **Msg 1020** — see [Column-level grants](#column-level-grants).
  Tables and views both carry column ordinals (a view's are its projection's).
  A table-level (`minor_id 0`) GRANT / REVOKE of the same permission subsumes the grantee's column rows for it (probe-confirmed: a later `GRANT SELECT ON t` collapses the prior `GRANT SELECT (col)` rows); a column-level apply keys on its own `minor_id`.
  See [Column-level grants](#column-level-grants).
- `WITH GRANT OPTION` stores a **single `W` row** (not `G`+`W`).
- `REVOKE GRANT OPTION FOR … [CASCADE]` downgrades `W`→`G` and (with CASCADE) removes the rows the grantee delegated; a `REVOKE` (or `REVOKE GRANT OPTION FOR`) of a grantable row without CASCADE raises **Msg 4611**, delegated or not, at every scope (probed 2026-09-29 against SQL Server 2025; a `DENY` of one is 4611 on real too, which the missing `DENY … CASCADE` grammar keeps out of reach).
  Full `REVOKE … CASCADE` removes the whole delegation subtree (rows whose grantor is in the revoked-from set, transitively via `grantor_principal_id`).
- A triple holds one row: `GRANT` replaces a `D` row and `DENY` a `G` / `W` one (probed 2026-09-28 against SQL Server 2025); a plain REVOKE removes whichever is there.
- A GRANT / DENY / REVOKE targeting `sa` / `dbo` / `sys` / `INFORMATION_SCHEMA` / self silently no-ops and delivers **Msg 4624 on the info-message channel** at class 0 state 2 (`SimulatedDbConnection.InfoMessage`) — not catchable by TRY/CATCH, no row stored.
- A non-dbo grantor needs owner authority (ownership or an effective `CONTROL` reaching the securable through the graph, or the fixed role that administers its class), or a `W` row of the **exact** permission on the **same securable** — a `CONTROL` `W` row doesn't authorize granting `SELECT`, and a wider-scope `W` row doesn't reach an object (probed 2026-10-04 against SQL Server 2025).
  A `DENY` needs owner authority; a `W` row alone doesn't admit one.
  Missing authority is Msg 4613 at database scope and the Msg 15151 object / schema wording below it (permission errors leak as "cannot find").
- `CREATE USER` auto-seeds a CONNECT grant (class 0, type `CO`, grantor dbo, state G); the grants every database starts with are in [Seeded grants](#seeded-grants).

### Enforcement (execution-time)

`PermissionChecker` (the effective-permission engine) + `PermissionEnforcement` (the dispatch/row-source glue).
Both short-circuit on `EffectiveIsDbo` before any allocation, and on a static module body (ownership chaining), so a dbo session and any module-internal reference pay nothing.

Algorithm:
1. **Principal closure** — the effective principal + every role it belongs to transitively (nested roles) + `public` (id 0).
   Fixed-role memberships live in `Database.RoleMembers` like any role, so the closure folds them in.
2. **DENY binds first** — an explicit `D` row (or a deny-role: `db_denydatareader` → SELECT, `db_denydatawriter` → IUD) matching the permission or any covering permission at any scope denies, regardless of grants; explicit DENY binds even a `db_owner` member.
3. **GRANT test** — a `G`/`W` row (or a grant-role) matching the permission or a covering permission at object → schema → database scope.
   Grant-roles: `db_owner` → everything, `db_datareader` → SELECT, `db_datawriter` → IUD, `db_ddladmin` → DDL (ALTER / CREATE TABLE).
4. **Covering / scope** — the covering graph is imported from `sys.fn_builtin_permissions` for the OBJECT / SCHEMA / DATABASE classes: OBJECT SELECT ← RECEIVE ← CONTROL, DATABASE CREATE TABLE ← ALTER ← CONTROL, everything else ← CONTROL; each scope's permission maps same-name up (object SELECT → schema SELECT → database SELECT).
5. **Server permissions** — a request no database row granted or denied asks the login's server permissions (`ServerLoginRights`): its database covering chain, each link answered by the SERVER-class permission `sys.fn_builtin_permissions` names as its parent, so `CONTROL SERVER` implies everything, `ALTER ANY DATABASE` each database's `ALTER` and what that covers, `VIEW ANY DEFINITION` its `VIEW DEFINITION`, and `SELECT ALL USER SECURABLES` every user object's `SELECT`.
   A database `DENY` binds first even for a `CONTROL SERVER` grantee, and an identity minted inside one database — `EXECUTE AS USER`, a module frame, an application role — draws on none of it (probed 2026-09-29 against SQL Server 2025).

Denial is **Msg 229** (`The <PERM> permission was denied on the object '<name>', database '<db>', schema '<schema>'.`), except TRUNCATE (**Msg 1088**, its own double-quoted shape) and the CREATE gates (**Msg 262** / **2760** / **15247**).
A database-scope CREATE gate's Msg 262 ends the batch — through an `EXEC` too — and rolls the transaction back as under `SET XACT_ABORT ON`, catchable by a TRY; `EXECUTE AS LOGIN` for a login with no way into the current database is Msg 916 state 4 with the same reach (probed 2026-09-27 against SQL Server 2025).
Existence leaks: SELECT on a missing object is plain Msg 208; Msg 229 fires only for existing objects.

Wiring:
- **SELECT** — each real table / view / TVF read (including nested subqueries and derived tables) is recorded on `Selection.ReferencedSecurables` at parse time (principal-independent, so it rides the cached plan) and checked at execution entry (and on plan-cache replay).
  A reference written through a synonym records the **synonym** as its securable — see [Reference provenance: synonyms](#reference-provenance-synonyms).
  A table / view read additionally records its referenced column ordinals on `Selection.ReadColumnsByObject`, so the SELECT check is **column-grain** — see [Column-level grants](#column-level-grants).
  A scalar UDF invoked in a query records an EXECUTE securable the same way (checked once per statement, never per row).
- **A subquery that owns its own read list** — one written in an expression slot no query expression encloses: a scalar UDF's value-form `RETURN (SELECT …)`, a `SET` / `DECLARE` initializer, an `IF` / `WHILE` condition, a `PRINT` operand, an `UPDATE` SET-RHS or `INSERT … VALUES` element, a **CTE** body, a **`MERGE … USING`** source — reaches none of the per-statement check sites, so its list is checked where its plan executes (`PermissionEnforcement.CheckSubqueryReads`, called from the four subquery expression classes and from the MERGE source materializer; a CTE body's list rides the referencing statement's instead, folded in by `Selection.FoldSecurables` at the FROM source).
  A subquery *nested* in a query expression records into that statement's list and carries none of its own, so the per-row evaluation path reads one null field.
  Real draws no distinction between those shapes and an ordinary read (probe-confirmed against SQL Server 2025): the two scalar-UDF body forms — `RETURN (SELECT … FROM t)` and `SELECT @v = … FROM t` — behave *identically*, an intact ownership chain skipping the check for both and a chain broken by an other-owner schema or by the database boundary raising Msg 229 naming the base object for both.
- **INSERT / UPDATE / DELETE / MERGE** — the target's write permission is checked (INSERT / UPDATE / DELETE; MERGE checks the union of its action kinds plus SELECT on the target); `INSERT … SELECT` also checks SELECT on the source's recorded reads.
  **UPDATE / DELETE read-implies-SELECT** (probe M1/M2): the target's SELECT is also required *when the statement reads it* — a WHERE clause, or a SET expression that references a target column (`SET v = v + 'x'`, detected via a static column-reference probe). A constant-SET UPDATE / bare DELETE with no WHERE reads nothing and needs only the write permission. The SELECT check runs *first*, and with neither SELECT nor the write granted both records surface, SELECT then the write, as one error — and a column-grain denial lists every denied column (Msg 230 per column) the way real does (probed 2026-10-04 against SQL Server 2025). A joined UPDATE / DELETE (`… FROM t JOIN u …`) SELECT-checks every backing-table source — the non-target sources first, then the target (matching real's ordering).
  On a **single target** (the no-FROM UPDATE / DELETE path) both the read-implies-SELECT and the UPDATE are **column-grain**, against a base table or a view alike (SELECT per WHERE / SET-RHS column, UPDATE per assigned column); the joined form, and any target reached through a synonym, stay object-grain. See [Column-level grants](#column-level-grants).
- **EXEC proc** / **scalar UDF invocation** — EXECUTE on the module at the call site (the Msg 229 for EXEC is attributed to the procedure as the call spells it, brackets dropped, at line 1; a call through a synonym checks the synonym and carries none — probed 2026-09-27). The scalar-UDF check fires at the invocation seam (`PermissionEnforcement.CheckScalarFunctionExecute`, memoized once-per-statement) so SET / IF operand invocations are covered too.
- **Table variables and temp tables** are no securables: every session that can name one may write it, so the write check skips them (probed 2026-09-27 against SQL Server 2025 — a restricted user inserts into its own `@t` and `#t`).
- **UNMASK** is no gate but a value filter: a principal without it reads masked values rather than being refused, and ownership chaining doesn't lift it — see [`data-masking.md`](data-masking.md#unmask).
- **TRUNCATE** — ALTER on the object → Msg 1088 (state 7).
- **DDL gates** — see [DDL statement gates](#ddl-statement-gates) for the per-statement matrix.
- **Ownership chaining** — see [Ownership](#ownership) for how the chain compares owners.
  Inside a proc / view / TVF / scalar-UDF / trigger body (`BatchContext.EnforcesPermissions` is false there) a reference to an object with the module's own owner is unchecked; dynamic SQL (`EXEC('…')` / `sp_executesql`, whose `ProcFrame.IsDynamicSql` is set) re-enables every check.
  Chaining covers DML and execution only: a module body's DDL is checked against the frame's principal like a top-level statement, so `DROP TABLE` inside a procedure is Msg 3701 for a caller who only holds EXECUTE on it (probed 2026-10-04 against SQL Server 2025).
  A synonym reached in a module body has its base checked too when the chain to the base breaks, and an `xml(<collection>)` variable or column needs EXECUTE on the collection (Msg 229 + Msg 15247 for a column, Msg 229 for a variable).

### DDL statement gates

Every modeled CREATE / ALTER / DROP statement is gated for a non-dbo principal.
All rows probe-confirmed against SQL Server 2025 for the permission that admits the statement and for the exact number / severity / state / wording of the refusal.
`dbo` short-circuits before any allocation, and a create-time bind (`BatchContext.CreateTimeBinding`) checks nothing.

Two shapes recur, and the difference between them is load-bearing:

- an **ALTER-shaped** gate asks for `ALTER` on the object, which the covering walk also satisfies from schema-scope ALTER, object CONTROL, database-scope ALTER / CONTROL, and `db_ddladmin` / `db_owner`;
- a **DROP-shaped** gate (`PermissionEnforcement.HasDropAuthority`) asks for schema ALTER **or** object CONTROL — a plain object-scope ALTER is *not* enough, which is exactly what separates `DROP TABLE` from `ALTER TABLE`.

| Statement | Gate | Denial |
|---|---|---|
| `CREATE TABLE` | db-scope CREATE TABLE, then ALTER on the target schema; temp tables exempt | **Msg 262** state 1, then **Msg 2760** |
| `CREATE VIEW` / `PROCEDURE` / `FUNCTION` | the same-named db-scope permission, then schema ALTER | **Msg 262** **state 18** (object as `Procedure` attribution), then **Msg 2760** |
| `CREATE SYNONYM` / `CREATE TYPE` (alias + table) | db-scope `CREATE SYNONYM` / `CREATE TYPE`, then schema ALTER | **Msg 262** state 1, then **Msg 2760** |
| `CREATE XML SCHEMA COLLECTION` | schema ALTER **first**, then db-scope `CREATE XML SCHEMA COLLECTION` — the halves run in the opposite order from every other dual gate | **Msg 15151** `Cannot alter the schema '<s>'…`, then **Msg 262** state 1 |
| `CREATE ASSEMBLY` | db-scope `CREATE ASSEMBLY` | **Msg 262** state 1 |
| `CREATE FULLTEXT CATALOG` | db-scope `CREATE FULLTEXT CATALOG` | **Msg 7666** sev 16 state 2 |
| `CREATE SEQUENCE` | db-scope `CREATE SEQUENCE` on the target schema's covering walk | **Msg 15247** |
| `CREATE ROLE` / `USER` / `SCHEMA` / `APPLICATION ROLE` | the named db-scope permission (`CREATE ROLE`, `ALTER ANY USER`, `CREATE SCHEMA`, `ALTER ANY APPLICATION ROLE`); the creator owns the role / schema it makes, and `AUTHORIZATION` names a principal the caller may act as | **Msg 15247**, then real's trailing **Msg 2759** for CREATE SCHEMA |
| `ALTER` / `CREATE OR ALTER` of an existing view / procedure / function | ALTER-shaped, on the module | **Msg 3701** sev 14 state 20, `Cannot alter the <kind> '<leaf>'…` |
| `CREATE OR ALTER` over a free name | the plain-CREATE gate for that kind | **Msg 262** state 18 |
| `CREATE` / `ALTER` / `DROP TRIGGER` (DML) | ALTER-shaped, on the **parent table / view** — a DML trigger is not its own securable | **Msg 2104** sev 14 state 1 on create (name echoed *as written*); **Msg 3701** state 20 on alter / drop (leaf) |
| `CREATE` / `ALTER` / `DROP TRIGGER … ON DATABASE` | db-scope `ALTER ANY DATABASE DDL TRIGGER` | same 2104 / 3701 pair |
| `CREATE` / `ALTER` / `DROP TRIGGER … ON ALL SERVER` | server-scope `CONTROL SERVER`; a `db_owner` is refused (probed 2026-09-28), a `CONTROL SERVER` grantee admitted (probed 2026-09-29) | same 2104 / 3701 pair |
| `CREATE INDEX` | ALTER-shaped, on the table (or the view, for an indexed view) | **Msg 1088** sev 16 **state 12**, double-quoted table name *as written* |
| `ALTER INDEX` | ALTER-shaped, on the table | **Msg 1088** **state 9**, table name as written |
| `DROP INDEX` | ALTER-shaped, on the table | **Msg 1088** **state 9**, `"<table as written>.<index>"` |
| `ALTER TABLE` | ALTER-shaped, on the table | **Msg 1088** **state 13**, leaf-named |
| `TRUNCATE TABLE` | ALTER-shaped, on the table | **Msg 1088** **state 7**, leaf-named |
| `ALTER SEQUENCE` | ALTER-shaped, on the sequence | **Msg 15151** state 1, `Cannot alter the sequence '<leaf>'…` |
| `DROP TABLE` / `VIEW` / `PROCEDURE` / `FUNCTION` / `SEQUENCE` / `SYNONYM` | DROP-shaped | **Msg 3701** sev 14 state 20, `Cannot drop the <kind> '<leaf>'…` |
| `DROP TYPE` (alias + table) | schema ALTER, or CONTROL on the type | **Msg 218** sev 16 state 1, naming the type **as written** |
| `DROP XML SCHEMA COLLECTION` | schema ALTER, or CONTROL on the collection | **Msg 15151** state 1, `Cannot drop the xml schema collection '<leaf>'…` |
| `DROP FULLTEXT CATALOG` | db-scope `ALTER ANY FULLTEXT CATALOG`, or CONTROL on the catalog | **Msg 7641** sev 16 state 5 |
| `DROP SCHEMA` | CONTROL on the schema, or db-scope `ALTER ANY SCHEMA` — schema **ALTER is not enough** here | **Msg 15151** state 1 |
| `ALTER SCHEMA … TRANSFER` | ALTER on the **destination** schema, then CONTROL on the moved object — ALTER on the *source* schema is not enough | **Msg 15151** `Cannot alter the schema '<dest>'…`, then **Msg 15151** `Cannot transfer the object '<leaf>'…` |
| `ALTER ROLE … ADD / DROP MEMBER` | db-scope `ALTER ANY ROLE` (ALTER / CONTROL on the role cover it) | **Msg 15151** **state 2**, `Cannot alter the role '<n>'…` |
| `DROP ROLE` | db-scope `ALTER ANY ROLE` | **Msg 15151** **state 1**, `Cannot drop the role '<n>'…` |
| `DROP USER` | db-scope `ALTER ANY USER` | **Msg 15151** |
| `ALTER USER` | ALTER on the user (`ALTER ANY USER` covers it) | **Msg 15151** |
| `ALTER COLUMN … ADD` / `DROP MASKED` | `ALTER` on the table and db-scope `ALTER ANY MASK` | **Msg 15247** state 5 |
| `SET IDENTITY_INSERT` | ALTER-shaped, on the table | **Msg 1088** state 11, ending the batch |
| `SELECT … INTO` | db-scope `CREATE TABLE`, then schema ALTER | **Msg 262**, then **Msg 2760** |
| `CREATE TABLE` with a `FOREIGN KEY` | `REFERENCES` on every referenced column (column-grain) | the REFERENCES denial (**Msg 229**, or one **Msg 230** per column), then **Msg 1088** state 20 naming the referenced table as written, then **Msg 1750** |
| `ALTER DATABASE … SET` / `COLLATE` | db-scope `ALTER` (or CONTROL) on the target | **Msg 5011** sev 14 **state 9** — same wording as the state-5 unknown-database record, so nothing leaks |
| `sp_rename` | ALTER-shaped, on the object | **Msg 15225** sev 11 state 1 — the same not-found record a missing object earns |
| `CREATE DATABASE` | **server** scope: `CREATE ANY DATABASE` (covered by `ALTER ANY DATABASE`, carried by `dbcreator` and `##MS_DatabaseManager##`) | **Msg 262** state 1, naming **`master`** whatever the current database is, and ending only the statement (probed 2026-09-29) |
| `DROP DATABASE` | **server** scope: `ALTER ANY DATABASE` (`##MS_DatabaseManager##` carries it), or `dbcreator` membership | **Msg 3701** **sev 11 state 2** — a different shape from every object drop |

**Fixed-role coverage.**
Every check — DML, DDL and metadata alike — answers from one implication graph built from `sys.fn_builtin_permissions` (`PermissionGraph`): a request is satisfied by any (class, permission) pair whose grant implies it through the covering and parent-covering columns, transitively, and refused by a DENY on any of them.
Each fixed database role contributes a set of virtual database-scope grants, read off `fn_my_permissions(NULL, 'DATABASE')` for a member (`PermissionChecker.FixedRoleGrants`, probed 2026-10-04 against SQL Server 2025), and the two deny roles a database-scope DENY.
So `db_ddladmin` passes every object / schema / type DDL through `ALTER ANY SCHEMA` and the `CREATE *` permissions but neither role DDL nor `ALTER DATABASE`, `db_securityadmin` passes role and application-role DDL and sees all metadata through `VIEW DEFINITION`, `db_accessadmin` passes user DDL, and `db_owner`'s `CONTROL` passes everything.

**Ownership.**
Real also admits every ALTER / DROP above to the object's (or schema's) owner without an explicit grant — probe-confirmed against a `CREATE SCHEMA … AUTHORIZATION <user>` schema.
The checker's owner path answers it — see [Ownership](#ownership).

**Cross-database.**
The gates route through the same `PermissionEnforcement` seam as the DML checks, so a three-part DDL target resolves the login's principal in the *target* database (Msg 916 when it has none).

### Column-level grants

`GRANT` / `DENY SELECT | UPDATE | REFERENCES | UNMASK (col, …)` store one `DatabasePermission` row per column at `minor_id` = the column's 1-based ordinal (`sys.columns.column_id`); `sys.database_permissions` surfaces the `minor_id`, and `COL_NAME(major_id, minor_id)` resolves it.
Enforcement is probe-confirmed against SQL Server 2025.

The column list has two accepted placements (both probe-confirmed): after the permission (`GRANT SELECT (a, b) ON t TO u`) or after the object name (`GRANT SELECT ON t (a, b) TO u`), the latter applying its columns to every permission in the statement.
The two can't combine — `GRANT SELECT (a) ON t (b)` raises **Msg 1019** (`Invalid column list after object name in GRANT/REVOKE statement.`) — and a column list on a non-object scope (`GRANT SELECT ON SCHEMA::s (c)`) raises **Msg 1020** (`Sub-entity lists (such as column or security expressions) cannot be specified for entity-level permissions.`).

**Effective column permission.**
`PermissionChecker.IsColumnGranted(database, principalId, permission, objectId, schemaId, columnOrdinal)` answers "may the principal read / write this column?" with the same DENY-first / GRANT precedence as the object-grain `IsGranted`, but the object scope admits a row at the column's own `minor_id` alongside the object-level (`minor_id 0`), schema, and database scopes.
So a **column DENY overrides a table GRANT** (`GRANT SELECT ON t` + `DENY SELECT (b)` → `SELECT b` denied), and a **column GRANT stands in for an absent table grant** (`GRANT SELECT (id)` lets `SELECT id` but not `SELECT b`).
The object-grain `IsGranted` is `minor_id`-aware too: its satisfiers carry `minor_id 0`, so a column-scoped row never satisfies an object-grain check — which is what makes the 229-vs-230 boundary work.

**Which columns require which permission.**
Every column *read* — select list, WHERE, JOIN ON, GROUP BY, HAVING, ORDER BY, and an UPDATE `SET`'s RHS — requires SELECT on that column; `SELECT *` expands to all columns.
Every column *assigned* in an UPDATE `SET` requires UPDATE on that column.
A base table touched **without naming a column** (`COUNT(*)` / `SELECT 1` / `EXISTS (SELECT * …)`) is checked as requiring SELECT on **every** column (real's behavior — probed).
**INSERT stays object-grain** (a table / schema / db INSERT grant suffices; column-level INSERT grants aren't modeled — see [Known gaps](#known-gaps)); `DELETE` is not column-grantable, so `DELETE` itself stays object-grain (only its read-implies-SELECT is column-grain).

**Msg 229 vs Msg 230.**
A denied column raises **Msg 230** (`The <PERM> permission was denied on the column '<col>' of the object '<obj>', database '<db>', schema '<schema>'.`, sev 14 state 1), naming the first offending column in ascending ordinal order.
But when the object is inaccessible at object grain (no grant, or an object / schema / database DENY or a deny-role nullifying the grant) **and** the principal holds no column grant on it, the object-level **Msg 229** fires instead (probe: zero access → 229 even for an explicit column reference; an object-scope DENY-beats-GRANT → 229, not a per-column 230).
`PermissionChecker.HasColumnLevelGrant` pairs with `IsGranted` to draw that line.

**Read-column tracking (parse-time, principal-independent).**
`Selection.ReadColumnsByObject` maps each table / view `object_id` → a `ColumnReadTarget` (the securable, its columns, and the ordinals read), accumulated in `BuildSqlProjection` from the resolved column references across the projection (through the schema-resolution walk), plus a structural walk of the WHERE / JOIN ON / GROUP BY / HAVING / ORDER BY / aggregate-operand expressions.
It rides the cached plan (recorded once, checked per execution against the current principal via `PermissionEnforcement.CheckReadSources`), and the `dbo` / module-body fast path pays nothing — the check short-circuits on `EffectiveIsDbo`, and the runtime row closure keeps the non-recording resolver, so recording adds nothing to execution.
The UPDATE / DELETE single-target paths don't ride a `Selection` plan, so they build a `ColumnReadTarget` inline (gated on `PermissionEnforcement.Applies`, so `dbo` skips the collection entirely) and call `PermissionEnforcement.CheckColumns`.

**Views are column-grantable too.**
A view carries its own column ordinals (`View.OutputColumns`, what `GRANT SELECT (col) ON <view>` stores as `minor_id`), and enforcement uses them rather than the base table's — so a view column computed from several base columns (`a + b AS both`) is one grantable unit and a denial names it.
While the view and its base share an owner the base table is **never** consulted for a reference through the view (ownership chaining): a grant on the base does not admit the view read, and a DENY on the base does not block it.
Both SELECT and the UPDATE pair (assigned columns need UPDATE, WHERE / SET-RHS columns need SELECT) are column-grain through a view; INSERT and DELETE through a view stay object-grain, matching real.
All probe-confirmed against SQL Server 2025.

**Coverage note.**
Column collection uses the structural expression visitors, which don't recurse through every container (fixed-return scalar functions like `DATALENGTH(col)`, and columns buried in some non-arithmetic function args, are missed) — a residual gap that can under- or over-report a column in those uncommon shapes.
Direct references, arithmetic / comparison, `CAST`, aggregates, and `SELECT *` are covered.

### Cross-database references

A login's rights are **per database**, so a reference through a three-part name (`other.dbo.t`, a synonym whose base is one, or the `db..t` short form) is checked against the login's user *in the target*, not the session's principal.
`PermissionEnforcement.TryResolveScope(batch, targetDatabase, out principalId)` is the seam every object-scoped check runs through; the target database comes off the securable (`BatchContext.DatabaseFor`, or `DatabaseForName` at the DDL gates that run before the object resolves) and rides the cached plan on `ReferencedSecurable.Database`.
All probe-confirmed against SQL Server 2025.

| Situation | Result |
|---|---|
| The login's user in the target holds the permission | allowed |
| It holds nothing (the session-database user's grant does **not** travel) | **Msg 229** naming the *target* database — `The SELECT permission was denied on the object 't2', database 'other', schema 'dbo'.` |
| The login has **no user** in the target | **Msg 916** sev 14 state 2 — `The server principal "app" is not able to access the database "other" under the current security context.` |
| The effective principal is `dbo` (sysadmin, or the unauthenticated in-process default) | unrestricted — unless the `dbo` is a database-scoped frame, which is refused like any other one |

The `dbo` bypass stays two field reads on the session's effective frame, so nothing but a genuinely restricted principal ever pays a lookup, and the lookup only runs when the touched database differs from the session's.
That bypass is exact in the simulator's principal model: an effective `dbo` can only have come from a sysadmin login, the empty-registry dev mode, or a database-scoped frame — the first two `dbo` in every database, and the third answering at the boundary like the other database-scoped identities below.
`PermissionEnforcement.Bypasses(connection, target)` is the boundary-aware form every cross-database check site asks (`BypassesEverywhere` the same question with no target in hand, for the securable-list skips); `SessionSecurityContext.EffectiveIsDbo` remains the same-database one.

A **catalog-view** read of another database asks the same question — see [Cross-database metadata visibility](#cross-database-metadata-visibility).

**A database-scoped identity crosses only out of a `TRUSTWORTHY` database.**
An `EXECUTE AS USER` frame, any of a module's `WITH EXECUTE AS` frames, and an activated application role carry no server principal, so out of an ordinary database *every* cross-database reference raises Msg 916 whatever the target's grants say.
The name in the message is the frame's reported login identity: the login for a `FOR LOGIN` user or an application role (the session's login survives the activation), and the `S-1-9-3-…` SID for a `WITHOUT LOGIN` user.
`SecurityPrincipalFrame.IsDatabaseScoped` is the marker.

**A frame that resolves to `dbo` is database-scoped too** — `EXECUTE AS USER = 'dbo'`, and `WITH EXECUTE AS OWNER` / `SELF` in a dbo-owned or dbo-created module: the token is minted in the module's database and its `dbo`-ness stops at the boundary, so a body that reads, writes, `USE`s or reads the catalog of another database out of a non-trustworthy source is refused — probe-confirmed, and refused even when the session's own login is `sa`.
Data reference, catalog read and `OBJECT_ID`'s three-part name all raise the same Msg 916; the id-form `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` still answer, since those ask only the visibility question (see [Cross-database metadata visibility](#cross-database-metadata-visibility)).
Everything the frame does in its *own* database is unaffected — the bypass is boundary-aware, not withdrawn.
The message names the database owner's login, which is the frame's login.

Turning the **source** database's `TRUSTWORTHY` on (the database the token was made in — the target's flag is irrelevant) accepts the token, after which the frame's own login answers in the target like any ordinary session's: an object it holds nothing on is Msg 229 naming the target, and a login with no user there is still Msg 916 — and a `dbo` frame answers as the database owner's login, so a `sa`-owned source's is unrestricted and another owner's is checked as that login's user there (probed 2026-09-27 against SQL Server 2025).
So a `WITHOUT LOGIN` user, whose reported identity is a SID rather than a login, is refused however trustworthy the source is.
All probe-confirmed against SQL Server 2025.

The crossing also needs an **authenticator**: the source database's owner must be `dbo` in the target or hold `AUTHENTICATE` there — probed as the exact line between allowed and refused, with a `sa`-owned source qualifying through `dbo`, an owner that owns the target or is a `db_owner` member there qualifying too, and an owner with no user in the target, or one whose user lacks `AUTHENTICATE`, refused with Msg 916 (probed 2026-09-27 against SQL Server 2025).
`PermissionEnforcement.AcceptsDatabaseScopedToken` is the rule.

**Ownership chaining crosses the database boundary only with `DB_CHAINING` on in both databases.**
With either side off — the default for a user database — a dbo-owned module does not lend its owner's rights to an object in another database: the caller needs its own grant there and the denial names the base object.
With both on the chain re-links and the module's reference is unchecked, through a view and through a statement-dispatching body alike.
Chaining lends **rights, not access**: the caller still needs a user in the target, so a login with none is Msg 916 either way (probe-confirmed — a `guest` grant in the target is enough to satisfy it).
The two objects must also share an owner, compared by **login**: a `dbo` owner stands for its database's owner, a `FOR LOGIN` user for its login, and anything else matches nothing (`Ownership.OwnerLogin`).
So databases owned by different logins break the chain, as does a target owned by a user of another login, while two users of one login in the two databases keep it (probed 2026-09-27 against SQL Server 2025).
A reference the *user* wrote is never chained, whatever the flags say.

Mechanically, `PermissionEnforcement.Applies(batch, target)` keeps the module-body suppression only for a same-database securable, which covers procedure / trigger / scalar-UDF bodies through the ordinary per-statement check sites; a **view or inline-TVF body is inlined** into the referencing statement and reaches none of those, so its plan's cross-database reads are checked once at invocation via `PermissionEnforcement.CheckCrossDatabaseReads`.
The chaining exemption sits one step *after* the principal resolution in both (`TryResolveScope` and `CheckCrossDatabaseReads`), which is what keeps the Msg 916 in play when the chain links.
A create-time bind suppresses everything either way — it reads no row.

**`USE` / `ChangeDatabase` ask the same question.**
A restricted principal may switch to a database its login maps into, and the session's base frame **rebinds to that database's user** — `CURRENT_USER` follows the switch while `SYSTEM_USER` / `ORIGINAL_LOGIN()` stay put (probe-confirmed: a login with different user names in two databases reports each in turn).
A login with no user there gets Msg 916 and the session stays put; a missing database is Msg 911 first (probe-confirmed — existence is reported even to a principal that could not have opened it); an active application role is Msg 505 ahead of both.
`Simulation.SwitchDatabase` is the shared implementation.

`USE` runs the same gate, so a `TRUSTWORTHY` source lets an impersonating session switch where a non-trustworthy one gets Msg 916 (probe-confirmed).

The `TRUSTWORTHY` flag and the authenticator's owner are read off the session's current database, which is the token's home: the session's own for a direct `EXECUTE AS USER`, and the module's for a module's frame, since a body reached through a three-part name runs in its own database.

**A module reached through a three-part name runs as the login's user in its database** — `USER_NAME()` reads that user, or `guest` where `GRANT CONNECT TO guest` enabled it, and a login with no user there is Msg 916 at the calling statement (probed 2026-09-28 against SQL Server 2025).
The body's same-database references then chain as they would for a caller in that database, and a reference back into the session's database is a cross-database one — see [`schemas.md`](schemas.md#modules-reached-through-a-three-part-name).

### Reference provenance: synonyms

A synonym is **its own securable**, and a reference written through one is checked against the synonym — never walked through to the base object.
Probe-confirmed against SQL Server 2025, in both directions:

| Held | Reference | Result |
|---|---|---|
| `GRANT SELECT ON syn` | `SELECT … FROM syn` | allowed |
| `GRANT SELECT ON syn` | `SELECT … FROM base` | **Msg 229** naming `base` |
| `GRANT SELECT ON base` | `SELECT … FROM syn` | **Msg 229** naming `syn` |
| `GRANT` on base + `DENY` on syn | `FROM syn` denied, `FROM base` allowed | the DENY doesn't reach the base |
| `GRANT` on syn + `DENY` on base | `FROM syn` allowed | the DENY doesn't reach the synonym |
| `GRANT SELECT ON SCHEMA::s` | `FROM s.syn` | allowed — the ordinary scope walk, on the synonym's own schema |

The same holds for `INSERT` / `UPDATE` / `DELETE` / `MERGE` through a table synonym and `EXEC` through a procedure synonym.
The EXEC denial names the synonym and carries **no `Procedure` attribution** (the module was never entered), unlike a direct `EXEC dbo.p`, which attributes `dbo.p`.

Because a synonym takes no column list at all, every check through one is **object-grain** — `GRANT SELECT (col) ON <synonym>` raises **Msg 1020** (severity 16, state 3), which is a *different* variant from the entity-level-permission rejection (class 15, state 1): real raises the synonym one after the securable resolves, so it is catchable and beats the Msg 4615 unknown-column check.

The carrier is the *written* name.
`PermissionEnforcement.SecurableFor(batch, writtenName, resolved)` returns the `Synonym` when the name is one and the resolved object otherwise; `CheckReference` is the check that wraps it, and `CheckSchemaObject` the already-resolved form.
For query sources the provenance rides `FromSource.ViaSynonym`, stamped during FROM parsing — read both by the securable sink (which records the synonym in place of the object) and by the joined UPDATE / DELETE source checks.
A source with `ViaSynonym` set is excluded from `Selection.ReadColumnsByObject` entirely, so the column-grain lookup misses and the object-grain path fires.

### Metadata visibility

A restricted principal sees an object-scoped catalog-view row — and gets a non-NULL `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` result — only for objects it may view metadata for; everything else disappears (probe-confirmed against SQL Server 2025).
`PermissionChecker.CanViewMetadata` is the rule, probed 2026-10-04 against SQL Server 2025.
A `DENY VIEW DEFINITION` reaching the object (on it, its schema or the database, or a `DENY CONTROL` covering one) hides it whatever else is granted.
Otherwise the bypass sees everything: a `db_ddladmin` member, or a holder of `VIEW DEFINITION` at database scope — which `db_owner`'s `CONTROL` and `db_securityadmin`'s own grant carry — that no object- or schema-scope deny of `VIEW DEFINITION` / `CONTROL` takes away (`PermissionChecker.HasFullMetadataVisibility`).
Short of that, the owner sees its objects, and anyone else sees an object only through an **effective** grant of a permission that applies to the object's **kind**: any row on the object itself (a column-scope grant included), and at schema or database scope a permission whose graph reaches one the kind takes.
So a schema `SELECT` or `db_datareader` reveals the schema's tables and views but not its procedures, a schema `EXECUTE` the reverse, and a grant a DENY cancels reveals nothing (`db_datareader` beside `db_denydatareader` sees no table).
Visibility is **object-grain**: one permission on the object reveals *all* its column / index / parameter / constraint rows, and a trigger's visibility follows its parent table / view.

**Definitions** are a second, narrower gate over a visible row: `sys.sql_modules.definition`, `INFORMATION_SCHEMA.ROUTINES` / `VIEWS`, `OBJECT_DEFINITION`, `sp_helptext` (Msg 15197 rather than the text) and `sys.dm_sql_referenced_entities` need the owner, or `VIEW DEFINITION` / `ALTER` / `CONTROL` / `TAKE OWNERSHIP` on the object, so `SELECT` or `EXECUTE` alone shows the row with a NULL definition.
`sys.sql_expression_dependencies` keeps only the rows whose referencing object's definition the principal may see.

**Principals, permissions and types** are filtered too, each by its own rule (`PermissionChecker.VisiblePrincipals` / `VisibleGrantees` / `CanViewTypeMetadata`): `sys.database_principals` shows the catalog principals and fixed roles, the principal's own closure, what it owns and what it holds a permission on, plus every user / role / application role when it holds that kind's `ALTER ANY`; `sys.database_role_members` follows the principal visibility, `sys.database_permissions` shows only the closure's own rows short of the same `ALTER ANY` widening, and `sys.types` / `sys.table_types` show a user-defined type to its owner or a holder of any permission on it.
`database VIEW DEFINITION` lifts all three.

The filter is a per-enumeration seam on the catalog-view row generators (`BuiltInResources.ApplyMetadataFilter`, wired into both `Selection.ForCatalogView` overloads), gated by `PermissionEnforcement.MetadataVisibilityPrincipal(batch, targetDatabase)` — which returns the principal to filter by, or null for full visibility. It is a **session**-principal check that (unlike `Applies`) is NOT suppressed inside a module body, since metadata visibility is a property of the session principal, not the execution frame.
The `OBJECT_ID` / `OBJECT_NAME` / `OBJECT_SCHEMA_NAME` scalars read the same seam — `MetadataVisibilityPrincipal` for the name form, `TryMetadataVisibilityPrincipal` (which hides instead of raising) for the id form.
The dbo / full-visibility fast path short-circuits on the session principal before any allocation, so existing (dbo) and SMO-as-sysadmin consumers pay one bool read and are unaffected.
Each filtered view carries a `CatalogView.MetadataVisibilityKey` (set once at registration in `BuiltInResources.MetadataVisibility.cs`) naming the row column that governs visibility: the object-id-keyed `sys.*` views key on the row's `object_id` (or `parent_object_id`), the name-keyed `INFORMATION_SCHEMA.*` object views on the owning schema + object name.
Filtered views: `sys.objects` / `all_objects` / `tables` / `views` / `all_views` / `procedures` / `columns` / `all_columns` / `parameters` / `all_parameters` / `sql_modules` / `all_sql_modules` / `indexes` / `index_columns` / `foreign_keys` / `foreign_key_columns` / `check_constraints` / `default_constraints` / `key_constraints` / `triggers` / `identity_columns` / `computed_columns` / `sequences` / `synonyms`, and `INFORMATION_SCHEMA.TABLES` / `COLUMNS` / `VIEWS` / `ROUTINES` / `PARAMETERS`.
Deliberately unfiltered (probe-confirmed broadly visible to a restricted principal): `sys.schemas` and the DMVs.
`sys.server_principals` / `sys.sql_logins` / `sys.server_permissions` / `sys.server_role_members` carry their own server-scope filter — see [Server-principal metadata visibility](#server-principal-metadata-visibility) — and `sys.databases` follows `VIEW ANY DATABASE`, which `public` holds from the start — see [Database visibility](#database-visibility).
A column-scope grant (`minor_id > 0`) reveals its object object-grain — `sys.columns` shows every column of a column-granted object, including the ungranted / denied ones.

#### Cross-database metadata visibility

A catalog-view read of another database (`other.sys.tables`) resolves the login's user *there* and filters by **that** principal's visibility — the same resolution a data reference runs, so the whole rule above (its own full-visibility bypass included) re-answers in the target.
So a login restricted at home and `db_ddladmin` away sees the away catalog whole, and a grant held at home reveals nothing away.
All probe-confirmed against SQL Server 2025.

A login with **no user** in the target gets **Msg 916**, and real raises it for *every* cross-database catalog view — including the ones it would never have filtered (`sys.databases`, `sys.schemas`, `sys.types`, `sys.database_principals` all refuse alongside `sys.tables`), since the refusal is about reaching the database rather than about the view.
`ApplyMetadataFilter` therefore resolves ahead of the `MetadataKey` test; only an unfiltered view of the session's *own* database short-circuits before the closure build, so a restricted session keeps paying nothing for a local `sys.databases` read.
`DB_ID` / `DB_NAME` still answer for a database the login can't reach (probe-confirmed — they read no metadata of it).

The guest rule follows the data path exactly: `master` / `tempdb` / `msdb` resolve to `guest` and filter by it, while `model` refuses like any user database.
A database-scoped frame reaches another database's catalog only out of a `TRUSTWORTHY` source, as it reaches its data — see [Cross-database references](#cross-database-references).

**The `OBJECT_*` scalars ask in the database the argument names**, and real splits them by argument form — probe-confirmed against SQL Server 2025.

`OBJECT_ID('other.dbo.t')` resolves the object first and gates second, so it behaves like a catalog read of `other`: the target user's visibility decides, and a login with no user there gets **Msg 916**.
The resolve-first order is observable — a name that matches nothing (`other.dbo.no_such_table`), a name the type filter excludes (`OBJECT_ID('other.dbo.t', 'P')`), and an unknown database all answer NULL rather than raising, in a database the login could never have reached.
The guest rule follows the data path: `master` / `tempdb` / `msdb` resolve to `guest` and filter by it, `model` refuses.
A registered catalog view (`other.sys.tables`) answers its id ungated — real reveals the system views to everyone.

`OBJECT_NAME(id, database_id)` and `OBJECT_SCHEMA_NAME(id, database_id)` ask the visibility question **alone** and never raise: a database the login has no user in simply reveals nothing, so the answer is NULL.
The same goes for a database-scoped frame out of a non-`TRUSTWORTHY` database: the `WITH EXECUTE AS OWNER` body that gets Msg 916 for `other.sys.tables` reads NULL for `OBJECT_NAME(id, <other's id>)`, and `DB_ID('other')` is NULL there to begin with (probed 2026-09-29 against SQL Server 2025).
`OBJECT_DEFINITION` takes no database argument, so it has no cross-database path at all (real's Msg 916 in that shape comes from the `OBJECT_ID` feeding it).

### Principal DDL

- `CREATE USER name [{FOR | FROM} ...] [WITH option = value, …]` — name + principal_id allocation; `type_code='S'`.
  `FOR / FROM LOGIN` and `WITHOUT LOGIN` are read, a user with no source clause at all maps to the login of its own name, and a login the server doesn't know is **Msg 15007** either way (probed 2026-09-29 and 2026-09-30 against SQL Server 2025); the `WITH` list records `DEFAULT_SCHEMA` (as written, even when no such schema exists) and reads the other options without effect; anything else parses-and-discards through the next statement boundary.
- `ALTER USER name WITH option = value, …` — `NAME` renames, `DEFAULT_SCHEMA` sets the default schema, the rest are read without effect; a missing user is **Msg 15151** state 1 and a taken name **Msg 15023** state 10.
  The default schema is catalog-only: an unqualified name still resolves through `dbo` for every user (not built yet).
- `CREATE ROLE name [AUTHORIZATION owner]` — `type_code='R'`.
  `AUTHORIZATION` sets `owning_principal_id`; an unknown owner is Msg 15151's *user* wording (probed 2026-09-27 against SQL Server 2025).
- `ALTER ROLE name { ADD MEMBER name | DROP MEMBER name | WITH NAME = newname }` — ADD/DROP MEMBER append/remove `(role_id, member_id)` on `Database.RoleMembers`, refusing `dbo` (**Msg 15405**), the role itself (**Msg 15413**) and `[public]` (**Msg 15081**); a name that isn't a role is `Cannot alter the role` and a missing member `Cannot add` / `Cannot drop the principal` (all Msg 15151 state 1); `WITH NAME` renames, a taken name being **Msg 15023** state 10.
  `sp_addrolemember` / `sp_droprolemember` run the same change, differing only in a missing member to add (**Msg 15410**, class 11); both spellings raise `ADD_ROLE_MEMBER` / `DROP_ROLE_MEMBER` naming the member (probed 2026-09-25 against SQL Server 2025).
- `DROP USER [IF EXISTS] name` and `DROP ROLE [IF EXISTS] name` — drop from `Database.Principals`; a user's memberships go with it, while a role that still has members is **Msg 15144**.
  All probed 2026-09-25 against SQL Server 2025.
  Each sees only its own kind: a role named to `DROP USER`, or a user to `DROP ROLE`, reads as missing (`Cannot drop the user 'x', …`, `Cannot drop the role 'x', …`), and a missing grantee or `USER::` securable of `GRANT` / `DENY` / `REVOKE` is `Cannot find the user 'x', …`, while `ALTER ROLE … ADD MEMBER` keeps `Cannot find the user, login, role, or principal` and the application-role statements say `Cannot alter` / `Cannot drop the application role` (probed 2026-09-29).
  A principal that still owns something can't be dropped — see [Ownership](#ownership).
  Dispatched ahead of the generic DROP-target switch in `Simulation.Drop.cs` because principals don't live in a per-schema dict.

### Ownership

Probed 2026-09-27 against SQL Server 2025 throughout.

**Who owns what.**
An object, alias or table type, or XML schema collection carries an explicit owner only once `ALTER AUTHORIZATION` gives it one (`SchemaObject.OwnerPrincipalId`, `sys.objects.principal_id`); until then — and again after `TO SCHEMA OWNER` — it is owned by its schema's owner.
A trigger or constraint is owned through its parent.
`Ownership.EffectiveOwnerId` is the one answer `OBJECTPROPERTY(…, 'OwnerId')`, `TYPEPROPERTY(…, 'OwnerId')`, `sp_help`'s `Owner`, the checker and the chain all read.
A role has an owner (`owning_principal_id`, dbo by default), itself allowed; a schema, a full-text catalog and an assembly have one each; a database is owned by a **login** (`Database.OwnerLoginName`, `sa` unless another registered login ran the `CREATE DATABASE`).

**`ALTER AUTHORIZATION ON [<class>::]<entity> TO {<principal> | SCHEMA OWNER}`** (`Simulation.AlterAuthorization.cs`) covers `OBJECT` (the default), `SCHEMA`, `TYPE`, `XML SCHEMA COLLECTION`, `ROLE`, `FULLTEXT CATALOG`, `ASSEMBLY` and `DATABASE`, plus `sp_changedbowner`.
Any database principal may own a database-scoped securable — users, `guest`, `dbo`, roles (fixed ones and `[public]` included) and application roles — but not `sys` / `INFORMATION_SCHEMA`; `SCHEMA OWNER` is accepted only for an object, type or XML schema collection.
The refusals, each an error factory with its number: a trigger or constraint (Msg 15346), a temp table, a `USER::` or an `APPLICATION ROLE::` (Msg 15344), the four fixed schemas `dbo` / `guest` / `sys` / `INFORMATION_SCHEMA` (Msg 15150; the fixed-role schemas are movable), a system type (Msg 15247), `master` / `model` / `tempdb` (Msg 15109), a database owner that is a server role (Msg 15353) or already a user there (Msg 15110), and Msg 15151 for everything not found — worded by the class looked for, and a three-part object name is never found.
The entity resolves before the new owner.
A restricted caller needs `TAKE OWNERSHIP` on the entity (else the entity's own not-found wording) and must be the new owner, a member of it, or hold `IMPERSONATE` on it (else `Cannot find the principal`, state 1).
A change of **effective** owner drops every permission granted on the securable — objects, schemas and roles alike — while naming the owner it already has (`TO dbo` on a dbo-schema table) keeps them.
Every change rolls back with the transaction, and each raises `ALTER_AUTHORIZATION_DATABASE` with an `OwnerName` element — a database's too, with `ObjectType` `DATABASE` and an empty `SchemaName`, fired in the *session's* database whichever database changed hands (probed 2026-09-27 against SQL Server 2025).
Changing a database's owner additionally asks a caller short of `dbo` — a `db_owner` member included — for `IMPERSONATE` on the new owner's login (`sa` included), refusing with `Cannot find the principal` ahead of Msg 15110.

**What an owner gets.**
An owner holds `CONTROL` that no `DENY` removes, and so does every member of a role that owns (`PermissionChecker.OwnsSecurable`): an object's owner or its schema's owner reads, writes, alters and sees the metadata of the object with no grant.
A principal that owns anything can't be dropped, and real checks in a fixed order — objects (Msg 15183), types (Msg 15184), schemas (Msg 15138), roles including itself (Msg 15421), then XML schema collections and full-text catalogs (Msg 15138's other wordings); `DROP APPLICATION ROLE` takes the same checks.
A login that owns a database connects to it as `dbo`, can't also be given a user there (Msg 15063), and can't be dropped (Msg 15174).

**Ownership chaining compares owners.**
A procedure, scalar function or DML trigger body records its module's effective owner (`BatchContext.OwnershipChainOwnerId`; a trigger's is its table's), and a same-database reference whose object has a different owner is checked against the caller as though no module intervened — Msg 229 naming the base object, with the module's Procedure attribution.
A view or inline TVF, inlined into the referencing statement, checks its body's other-owner reads once at invocation (`PermissionEnforcement.CheckModuleBodyReads`), and DML through a single-table updatable view checks the base table after the view when the two owners differ — even from a module body whose reference to the view is chained.
A **view over a view** is a chain of links, each object's owner compared with the next's (`View.UpstreamView` / `UpstreamColumnOrdinals` carry the per-level link `BaseTable` composes away): every object whose owner differs from its predecessor's needs the permission itself, in order, so `dbo.v1` over `u1`'s `v2` over `dbo`'s table needs SELECT on `v2` and then on the table, while `dbo.v1` over `u1`'s `v2` over `u1`'s table needs only `v2` (probed 2026-09-29 against SQL Server 2025 for `SELECT`, `UPDATE`, `INSERT`, `DELETE` and `MERGE`; the read path already walked the same rule).
`INSERT` and `DELETE` check the write object-grain (`CheckBrokenChainWrite`); `UPDATE`'s SELECT and UPDATE and `DELETE`'s SELECT are column-grain on the base columns the view's columns read (`CheckBrokenChainColumns`, Msg 230 naming the base column); `MERGE` checks SELECT and each action's permission object-grain.
Through a **join view** the chain is judged link by link, top down, and stops at the first link whose owners differ and whose permission is missing (probed 2026-09-29 against SQL Server 2025, `CheckJoinViewBrokenChains` / `CheckJoinViewInsertChain`).
An UPDATE needs, on each view below the one named whose owner differs from the one above it, SELECT on the columns read through it and then UPDATE at object grain — real reports both where both are missing, SELECT first — and a view the join only *reads* takes the SELECT alone.
Under the join view, every other-owner base table is checked for SELECT on the columns the statement reads of it — its join and filter columns included, so an UPDATE writing the other table still needs SELECT on the join column — and the written table for UPDATE column-grain.
A join view reading another join view descends into it, so the tables under the inner one are judged from the inner view's owner; an INSERT checks INSERT at object grain on each differing view and then on the table written.
A `MERGE` through a join view takes the same walk with each action's own permission in the UPDATE's place — SELECT on the columns the `ON`, `WHEN` conditions and `SET` values read, then INSERT / UPDATE / DELETE on each differing view (object grain) and on the table written, INSERT and DELETE at object grain and UPDATE column-grain (probed 2026-09-30 against SQL Server 2025, one action per statement; a statement with several action kinds checks them in INSERT, UPDATE, DELETE order, unprobed).
A single-table view a join view reads is a link of its own, refused and granted as a view over another owner's join view is.
**Where several denied objects are reachable, real names the last one bound** — a join over two ungranted tables names the second, a subquery's the subquery's, and `INSERT … SELECT` the source's — so `CheckReadSources` and `CheckModuleBodyReads` walk their lists backwards (probed 2026-09-29: joins, subqueries, unions, view bodies).
A `SELECT` through a view checks the body's other-owner reads with the statement (`ReferencedSecurable.ModuleReads`, carried by the view's own reference and nested through views it reads), so the refusal arrives from `ExecuteReader` rather than the first `Read`; an inline TVF's body is still checked when it is invoked.
Under an `INSTEAD OF` trigger an `INSERT` checks nothing on the base, while an `UPDATE` or `DELETE` still needs SELECT on every base column for the pseudo-tables.
All probed 2026-09-27 against SQL Server 2025, which raises the SELECT denial and the write denial together where the simulator raises the first.

**`WITH EXECUTE AS OWNER`** runs the body as the module's effective owner; an owner that is a role or an application role can't be impersonated, which is Msg 15517 naming it (a procedure's at line 0 under its unqualified name).

**Catalog.**
`principal_id` reports the explicit owner in `sys.objects` / `all_objects` / `tables` / `views` / `procedures` / `sequences` / `synonyms` / `types` / `table_types` / `xml_schema_collections`, but stays NULL on a table type's `TT` row; `sys.schemas.principal_id` and `INFORMATION_SCHEMA.SCHEMATA.SCHEMA_OWNER` follow the schema owner, `sys.databases.owner_sid` / `dbo`'s `sid` / `sp_helpdb`'s `owner` / `sp_helpuser`'s dbo `LoginName` the database owner.

**Divergences.**
The assembly gate asks for `TAKE OWNERSHIP` on the database, since `GRANT` models no assembly class; the type, XML-schema-collection and full-text-catalog gates ask on the securable itself (its schema and the database cover it), and a change of effective owner drops that securable's grants (probed 2026-09-29 against SQL Server 2025).
A `WITHOUT LOGIN` owner's or creator's `SYSTEM_USER` under `EXECUTE AS OWNER` / `SELF` is the deterministic SID every such user reports, where real's is random.

**Not modeled yet.**
The `ALTER AUTHORIZATION` classes past the eight above raise `NotSupportedException`.
An `INSERT … SELECT` over two denied objects raises the target's INSERT denial with the source's, where real names only the source.
Server-scope DDL triggers aren't modeled, so `ALTER AUTHORIZATION ON DATABASE` raises no `ALTER_AUTHORIZATION_SERVER` event.

### Legacy security procedures

Probed 2026-09-30 against SQL Server 2025.
A login carries a default database, default language and the `CHECK_POLICY` / `CHECK_EXPIRATION` flags.
`CHECK_POLICY = ON` enforces a minimum length of eight (Msg 33062) and three of the four character classes without the login name in it (Msg 33064).
The legacy procedures (`sp_addlogin`, `sp_password`, `sp_adduser`, `sp_addrole`, `sp_change_users_login` and their siblings) build and run the ordinary statement real does, and raise real's own errors from real's line numbers; see [`catalog-views.md`](catalog-views.md#system-procedures-over-the-registries).
A user is an orphan when its login link no longer matches the login of its name.
A `CREATE LOGIN` `SID` is derived from the name, so `sp_addlogin @sid` is ignored.

### Server logins (`Simulation/Simulation.LoginDdl.cs`)

Server-scope, stored in `Simulation.Logins` (`ConcurrentDictionary<string, ServerLogin>`, `BuiltInToken.Comparer` — the same case-insensitive keying as the sibling server-scope dicts, a slight divergence from real keying by server collation).
Each `ServerLogin` is immutable (name, password hash, create date, password-last-set date); mutations replace the entry wholesale so the TDS endpoint's concurrent reads see a consistent hash.
The hash uses the legacy `0x0200` single-pass-SHA-512 format (`PasswordHash.EncryptLegacy`) rather than PWDENCRYPT's `0x0300` PBKDF2: never-persisted hashes gain nothing from 100k-iteration hardening, which would otherwise bill every TDS connection open ~50ms.
`PasswordHash.Verify` dispatches on the version tag, so both forms verify; the T-SQL `PWDENCRYPT` keeps emitting `0x0300`.
In-process connections never authenticate — login DDL through one is how the registry is seeded.

- `CREATE LOGIN name WITH PASSWORD = '…' [MUST_CHANGE] [, option …]` — only the SQL-auth clear-text form is modeled; the option tail (CHECK_POLICY / CHECK_EXPIRATION / DEFAULT_DATABASE / DEFAULT_LANGUAGE / SID / CREDENTIAL) parses-and-discards.
  `FROM WINDOWS` / certificate / asymmetric-key / external-provider forms and `PASSWORD = 0x… HASHED` raise `NotSupportedException`.
  A password over SQL Server's documented **128-character cap** raises Msg 6607 (CREATE and ALTER alike) — **approximate**: 6607 is the password-machinery error probe-confirmed on the `PWDENCRYPT` cap, but real's CREATE LOGIN rejection shape is unverifiable from the reference instance (its login hits the Msg 15247 permission wall before password validation).
- `ALTER LOGIN name WITH PASSWORD = '…'` re-hashes and stamps `PasswordLastSetTime` (readable via `LOGINPROPERTY`).
  `ALTER LOGIN name DISABLE` / `ENABLE` set the login's `IsDisabled`, which `is_disabled` projects and the [login gate](#the-login-gate) refuses; the other `WITH` options parse-and-discard after the existence check.
- `DROP LOGIN name` — **no `IF EXISTS` clause**: real SQL Server's DROP LOGIN grammar rejects it (probe-confirmed Msg 156 near 'IF'), reproduced verbatim — a reserved keyword in any of the three login-name positions raises the keyword-flavored Msg 156, not the generic Msg 102.
- **`sa` resolves without being in the registry.**
  The registry has to stay *empty* in a simulation nobody created a login in, because the TDS endpoint reads an empty registry as "accept any credentials" — so `sa` is a fixed login the catalog views synthesize rather than a `Logins` entry, the way `EXECUTE AS`, the GRANT family, `sp_addsrvrolemember` and the server-role paths already resolve it by name.
  `ALTER LOGIN [sa]` therefore resolves by name too (real accepts it — probe-confirmed with `DEFAULT_LANGUAGE`), and every option parses and discards for it, `DISABLE` included.
  `ALTER LOGIN [sa] WITH PASSWORD` raises `NotSupportedException`: recording it would mean adding `sa` to the registry, which flips the endpoint from accepting any credentials to enforcing them — a large behavioural change to fall out of a password change.
  `CREATE LOGIN` collides against every *server principal*, not just a previously created login, so `CREATE LOGIN [sa]` / `[public]` / a fixed server-role name is Msg 15025 rather than a second row the catalog views would project alongside the built-in.
  `DROP LOGIN [sa]` stays Msg 15151; real refuses it too, but the exact message is unprobed — running it against the reference instance risks the account the harness connects with.

| Msg | When | Provenance |
|---|---|---|
| 15025 | Duplicate `CREATE LOGIN` name: `The server principal 'x' already exists.` | Docs-derived — the reference login lacks the server permission to reach the duplicate check (Msg 15247 fires first). |
| 15151 | `ALTER LOGIN` / `DROP LOGIN` on a missing login: `Cannot {alter\|drop} the login 'x', because it does not exist or you do not have permission.` | Probe-confirmed — distinct wording from the database-principal 15151 (`CannotFindPrincipal`). |

### Server roles + server-scope permissions (`Simulation/Simulation.ServerRoles.cs`)

Server scope outlives any database, so its registries live on `Simulation`.
- **Fixed server roles** (`Simulation.FixedServerRoles`, probe6 N1) seed `sys.server_principals` at their real ids 3–20 (`sysadmin`=3 … `##MS_ServerPerformanceStateReader##`=20; `public` stays id 2 with `is_fixed_role 0`). User server principals — created logins **and** custom server roles — take ids from **258** via `AllocatePrincipalId` (real reserves the block past the fixed roles; observed 258+).
- `CREATE SERVER ROLE x` (→ `Simulation.ServerRoles`, `type R`, `is_fixed 0`), `ALTER SERVER ROLE r { ADD | DROP } MEMBER l` (→ `Simulation.ServerRoleMembers`, works for fixed and custom roles), `DROP SERVER ROLE x` (dropping a fixed role → **Msg 15150**). `SERVER` isn't a reserved keyword, so the CREATE / ALTER / DROP dispatchers match a `Name`-guard case. Errors are the 15151 family: unknown role `Cannot alter the server role '<r>'…`; unknown member `Cannot add the server principal '<l>'…`; unknown grantee login `Cannot find the login '<l>'…`.
- **sysadmin semantics** (probe6 N3): a sysadmin-member login (incl. `sa`) maps to dbo in every database (see [Authentication](#session-principal--impersonation)); `IsLoginSysadmin` walks the `ServerRoleMembers` closure, which starts with `sa`'s own `sysadmin` row as real's does.
  What every other fixed role carries is in [Server permissions and the fixed server roles](#server-permissions-and-the-fixed-server-roles).
- **`IS_SRVROLEMEMBER`** reads the registry: `public` → 1; a sysadmin member → 1 for **every fixed** server role (N2); real membership → 1/0; a non-role name → NULL; the 2-arg form looks up the named login (an unknown named login → NULL).
- **Server-scope GRANT / DENY / REVOKE** — three routes into `ApplyServerScopeGrant`: an ON-less GRANT whose permissions are all recognized SERVER-class names (`CONNECT SQL`, `VIEW SERVER STATE`, …), an explicit `ON SERVER::<name>`, or an `ON LOGIN::<name>`.
  Legal only when the current database is `master` (**Msg 4621**, severity 16 **state 10**, no trailing period — elsewhere), stored in `Simulation.ServerPermissions`, which starts with real's two class-100 rows, `sa`'s `CONNECT SQL` and `public`'s `VIEW ANY DATABASE`.
  `CREATE LOGIN` auto-seeds a `CONNECT SQL` G row (N4b).
  **Server-scope DENY replaces the prior G row** (N4 — divergent from database scope, where G + D coexist); REVOKE removes the rows.
  Every stored row is enforced — see [Server permissions and the fixed server roles](#server-permissions-and-the-fixed-server-roles).
  - **Class 100** (`class_desc` `SERVER`, `major_id` 0) — the ON-less and `ON SERVER::` forms.
    `ON SERVER::<name>` is an **alias of the ON-less form and its name is ignored** (probe-confirmed: real accepts any name there and stores the same row).
    Type codes come from `PermissionCatalog`, which carries all 51 SERVER-class permissions (`CONNECT SQL`→`COSQ`, `VIEW SERVER STATE`→`VWSS`, …).
  - **Class 101** (`class_desc` `SERVER_PRINCIPAL`, `major_id` = the target login's `principal_id`) — the `ON LOGIN::` form; see below.
    An unknown login there raises the Msg 15151 `CannotFindLogin` variant.
  - A permission name in `PermissionCatalog` projects its **canonical uppercase spelling** regardless of the GRANT's casing (matching real, and matching the database-scope path); an off-catalog name keeps its raw text.

### `ON LOGIN::` securables

`GRANT | DENY | REVOKE <perm> ON LOGIN::<login> TO <principal>` stores a **class 101** row (`class_desc` `SERVER_PRINCIPAL`) whose `major_id` is the *target* login's `principal_id`.
Type codes are the ordinary `PermissionCatalog` ones — `IMPERSONATE`→`IM`, `ALTER`→`AL`, `VIEW DEFINITION`→`VW`, `CONTROL`→`CL` (all probe-confirmed against `sys.server_permissions`).

`Simulation.HoldsServerPrincipalPermission(login, targetPrincipalId, permission, blanketEquivalent)` is the checker.
It is the same DENY-first / GRANT scan over the login's server-principal closure that `HoldsServerPermission` runs (which is now a thin wrapper on it), except that a request carries both a per-login permission and the **server-wide permission that covers every login**:

| Per-login (class 101) | Blanket equivalent (class 100) |
|---|---|
| `IMPERSONATE` | `IMPERSONATE ANY LOGIN` |
| `VIEW DEFINITION` | `VIEW ANY SECURITY DEFINITION` (which `VIEW ANY DEFINITION` covers) |
| `ALTER` | `ALTER ANY LOGIN` |

A class-101 row answers only when it names the same target, and covers through the **object-class** graph (so `CONTROL ON LOGIN::x` covers all three); a class-100 row answers through the server-class graph, so `CONTROL SERVER` covers every blanket.
**DENY over either class binds first**, so `DENY IMPERSONATE ON LOGIN::x` beats `GRANT IMPERSONATE ANY LOGIN` (probe-confirmed).

Three gates consume it: `EXECUTE AS LOGIN` (IMPERSONATE — a login may always impersonate itself, probed 2026-09-29), [server-principal metadata visibility](#server-principal-metadata-visibility) (VIEW DEFINITION / ALTER / IMPERSONATE), and [login DDL](#login-ddl-gating) (ALTER); `HAS_PERMS_BY_NAME(<login>, 'LOGIN', …)` and `fn_my_permissions(<login>, 'LOGIN')` read it too.

### Server-principal metadata visibility

The server-scope analogue of the database [Metadata visibility](#metadata-visibility) rules, applied to `sys.server_principals` and `sys.sql_logins`.
A **restricted** session (non-`dbo` effective principal; dbo / sysadmin short-circuit on one bool read before any allocation) sees a row only when `Simulation.CanViewServerPrincipal(login, targetPrincipalId)` says so:

- the **fixed block is always visible** — `sa` (1), `public` (2) and the 18 fixed server roles (3–20), 20 rows;
- its **own** login row;
- a **server role it belongs to** (transitively);
- any login it holds `VIEW DEFINITION`, `ALTER` or `IMPERSONATE` on — per-login (class 101) or through the blanket class-100 equivalent.

Probe-confirmed: a freshly created login sees only itself past the fixed block; `ALTER ON LOGIN::x` reveals x; `VIEW ANY DEFINITION` reveals every login; and a `DENY VIEW DEFINITION ON LOGIN::x` **re-hides x under a blanket grant** — DENY hides at server scope as it does at database scope (see [Metadata visibility](#metadata-visibility)).

The filter is `BuiltInResources.ServerPrincipalVisibility(batch)`, returning `null` for the full-visibility fast path and a per-`principal_id` predicate otherwise; both row generators apply it.
`sys.server_permissions` shows the rows whose grantee it can see — `sa`'s, `public`'s and its own for a bare login — and `sys.server_role_members` every fixed role's memberships whoever the member, a custom role's only where both the role and the member are visible (probed 2026-09-29 against SQL Server 2025).
`sp_helpsrvrolemember` reads through the same two views.

### Login DDL gating

Login DDL is server-scope, so a restricted session needs `ALTER ANY LOGIN` (class 100) or `ALTER ON LOGIN::<target>` (class 101):

| Statement | Gate | Denial |
|---|---|---|
| `CREATE LOGIN` | server-wide `CREATE LOGIN`, which `ALTER ANY LOGIN` covers (there is no per-login target); the creator gets no `ALTER` on the login it made (probed 2026-09-29) | **Msg 15247** `User does not have permission to perform this action.` |
| `ALTER LOGIN <l>` | `ALTER` on `l` | **Msg 15151** — the *same* `Cannot alter the login '<l>'…` wording a missing login gets, leaking nothing |
| `DROP LOGIN <l>` | `ALTER` on `l` | **Msg 15151** `Cannot drop the login '<l>'…` |

All probe-confirmed.
dbo / sysadmin bypass, so the existing login-DDL corpus (which seeds registries from an unauthenticated in-process connection) is unaffected.

### Application roles

A password-protected database principal a session activates with `sp_setapprole`, swapping its database identity wholesale.
`Simulation/Simulation.ApplicationRoles.cs`.

**DDL.**
- `CREATE APPLICATION ROLE <n> WITH PASSWORD = '…' [, DEFAULT_SCHEMA = <s>]` — a `DatabasePrincipal` with `type` `A` / `type_desc` `APPLICATION_ROLE`, `is_fixed_role` 0, `owning_principal_id` NULL, `default_schema_name` defaulting to `dbo`.
  A duplicate name raises **Msg 15023** like any other principal.
- `ALTER APPLICATION ROLE <n> WITH { NAME = <new> | PASSWORD = '…' | DEFAULT_SCHEMA = <s> } [, …]` — a rename re-keys `Database.Principals` but **preserves the `principal_id`**, so grants and role memberships follow the role.
- `DROP APPLICATION ROLE <n>` — drops the principal and cascades its `Database.RoleMembers` entries, like `DROP ROLE`.
- All three are gated on db-scope `ALTER ANY APPLICATION ROLE` (`db_securityadmin` carries it): Msg 15247 for CREATE, the `Cannot alter` / `Cannot drop the application role` Msg 15151 wording for the other two (probed 2026-10-04 against SQL Server 2025).
  A password failing the policy check is refused before the name is taken, and a name taken by another principal is Msg 15023 state 11 on create, state 13 on rename.
- An application role can be a **member of a database role** (`ALTER ROLE db_datareader ADD MEMBER app1`), and the membership flows through the ordinary role closure.

**The context swap.**
`EXEC sp_setapprole '<role>', '<password>' [, @fCreateCookie = 1] [, @cookie = @c OUTPUT]` replaces the session's **base** frame (not an impersonation push) with the role's principal, keeping the login:

- `USER_NAME()` / `CURRENT_USER` / `USER_ID()` / `DATABASE_PRINCIPAL_ID()` → the application role;
- `SUSER_NAME()` / `SYSTEM_USER` / `ORIGINAL_LOGIN()` → **unchanged**, still the login;
- the pre-activation user's own grants **stop applying** — only the role's own grants plus `public` (probe-confirmed: a table granted to the pre-activation user raises Msg 229 after activation, one granted to `public` still reads);
- the session is **pinned to its database**: `USE` / `ChangeDatabase` raises **Msg 505**;
- there is **no way back without the cookie** — `sp_setapprole` with no `@fCreateCookie` / `@cookie OUTPUT` pins the session for its lifetime.

`SessionSecurityContext` carries `ApplicationRoleName` / `ApplicationRoleCookie` / `HasApplicationRole` plus the pre-activation frame; `SetApplicationRole` / `TryUnsetApplicationRole` are the pair.
The cookie is 50 opaque random bytes, matching real's `varbinary` width.
`EXEC sp_unsetapprole @c` restores the pre-activation principal (and releases the database pin); a non-matching cookie, or no role set, raises **Msg 15592**.

| Msg | When |
|---|---|
| 15161 | `sp_setapprole` on a missing role **or** with the wrong password — real leaks no distinction: `Cannot set application role '<r>' because it does not exist or the password is incorrect.` |
| 2762 | `sp_setapprole` on a session that already has one set: `sp_setapprole was not invoked correctly. Refer to the documentation for more information.` |
| 15002 | `sp_setapprole` inside a user transaction |
| 15431 | `sp_setapprole` with a NULL role name |
| 15600 | `sp_setapprole` with an `@encrypt` other than `'none'` / `'odbc'` |
| 15422 | `sp_setapprole` from inside a module or dynamic SQL — application roles activate only at the ad hoc level |
| 15592 | `sp_unsetapprole` with no role set or an invalid cookie: `Cannot unset application role because none was set or the cookie is invalid.` |
| 505 | `USE` / `ChangeDatabase` while a role is active: `The current user account was invoked with SETUSER or SP_SETAPPROLE. Changing databases is not allowed.` |

All probe-confirmed against SQL Server 2025.

**Divergences.**
- `@encrypt = 'odbc'` with the ODBC `{Encrypt N'…'}` escape around the password is Msg 155 here, where real accepts the escape (probed 2026-10-04 against SQL Server 2025).
- **Pooled-connection reset**: real *refuses* to reset a connection with an active application role and kills the session — a reopen from the pool fails with **Msg 596, class 21** (`Cannot continue the execution because the session is in the kill state.`), probe-confirmed over SqlClient.
  The simulator's TDS `ResetConnection` rebuilds the connection from the original login, so the role is simply **cleared** and the pooled connection stays usable.
  The simulator is the more forgiving side; a consumer relying on real's poisoning behavior would diverge.
- `sp_setapprole` under `EXECUTE AS` replaces the base frame beneath the impersonation, so `USER_NAME()` still reports the impersonated user, where real reports the application role (probed 2026-10-04 against SQL Server 2025).

### DMV server-state gating

A restricted session (any non-`dbo` effective principal — a mapped user or `guest`; sysadmin logins map to `dbo` and bypass) reading a modeled DMV is gated by the `VIEW …STATE` permissions (probe-confirmed against SQL Server 2025).
The `VIEW …STATE` permission enum, type codes (`VIEW SERVER STATE`→`VWSS`, `VIEW SERVER PERFORMANCE STATE`→`VSP `, `VIEW SERVER SECURITY STATE`→`VSS `, `VIEW DATABASE STATE`→`VWDS`, `VIEW DATABASE PERFORMANCE STATE`→`VDP `), and covering graph live in `Permission.cs`; the covering edges are: `VIEW SERVER STATE` covers `VIEW SERVER PERFORMANCE STATE` / `VIEW SERVER SECURITY STATE` (server scope), `VIEW DATABASE STATE` covers `VIEW DATABASE PERFORMANCE STATE` (database scope), and cross-scope a covering server permission satisfies the database requirement.

`ServerPermissionChecker.Holds(simulation, login, permission)` is the server-scope counterpart to `PermissionChecker` — sysadmin bypass, then a DENY-first / GRANT scan over the login's server-principal closure (`Simulation.BuildServerPrincipalClosure`: the login's server-principal id + its transitive server-role memberships + `public`) with the server-scope covering graph, over `Simulation.ServerPermissions`.
It also answers the cross-scope database-state requirement (a database `VIEW …STATE` need met by a covering server permission), so the DMV gate consults one method for both.
The fixed server roles and `CONTROL SERVER` feed it like any grant, so a `##MS_ServerStateReader##` member reads every session and a `CONTROL SERVER` grantee the server DMVs — unless a `DENY` binds, which beats `CONTROL SERVER` (probed 2026-09-29 against SQL Server 2025).

The gate hangs off a per-DMV `CatalogView.DmvGate` descriptor (`DmvGateKind`), set once at registration in `BuiltInResources.DmvGating.cs` (analogous to bundle 2's `MetadataVisibilityKey`), and is applied in `BuiltInResources.ApplyDmvGate` from both `Selection.ForCatalogView` overloads.
The `dbo` / sysadmin fast path short-circuits on `SessionSecurityContext.EffectiveIsDbo` before any allocation, so existing in-process DMV reads pay one bool read and are byte-identical.

| DMV | Gate | Denial |
|---|---|---|
| `sys.dm_tran_locks`, `sys.dm_os_waiting_tasks`, `sys.dm_os_sys_info`, `sys.dm_exec_input_buffer` (even for the caller's own session), `sys.dm_tran_version_store`, `sys.dm_tran_version_store_space_usage`, `sys.dm_tran_active_snapshot_database_transactions`, `sys.dm_tran_active_transactions`, `sys.dm_tran_session_transactions`, `sys.dm_tran_current_transaction`, `sys.dm_hadr_cluster` | server-scope — `VIEW SERVER PERFORMANCE STATE` (covered by `VIEW SERVER STATE`) | **Msg 300** sev 14 state 1: `VIEW SERVER PERFORMANCE STATE permission was denied on object 'server', database '<db>'.` |
| `sys.dm_exec_connections`, `sys.dm_exec_sql_text` | server-scope, as above | **Msg 371** sev 14 state 3, naming the external policy action as well as the permission (probed 2026-09-25) |
| `sys.dm_db_partition_stats`, `sys.dm_hadr_database_replica_states` | database-scope — `VIEW DATABASE PERFORMANCE STATE` at db scope, or a covering server permission cross-scope | **Msg 262** sev 14 state 1: `VIEW DATABASE PERFORMANCE STATE permission denied in database '<db>'.` |
| `sys.dm_exec_sessions`, `sys.dm_exec_requests` | self-filter — restricted sessions without `VIEW SERVER STATE` see only their own SPID's row (a row filter, not a hard denial) | — |
| `DBCC INPUTBUFFER` | its own session open to all; another session's takes `VIEW SERVER STATE` | **Msg 2571** sev 14 state 10 naming the user (probed 2026-09-25) |
| `sys.dm_os_host_info`, `sys.fn_helpcollations`, `sys.dm_db_xtp_table_memory_stats` | ungated (probe: readable by `guest`) | — |

Real also raises a trailing **Msg 297** after the 300 / 262; the simulator surfaces the single 300 / 262.

| Msg | When |
|---|---|
| 15150 | `DROP SERVER ROLE` on a fixed role: `Cannot drop the server role 'sysadmin'.` |
| 4621 | Server-scope GRANT / DENY / REVOKE outside `master`. |

### Server permissions and the fixed server roles

Every stored server permission is enforced for a login that isn't `sysadmin`, and the fixed server roles carry what real's carry.
All probed 2026-09-29 against SQL Server 2025 with dedicated probe logins; the differential cases (`.vs/edge-probe`, `p_srvperm.sql`) match real save for the environmental and documented residue below.

**The model.**
`PermissionCatalog` carries all 51 SERVER-class permissions with the covering graph `sys.fn_builtin_permissions('SERVER')` lists — `CONTROL SERVER` at the top, `VIEW SERVER STATE` under `ALTER SERVER STATE`, `CREATE ANY DATABASE` under `ALTER ANY DATABASE`, `EXTERNAL ACCESS ASSEMBLY` under `UNSAFE ASSEMBLY`, the `VIEW ANY …` family under `VIEW ANY DEFINITION`.
`Simulation.HoldsServerPermission` answers for a login: `sysadmin` passes, then a `DENY` anywhere in the login's server-principal closure binds — `CONTROL SERVER` included — then a grant, then a fixed role's own grants (`Simulation.FixedServerRoleGrants`), whose covering closure is exactly what `fn_my_permissions(NULL, 'SERVER')` lists for a member of each role.
Real keeps those role grants out of `sys.server_permissions`, and so does the simulator.
The quirks that closure preserves: `securityadmin` carries only `ALTER ANY LOGIN` (and `CREATE LOGIN` under it), `serveradmin`'s `ALTER SERVER STATE` brings the three `VIEW SERVER … STATE` permissions, and `##MS_DefinitionReader##` gets no `VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION`, whose cover is `CONTROL SERVER` alone.

`Simulation.SessionHoldsServerPermission` is the question every server-scope statement asks of the session: an identity minted inside one database (`EXECUTE AS USER`, a module's own frame, an application role) holds no server permission at all, the empty-registry dev mode holds every one, and a `sa` session answers on one name compare.

**Into the databases.**
A server permission implies database permissions through each one's parent in `sys.fn_builtin_permissions`, which the checker's fifth step reads (`ServerLoginRights`; see [Enforcement](#enforcement-execution-time)): `CONTROL SERVER` reads, writes and runs DDL everywhere a user of the login's reaches, `ALTER ANY DATABASE` alters each database, `VIEW ANY DEFINITION` reveals every catalog, and `SELECT ALL USER SECURABLES` reads and reveals every user object without writing one.
Only `CONNECT ANY DATABASE` (and `CONTROL SERVER` over it) gets a login with no user into a database, as the principal-id-0 user the [authentication order](#session-principal--impersonation) describes; a `SELECT ALL USER SECURABLES` login without a user there is still Msg 916.
A `CONTROL SERVER` grantee is not `sysadmin` — `IS_SRVROLEMEMBER('sysadmin')` is 0, a database `DENY` binds it, and the `sysadmin`-only commands below refuse it — though it can `EXECUTE AS LOGIN = 'sa'` and become one.
The trustworthy-crossing authenticator counts `AUTHENTICATE SERVER` as its database `AUTHENTICATE`.

#### The login gate

`Simulation.RefuseLogin` is the one check both front doors run once a login exists: an unknown login or a wrong password is Msg 18456, a disabled one Msg 18470 (`Login failed for user '<l>'. Reason: The account is disabled.`, and only for the right password — a wrong one is still 18456), and a login holding no `CONNECT SQL` Msg 18456 again, whether it was denied or its grant revoked.
A server role's grant or `CONTROL SERVER` supplies `CONNECT SQL`, and a `sysadmin` member connects however it is denied.
The client sees state 1 for every cause.
`EXECUTE AS LOGIN` reaches a disabled or connect-denied login all the same.

#### Database visibility

`sys.databases`, `DB_ID(name)` and `DB_NAME(id)` show a database to a session only when it holds `VIEW ANY DATABASE`, or the database is `master`, `tempdb`, the current one or one its login owns (`Simulation.CanSeeDatabase`).
Since `public` holds `VIEW ANY DATABASE` from the start, a `DENY` of it is what hides the others — `DB_ID('msdb')` then answers NULL — and so does an identity minted inside one database, which sees `master`, `tempdb` and its own database.
A `DENY` binds even a login that also holds `ALTER ANY DATABASE`.

#### Statement gates

| Statement | Server permission | Refusal |
|---|---|---|
| `sp_configure` with a value | `ALTER SETTINGS` (`serveradmin`); reading takes nothing | **Msg 15247**, attributed to `sp_configure` line 105, ahead of the value's range check |
| `RECONFIGURE [WITH OVERRIDE]` | `ALTER SETTINGS` | **Msg 5812** sev 14 |
| `sp_addlinkedserver` / `sp_dropserver` / `sp_serveroption` / `sp_addlinkedsrvlogin` / `sp_droplinkedsrvlogin` | `ALTER ANY LINKED SERVER` (`setupadmin`) | **Msg 15247** once the arguments bind, at each procedure's own line — `sp_addlinkedserver`'s from `sys.sp_MSaddserver_internal` |
| `DBCC FREEPROCCACHE` / `DROPCLEANBUFFERS` / `FREESYSTEMCACHE` | `ALTER SERVER STATE` (`serveradmin`, `processadmin`, `##MS_ServerStateManager##`) | **Msg 2571** |
| `DBCC SQLPERF(LOGSPACE)` / `SQLPERF(<wait or latch DMV>, CLEAR)` | `VIEW SERVER STATE` / `ALTER SERVER STATE` | **Msg 297**, ending the batch and rolling the transaction back, catchable by a `TRY` |
| `DBCC FREESESSIONCACHE` / `TRACEON` / `TRACEOFF` / `LOGINFO` / `HELP` | `sysadmin` alone — neither `CONTROL SERVER` nor `ALTER SERVER STATE` / `ALTER TRACE` | **Msg 2571**, naming the user as `USER_NAME()` does — `public` for a login in through `CONNECT ANY DATABASE` |
| `RAISERROR … WITH LOG` | `ALTER TRACE` | **Msg 2778** |
| `BULK INSERT` | `ADMINISTER BULK OPERATIONS` (`bulkadmin`) | **Msg 4834**, ending the batch uncaught; see [`bulk-and-adhoc.md`](bulk-and-adhoc.md) |
| any file a bulk load reads | `CONTROL SERVER` — the Linux server lets no other login reach a file | **Msg 4860** at state 75 |
| `CREATE` / `ALTER` / `DROP TRIGGER … ON ALL SERVER` | `CONTROL SERVER` | see [DDL statement gates](#ddl-statement-gates) |
| `CREATE SERVER ROLE` | `CREATE SERVER ROLE` (under `ALTER ANY SERVER ROLE`) | **Msg 15247** |
| `ALTER SERVER ROLE <custom> ADD` / `DROP MEMBER`, `DROP SERVER ROLE` | `ALTER ANY SERVER ROLE` | **Msg 15151**, the missing-role wording |
| `ALTER SERVER ROLE <fixed> ADD` / `DROP MEMBER` | `sysadmin`, or a member of that same role — `CONTROL SERVER`, `ALTER ANY SERVER ROLE` and `securityadmin` are all refused | **Msg 15151** |
| `CREATE LOGIN`, `ALTER` / `DROP LOGIN` | see [Login DDL gating](#login-ddl-gating) | |
| `CREATE` / `DROP DATABASE` | see [DDL statement gates](#ddl-statement-gates) | |
| `EXECUTE AS LOGIN` | see [`ON LOGIN::` securables](#on-login-securables) | |


#### Scalars and functions

- `HAS_PERMS_BY_NAME(NULL, NULL, <p>)` asks the server: a SERVER-class permission answers the session's holding of it, anything else — `CREATE TABLE`, `CONNECT`, a name real doesn't know — is NULL, even for `sa`.
  The `SERVER` class answers the same for any non-NULL securable and NULL for a NULL one.
  The `LOGIN` class answers `IMPERSONATE` / `VIEW DEFINITION` / `ALTER` / `CONTROL` on the named login, 0 for a name that is no login and NULL for another permission.
- `fn_my_permissions(NULL, 'SERVER')` lists the session's server permissions (`entity_name` `server`, an empty `subentity_name`) and `fn_my_permissions(<login>, 'LOGIN')` the four on a login, both in `fn_builtin_permissions`' order, bare or `sys.`-qualified.
- `sys.fn_builtin_permissions` projects real's 301 rows verbatim (`BuiltInResources.BuiltinPermissions`): every class for `DEFAULT`, NULL or `''`, one class for its name in any casing, nothing for an unknown one.
- `sp_helpsrvrolemember [@srvrolename]` reports the fixed roles' members (`ServerRole` / `MemberName` / `MemberSID`) through `sys.server_role_members` and `sys.server_principals`, so a restricted session sees what those views show it; `sp_helpsrvrole [@srvrolename]` lists the 18 fixed roles with real's descriptions.
  A name that is no fixed role — a custom one included — is **Msg 15412** at line 10 of each.
- `IS_SRVROLEMEMBER` reads the same membership (a `sysadmin` member is 1 for every fixed role, a `##MS_…##` one included).

#### Divergences

- A `sp_helpsrvrolemember` `MemberSID` is the simulator's synthetic login SID (see `sys.server_principals`).
- Real follows the DMV Msg 300 with a Msg 297; the simulator raises the Msg 300 alone (see [DMV server-state gating](#dmv-server-state-gating)).

#### Not modeled yet

- The permissions whose statements the simulator doesn't have stay catalog truth: `SHUTDOWN`, `ALTER ANY CREDENTIAL`, the endpoint, event-session, event-notification, audit and availability-group families, `ALTER TRACE` past `RAISERROR … WITH LOG`, `ALTER RESOURCES` and `VIEW ANY ERROR LOG`.
- `UNSAFE ASSEMBLY` / `EXTERNAL ACCESS ASSEMBLY` for `CREATE ASSEMBLY`, which waits on `clr strict security` itself (see [`backlog.md`](backlog.md)).
- `HAS_PERMS_BY_NAME`'s `SERVER ROLE` class answers only for a `dbo` session.
- `ALTER LOGIN [sa] DISABLE` is discarded, since `sa` isn't in the registry, and `ALTER LOGIN [sa] WITH PASSWORD` (or `sp_password` on it) meets real's policy check (Msg 33062 for a too-short password, probed 2026-09-30 against SQL Server 2025) and then `NotSupportedException`: recording a password for `sa` would switch the TDS endpoint from accepting any credentials to enforcing them, and real's own answer to a wrong `OLD_PASSWORD` is Msg 15151.
- `KILL` is built (see [`locking.md`](locking.md#kill)), gated on `ALTER ANY CONNECTION`.

## Permission type-code derivation

`PermissionCatalog` (`src/SqlServerSimulator/Permission.cs`) is the single source of truth: one static table indexed by the `Permission` enum carries each member's canonical name, 4-char `sys.database_permissions.type` code (imported from `sys.fn_builtin_permissions` for the common OBJECT / SCHEMA / DATABASE / DATABASE_PRINCIPAL permissions — `SELECT` → `SL`, `UPDATE` → `UP`, `EXECUTE` → `EX`, `CONTROL` → `CL`, `IMPERSONATE` → `IM`, `CREATE TABLE` → `CRTB`, …), and read/write/DDL category; the covering graph and name→enum resolver live alongside it.
Codes are projected space-padded to 4 chars and the view's `type` column is `char(4)`, matching real's trailing-space-bearing values (`'SL  '`).
Names outside the catalog resolve to `Permission.Other` and project their raw stored text plus a first-letter-of-each-word type-code heuristic (`VIEW ANY COLUMN MASTER KEY DEFINITION` → `VACM`), which won't byte-match real for every long name.
Canonical names project their catalog spelling regardless of the GRANT's casing (real normalizes them the same way).

`class_desc` / `state_desc` are spelled out per the probe-confirmed enum:
- `class_desc`: `DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `DATABASE_PRINCIPAL`
- `state_desc`: `GRANT` / `GRANT_WITH_GRANT_OPTION` / `DENY` / `REVOKE`

## Catalog views

In `BuiltInResources.cs`:

**`sys.database_principals`** (14-col probe-confirmed subset): `name` / `principal_id` / `type` / `type_desc` / `default_schema_name` / `create_date` / `modify_date` / `owning_principal_id` / `sid` / `is_fixed_role` / `authentication_type` / `authentication_type_desc` / `default_language_name` / `default_language_lcid` (both NULL — untracked; SMO's User property-bag reads them via `ISNULL(u.default_language_lcid, -1)` / `ISNULL(u.default_language_name, N'')`).
Three of those follow per-principal rules real reports (probe-confirmed against SQL Server 2025):

- `default_schema_name` is `dbo` for `dbo` and every user, `guest` for `guest`, an application role's own declared schema, and NULL for roles and the `sys` / `INFORMATION_SCHEMA` catalog principals.
- `authentication_type` / `_desc` is `1` / `INSTANCE` for `dbo` and `0` / `NONE` for everything else — never NULL.
- `sid` is the well-known `0x01` for `dbo` and `0x00` for `guest`, NULL for the two catalog principals, the login's own 16-byte sid for a `FOR LOGIN` user (so the two catalogs join on it), a 28-byte `S-1-9-3-…` SID for a `WITHOUT LOGIN` user, and a 28-byte `S-1-9-4-…` SID for a role (probed 2026-09-25 against SQL Server 2025).
  Each SID is deterministic: a fixed database role encodes its principal_id in the final sub-authority the way real does (`db_owner` → `…00400000`), and everything else fills the four trailing words from the same per-quadrant FNV-1a hash `BuiltInResources.DeriveLoginSid` uses for logins.
  The bytes are stable per name but don't byte-match a real instance's.
`owning_principal_id` is the **creating principal** for a database role (`type='R'`) — `dbo` (1) for a `dbo` session, the creator otherwise, unless `AUTHORIZATION` names one (probed 2026-10-04 against SQL Server 2025) — and NULL otherwise.
This is load-bearing for bacpac export: DacFx's `SqlRole` reverse-engineering filters `USER_NAME(owning_principal_id) != N'cdc'`, and a NULL owner makes that predicate UNKNOWN, silently dropping every role from the model (WWI's 9 custom roles vanished until this was fixed).

**`sys.database_permissions`** (10-col probe-confirmed subset): `class` / `class_desc` / `major_id` / `minor_id` / `grantee_principal_id` / `grantor_principal_id` / `type` (4-char) / `permission_name` / `state` (1-char) / `state_desc`.

**`sys.user_token`** and **`sys.login_token`** list the effective database principal and every role in its closure (`public` included, and `db_owner` for `dbo`), and the login with its server roles, as `principal_id` / `sid` / `name` / `type` / `usage` (probed 2026-10-04 against SQL Server 2025); a database-scoped identity's `sys.login_token` is its user's SID and `public`, both `DENY ONLY`.
`sys.database_role_members` carries `dbo`'s `db_owner` membership row, which real reports though no `ALTER ROLE` made it.

### Seeded grants

Every database starts with the grants real's does (probed 2026-09-28 against SQL Server 2025), seeded by `Database.SeedPermissions` and shared across databases since the rows are immutable: `public` holding `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` and `VIEW ANY COLUMN MASTER KEY DEFINITION`, `dbo` holding `CONNECT`, `guest` holding `CONNECT` in `master`, `tempdb` and `msdb`, and `public` holding `SELECT` on 232 system objects by real's fixed ids (`Database.PublicSelectSeedObjectIds`) — most of them catalog views, the rest system objects the simulator has no catalog for.
`CREATE USER` adds the user's own `CONNECT`.
`master` adds its own: `public` holding `SELECT` on 645 further system objects and `EXECUTE` on 1827 system procedures and functions, by the same fixed ids (`Database.SeedPermissions.Master.cs`), and the policy engine's user and the agent's certificate-mapped user with their grants.
`msdb` seeds the principals its features run as — the agent, Database Mail, SSIS, data-collector, policy, server-group and utility roles and three users — with real's principal ids, memberships and database-level grants (probed 2026-09-28 against SQL Server 2025).
The seed is ordinary state: `REVOKE` removes a row, `GRANT` and `DENY` replace one (a securable holds one row per grantee and permission, so `DENY` after `GRANT` leaves only the `D` row, and the reverse), `sp_helprotect` reports them (the catalog views as `sys` objects with column `(All)`), and `HAS_PERMS_BY_NAME` answers from them.
Guest's access follows its `CONNECT` alone, and `master` and `tempdb` refuse to lose it (Msg 15182).

A catalog view is a securable of the database it's read in, `GRANT` / `REVOKE` / `DENY` resolving `sys.<view>` to its id.
A restricted principal's read of one is refused with Msg 229 naming the view in `mssqlsystemresource` when a `DENY` reaches it, or when the view is one the seed grants `public` and no grant reaches it any more — a revoked seed row refuses `sys.tables`, while a view outside the seed answers only to a `DENY`, since real grants those elsewhere (`PermissionChecker.CanReadCatalogView`, the seed set being `master`'s larger one there).

**Not modeled yet**: the grants real's `msdb` roles hold on msdb's own tables, procedures and XML schema collections, whose objects the simulator doesn't carry, and `master`'s `SELECT` for `public` on its five `spt_*` tables.

**`sys.database_role_members`** (2-col full row): `role_principal_id` / `member_principal_id`.

**`sys.server_principals`** (14-col full probe-confirmed shape): `name` / `principal_id` / `sid` / `type` / `type_desc` / `is_disabled` / `create_date` / `modify_date` / `default_database_name` / `default_language_name` / `credential_id` / `owning_principal_id` / `is_fixed_role` / `tenant_id`.
Projects the synthetic fixed rows — `sa` (id 1, sid `0x01`, `SQL_LOGIN`, default db `master`), `public` (id 2, sid `0x02`, `SERVER_ROLE`, `owning_principal_id` 1, `is_fixed_role` **0** — probe-confirmed quirk), and the 18 fixed server roles (ids 3–20, `SERVER_ROLE`, `is_fixed_role 1`) — plus one row per `Simulation.Logins` entry and per `Simulation.ServerRoles` (custom-role) entry (user ids from 258 via `Simulation.AllocatePrincipalId`; `modify_date` = password-last-set; `tenant_id` all-zero GUID matching real's SQL-login rows). Rows emit in principal_id order.
Created-login `sid`s are deterministic synthetic 16-byte values (FNV-derived from the name) — unique and stable, but won't byte-match real.
A created login's `is_disabled` follows `ALTER LOGIN … DISABLE` / `ENABLE`, here and in `sys.sql_logins`.
Rows are **filtered for a restricted session** — see [Server-principal metadata visibility](#server-principal-metadata-visibility); a dbo / sysadmin reader sees everything and pays one bool read.

**`sys.sql_logins`** (14-col full probe-confirmed shape): the first 10 `server_principals` columns plus `credential_id` / `is_policy_checked` / `is_expiration_checked` / `password_hash`.
Rows are the type-`S` subset (`sa` + created logins, not `public`).
`password_hash` is always NULL — matches what a low-privilege reader sees on real, and deliberately keeps the registry's stored hash unexposed.
`is_policy_checked` is always 1 (real's default when `CHECK_POLICY` is unspecified; the simulator parse-and-discards the option, so a login created with `CHECK_POLICY = OFF` diverges).
Rows carry the same restricted-session filter as `sys.server_principals`.

**`sys.server_permissions`** (10-col, `sys.database_permissions` shape) projects `Simulation.ServerPermissions` — class 100 / `class_desc` `SERVER` / `major_id` 0 for the ON-less and `ON SERVER::` forms, class 101 / `class_desc` `SERVER_PRINCIPAL` / `major_id` = the target login's `principal_id` for `ON LOGIN::` — with canonical type codes and canonical uppercase `permission_name`s, starting with `sa`'s `CONNECT SQL` and `public`'s `VIEW ANY DATABASE`; a restricted session sees only the rows whose grantee it can see.
**`sys.server_role_members`** (2-col) projects `Simulation.ServerRoleMembers` (`role_principal_id` / `member_principal_id`), which starts with `sa`'s `sysadmin` row.

**Empty encryption-key views** (full probe-confirmed SQL Server 2025 shape, zero rows — no principal-security key model): `sys.asymmetric_keys` (16-col), `sys.certificates` (17-col), `sys.credentials` (7-col).
SMO's Login / User property-bag and Script queries `LEFT JOIN` these — the User bag joins `sys.certificates` / `sys.asymmetric_keys` on `sid`; the Login bag joins `sys.credentials` on `credential_id`, `sys.server_permissions` on `grantee_principal_id`, and (as `master.sys.*`) certificates / asymmetric_keys on `sid`; Login scripting `INNER JOIN`s `sys.server_role_members` to enumerate fixed-server-role memberships.
`sys.asymmetric_keys.cryptographic_provider_algid` is `sql_variant` in real SQL Server; surfaced as nvarchar (the view is always empty).
Registered in `BuiltInResources.Security.cs` via the shared `EmptyCatalogRows`.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15151 | Unknown principal in GRANT/REVOKE/DENY/ALTER ROLE / ALTER APPLICATION ROLE; unknown securable object / missing grant authority (object-variant `CannotFindObject`); DROP USER without `ALTER ANY USER`; ALTER/DROP SERVER ROLE / server-scope grant / `ON LOGIN::` securable naming a missing role / member / login; `ALTER` / `DROP LOGIN` without `ALTER ANY LOGIN` (same wording as a missing login); and the DDL gates that reuse a not-found wording — ALTER SEQUENCE, DROP XML SCHEMA COLLECTION, DROP SCHEMA, DROP ROLE (state 1) / ALTER ROLE (**state 2**), and the `ALTER SCHEMA … TRANSFER` pair (`Cannot alter the schema` then `Cannot transfer the object`). |
| 15150 | DROP SERVER ROLE on a fixed server role. |
| 15023 | Duplicate `CREATE USER` / `CREATE ROLE` name. |
| 15247 | CREATE SEQUENCE / ROLE / USER / SCHEMA / APPLICATION ROLE by a principal lacking the named database permission, and `ALTER COLUMN … ADD` / `DROP MASKED` without `ALTER ANY MASK`; `CREATE LOGIN` / `CREATE SERVER ROLE`, an `sp_configure` write and the linked-server procedures without their server permission. |
| 218 | DROP TYPE without schema ALTER — the same record a missing type earns, naming the type as written. |
| 2104 | CREATE TRIGGER without ALTER on the parent object (DML) or `ALTER ANY DATABASE DDL TRIGGER` (database-scope), sev 14 state 1. |
| 5011 | ALTER DATABASE without database ALTER — **state 9**, the permission sibling of the state-5 unknown-database record. |
| 7641 | DROP FULLTEXT CATALOG without `ALTER ANY FULLTEXT CATALOG` (sev 16 state 5). |
| 7666 | CREATE FULLTEXT CATALOG without `CREATE FULLTEXT CATALOG` (sev 16 state 2). |
| 15225 | `sp_rename` without ALTER on the object — the same not-found record a missing object earns. |
| 229 | SELECT / INSERT / UPDATE / DELETE / EXECUTE denied (sev 14 state 5; Procedure attribution on EXEC; UPDATE/DELETE read-implies-SELECT; the object-level fallback when a column-grain check has no access at all). |
| 230 | SELECT / UPDATE denied on a specific **column** (sev 14 state 1) — the column-level grant model's denial, one record per denied column. |
| 4615 | GRANT / DENY / REVOKE column list naming a column the object lacks (`Invalid column name '<col>'.`). |
| 1020 | Column list on an entity-level *permission* (class 15 state 1, compile-time) or on a **synonym** securable (sev 16 state 3, post-resolution). |
| 262 | The database-scope CREATE gates at state 1 (CREATE TABLE / SYNONYM / TYPE / XML SCHEMA COLLECTION / ASSEMBLY, and the server-scope CREATE DATABASE naming `master`) or **state 18** with the object as Procedure attribution (CREATE VIEW / PROCEDURE / FUNCTION, and a `CREATE OR ALTER` over a free name); database-scope DMV read without `VIEW DATABASE PERFORMANCE STATE` (state 1). |
| 300 | Server-scope DMV read without `VIEW SERVER PERFORMANCE STATE` (sev 14 state 1). |
| 2760 | CREATE TABLE / VIEW / PROCEDURE / FUNCTION with the db-scope permission but no ALTER on the target schema (double-quoted schema name). |
| 1088 | TRUNCATE (state 7) / ALTER TABLE (state 13) — double-quoted leaf; CREATE INDEX (state 12) and ALTER / DROP INDEX (state 9) — double-quoted table name *as written*, the DROP form suffixed with the index leaf. |
| 3701 | An object DROP or a module ALTER denied — sev 14 state 20, leaf-named, with the kind noun real spells (`table` / `view` / `procedure` / `function` / `trigger` / `sequence` / `synonym`). DROP DATABASE has its own shape: **sev 11 state 2**. |
| 4606 | Permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF). |
| 4611 | REVOKE or DENY of a grantable permission without CASCADE. |
| 4613 | A database-scope GRANT / DENY by a grantor without authority. |
| 4617 | A GRANT / DENY / REVOKE naming a fixed database role as grantee. |
| 4628 | `GRANT ALL`'s deprecation notice, on the info channel. |
| 4629 | A GRANT on an `INFORMATION_SCHEMA` view or a `sys.sp_*` procedure outside `master`. |
| 15199 / 15196 / 15591 | A `REVERT` from another database, of a `NO REVERT` frame, or without the matching cookie — each ends the batch. |
| 15284 / 15539 / 15062 | Dropping a principal that granted or denied permissions; dropping `guest`; mapping a user to `guest`. |
| 4621 | Server-scope GRANT / DENY / REVOKE (incl. `ON SERVER::` / `ON LOGIN::`) outside the `master` database — severity 16 **state 10**, no trailing period. |
| 15161 | `sp_setapprole` on a missing application role or with the wrong password (one wording for both). |
| 2762 | `sp_setapprole` on a session that already has an application role set. |
| 15592 | `sp_unsetapprole` with no role set or an invalid cookie. |
| 505 | `USE` / `ChangeDatabase` while an application role is active. |
| 4624 | GRANT / DENY / REVOKE to sa / dbo / sys / INFORMATION_SCHEMA / self — **info channel**, not raised. |
| 15182 | `REVOKE` / `DENY` of `CONNECT` from `guest` in `master` or `tempdb`. |
| 18456 / 18470 | The [login gate](#the-login-gate): bad credentials or no `CONNECT SQL` / a disabled login. |
| 5812 | `RECONFIGURE` without `ALTER SETTINGS`. |
| 297 | `DBCC SQLPERF` without its server permission — ends the batch. |
| 15412 | `sp_helpsrvrolemember` / `sp_helpsrvrole` naming no fixed server role. |

All probe-confirmed against SQL Server 2025.

**Column lists are legal on `SELECT` / `UPDATE` / `REFERENCES` only.**
Every other permission is entity-level and takes no sub-entity list, so a column list on it raises **Msg 1020** (`"Sub-entity lists (such as column or security expressions) cannot be specified for entity-level permissions."`).
Probed across `INSERT` / `DELETE` / `EXECUTE` / `ALTER` / `CONTROL` / `TAKE OWNERSHIP` / `VIEW DEFINITION` / `VIEW CHANGE TRACKING` / `RECEIVE` — all rejected.
Real reports it at **Class 15**, a compile-time rejection raised before the securable resolves: `TRY`/`CATCH` cannot intercept it, and it beats the Msg 4606 permission-vs-object-kind check, so `GRANT EXECUTE (col)` on a *table* is Msg 1020 rather than 4606.
Both spellings are covered — the per-permission `GRANT EXECUTE (col) ON t` and the securable-placed `GRANT EXECUTE ON t (col)`.

**And only on a column-bearing securable.**
`SELECT` / `UPDATE` / `REFERENCES` accept a column list on a table or a view, but a **synonym** is entity-level regardless of the permission, so `GRANT SELECT (col) ON <synonym>` is Msg 1020 as well — at **severity 16 state 3**, since real raises this one only once the securable has resolved.
That makes it catchable (unlike the class-15 variant) and puts it ahead of the Msg 4615 unknown-column check, so a bogus column name on a synonym still reports 1020.

## Principal scalars

Probed against SQL Server 2025 for shape + return type.
The current-principal / id scalars read the session's effective principal; `HAS_PERMS_BY_NAME` / `IS_MEMBER` / `IS_ROLEMEMBER` route through the permission checker (a dbo session keeps its historical `1` / membership answers via the same dbo short-circuit).

**Current-principal placeholders** (parens-less when reserved, parens-bearing otherwise) — these read the session's effective principal (see [Session principal & impersonation](#session-principal--impersonation)); an unauthenticated, unimpersonated session still returns `'dbo'` everywhere:
- `CURRENT_USER` — reserved keyword, no parens (dispatched directly from `Expression.Parse`'s expression-start switch, NOT through `ResolveBuiltIn`).
- `SESSION_USER` — same shape, reserved + no parens.
- `SYSTEM_USER` — same shape, reserved + no parens.
- `USER` — same shape, reserved + no parens.
- `USER_NAME([id])` — zero-arg returns `'dbo'`; with an arg, looks up `Database.Principals` by id (matching `DatabasePrincipal.PrincipalId`) and returns the name or NULL.
- `SUSER_NAME([id])` / `SUSER_SNAME([sid])` — the effective login for the no-arg form, else the `sys.server_principals` row carrying that id or sid (NULL when none does).
- `ORIGINAL_LOGIN()` — returns `'dbo'`.

**Principal-id scalars** (`Parser/Expressions/PrincipalIdScalars.cs`):
- `USER_ID([name])` — zero-arg returns `Database.DboPrincipalId` (=1); with an arg, walks `Database.Principals` for a name match.
- `SUSER_ID([login_name])` — same lookup walk as `USER_ID`; the simulator doesn't separate database principals from server logins in its model.
  Result type `int`.
- `DATABASE_PRINCIPAL_ID([name])` — alias of `USER_ID` with the same lookup behavior; real SQL Server exposes both names against the same backing lookup.

**Permission-check placeholders**:
- `HAS_PERMS_BY_NAME(securable, securable_class, permission [, …])` returns NULL for a NULL `permission`, `1` for a dbo session on whatever exists (preserving the DacFx bacpac-export gate `HAS_PERMS_BY_NAME(NULL, N'DATABASE', N'VIEW DEFINITION')` = 1), and otherwise the real checker result (1/0) for a `DATABASE` / `OBJECT` / `SCHEMA` securable_class; the server forms — a NULL class, `SERVER` and `LOGIN` — are in [Scalars and functions](#scalars-and-functions).
  Even for dbo, real answers 0 for an object, schema or column that isn't there and NULL for a class it doesn't know or a NULL object (probed 2026-09-26 against SQL Server 2025); an unknown permission name is NULL on real but still 1 here, since the permission catalog covers only the modeled names.
  An unresolvable OBJECT / SCHEMA securable or an unrecognized class returns NULL.
- `IS_MEMBER(group_or_role)` — `public` → 1; the effective principal's transitive membership (nested roles + fixed roles via the checker's role closure) → 1/0; the dbo user → 1 for every fixed role but `db_denydatareader` / `db_denydatawriter`, with no membership row, where a `db_owner` member belongs to `db_owner` alone (probed 2026-09-30 against SQL Server 2025); any non-role / unknown name → NULL.
- `IS_ROLEMEMBER(role [, principal])` — same shape as `IS_MEMBER`; a named principal is resolved first (a missing one is NULL even for `public`), counts as a member of itself, and follows nested roles (probed 2026-09-25).
- `IS_SRVROLEMEMBER(role [, login])` — `public` → 1; real membership from `Simulation.ServerRoleMembers` (1/0); a sysadmin-member login → 1 for **every fixed** server role; a non-role name → NULL; NULL → NULL. The 1-arg form checks the session's effective login; the 2-arg form looks up the named login (an unknown named login → NULL).

## Known gaps

- **Column-level grants** ship for SELECT / UPDATE / REFERENCES reads and writes, on tables and views alike — see [Column-level grants](#column-level-grants). Residual gaps: **column-level INSERT** grants (INSERT stays object-grain) and the structural-visitor coverage gap for columns buried in some non-arithmetic function containers.
- **Server permissions whose statements aren't built** — `SHUTDOWN`, credentials, endpoints, event sessions, audits, traces, the error log, and `CREATE ASSEMBLY`'s `UNSAFE ASSEMBLY`; every modeled server-scope statement is gated — see [Server permissions and the fixed server roles](#not-modeled-yet).
- **`master`'s and `msdb`'s own seeded grants** — `EXECUTE` on the system procedures and the grants to principals the simulator doesn't carry; a grant naming a system procedure is refused in a user database on real and unresolved here.
- **`sys.server_permissions` endpoint rows** — real seeds `public` with per-endpoint `CONNECT` (class 105) alongside the class-100 rows the simulator seeds; the simulator models no endpoint class.
- **Application-role edges** — a pooled TDS reset clears the role instead of killing the session (real's Msg 596), `sp_setapprole` under `EXECUTE AS` leaves the impersonated identity on top, and the ODBC `{Encrypt}` password escape doesn't parse.
  See [Application roles](#application-roles).
- **DDL statement gates** cover every modeled CREATE / ALTER / DROP — see [DDL statement gates](#ddl-statement-gates).
  `CREATE ASSEMBLY` covers through `CONTROL` rather than real's `ALTER ANY ASSEMBLY`, which isn't in the catalog.
  Real pairs the ALTER DATABASE refusal with a terminating Msg 5069 and the CREATE INDEX / TRUNCATE family with no second record; the simulator raises the single leading error, matching how the DMV 300 / 262 pair is modeled.
- **`PERMISSIONS()`** answers `dbo`'s full bitmap for every caller rather than real's per-permission bits for a restricted one.
- **The default schema resolves nothing** — an unqualified name binds through `dbo` whatever the user's `DEFAULT_SCHEMA`, so `SCHEMA_NAME()` and an unqualified `CREATE` / reference read `dbo` where real reads the user's schema (probed 2026-10-04 against SQL Server 2025).
  The plan cache doesn't stand in its way, since nothing else on a plan depends on the principal ([`plan-cache.md`](plan-cache.md#principal-independence)), but this is the one resolution that must: a plan whose parse resolved an unqualified name through a default schema answers only principals with that default schema, so the cache needs the effective principal's default schema in `PlanCacheKey` (principals sharing one, `dbo` the common case, still share plans), read from the key the batch took rather than from the session while parsing so the principal-read watch stays quiet, and `MayCacheDmlPlan` must compare it per statement as it does the key's settings, since an `EXECUTE AS` earlier in the batch changes it.
  The names a plan records for its messages (`ReferencedSecurable`'s schema name, defaulted to `dbo`) follow the resolved schema then.
- **Residue from the differential sweep** (probed 2026-10-04 against SQL Server 2025):
  - a schema `DENY ALTER` doesn't stop `db_ddladmin`'s `CREATE TABLE` there on real, and a database `CONTROL` holder under it gets Msg 3701 for `DROP TABLE`; the simulator refuses the first and admits the second;
  - a `CREATE VIEW … WITH SCHEMABINDING` lacking both `CREATE VIEW` and `REFERENCES` raises real's REFERENCES Msg 229 first, Msg 262 here;
  - `ALTER AUTHORIZATION` with a missing new owner and an unpermitted securable reports the owner first on real, the securable here;
  - `CREATE FULLTEXT CATALOG` makes the creator the owner on real (it can then drop it) and a later reference by a non-owner is Msg 7641 state 4, where the simulator records `dbo` and answers state 5 or Msg 208;
  - a database-scope `DENY VIEW DEFINITION` narrows `sys.database_permissions` further on real than the grantee rule here;
  - unbracketed `ALTER ROLE public …` is Msg 102 on real;
  - `DROP SYNONYM` names the synonym as written in Msg 3701 on real, the leaf here.
- **`ALTER TABLE ADD`-column SET-reads detection** on the joined form isn't distinguished — a joined UPDATE / DELETE SELECT-checks all backing-table sources unconditionally.
- **Guest enable/disable**, **`CREATE USER … FROM EXTERNAL PROVIDER`** + the `WITH` option tail — parse-and-discard.
- **Grammar residue** — the `APPLICATION ROLE::` securable class isn't parsed (probed 2026-09-28 against SQL Server 2025).
- **Login-model edges** — password policy (`CHECK_POLICY` / expiration / lockout) is not enforced.
