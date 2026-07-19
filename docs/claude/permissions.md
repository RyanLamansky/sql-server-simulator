# Permission statements + principal DDL

`GRANT` / `REVOKE` / `DENY` parsing plus the principal DDL surface (`CREATE USER` / `CREATE ROLE` / `ALTER ROLE` / `DROP USER` / `DROP ROLE`, and the server-scope `CREATE LOGIN` / `ALTER LOGIN` / `DROP LOGIN`) + three new catalog views. Parse-and-store fidelity tier — no enforcement of the actual permissions on subsequent operations. The exception is logins: the TDS network endpoint enforces them as connection credentials (see [`tds-endpoint.md`](tds-endpoint.md)).

## Storage

**`DatabasePrincipal`** (`src/SqlServerSimulator/DatabasePrincipal.cs`) carries:
- `principal_id` (int)
- `name` (string)
- `type_code` (char): `S` = SQL_USER, `R` = DATABASE_ROLE
- `type_desc` (string): `SQL_USER` / `DATABASE_ROLE`
- `is_fixed_role` (bool)
- `create_date` / `modify_date`

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

User principals start at 5 via `Database.AllocatePrincipalId`.

## Parser

`Simulation/Simulation.GrantRevokeDeny.cs` + `Simulation/Simulation.PrincipalDdl.cs`.

### GRANT / REVOKE / DENY

```
GRANT <perm_list> [ON <securable>] TO <principal_list> [WITH GRANT OPTION] [AS <grantor>]
REVOKE [GRANT OPTION FOR] <perm_list> [ON <securable>] FROM <principal_list> [CASCADE] [AS <grantor>]
DENY <perm_list> [ON <securable>] TO <principal_list> [AS <grantor>]
```

- Permission list eats word sequences ending at comma / `ON` / `TO` / `AS` / `WITH`. A sequence of bare identifiers fuses into one permission name (e.g. `VIEW ANY COLUMN ENCRYPTION KEY DEFINITION` → single permission).
- `ON` clause accepts `<name>`, `OBJECT::<name>`, `SCHEMA::<name>`, `DATABASE::<name>`, `TYPE::<name>` via a peek-restore pattern for the `::` operator pair.
- Grantee names accept either `Name` or `ReservedKeyword` raw text (so `public` — tokenized as `ReservedKeyword.Public` — works without special-casing).
- `REVOKE GRANT OPTION FOR` removes the W-state row only.
- `CASCADE` parses-and-discards.
- `WITH GRANT OPTION` records the W state but doesn't propagate.

### Principal DDL

- `CREATE USER name [{FOR | FROM} ...] [WITH ...]` — name + principal_id allocation; `type_code='S'`. The optional clauses (FROM LOGIN / WITH PASSWORD / DEFAULT_SCHEMA / etc.) parse-and-discard through the next statement boundary via `ConsumeToStatementBoundary`.
- `CREATE ROLE name [AUTHORIZATION owner]` — `type_code='R'`. AUTHORIZATION clause parse-and-discards.
- `ALTER ROLE name { ADD MEMBER name | DROP MEMBER name | WITH NAME = newname }` — ADD/DROP MEMBER append/remove `(role_id, member_id)` on `Database.RoleMembers`. `WITH NAME` parses-and-discards.
- `DROP USER [IF EXISTS] name` and `DROP ROLE [IF EXISTS] name` — drop from `Database.Principals` and cascade-remove `Database.RoleMembers` entries that reference the removed id. Dispatched ahead of the generic DROP-target switch in `Simulation.Drop.cs` because principals don't live in a per-schema dict.

### Server logins (`Simulation/Simulation.LoginDdl.cs`)

