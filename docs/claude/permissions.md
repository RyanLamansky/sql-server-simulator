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

**`DatabasePermission`** (`src/SqlServerSimulator/DatabasePermission.cs`) carries class + major_id + minor_id + grantee/grantor ids + permission_name + 4-char type code + state (`G`/`W`/`D`/`R`).

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
- Nested impersonation by a non-dbo principal needs a class-4 IMPERSONATE grant on the target (a direct `Database.Permissions` scan — role-closure expansion is deferred).
- Module `WITH EXECUTE AS {CALLER | SELF | OWNER | 'user'}` is captured on `Procedure.ExecuteAsClause` and pushed/popped around the body in `InvokeProcedure` (OWNER / SELF → dbo, CALLER → no-op).
  Function / trigger `EXECUTE AS` clauses stay parse-and-discard (runtime honoring deferred).

**Authentication.** A login validates against `Simulation.Logins` (empty registry accepts anything) at TDS connect and at in-process `Open()` when the connection string carries `User ID=`, then maps to a database user via `Simulation.TryMapLoginToDatabaseUser`: an explicit `FOR LOGIN` user in the target DB, else `sa` → dbo, else — for a login that is mapping-managed anywhere — `guest` in `master` or a Msg 4060 refusal, else the **permissive dbo default** (an unmapped login keeps the pre-identity endpoint's any-credentials / full-access behavior; the strict guest/4060 path engages only once a login has a `FOR LOGIN` user).
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
  An unknown securable raises the Msg 15151 object-variant (`Cannot find the object '<name>', because it does not exist or you do not have permission.`).
  A permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF) raises **Msg 4606**.
- Grantee names accept either `Name` or `ReservedKeyword` raw text (so `public` works without special-casing).
- The stored row's grantor is the granting session's **effective principal** (an impersonated grant records the impersonated grantor).
- `WITH GRANT OPTION` stores a **single `W` row** (not `G`+`W`).
- `REVOKE GRANT OPTION FOR … [CASCADE]` downgrades `W`→`G` and (with CASCADE) removes the rows the grantee delegated; a plain `REVOKE` of a grantable row that has live delegations, without CASCADE, raises **Msg 4611**.
  Full `REVOKE … CASCADE` removes the whole delegation subtree (rows whose grantor is in the revoked-from set, transitively via `grantor_principal_id`).
- `G` and `D` rows coexist for the same triple; a plain REVOKE removes both.
- A GRANT / DENY / REVOKE targeting `sa` / `dbo` / `sys` / `INFORMATION_SCHEMA` / self silently no-ops and delivers **Msg 4624 on the info-message channel** (`SimulatedDbConnection.InfoMessage`) — not catchable by TRY/CATCH, no row stored.
- A non-dbo grantor must hold a `W` row for the permission being granted; missing authority surfaces the same Msg 15151 object-variant (permission errors leak as "cannot find the object").
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

Denial is **Msg 229** (`The <PERM> permission was denied on the object '<name>', database '<db>', schema '<schema>'.`), except TRUNCATE (**Msg 1088**, its own double-quoted shape) and CREATE TABLE (**Msg 262**).
Existence leaks: SELECT on a missing object is plain Msg 208; Msg 229 fires only for existing objects.

Wiring:
- **SELECT** — each real table / view / TVF read (including nested subqueries and derived tables) is recorded on `Selection.ReferencedSecurables` at parse time (principal-independent, so it rides the cached plan) and checked at execution entry (and on plan-cache replay).
  A scalar UDF invoked in a query records an EXECUTE securable the same way (checked once per statement, never per row).
