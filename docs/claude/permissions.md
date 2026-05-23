# Permission statements + principal DDL

`GRANT` / `REVOKE` / `DENY` parsing plus the principal DDL surface (`CREATE USER` / `CREATE ROLE` / `ALTER ROLE` / `DROP USER` / `DROP ROLE`) + three new catalog views. Parse-and-store fidelity tier — no enforcement of the actual permissions on subsequent operations.

## Storage

**`DatabasePrincipal`** (`SqlServerSimulator/DatabasePrincipal.cs`) carries:
- `principal_id` (int)
- `name` (string)
- `type_code` (char): `S` = SQL_USER, `R` = DATABASE_ROLE
- `type_desc` (string): `SQL_USER` / `DATABASE_ROLE`
- `is_fixed_role` (bool)
- `create_date` / `modify_date`

**`DatabasePermission`** (`SqlServerSimulator/DatabasePermission.cs`) carries class + major_id + minor_id + grantee/grantor ids + permission_name + 4-char type code + state (`G`/`W`/`D`/`R`).

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

## Permission type-code derivation

The shipped 4-char code is the first-letter-of-each-word right-padded with spaces (e.g. `VIEW ANY COLUMN MASTER KEY DEFINITION` → `VACM`). **Approximate** — real SQL Server's mapping uses a per-permission lookup that diverges for short names (`SELECT` → `SL`, `UPDATE` → `UP`). A polish pass would import the canonical table.

`class_desc` / `state_desc` are spelled out per the probe-confirmed enum:
- `class_desc`: `DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `DATABASE_PRINCIPAL`
- `state_desc`: `GRANT` / `GRANT_WITH_GRANT_OPTION` / `DENY` / `REVOKE`

## Catalog views

In `BuiltInResources.cs`:

**`sys.database_principals`** (12-col probe-confirmed subset): `name` / `principal_id` / `type` / `type_desc` / `default_schema_name` (NULL) / `create_date` / `modify_date` / `owning_principal_id` (NULL) / `sid` (NULL) / `is_fixed_role` / `authentication_type` / `authentication_type_desc` (both NULL).

**`sys.database_permissions`** (10-col probe-confirmed subset): `class` / `class_desc` / `major_id` / `minor_id` / `grantee_principal_id` / `grantor_principal_id` / `type` (4-char) / `permission_name` / `state` (1-char) / `state_desc`.

**`sys.database_role_members`** (2-col full row): `role_principal_id` / `member_principal_id`.

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
- `HAS_PERMS_BY_NAME(securable, securable_class, permission [, sub-securable, sub-securable-class])` returns `1` for any non-NULL `permission` argument and NULL for NULL — the simulator doesn't enforce permissions, so any check passes. Real SQL Server returns 1 / 0 based on the actual grant; the always-1 stance lets code paths gated on this scalar fall through naturally.
- `IS_MEMBER('public')` returns 1; `IS_MEMBER('<other-role>')` returns 0; NULL → NULL. Same pattern as real SQL Server's behavior for the default dbo principal (public membership is universal).
- `IS_ROLEMEMBER(role [, principal])` — same `public → 1` / other → 0 / NULL → NULL shape as `IS_MEMBER`. The 2-arg form accepts a principal name; the simulator's single-principal model returns the same result regardless.
- `IS_SRVROLEMEMBER(role [, login])` — returns 0 for any role (the simulator has no server-role model); NULL → NULL.

## Known gaps

- **Server-scope grants** (`GRANT … ON SERVER`), schema-scope grants, column-scope grants — not modeled.
- **`WITH GRANT OPTION` cascading** — records the W state but doesn't propagate to descendants.
- **Canonical 4-char permission type codes** — simulator uses a first-letter heuristic; real SQL Server has a per-permission table.
- **`CREATE LOGIN` / `ALTER LOGIN` / `DROP LOGIN`** — server-scope, not modeled.
- **`CREATE USER … FROM EXTERNAL PROVIDER` / `WITH PASSWORD` semantics** — parse-and-discard.
- **Permission enforcement** — the simulator never checks granted/denied permissions when dispatching subsequent operations.