Server-scope, stored in `Simulation.Logins` (`ConcurrentDictionary<string, ServerLogin>`, `BuiltInToken.Comparer` — the same case-insensitive keying as the sibling server-scope dicts, a slight divergence from real keying by server collation). Each `ServerLogin` is immutable (name, password hash, create date, password-last-set date); mutations replace the entry wholesale so the TDS endpoint's concurrent reads see a consistent hash. The hash uses the legacy `0x0200` single-pass-SHA-512 format (`PasswordHash.EncryptLegacy`) rather than PWDENCRYPT's `0x0300` PBKDF2: never-persisted hashes gain nothing from 100k-iteration hardening, which would otherwise bill every TDS connection open ~50ms. `PasswordHash.Verify` dispatches on the version tag, so both forms verify; the T-SQL `PWDENCRYPT` keeps emitting `0x0300`. In-process connections never authenticate — login DDL through one is how the registry is seeded.

- `CREATE LOGIN name WITH PASSWORD = '…' [MUST_CHANGE] [, option …]` — only the SQL-auth clear-text form is modeled; the option tail (CHECK_POLICY / CHECK_EXPIRATION / DEFAULT_DATABASE / DEFAULT_LANGUAGE / SID / CREDENTIAL) parses-and-discards. `FROM WINDOWS` / certificate / asymmetric-key / external-provider forms and `PASSWORD = 0x… HASHED` raise `NotSupportedException`. A password over SQL Server's documented **128-character cap** raises Msg 6607 (CREATE and ALTER alike) — **approximate**: 6607 is the password-machinery error probe-confirmed on the `PWDENCRYPT` cap, but real's CREATE LOGIN rejection shape is unverifiable from the reference instance (its login hits the Msg 15247 permission wall before password validation).
- `ALTER LOGIN name WITH PASSWORD = '…'` re-hashes and stamps `PasswordLastSetTime` (readable via `LOGINPROPERTY`). Every other ALTER form (ENABLE / DISABLE / other WITH options) parses-and-discards after the existence check — DISABLE does **not** block endpoint logins.
- `DROP LOGIN name` — **no `IF EXISTS` clause**: real SQL Server's DROP LOGIN grammar rejects it (probe-confirmed Msg 156 near 'IF'), reproduced verbatim — a reserved keyword in any of the three login-name positions raises the keyword-flavored Msg 156, not the generic Msg 102.

| Msg | When | Provenance |
|---|---|---|
| 15025 | Duplicate `CREATE LOGIN` name: `The server principal 'x' already exists.` | Docs-derived — the reference login lacks the server permission to reach the duplicate check (Msg 15247 fires first). |
| 15151 | `ALTER LOGIN` / `DROP LOGIN` on a missing login: `Cannot {alter\|drop} the login 'x', because it does not exist or you do not have permission.` | Probe-confirmed (2026-07-13) — distinct wording from the database-principal 15151 (`CannotFindPrincipal`). |

## Permission type-code derivation

The shipped 4-char code is the first-letter-of-each-word right-padded with spaces (e.g. `VIEW ANY COLUMN MASTER KEY DEFINITION` → `VACM`). **Approximate** — real SQL Server's mapping uses a per-permission lookup that diverges for short names (`SELECT` → `SL`, `UPDATE` → `UP`). A polish pass would import the canonical table.