- **INSERT / UPDATE / DELETE / MERGE** — the target's write permission is checked (INSERT / UPDATE / DELETE; MERGE checks the union of its action kinds plus SELECT on the target); `INSERT … SELECT` also checks SELECT on the source's recorded reads.
- **EXEC proc** / **scalar UDF invocation** — EXECUTE on the module at the call site (the Msg 229 carries the proc's schema-qualified name as its Procedure attribution).
- **TRUNCATE** — ALTER on the object → Msg 1088.
- **CREATE TABLE** — the `db_ddladmin` / `db_owner` / explicit-CREATE-TABLE gate → Msg 262 (temp tables exempt).
- **Ownership chaining** — inside a proc / view / TVF / scalar-UDF / trigger body (`BatchContext.EnforcesPermissions` is false there) all checks are suppressed; dynamic SQL (`EXEC('…')` / `sp_executesql`, whose `ProcFrame.IsDynamicSql` is set) re-enables them.
  Everything is dbo-owned, so all static chains are unbroken — only the outermost referenced object of the user's statement is checked.

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
| 15151 | `ALTER LOGIN` / `DROP LOGIN` on a missing login: `Cannot {alter\|drop} the login 'x', because it does not exist or you do not have permission.` | Probe-confirmed (2026-07-13) — distinct wording from the database-principal 15151 (`CannotFindPrincipal`). |

## Permission type-code derivation

`Simulation.CanonicalPermissionTypeCode` imports the canonical 4-char `sys.database_permissions.type` codes from `sys.fn_builtin_permissions` for the common OBJECT / SCHEMA / DATABASE / DATABASE_PRINCIPAL permissions (`SELECT` → `SL`, `UPDATE` → `UP`, `EXECUTE` → `EX`, `CONTROL` → `CL`, `IMPERSONATE` → `IM`, `CREATE TABLE` → `CRTB`, …).
Codes are stored space-padded to 4 chars and the view's `type` column is `char(4)`, matching real's trailing-space-bearing values (`'SL  '`).
Names outside the imported set (AW's `VIEW ANY COLUMN … DEFINITION` grants) fall back to the first-letter-of-each-word heuristic (`VIEW ANY COLUMN MASTER KEY DEFINITION` → `VACM`), which won't byte-match real for every long name.

`class_desc` / `state_desc` are spelled out per the probe-confirmed enum:
- `class_desc`: `DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `DATABASE_PRINCIPAL`
- `state_desc`: `GRANT` / `GRANT_WITH_GRANT_OPTION` / `DENY` / `REVOKE`

## Catalog views

In `BuiltInResources.cs`:

**`sys.database_principals`** (14-col probe-confirmed subset): `name` / `principal_id` / `type` / `type_desc` / `default_schema_name` (NULL) / `create_date` / `modify_date` / `owning_principal_id` / `sid` (NULL) / `is_fixed_role` / `authentication_type` / `authentication_type_desc` (both NULL) / `default_language_name` / `default_language_lcid` (both NULL — untracked; SMO's User property-bag reads them via `ISNULL(u.default_language_lcid, -1)` / `ISNULL(u.default_language_name, N'')`).
`owning_principal_id` is **dbo (1) for database roles** (`type='R'`), NULL otherwise — probe-confirmed on WWI's custom roles.
This is load-bearing for bacpac export: DacFx's `SqlRole` reverse-engineering filters `USER_NAME(owning_principal_id) != N'cdc'`, and a NULL owner makes that predicate UNKNOWN, silently dropping every role from the model (WWI's 9 custom roles vanished until this was fixed).

**`sys.database_permissions`** (10-col probe-confirmed subset): `class` / `class_desc` / `major_id` / `minor_id` / `grantee_principal_id` / `grantor_principal_id` / `type` (4-char) / `permission_name` / `state` (1-char) / `state_desc`.

**`sys.database_role_members`** (2-col full row): `role_principal_id` / `member_principal_id`.

**`sys.server_principals`** (14-col full probe-confirmed shape, 2026-07-15): `name` / `principal_id` / `sid` / `type` / `type_desc` / `is_disabled` / `create_date` / `modify_date` / `default_database_name` / `default_language_name` / `credential_id` / `owning_principal_id` / `is_fixed_role` / `tenant_id`.
Projects two synthetic fixed rows — `sa` (id 1, sid `0x01`, `SQL_LOGIN`, default db `master`) and `public` (id 2, sid `0x02`, `SERVER_ROLE`, `owning_principal_id` 1, `is_fixed_role` **0** — probe-confirmed quirk) — plus one row per `Simulation.Logins` entry (ids from 3 via `Simulation.AllocatePrincipalId`; `modify_date` = password-last-set; `tenant_id` all-zero GUID matching real's SQL-login rows).
Created-login `sid`s are deterministic synthetic 16-byte values (FNV-derived from the name) — unique and stable, but won't byte-match real.

**`sys.sql_logins`** (14-col full probe-confirmed shape): the first 10 `server_principals` columns plus `credential_id` / `is_policy_checked` / `is_expiration_checked` / `password_hash`.
Rows are the type-`S` subset (`sa` + created logins, not `public`).
`password_hash` is always NULL — matches what a low-privilege reader sees on real, and deliberately keeps the registry's stored hash unexposed.
`is_policy_checked` is always 1 (real's default when `CHECK_POLICY` is unspecified; the simulator parse-and-discards the option, so a login created with `CHECK_POLICY = OFF` diverges).

**Empty encryption-key / permission / role-membership views** (full probe-confirmed SQL Server 2025 shape, zero rows — no principal-security key model): `sys.asymmetric_keys` (16-col), `sys.certificates` (17-col), `sys.credentials` (7-col), `sys.server_permissions` (10-col, sys.database_permissions shape), `sys.server_role_members` (2-col).
SMO's Login / User property-bag and Script queries `LEFT JOIN` these — the User bag joins `sys.certificates` / `sys.asymmetric_keys` on `sid`; the Login bag joins `sys.credentials` on `credential_id`, `sys.server_permissions` on `grantee_principal_id`, and (as `master.sys.*`) certificates / asymmetric_keys on `sid`; Login scripting `INNER JOIN`s `sys.server_role_members` to enumerate fixed-server-role memberships.
Each miss surfaced only after the prior one cleared (a single query names several), so all five ship together.
`sys.asymmetric_keys.cryptographic_provider_algid` is `sql_variant` in real SQL Server; surfaced as nvarchar (the view is always empty).
Registered in `BuiltInResources.Security.cs` via the shared `EmptyCatalogRows`.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15151 | Unknown principal in GRANT/REVOKE/DENY/ALTER ROLE; unknown securable object / missing grant authority (object-variant `CannotFindObject`). |
| 15023 | Duplicate `CREATE USER` / `CREATE ROLE` name. |
| 229 | SELECT / INSERT / UPDATE / DELETE / EXECUTE denied (sev 14 state 5; Procedure attribution on EXEC). |
| 262 | CREATE TABLE by a principal lacking `db_ddladmin` / `db_owner`. |
| 1088 | TRUNCATE denied (ALTER on the object) — double-quoted, sev 16 state 7. |
| 4606 | Permission incompatible with the object kind (SELECT on a proc, EXECUTE on a table / view / TVF). |
| 4611 | Plain REVOKE of a grantable permission with live delegations, without CASCADE. |
| 4624 | GRANT / DENY / REVOKE to sa / dbo / sys / INFORMATION_SCHEMA / self — **info channel**, not raised. |

All probe-confirmed against SQL Server 2025.

## Principal scalars

Probed against SQL Server 2025 for shape + return type.
The current-principal / id scalars read the session's effective principal; `HAS_PERMS_BY_NAME` / `IS_MEMBER` / `IS_ROLEMEMBER` route through the permission checker (a dbo session keeps its historical `1` / membership answers via the same dbo short-circuit).

**Current-principal placeholders** (parens-less when reserved, parens-bearing otherwise) — these now read the session's effective principal (see [Session principal & impersonation](#session-principal--impersonation)); an unauthenticated, unimpersonated session still returns `'dbo'` everywhere:
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
- `IS_SRVROLEMEMBER(role [, login])` — `public` → 1, the other eight fixed server roles → 0 (no server-role membership model), any other name → NULL; NULL → NULL.

## Known gaps

- **Column-level grants** (`GRANT SELECT (col) …`, Msg 230), **server-scope grants** (`GRANT … ON SERVER`, server roles beyond `public`, `ALTER SERVER ROLE`), **application roles** — not modeled.
- **Metadata-visibility filtering** — catalog views (`sys.*` / `INFORMATION_SCHEMA.*`) return every object regardless of the reader's permissions; real hides rows the principal can't see.
- **General DDL-statement permissions** — only `CREATE TABLE` is gated (Msg 262, via the `db_ddladmin` / `db_owner` fixed-role rule). Other CREATE / ALTER / DROP statements aren't permission-checked.
- **`db_accessadmin` / `db_securityadmin` / `db_backupoperator`** — membership is tracked and projected, but carries no enforced effect in this bundle.
- **UPDATE / DELETE additional-read enforcement** — only the write permission on the target is checked. Real also requires SELECT on the target when the statement reads it (probe A2's two-error round trip) and SELECT on any `FROM` / subquery sources of an UPDATE / DELETE; these are not checked (the DML paths don't route their read sources through the securable sink). The `INSERT … SELECT` and MERGE-target-SELECT paths **are** checked.
- **Scalar UDF in a non-query context** — a UDF invoked in a `SET` / `IF` operand (no active securable sink) is EXECUTE-unchecked; UDFs invoked inside a query are checked.
- **Guest enable/disable**, **`CREATE USER … FROM EXTERNAL PROVIDER`** + the `WITH` option tail — parse-and-discard.
- **Login-model edges** — login DDL itself is permission-unchecked; DISABLE / password policy / lockout not enforced.
- **Delegated-grant authority** is a direct-W-row check (exact securable), not the full covering/scope walk — a non-dbo grantor holding only a wider-scope `W` (schema / database CONTROL) isn't recognized as authorized.
- **Msg 229 multi-error round trip** — a single denial is raised, not real's paired SELECT-then-write error records.
