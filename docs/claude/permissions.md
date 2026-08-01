# Permissions: identity, enforcement, and the writer surface

`GRANT` / `REVOKE` / `DENY` plus the principal DDL surface (`CREATE USER` / `CREATE ROLE` / `ALTER ROLE` / `DROP USER` / `DROP ROLE`, and the server-scope `CREATE LOGIN` / `ALTER LOGIN` / `DROP LOGIN`) + catalog views.
**Permissions are enforced**: a non-dbo session's SELECT / INSERT / UPDATE / DELETE / EXECUTE / TRUNCATE(=ALTER) / CREATE TABLE is checked at execution time against its effective principal, with role closure, fixed roles, DENY-beats-GRANT, covering permissions, and ownership chaining.
**Session identity is real**: a per-connection principal (original login + database user + impersonation stack) drives the identity scalars, `EXECUTE AS` / `REVERT`, module `WITH EXECUTE AS`, connection-string / TDS authentication, and the Msg 916 restricted-principal `USE` gate.
A session that never authenticates and never runs `EXECUTE AS` is `dbo`, and **dbo bypasses every check** — the enforcement layer short-circuits on `SessionSecurityContext.EffectiveIsDbo` before any allocation, so existing (dbo) consumers see byte-identical behavior.
Logins are enforced as connection credentials at both front doors (TDS endpoint — see [`tds-endpoint.md`](tds-endpoint.md) — and in-process `User ID=` connection strings).

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
- `DefaultSchemaName` (string?) — the `sys.database_principals.default_schema_name` value, non-null **only for an application role** (`dbo` unless `DEFAULT_SCHEMA` said otherwise); every other principal keeps the NULL the column has always projected.
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
`Effective` is the top frame (or the base); `EffectiveIsDbo` (principal id == 1) is the "unrestricted, may USE" gate today and the read a future enforcement stage's dbo bypass consumes.
An unauthenticated in-process connection uses `CreateDefault()` — dbo as login, database user, and original login everywhere — so existing consumers see byte-identical identity output.

**Identity scalars read the effective frame**: `CURRENT_USER` / `SESSION_USER` / `USER` / `USER_NAME()` / `USER_ID()` / `DATABASE_PRINCIPAL_ID()` → the effective database user; `SYSTEM_USER` / `SUSER_SNAME()` / `SUSER_NAME()` → the effective login (or the WITHOUT-LOGIN SID string); `ORIGINAL_LOGIN()` → the session's original login.