`class_desc` / `state_desc` are spelled out per the probe-confirmed enum:
- `class_desc`: `DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `DATABASE_PRINCIPAL`
- `state_desc`: `GRANT` / `GRANT_WITH_GRANT_OPTION` / `DENY` / `REVOKE`

## Catalog views

In `BuiltInResources.cs`:

**`sys.database_principals`** (14-col probe-confirmed subset): `name` / `principal_id` / `type` / `type_desc` / `default_schema_name` (NULL) / `create_date` / `modify_date` / `owning_principal_id` / `sid` (NULL) / `is_fixed_role` / `authentication_type` / `authentication_type_desc` (both NULL) / `default_language_name` / `default_language_lcid` (both NULL — untracked; SMO's User property-bag reads them via `ISNULL(u.default_language_lcid, -1)` / `ISNULL(u.default_language_name, N'')`). `owning_principal_id` is **dbo (1) for database roles** (`type='R'`), NULL otherwise — probe-confirmed on WWI's custom roles. This is load-bearing for bacpac export: DacFx's `SqlRole` reverse-engineering filters `USER_NAME(owning_principal_id) != N'cdc'`, and a NULL owner makes that predicate UNKNOWN, silently dropping every role from the model (WWI's 9 custom roles vanished until this was fixed).

**`sys.database_permissions`** (10-col probe-confirmed subset): `class` / `class_desc` / `major_id` / `minor_id` / `grantee_principal_id` / `grantor_principal_id` / `type` (4-char) / `permission_name` / `state` (1-char) / `state_desc`.

**`sys.database_role_members`** (2-col full row): `role_principal_id` / `member_principal_id`.

**`sys.server_principals`** (14-col full probe-confirmed shape, 2026-07-15): `name` / `principal_id` / `sid` / `type` / `type_desc` / `is_disabled` / `create_date` / `modify_date` / `default_database_name` / `default_language_name` / `credential_id` / `owning_principal_id` / `is_fixed_role` / `tenant_id`. Projects two synthetic fixed rows — `sa` (id 1, sid `0x01`, `SQL_LOGIN`, default db `master`) and `public` (id 2, sid `0x02`, `SERVER_ROLE`, `owning_principal_id` 1, `is_fixed_role` **0** — probe-confirmed quirk) — plus one row per `Simulation.Logins` entry (ids from 3 via `Simulation.AllocatePrincipalId`; `modify_date` = password-last-set; `tenant_id` all-zero GUID matching real's SQL-login rows). Created-login `sid`s are deterministic synthetic 16-byte values (FNV-derived from the name) — unique and stable, but won't byte-match real.

**`sys.sql_logins`** (14-col full probe-confirmed shape): the first 10 `server_principals` columns plus `credential_id` / `is_policy_checked` / `is_expiration_checked` / `password_hash`. Rows are the type-`S` subset (`sa` + created logins, not `public`). `password_hash` is always NULL — matches what a low-privilege reader sees on real, and deliberately keeps the registry's stored hash unexposed. `is_policy_checked` is always 1 (real's default when `CHECK_POLICY` is unspecified; the simulator parse-and-discards the option, so a login created with `CHECK_POLICY = OFF` diverges).

**Empty encryption-key / permission / role-membership views** (full probe-confirmed SQL Server 2025 shape, zero rows — no principal-security key model): `sys.asymmetric_keys` (16-col), `sys.certificates` (17-col), `sys.credentials` (7-col), `sys.server_permissions` (10-col, sys.database_permissions shape), `sys.server_role_members` (2-col). SMO's Login / User property-bag and Script queries `LEFT JOIN` these — the User bag joins `sys.certificates` / `sys.asymmetric_keys` on `sid`; the Login bag joins `sys.credentials` on `credential_id`, `sys.server_permissions` on `grantee_principal_id`, and (as `master.sys.*`) certificates / asymmetric_keys on `sid`; Login scripting `INNER JOIN`s `sys.server_role_members` to enumerate fixed-server-role memberships. Each miss surfaced only after the prior one cleared (a single query names several), so all five ship together. `sys.asymmetric_keys.cryptographic_provider_algid` is `sql_variant` in real SQL Server; surfaced as nvarchar (the view is always empty). Registered in `BuiltInResources.Security.cs` via the shared `EmptyCatalogRows`.

## Errors enforced verbatim

| Msg | When |
|---|---|
| 15151 | Unknown principal in GRANT/REVOKE/DENY/ALTER ROLE. |
| 15023 | Duplicate `CREATE USER` / `CREATE ROLE` name. |

Both probe-confirmed against SQL Server 2025.

## Principal scalars

The simulator doesn't enforce permissions, so these return values that let permission-checking code paths fall through cleanly rather than authoritatively reflecting the (un-modeled) ACL state. Probed against SQL Server 2025 (2026-05-11 / 2026-05-22) for shape + return type.

**Current-principal placeholders** (parens-less when reserved, parens-bearing otherwise) — all return `'dbo'` since the simulator's only modeled login is the dbo user:
- `CURRENT_USER` — reserved keyword, no parens (dispatched directly from `Expression.Parse`'s expression-start switch, NOT through `ResolveBuiltIn`).
- `SESSION_USER` — same shape, reserved + no parens.
- `SYSTEM_USER` — same shape, reserved + no parens.
- `USER` — same shape, reserved + no parens.
- `USER_NAME([id])` — zero-arg returns `'dbo'`; with an arg, looks up `Database.Principals` by id (matching `DatabasePrincipal.PrincipalId`) and returns the name or NULL.
- `SUSER_NAME([id])` / `SUSER_SNAME([sid])` — both return `'dbo'` for the no-arg form. `SUSER_NAME(id)` resolves through `Database.Principals` by principal_id; `SUSER_SNAME(sid)` accepts a binary SID arg but the simulator has no SID model, so it always returns `'dbo'` for non-NULL input and NULL for NULL input.
- `ORIGINAL_LOGIN()` — returns `'dbo'`.

**Principal-id scalars** (`Parser/Expressions/PrincipalIdScalars.cs`):
- `USER_ID([name])` — zero-arg returns `Database.DboPrincipalId` (=1); with an arg, walks `Database.Principals` for a name match.
- `SUSER_ID([login_name])` — same lookup walk as `USER_ID`; the simulator doesn't separate database principals from server logins in its model. Result type `int`.
- `DATABASE_PRINCIPAL_ID([name])` — alias of `USER_ID` with the same lookup behavior; real SQL Server exposes both names against the same backing lookup.

**Permission-check placeholders**:
- `HAS_PERMS_BY_NAME(securable, securable_class, permission [, sub-securable, sub-securable-class])` returns `1` for any non-NULL `permission` argument and NULL for a NULL `permission` — the simulator doesn't enforce permissions, so any check passes. NULL `securable` / `securable_class` are legal and don't affect the result (real reads NULL securable as "the current server or database"; DacFx's bacpac-export gate sends `HAS_PERMS_BY_NAME(NULL, N'DATABASE', N'VIEW DEFINITION')` and requires 1 — probe-confirmed). Real SQL Server returns 1 / 0 based on the actual grant; the always-1 stance lets code paths gated on this scalar fall through naturally.
- `IS_MEMBER(group_or_role)` — probe-confirmed 1/0/NULL shape for the dbo session principal: `public` and `db_owner` → 1 (dbo is always a member of both), the other eight fixed database roles → 0, a user-created role → membership per `sys.database_role_members` (dbo's principal id 1), any non-role / unknown name → NULL.
- `IS_ROLEMEMBER(role [, principal])` — same shape as `IS_MEMBER`. The 2-arg form accepts a principal name; the simulator's single-principal model returns the same result regardless.
- `IS_SRVROLEMEMBER(role [, login])` — `public` → 1, the other eight fixed server roles → 0 (no server-role membership model), any other name (including database roles) → NULL; NULL → NULL. Probe-confirmed against a non-sysadmin login.

## Known gaps

- **Server-scope grants** (`GRANT … ON SERVER`), schema-scope grants, column-scope grants — not modeled.
- **`WITH GRANT OPTION` cascading** — records the W state but doesn't propagate to descendants.
- **Canonical 4-char permission type codes** — simulator uses a first-letter heuristic; real SQL Server has a per-permission table.
- **Login-model edges** — login DDL itself is permission-unchecked (anyone can CREATE LOGIN); DISABLE / password policy / lockout not enforced; logins aren't linked to database users (`CREATE USER … FOR LOGIN` still parse-and-discards); no server-role model beyond the fixed `public` row (`ALTER SERVER ROLE` not modeled).
- **`CREATE USER … FROM EXTERNAL PROVIDER` / `WITH PASSWORD` semantics** — parse-and-discard.
- **Permission enforcement** — the simulator never checks granted/denied permissions when dispatching subsequent operations.