**`EXECUTE AS` / `REVERT`** (`Simulation/Simulation.ExecuteAs.cs`, dispatched by peeking the `AS` after `EXEC`/`EXECUTE`; `REVERT` is its own statement).
- `EXECUTE AS USER = 'x'` pushes x's database-principal frame; `EXECUTE AS USER = 'dbo'` always raises Msg 15517 (probed quirk), as does a missing / non-user target.
- `EXECUTE AS LOGIN = 'l'` maps l to its database user in the current DB (Msg 15406 on a missing login).
- `REVERT` pops one frame; a stray REVERT at the base is a silent no-op.
- Nested `EXECUTE AS USER` by a non-dbo principal needs a class-4 IMPERSONATE grant on the target (a direct `Database.Permissions` scan — role-closure expansion is deferred).
- Nested `EXECUTE AS LOGIN` gates at **server** scope instead: `IMPERSONATE ON LOGIN::<target>` (class 101) or the server-wide `IMPERSONATE ANY LOGIN` (class 100), with a class-101 DENY overriding the blanket grant and `CONTROL ON LOGIN::` covering IMPERSONATE.
  A refusal reports the same Msg 15406 as a missing login — real leaks no distinction (probe-confirmed).
  See [`ON LOGIN::` securables](#on-login-securables).
- Module `WITH EXECUTE AS {CALLER | SELF | OWNER | 'user'}` is captured (on `Procedure.ExecuteAsClause` / `UserDefinedFunction.ExecuteAsClause` / `Trigger.ExecuteAsClause`) and pushed/popped around the body via the shared `PushModuleExecuteAsFrame` — procedures (`InvokeProcedure`), scalar UDFs / TVFs (`InvokeScalarFunction`), and triggers (`InvokeTrigger`) all honor it at runtime (OWNER / SELF → dbo, CALLER → no-op, a named user → that principal).
  A scalar UDF's own `EXECUTE` permission is checked at the invocation seam (once per statement, memoized on `BatchContext.ExecuteCheckedFunctionIds`), covering the SET / IF operand contexts the query read-source sink doesn't reach.

**Authentication.** A login validates against `Simulation.Logins` at TDS connect and at in-process `Open()` when the connection string carries `User ID=`, then maps to a database user via `Simulation.TryMapLoginToDatabaseUser`.
The mapping is faithful (probe-confirmed against SQL Server 2025, PROBE_NOTES_HARDENING bundle 1) — resolution order for an authenticated login `l` in database `D`:
1. **Empty login registry ⇒ open dev mode.** When `Simulation.Logins.IsEmpty` the front doors accept any credentials and the session is **dbo** in every `D` — the honest "no authentication configured ⇒ open" default and the back-compat invariant the whole no-login test corpus rides on. The strict path below engages only once the registry is non-empty.
2. A **sysadmin-member login** (`sa`, or any login added to the `sysadmin` fixed server role) → **dbo** in every `D`, overriding any `FOR LOGIN` mapping (the dbo effective principal then bypasses every check, including explicit DENY).
3. An explicit `CREATE USER … FOR LOGIN l` user in `D` → **that (restricted) user**.
4. **`guest` where accessible** — `master` / `tempdb` / `msdb` (aligned with `HAS_DBACCESS`; not `model`, not user databases) → the **guest** principal (id 2, a genuinely restricted principal whose effective rights flow through the normal checker: CONNECT + anything granted to `guest` / `public`).
5. Otherwise **refuse** — the login cannot open `D`: at connect, the Msg 4060 shape (`Cannot open database "<D>" requested by the login. The login failed.`, on the wire followed by Msg 18456 `Login failed for user '<l>'.`, then the connection closes); the session never opens on `D`.
There is **no permissive dbo fallback** for an authenticated login once the registry is non-empty — an unmapped login lands on `guest` where accessible or is refused, matching real SQL Server.
The unauthenticated in-process path (`CreateDbConnection()` with no `User ID=`) stays **dbo** always — the trusted in-process front door EF Core rides.
`USE` / `ChangeDatabase` under a restricted (non-dbo effective) principal raises Msg 916; the session stays put.

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
  `SERVER::<name>` and `LOGIN::<name>` route out of the database entirely, to `Simulation.ServerPermissions` — see [Server roles + server-scope permissions](#server-roles--server-scope-permissions-simulationsimulationserverrolescs).
  An unknown securable raises the Msg 15151 object-variant (`Cannot find the object '<name>', because it does not exist or you do not have permission.`).
  A permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF) raises **Msg 4606**.
- Grantee names accept either `Name` or `ReservedKeyword` raw text (so `public` works without special-casing).
- The stored row's grantor is the granting session's **effective principal** (an impersonated grant records the impersonated grantor).
- A **column list** after a permission name — `GRANT SELECT (a, b) ON t TO u`, `DENY SELECT (c) ON t TO u`, `GRANT UPDATE (b) ON t TO u`, `REFERENCES (col)` — stores **one row per column** at `minor_id` = the column's 1-based ordinal (`sys.columns.column_id`); an unknown column raises **Msg 4615** (`Invalid column name '<col>'.`).
  The list may sit after the permission (`SELECT (a, b) ON t`) or after the object name (`SELECT ON t (a, b)`); the two placements can't combine (**Msg 1019**), and a list on a non-object scope — or on a **synonym**, which is entity-level — raises **Msg 1020** — see [Column-level grants](#column-level-grants).
  Tables and views both carry column ordinals (a view's are its projection's).
  A table-level (`minor_id 0`) GRANT / REVOKE of the same permission subsumes the grantee's column rows for it (probe-confirmed: a later `GRANT SELECT ON t` collapses the prior `GRANT SELECT (col)` rows); a column-level apply keys on its own `minor_id`.
  See [Column-level grants](#column-level-grants).
- `WITH GRANT OPTION` stores a **single `W` row** (not `G`+`W`).
- `REVOKE GRANT OPTION FOR … [CASCADE]` downgrades `W`→`G` and (with CASCADE) removes the rows the grantee delegated; a plain `REVOKE` of a grantable row that has live delegations, without CASCADE, raises **Msg 4611**.
  Full `REVOKE … CASCADE` removes the whole delegation subtree (rows whose grantor is in the revoked-from set, transitively via `grantor_principal_id`).
- `G` and `D` rows coexist for the same triple; a plain REVOKE removes both.
- A GRANT / DENY / REVOKE targeting `sa` / `dbo` / `sys` / `INFORMATION_SCHEMA` / self silently no-ops and delivers **Msg 4624 on the info-message channel** (`SimulatedDbConnection.InfoMessage`) — not catchable by TRY/CATCH, no row stored.
- A non-dbo grantor must hold a `W` row on the **same securable** for the permission being granted or any permission that covers it (CONTROL-W on the object authorizes granting SELECT on it — probe M9); a **wider-scope** W row does NOT (schema-scope SELECT-W does not authorize an object-scope grant — probe M9b, so `HasGrantAuthority`'s covering walk stays within `(class, major_id)`). Missing authority surfaces the same Msg 15151 object-variant (permission errors leak as "cannot find the object").
- `CREATE USER` auto-seeds a CONNECT grant (class 0, type `CO`, grantor dbo, state G).

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

Denial is **Msg 229** (`The <PERM> permission was denied on the object '<name>', database '<db>', schema '<schema>'.`), except TRUNCATE (**Msg 1088**, its own double-quoted shape) and the CREATE gates (**Msg 262** / **2760** / **15247**).
Existence leaks: SELECT on a missing object is plain Msg 208; Msg 229 fires only for existing objects.

Wiring:
- **SELECT** — each real table / view / TVF read (including nested subqueries and derived tables) is recorded on `Selection.ReferencedSecurables` at parse time (principal-independent, so it rides the cached plan) and checked at execution entry (and on plan-cache replay).
  A reference written through a synonym records the **synonym** as its securable — see [Reference provenance: synonyms](#reference-provenance-synonyms).
  A table / view read additionally records its referenced column ordinals on `Selection.ReadColumnsByObject`, so the SELECT check is **column-grain** — see [Column-level grants](#column-level-grants).
  A scalar UDF invoked in a query records an EXECUTE securable the same way (checked once per statement, never per row).
- **INSERT / UPDATE / DELETE / MERGE** — the target's write permission is checked (INSERT / UPDATE / DELETE; MERGE checks the union of its action kinds plus SELECT on the target); `INSERT … SELECT` also checks SELECT on the source's recorded reads.
  **UPDATE / DELETE read-implies-SELECT** (probe M1/M2): the target's SELECT is also required *when the statement reads it* — a WHERE clause, or a SET expression that references a target column (`SET v = v + 'x'`, detected via a static column-reference probe). A constant-SET UPDATE / bare DELETE with no WHERE reads nothing and needs only the write permission. The SELECT check runs *first*, so with neither SELECT nor the write granted the SELECT denial surfaces (real raises both records; the simulator raises the SELECT-first single error). A joined UPDATE / DELETE (`… FROM t JOIN u …`) SELECT-checks every backing-table source — the non-target sources first, then the target (matching real's ordering).
  On a **single target** (the no-FROM UPDATE / DELETE path) both the read-implies-SELECT and the UPDATE are **column-grain**, against a base table or a view alike (SELECT per WHERE / SET-RHS column, UPDATE per assigned column); the joined form, and any target reached through a synonym, stay object-grain. See [Column-level grants](#column-level-grants).
- **EXEC proc** / **scalar UDF invocation** — EXECUTE on the module at the call site (the Msg 229 for EXEC carries the proc's schema-qualified name as its Procedure attribution; a call through a synonym checks the synonym and carries none). The scalar-UDF check fires at the invocation seam (`PermissionEnforcement.CheckScalarFunctionExecute`, memoized once-per-statement) so SET / IF operand invocations are covered too.
- **TRUNCATE** — ALTER on the object → Msg 1088 (state 7).
- **DDL gates** (all non-dbo only; `db_owner` / `db_ddladmin` pass everything):
  - **CREATE TABLE** — db-scope CREATE TABLE (else Msg 262 state 1) **and** ALTER on the target schema (else Msg 2760); temp tables exempt.
  - **CREATE VIEW / PROCEDURE / FUNCTION** — the same-named db-scope permission (else Msg 262 **state 18**, the object carried as `Procedure` attribution) **and** schema ALTER (else Msg 2760).
  - **CREATE SEQUENCE / ROLE / USER / SCHEMA** — not modeled as a named permission → Msg 15247 (real's CREATE SCHEMA also raises a trailing Msg 2759, omitted).
  - **ALTER TABLE** — object-scope ALTER (object-ALTER suffices) → Msg 1088 **state 13** (leaf-named).
  - **DROP TABLE** — schema-scope ALTER (object-ALTER is insufficient) → Msg 3701 **sev 14 state 20** (leaf-named).
  - **DROP USER** — `db_owner` only (no ALTER ANY USER model) → Msg 15151. DROP ROLE isn't gated (its distinct wording is unprobed).
- **Ownership chaining** — inside a proc / view / TVF / scalar-UDF / trigger body (`BatchContext.EnforcesPermissions` is false there) all checks are suppressed; dynamic SQL (`EXEC('…')` / `sp_executesql`, whose `ProcFrame.IsDynamicSql` is set) re-enables them.
  Everything is dbo-owned, so all static chains are unbroken — only the outermost referenced object of the user's statement is checked.

### Column-level grants

`GRANT` / `DENY SELECT | UPDATE | REFERENCES (col, …)` store one `DatabasePermission` row per column at `minor_id` = the column's 1-based ordinal (`sys.columns.column_id`); `sys.database_permissions` surfaces the `minor_id`, and `COL_NAME(major_id, minor_id)` resolves it.
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
The base table is **never** consulted for a reference through the view (ownership chaining): a grant on the base does not admit the view read, and a DENY on the base does not block it.
Both SELECT and the UPDATE pair (assigned columns need UPDATE, WHERE / SET-RHS columns need SELECT) are column-grain through a view; INSERT and DELETE through a view stay object-grain, matching real.
All probe-confirmed against SQL Server 2025.

**Coverage note.**
Column collection uses the structural expression visitors, which don't recurse through every container (fixed-return scalar functions like `DATALENGTH(col)`, and columns buried in some non-arithmetic function args, are missed) — a residual gap that can under- or over-report a column in those uncommon shapes.
Direct references, arithmetic / comparison, `CAST`, aggregates, and `SELECT *` are covered.

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
`PermissionChecker.CanViewMetadata(database, principalId, objectId, schemaId)` is the rule: the full-visibility bypass, else any *granted* object-applicable permission reaching the object.
The bypass (sees everything, no filtering) is dbo / a `db_owner` / `db_ddladmin` / `db_securityadmin` member / a holder of `CONTROL` or `VIEW DEFINITION` at database scope (`PermissionChecker.HasFullMetadataVisibility`) — `db_ddladmin` / `db_securityadmin` were probe-confirmed to see everything.
Otherwise the object is revealed by any `G`/`W` row (any permission, including a column-scope grant via `minor_id`, and `VIEW DEFINITION` which reveals metadata without data access) at object scope, at schema scope, at database scope (restricted to the object-applicable permissions, so the auto-seeded `CONNECT` can't blanket-reveal the catalog), or by the `db_datareader` / `db_datawriter` fixed roles.
Visibility is **object-grain**: one permission on the object reveals *all* its column / index / parameter / constraint rows, and a trigger's visibility follows its parent table / view.
DENY does not hide metadata (grant-only scan — an assumption; DENY-hides-metadata was not probed).

The filter is a per-enumeration seam on the catalog-view row generators (`BuiltInResources.ApplyMetadataFilter`, wired into both `Selection.ForCatalogView` overloads), gated by `PermissionEnforcement.MetadataVisibilityApplies(batch)` — a restricted-**session**-principal check that (unlike `Applies`) is NOT suppressed inside a module body, since metadata visibility is a property of the session principal, not the execution frame.
The dbo / full-visibility fast path short-circuits on the session principal before any allocation, so existing (dbo) and SMO-as-sysadmin consumers pay one bool read and are unaffected.
Each filtered view carries a `CatalogView.MetadataVisibilityKey` (set once at registration in `BuiltInResources.MetadataVisibility.cs`) naming the row column that governs visibility: the object-id-keyed `sys.*` views key on the row's `object_id` (or `parent_object_id`), the name-keyed `INFORMATION_SCHEMA.*` object views on the owning schema + object name.
Filtered views: `sys.objects` / `all_objects` / `tables` / `views` / `all_views` / `procedures` / `columns` / `all_columns` / `parameters` / `all_parameters` / `sql_modules` / `all_sql_modules` / `indexes` / `index_columns` / `foreign_keys` / `foreign_key_columns` / `check_constraints` / `default_constraints` / `key_constraints` / `triggers` / `identity_columns` / `computed_columns` / `sequences` / `synonyms`, and `INFORMATION_SCHEMA.TABLES` / `COLUMNS` / `VIEWS` / `ROUTINES` / `PARAMETERS`.
Deliberately unfiltered (probe-confirmed broadly visible to a restricted principal): `sys.database_principals` / `sys.schemas` / `sys.database_permissions` / `sys.database_role_members` / `sys.types` / `sys.databases` and the DMVs.
`sys.server_principals` / `sys.sql_logins` carry their own server-scope filter — see [Server-principal metadata visibility](#server-principal-metadata-visibility).
`sys.databases` stays unfiltered because real grants `VIEW ANY DATABASE` to `public` by default, so a plain login does see every database (probe-confirmed); the seeded `public` grant row itself isn't modeled.
Scope limits (noted): filtering engages only for a same-database read (a cross-database catalog read passes through, since the session principal is a current-database user — a cross-database *write* through a three-part name likewise checks the session database's grants against the target's `object_id`, where real resolves the login's user in the target, see [`schemas.md`](schemas.md#cross-database-writes)); `db_datareader` slightly over-reveals procedure metadata; a column-scope grant (`minor_id > 0`) reveals its object object-grain — `sys.columns` shows every column of a column-granted object, including the ungranted / denied ones (probe Q2).

### Principal DDL

- `CREATE USER name [{FOR | FROM} ...] [WITH ...]` — name + principal_id allocation; `type_code='S'`.
  The optional clauses (FROM LOGIN / WITH PASSWORD / DEFAULT_SCHEMA / etc.) parse-and-discard through the next statement boundary via `ConsumeToStatementBoundary`.
- `CREATE ROLE name [AUTHORIZATION owner]` — `type_code='R'`.
  AUTHORIZATION clause parse-and-discards.
- `ALTER ROLE name { ADD MEMBER name | DROP MEMBER name | WITH NAME = newname }` — ADD/DROP MEMBER append/remove `(role_id, member_id)` on `Database.RoleMembers`.
  `WITH NAME` parses-and-discards.
- `DROP USER [IF EXISTS] name` and `DROP ROLE [IF EXISTS] name` — drop from `Database.Principals` and cascade-remove `Database.RoleMembers` entries that reference the removed id.
  Dispatched ahead of the generic DROP-target switch in `Simulation.Drop.cs` because principals don't live in a per-schema dict.

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
  Every other ALTER form (ENABLE / DISABLE / other WITH options) parses-and-discards after the existence check — DISABLE does **not** block endpoint logins.
- `DROP LOGIN name` — **no `IF EXISTS` clause**: real SQL Server's DROP LOGIN grammar rejects it (probe-confirmed Msg 156 near 'IF'), reproduced verbatim — a reserved keyword in any of the three login-name positions raises the keyword-flavored Msg 156, not the generic Msg 102.

| Msg | When | Provenance |
|---|---|---|
| 15025 | Duplicate `CREATE LOGIN` name: `The server principal 'x' already exists.` | Docs-derived — the reference login lacks the server permission to reach the duplicate check (Msg 15247 fires first). |
| 15151 | `ALTER LOGIN` / `DROP LOGIN` on a missing login: `Cannot {alter\|drop} the login 'x', because it does not exist or you do not have permission.` | Probe-confirmed — distinct wording from the database-principal 15151 (`CannotFindPrincipal`). |

### Server roles + server-scope permissions (`Simulation/Simulation.ServerRoles.cs`)

Server scope outlives any database, so its registries live on `Simulation`.
- **Fixed server roles** (`Simulation.FixedServerRoles`, probe6 N1) seed `sys.server_principals` at their real ids 3–20 (`sysadmin`=3 … `##MS_ServerPerformanceStateReader##`=20; `public` stays id 2 with `is_fixed_role 0`). User server principals — created logins **and** custom server roles — take ids from **258** via `AllocatePrincipalId` (real reserves the block past the fixed roles; observed 258+).
- `CREATE SERVER ROLE x` (→ `Simulation.ServerRoles`, `type R`, `is_fixed 0`), `ALTER SERVER ROLE r { ADD | DROP } MEMBER l` (→ `Simulation.ServerRoleMembers`, works for fixed and custom roles), `DROP SERVER ROLE x` (dropping a fixed role → **Msg 15150**). `SERVER` isn't a reserved keyword, so the CREATE / ALTER / DROP dispatchers match a `Name`-guard case. Errors are the 15151 family: unknown role `Cannot alter the server role '<r>'…`; unknown member `Cannot add the server principal '<l>'…`; unknown grantee login `Cannot find the login '<l>'…`.
- **sysadmin semantics** (probe6 N3): a sysadmin-member login (incl. `sa`) maps to dbo in every database (see [Authentication](#session-principal--impersonation)); `IsLoginSysadmin` walks the `ServerRoleMembers` closure.
- **`IS_SRVROLEMEMBER`** reads the registry: `public` → 1; a sysadmin member → 1 for **every fixed** server role (N2); real membership → 1/0; a non-role name → NULL; the 2-arg form looks up the named login (an unknown named login → NULL).
- **Server-scope GRANT / DENY / REVOKE** — three routes into `ApplyServerScopeGrant`: an ON-less GRANT whose permissions are all recognized SERVER-class names (`CONNECT SQL`, `VIEW SERVER STATE`, …), an explicit `ON SERVER::<name>`, or an `ON LOGIN::<name>`.
  Legal only when the current database is `master` (**Msg 4621**, severity 16 **state 10**, no trailing period — elsewhere), stored in `Simulation.ServerPermissions`.
  `CREATE LOGIN` auto-seeds a `CONNECT SQL` G row (N4b).
  **Server-scope DENY replaces the prior G row** (N4 — divergent from database scope, where G + D coexist); REVOKE removes the rows.
  Beyond catalog truth + `IS_SRVROLEMEMBER` + the sysadmin mapping, the `VIEW …STATE` server permissions **gate the modeled DMVs** — see [DMV server-state gating](#dmv-server-state-gating).
  - **Class 100** (`class_desc` `SERVER`, `major_id` 0) — the ON-less and `ON SERVER::` forms.
    `ON SERVER::<name>` is an **alias of the ON-less form and its name is ignored** (probe-confirmed: real accepts any name there and stores the same row).
    Type codes come from the `ServerPermissionCodes` table (`CONNECT SQL`→`COSQ`, `VIEW SERVER STATE`→`VWSS`, …).
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
| `VIEW DEFINITION` | `VIEW ANY DEFINITION` |
| `ALTER` | `ALTER ANY LOGIN` |

A class-101 row answers only when it names the same target, and covers through the **object-class** graph (so `CONTROL ON LOGIN::x` covers all three); a class-100 row answers through the server-class graph.
**DENY over either class binds first**, so `DENY IMPERSONATE ON LOGIN::x` beats `GRANT IMPERSONATE ANY LOGIN` (probe-confirmed).

Three gates consume it: `EXECUTE AS LOGIN` (IMPERSONATE), [server-principal metadata visibility](#server-principal-metadata-visibility) (VIEW DEFINITION / ALTER / IMPERSONATE), and [login DDL](#login-ddl-gating) (ALTER).

### Server-principal metadata visibility

The server-scope analogue of the database [Metadata visibility](#metadata-visibility) rules, applied to `sys.server_principals` and `sys.sql_logins`.
A **restricted** session (non-`dbo` effective principal; dbo / sysadmin short-circuit on one bool read before any allocation) sees a row only when `Simulation.CanViewServerPrincipal(login, targetPrincipalId)` says so:

- the **fixed block is always visible** — `sa` (1), `public` (2) and the 18 fixed server roles (3–20), 20 rows;
- its **own** login row;
- a **server role it belongs to** (transitively);
- any login it holds `VIEW DEFINITION`, `ALTER` or `IMPERSONATE` on — per-login (class 101) or through the blanket class-100 equivalent.

Probe-confirmed: a freshly created login sees only itself past the fixed block; `ALTER ON LOGIN::x` reveals x; `VIEW ANY DEFINITION` reveals every login; and a `DENY VIEW DEFINITION ON LOGIN::x` **re-hides x under a blanket grant** — DENY hides at server scope, unlike the database-scope grant-only scan (which is documented as an unprobed assumption).

The filter is `BuiltInResources.ServerPrincipalVisibility(batch)`, returning `null` for the full-visibility fast path and a per-`principal_id` predicate otherwise; both row generators apply it.

### Login DDL gating

Login DDL is server-scope, so a restricted session needs `ALTER ANY LOGIN` (class 100) or `ALTER ON LOGIN::<target>` (class 101):

| Statement | Gate | Denial |
|---|---|---|
| `CREATE LOGIN` | server-wide `ALTER ANY LOGIN` (there is no per-login target) | **Msg 15247** `User does not have permission to perform this action.` |
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
| 15592 | `sp_unsetapprole` with no role set or an invalid cookie: `Cannot unset application role because none was set or the cookie is invalid.` |
| 505 | `USE` / `ChangeDatabase` while a role is active: `The current user account was invoked with SETUSER or SP_SETAPPROLE. Changing databases is not allowed.` |

All probe-confirmed against SQL Server 2025.

**Divergences.**
- Real attributes these errors to the system proc's own body (`Procedure sp_setapprole, Line 46`); the simulator has no system-proc body text, so they carry the caller's statement line and no `Procedure` attribution — the existing convention for every system-proc error (`sp_getapplock`'s Msg 201, …).
- **Pooled-connection reset**: real *refuses* to reset a connection with an active application role and kills the session — a reopen from the pool fails with **Msg 596, class 21** (`Cannot continue the execution because the session is in the kill state.`), probe-confirmed over SqlClient.
  The simulator's TDS `ResetConnection` rebuilds the connection from the original login, so the role is simply **cleared** and the pooled connection stays usable.
  The simulator is the more forgiving side; a consumer relying on real's poisoning behavior would diverge.
- Application-role DDL is gated on the same `db_owner` / `db_ddladmin` capability as `CREATE ROLE` (Msg 15247), not on real's own `ALTER ANY APPLICATION ROLE`.

### DMV server-state gating

A restricted session (any non-`dbo` effective principal — a mapped user or `guest`; sysadmin logins map to `dbo` and bypass) reading a modeled DMV is gated by the `VIEW …STATE` permissions (probe-confirmed against SQL Server 2025).
The `VIEW …STATE` permission enum, type codes (`VIEW SERVER STATE`→`VWSS`, `VIEW SERVER PERFORMANCE STATE`→`VSP `, `VIEW SERVER SECURITY STATE`→`VSS `, `VIEW DATABASE STATE`→`VWDS`, `VIEW DATABASE PERFORMANCE STATE`→`VDP `), and covering graph live in `Permission.cs`; the covering edges are: `VIEW SERVER STATE` covers `VIEW SERVER PERFORMANCE STATE` / `VIEW SERVER SECURITY STATE` (server scope), `VIEW DATABASE STATE` covers `VIEW DATABASE PERFORMANCE STATE` (database scope), and cross-scope a covering server permission satisfies the database requirement.

`ServerPermissionChecker.Holds(simulation, login, permission)` is the server-scope counterpart to `PermissionChecker` — sysadmin bypass, then a DENY-first / GRANT scan over the login's server-principal closure (`Simulation.BuildServerPrincipalClosure`: the login's server-principal id + its transitive server-role memberships + `public`) with the server-scope covering graph, over `Simulation.ServerPermissions`.
It also answers the cross-scope database-state requirement (a database `VIEW …STATE` need met by a covering server permission), so the DMV gate consults one method for both.
(`CONTROL SERVER` isn't modeled separately — sysadmin-only in practice, so it's folded into the bypass.)

The gate hangs off a per-DMV `CatalogView.DmvGate` descriptor (`DmvGateKind`), set once at registration in `BuiltInResources.DmvGating.cs` (analogous to bundle 2's `MetadataVisibilityKey`), and is applied in `BuiltInResources.ApplyDmvGate` from both `Selection.ForCatalogView` overloads.
The `dbo` / sysadmin fast path short-circuits on `SessionSecurityContext.EffectiveIsDbo` before any allocation, so existing in-process DMV reads pay one bool read and are byte-identical.

| DMV | Gate | Denial |
|---|---|---|
| `sys.dm_tran_locks`, `sys.dm_os_waiting_tasks`, `sys.dm_tran_version_store`, `sys.dm_tran_version_store_space_usage`, `sys.dm_tran_active_snapshot_database_transactions`, `sys.dm_hadr_cluster` | server-scope — `VIEW SERVER PERFORMANCE STATE` (covered by `VIEW SERVER STATE`) | **Msg 300** sev 14 state 1: `VIEW SERVER PERFORMANCE STATE permission was denied on object 'server', database '<db>'.` |
| `sys.dm_db_partition_stats`, `sys.dm_hadr_database_replica_states` | database-scope — `VIEW DATABASE PERFORMANCE STATE` at db scope, or a covering server permission cross-scope | **Msg 262** sev 14 state 1: `VIEW DATABASE PERFORMANCE STATE permission denied in database '<db>'.` |
| `sys.dm_exec_sessions` | self-filter — restricted sessions without `VIEW SERVER STATE` see only their own SPID's row (a row filter, not a hard denial) | — |
| `sys.dm_os_host_info`, `sys.fn_helpcollations`, `sys.dm_db_xtp_table_memory_stats` | ungated (probe: readable by `guest`) | — |

Real also raises a trailing **Msg 297** after the 300 / 262; the simulator surfaces the single 300 / 262.

| Msg | When |
|---|---|
| 15150 | `DROP SERVER ROLE` on a fixed role: `Cannot drop the server role 'sysadmin'.` |
| 4621 | Server-scope GRANT / DENY / REVOKE outside `master`. |

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

**`sys.database_principals`** (14-col probe-confirmed subset): `name` / `principal_id` / `type` / `type_desc` / `default_schema_name` (NULL except for application roles) / `create_date` / `modify_date` / `owning_principal_id` / `sid` (NULL) / `is_fixed_role` / `authentication_type` / `authentication_type_desc` (both NULL) / `default_language_name` / `default_language_lcid` (both NULL — untracked; SMO's User property-bag reads them via `ISNULL(u.default_language_lcid, -1)` / `ISNULL(u.default_language_name, N'')`).
`owning_principal_id` is **dbo (1) for database roles** (`type='R'`), NULL otherwise — probe-confirmed on WWI's custom roles.
This is load-bearing for bacpac export: DacFx's `SqlRole` reverse-engineering filters `USER_NAME(owning_principal_id) != N'cdc'`, and a NULL owner makes that predicate UNKNOWN, silently dropping every role from the model (WWI's 9 custom roles vanished until this was fixed).

**`sys.database_permissions`** (10-col probe-confirmed subset): `class` / `class_desc` / `major_id` / `minor_id` / `grantee_principal_id` / `grantor_principal_id` / `type` (4-char) / `permission_name` / `state` (1-char) / `state_desc`.

**`sys.database_role_members`** (2-col full row): `role_principal_id` / `member_principal_id`.

**`sys.server_principals`** (14-col full probe-confirmed shape): `name` / `principal_id` / `sid` / `type` / `type_desc` / `is_disabled` / `create_date` / `modify_date` / `default_database_name` / `default_language_name` / `credential_id` / `owning_principal_id` / `is_fixed_role` / `tenant_id`.
Projects the synthetic fixed rows — `sa` (id 1, sid `0x01`, `SQL_LOGIN`, default db `master`), `public` (id 2, sid `0x02`, `SERVER_ROLE`, `owning_principal_id` 1, `is_fixed_role` **0** — probe-confirmed quirk), and the 18 fixed server roles (ids 3–20, `SERVER_ROLE`, `is_fixed_role 1`) — plus one row per `Simulation.Logins` entry and per `Simulation.ServerRoles` (custom-role) entry (user ids from 258 via `Simulation.AllocatePrincipalId`; `modify_date` = password-last-set; `tenant_id` all-zero GUID matching real's SQL-login rows). Rows emit in principal_id order.
Created-login `sid`s are deterministic synthetic 16-byte values (FNV-derived from the name) — unique and stable, but won't byte-match real.
Rows are **filtered for a restricted session** — see [Server-principal metadata visibility](#server-principal-metadata-visibility); a dbo / sysadmin reader sees everything and pays one bool read.

**`sys.sql_logins`** (14-col full probe-confirmed shape): the first 10 `server_principals` columns plus `credential_id` / `is_policy_checked` / `is_expiration_checked` / `password_hash`.
Rows are the type-`S` subset (`sa` + created logins, not `public`).
`password_hash` is always NULL — matches what a low-privilege reader sees on real, and deliberately keeps the registry's stored hash unexposed.
`is_policy_checked` is always 1 (real's default when `CHECK_POLICY` is unspecified; the simulator parse-and-discards the option, so a login created with `CHECK_POLICY = OFF` diverges).
Rows carry the same restricted-session filter as `sys.server_principals`.

**`sys.server_permissions`** (10-col, `sys.database_permissions` shape) projects `Simulation.ServerPermissions` — class 100 / `class_desc` `SERVER` / `major_id` 0 for the ON-less and `ON SERVER::` forms, class 101 / `class_desc` `SERVER_PRINCIPAL` / `major_id` = the target login's `principal_id` for `ON LOGIN::` — with canonical type codes and canonical uppercase `permission_name`s.
**`sys.server_role_members`** (2-col) projects `Simulation.ServerRoleMembers` (`role_principal_id` / `member_principal_id`).

**Empty encryption-key views** (full probe-confirmed SQL Server 2025 shape, zero rows — no principal-security key model): `sys.asymmetric_keys` (16-col), `sys.certificates` (17-col), `sys.credentials` (7-col).
SMO's Login / User property-bag and Script queries `LEFT JOIN` these — the User bag joins `sys.certificates` / `sys.asymmetric_keys` on `sid`; the Login bag joins `sys.credentials` on `credential_id`, `sys.server_permissions` on `grantee_principal_id`, and (as `master.sys.*`) certificates / asymmetric_keys on `sid`; Login scripting `INNER JOIN`s `sys.server_role_members` to enumerate fixed-server-role memberships.
`sys.asymmetric_keys.cryptographic_provider_algid` is `sql_variant` in real SQL Server; surfaced as nvarchar (the view is always empty).
Registered in `BuiltInResources.Security.cs` via the shared `EmptyCatalogRows`.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15151 | Unknown principal in GRANT/REVOKE/DENY/ALTER ROLE / ALTER APPLICATION ROLE; unknown securable object / missing grant authority (object-variant `CannotFindObject`); DROP USER by a non-`db_owner`; ALTER/DROP SERVER ROLE / server-scope grant / `ON LOGIN::` securable naming a missing role / member / login; `ALTER` / `DROP LOGIN` without `ALTER ANY LOGIN` (same wording as a missing login). |
| 15150 | DROP SERVER ROLE on a fixed server role. |
| 15023 | Duplicate `CREATE USER` / `CREATE ROLE` name. |
| 15247 | CREATE SEQUENCE / ROLE / USER / SCHEMA / APPLICATION ROLE by a principal lacking `db_ddladmin` / `db_owner`; `CREATE LOGIN` by a principal lacking server-scope `ALTER ANY LOGIN`. |
| 229 | SELECT / INSERT / UPDATE / DELETE / EXECUTE denied (sev 14 state 5; Procedure attribution on EXEC; UPDATE/DELETE read-implies-SELECT; the object-level fallback when a column-grain check has no access at all). |
| 230 | SELECT / UPDATE denied on a specific **column** (sev 14 state 1) — the column-level grant model's denial, naming the first inaccessible column. |
| 4615 | GRANT / DENY / REVOKE column list naming a column the object lacks (`Invalid column name '<col>'.`). |
| 1020 | Column list on an entity-level *permission* (class 15 state 1, compile-time) or on a **synonym** securable (sev 16 state 3, post-resolution). |
| 262 | CREATE TABLE (state 1, no attribution) / CREATE VIEW / PROCEDURE / FUNCTION (state 18, object as Procedure attribution) db-scope-permission gate; database-scope DMV read without `VIEW DATABASE PERFORMANCE STATE` (state 1). |
| 300 | Server-scope DMV read without `VIEW SERVER PERFORMANCE STATE` (sev 14 state 1). |
| 2760 | CREATE TABLE / VIEW / PROCEDURE / FUNCTION with the db-scope permission but no ALTER on the target schema (double-quoted schema name). |
| 1088 | TRUNCATE denied (state 7) / ALTER TABLE denied (state 13) — double-quoted object name. |
| 3701 | DROP TABLE denied — schema ALTER missing (sev 14 state 20, leaf-named). |
| 4606 | Permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF). |
| 4611 | Plain REVOKE of a grantable permission with live delegations, without CASCADE. |
| 4621 | Server-scope GRANT / DENY / REVOKE (incl. `ON SERVER::` / `ON LOGIN::`) outside the `master` database — severity 16 **state 10**, no trailing period. |
| 15161 | `sp_setapprole` on a missing application role or with the wrong password (one wording for both). |
| 2762 | `sp_setapprole` on a session that already has an application role set. |
| 15592 | `sp_unsetapprole` with no role set or an invalid cookie. |
| 505 | `USE` / `ChangeDatabase` while an application role is active. |
| 4624 | GRANT / DENY / REVOKE to sa / dbo / sys / INFORMATION_SCHEMA / self — **info channel**, not raised. |

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
- `SUSER_NAME([id])` / `SUSER_SNAME([sid])` — both return `'dbo'` for the no-arg form.
  `SUSER_NAME(id)` resolves through `Database.Principals` by principal_id; `SUSER_SNAME(sid)` accepts a binary SID arg but the simulator has no SID model, so it always returns `'dbo'` for non-NULL input and NULL for NULL input.
- `ORIGINAL_LOGIN()` — returns `'dbo'`.

**Principal-id scalars** (`Parser/Expressions/PrincipalIdScalars.cs`):
- `USER_ID([name])` — zero-arg returns `Database.DboPrincipalId` (=1); with an arg, walks `Database.Principals` for a name match.
- `SUSER_ID([login_name])` — same lookup walk as `USER_ID`; the simulator doesn't separate database principals from server logins in its model.
  Result type `int`.
- `DATABASE_PRINCIPAL_ID([name])` — alias of `USER_ID` with the same lookup behavior; real SQL Server exposes both names against the same backing lookup.

**Permission-check placeholders**:
- `HAS_PERMS_BY_NAME(securable, securable_class, permission [, …])` returns NULL for a NULL `permission`, `1` everywhere for a dbo session (preserving the DacFx bacpac-export gate `HAS_PERMS_BY_NAME(NULL, N'DATABASE', N'VIEW DEFINITION')` = 1), and otherwise the real checker result (1/0) for a `DATABASE` / `OBJECT` / `SCHEMA` securable_class.
  A NULL securable_class is the ambiguous "current server or database" request the simulator returns NULL for; an unresolvable OBJECT / SCHEMA securable or an unrecognized class returns NULL.
- `IS_MEMBER(group_or_role)` — `public` → 1; the effective principal's transitive membership (nested roles + fixed roles via the checker's role closure) → 1/0; dbo → 1 for `db_owner`; any non-role / unknown name → NULL.
- `IS_ROLEMEMBER(role [, principal])` — same shape as `IS_MEMBER` (the 2-arg named-principal form is not distinguished from the effective principal).
- `IS_SRVROLEMEMBER(role [, login])` — `public` → 1; real membership from `Simulation.ServerRoleMembers` (1/0); a sysadmin-member login → 1 for **every fixed** server role; a non-role name → NULL; NULL → NULL. The 1-arg form checks the session's effective login; the 2-arg form looks up the named login (an unknown named login → NULL).

## Known gaps

- **Column-level grants** ship for SELECT / UPDATE / REFERENCES reads and writes, on tables and views alike — see [Column-level grants](#column-level-grants). Residual gaps: **column-level INSERT** grants (INSERT stays object-grain) and the structural-visitor coverage gap for columns buried in some non-arithmetic function containers.
- **Server-permission enforcement beyond the four gated points** — the `VIEW …STATE` permissions gate the modeled DMVs ([DMV server-state gating](#dmv-server-state-gating)), `IMPERSONATE` gates `EXECUTE AS LOGIN`, `VIEW DEFINITION` / `ALTER` / `IMPERSONATE` gate [server-principal metadata visibility](#server-principal-metadata-visibility), and `ALTER ANY LOGIN` gates [login DDL](#login-ddl-gating).
  Other server permissions (`CONNECT SQL` as a connect-time gate, `ALTER ANY DATABASE`, `CREATE ANY DATABASE`, …) are stored and projected but not separately enforced; `CONTROL SERVER` isn't modeled as its own permission (folded into the sysadmin bypass).
- **`sys.server_permissions` default rows** — real seeds `public` with `VIEW ANY DATABASE` (class 100) and per-endpoint `CONNECT` (class 105); the simulator seeds neither, and models no endpoint class.
  The observable behavior still matches, since `sys.databases` is unfiltered either way.
- **Application-role edges** — DDL is gated on the `db_owner` / `db_ddladmin` capability rather than `ALTER ANY APPLICATION ROLE`; a pooled TDS reset clears the role instead of killing the session (real's Msg 596).
  See [Application roles](#application-roles).
- **DDL statement permissions beyond the gated set** — CREATE TABLE / VIEW / PROCEDURE / FUNCTION / SEQUENCE / ROLE / USER / SCHEMA, ALTER TABLE, DROP TABLE, and DROP USER are gated; other CREATE / ALTER / DROP statements (indexes, triggers, types, sequences-alter, …) aren't. `ALTER` / `CREATE OR ALTER` of an existing module isn't gated (only pure CREATE is).
- **`db_accessadmin` / `db_securityadmin` / `db_backupoperator`** — membership is tracked and projected, but carries no enforced effect (the DDL gates treat `db_owner` / `db_ddladmin` as the "may run any DDL" pair per probe).
- **Msg 229 multi-error round trip** — when both SELECT and the write permission are missing, a single SELECT-first denial is raised, not real's paired SELECT-then-write error records.
- **`ALTER TABLE ADD`-column SET-reads detection** on the joined form isn't distinguished — a joined UPDATE / DELETE SELECT-checks all backing-table sources unconditionally.
- **Guest enable/disable**, **`CREATE USER … FROM EXTERNAL PROVIDER`** + the `WITH` option tail — parse-and-discard.
- **Login-model edges** — login DDL itself is permission-unchecked (the reference login can't reach those checks anyway); DISABLE / password policy / lockout not enforced.
