# BACPAC import — prerequisite feature checklist

Working document for the eventual `Simulation.FromBacpac` (or `FromBacPac` — naming TBD) entry point. The plan is **emit T-SQL CREATE statements from `model.xml`, feed them through the existing parser**, then load BCP data files in `DataPhaseTables` order. The loader is a translator, not a second object-construction pipeline; the more T-SQL the parser already accepts, the smaller the loader.

Reference sample: `.vs/AdventureWorks2025.bacpac` (Microsoft AdventureWorks2025, 71 tables, 760,167 total rows, 17 MB compressed). Element counts and AW-usage tallies below are probe-confirmed from that file's `model.xml` + `Origin.xml` on 2026-05-14.

## Model.xml — the simulator already handles

These Element types map 1:1 to features the parser already eats; the loader synthesizes the appropriate `CREATE …` text and the existing code paths do the work. No new simulator features needed.

| Element | AW count | Maps to |
|---|---|---|
| `SqlSchema` | 5 | `CREATE SCHEMA` |
| `SqlTable` / `SqlSimpleColumn` / `SqlComputedColumn` / `SqlTypeSpecifier` | 71 / 481 / 302 | `CREATE TABLE` with columns + computed |
| `SqlPrimaryKeyConstraint` / `SqlUniqueConstraint` | 71 / 1 | inline + table-level PK/UQ |
| `SqlForeignKeyConstraint` (incl. `OnDeleteAction`=CASCADE) | 90 (2 cascade) | `CONSTRAINT … FOREIGN KEY … REFERENCES … ON DELETE CASCADE` |
| `SqlCheckConstraint` (raw T-SQL in `CheckExpressionScript`) | 89 | `CONSTRAINT … CHECK (…)` |
| `SqlDefaultConstraint` (raw T-SQL in `DefaultExpressionScript`) | 152 | `CONSTRAINT … DEFAULT (…)` |
| `SqlIndex` / `SqlIndexedColumnSpecification` | 95 | `CREATE [UNIQUE] [CLUSTERED] INDEX` |
| `SqlView` (raw SELECT in `QueryScript`) | 20 | `CREATE VIEW … [WITH SCHEMABINDING] AS …` (SCHEMABINDING parses + discards) |
| `SqlProcedure` (raw body in `BodyScript`, header in `SysCommentsObjectAnnotation.HeaderContents`) | 10 | `CREATE PROCEDURE` |
| `SqlScalarFunction` / `SqlMultiStatementTableValuedFunction` / `SqlScriptFunctionImplementation` | 10 / 1 / 11 | `CREATE FUNCTION` |
| `SqlDmlTrigger` (`SqlTriggerType` 2=AFTER, 3=INSTEAD OF; `IsInsert/Update/DeleteTrigger`) | 10 | `CREATE TRIGGER` |
| `SqlSubroutineParameter` (`IsOutput`, type via `TypeSpecifier`) | 41 | function/proc parameters |
| `SqlInlineConstraintAnnotation` | 1 | constraint inline-vs-table-level marker |
| `OnlinePropertyAnnotation Name="[LastValue]"` | (per identity column) | identity high-water resume |
| `SysCommentsObjectAnnotation` (`HeaderContents`, `FooterContents`) | 52 | header reconstruction for proc/func/view/trigger |

DDL emission strategy: walk the model in dependency-correct order (schemas → tables → table constraints/indexes → views → functions → procedures → triggers), use each `<Element>`'s properties to assemble the `CREATE` header, concatenate `BodyScript` / `QueryScript` / `CheckExpressionScript` / etc. as-is (they're already valid T-SQL), feed the result through `SimulatedDbCommand.ExecuteNonQuery`. The `HeaderContents` annotation gives a probe-confirmed canonical form to copy when in doubt.

## Model.xml — prerequisite features (blocking AW load)

Sorted approximately by surface-area / effort, smallest first. Each is a candidate for its own bundle.

### [x] Database options parse-and-discard expansion (shipped 2026-05-14)
`SqlDatabaseOptions` carries 18 properties; the `ALTER DATABASE name SET …` parser accept-list now covers every database-scope toggle SqlPackage emits from `model.xml`. Closed-accept-list dict in `Simulation.Alter.cs` (`RecognizedDatabaseOptions`) maps each option to its value shape — `OnOff` (ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS / ARITHABORT / CONCAT_NULL_YIELDS_NULL / NUMERIC_ROUNDABORT / QUOTED_IDENTIFIER / TORN_PAGE_DETECTION / TEMPORAL_HISTORY_RETENTION), `EnumIdent` (RECOVERY: FULL / BULK_LOGGED / SIMPLE; PAGE_VERIFY: CHECKSUM / TORN_PAGE_DETECTION / NONE; CURSOR_DEFAULT: GLOBAL / LOCAL), `EqualsOnOff` (ACCELERATED_DATABASE_RECOVERY, OPTIMIZED_LOCKING — `=` required per probe), `IntegerWithUnit` (TARGET_RECOVERY_TIME = N SECONDS|MINUTES — unit required per probe), and `QueryStore` (a dedicated sub-grammar — `= ON [( … )] | = OFF | CLEAR [ALL]`). QUERY_STORE sub-options are themselves a closed accept-list (`RecognizedQueryStoreSubOptions`): OPERATION_MODE / CLEANUP_POLICY / DATA_FLUSH_INTERVAL_SECONDS / MAX_STORAGE_SIZE_MB / INTERVAL_LENGTH_MINUTES / SIZE_BASED_CLEANUP_MODE / QUERY_CAPTURE_MODE / MAX_PLANS_PER_QUERY / WAIT_STATS_CAPTURE_MODE / QUERY_CAPTURE_POLICY. The two nested-block sub-options (CLEANUP_POLICY, QUERY_CAPTURE_POLICY) eat balanced parens via `SkipBalancedParens` without enforcing inner-block sub-option names.

**Load-bearing options unchanged**: COMPATIBILITY_LEVEL, ALLOW_SNAPSHOT_ISOLATION, READ_COMMITTED_SNAPSHOT dispatch via their dedicated helpers and keep their behavior wiring. **COLLATE clause** (separate top-level grammar, not under SET): `ALTER DATABASE name COLLATE <name>` hard-errors with `NotSupportedException` on anything other than `SQL_Latin1_General_CP1_CI_AS` — silently accepting would mean the bacpac loader silently mis-loads collation-sensitive data on non-default-collation models. **IsFullTextEnabled** is not handled here (emitted as `EXEC sp_fulltext_database 'enable|disable'`, a system sproc the simulator doesn't model — defer with the rest of full-text below).

Coverage: 53 new tests in `AlterDatabaseOptionsTests.cs` exercise every option × value-shape combination, the QUERY_STORE block (single sub-option / nested / multi / CLEAR forms), the COLLATE hard-error, the load-bearing-options-still-wired regression, and the three syntax-error paths the probe found (`SET RECOVERY = FULL`, `SET ACCELERATED_DATABASE_RECOVERY ON` without `=`, `SET TARGET_RECOVERY_TIME = 60` without unit — all Msg 102, matching probe).

### [x] UDDTs / alias types (`CREATE TYPE … FROM …`) (shipped 2026-05-14)
Real-feature path landed: `CREATE TYPE schema.name FROM <builtin>[(N[, S])] [NULL | NOT NULL]` parses to a new `AliasType` (`SqlServerSimulator/AliasType.cs`) registered on `Schema.AliasTypes` (`ConcurrentDictionary<string, AliasType>`, sharing the type-name namespace with `TableTypes` — duplicate-name collision raises Msg 219 verbatim across both dicts). The 6 AW alias types (`AccountNumber` / `Flag` / `Name` / `NameStyle` / `OrderNumber` / `Phone`) load successfully end-to-end; a smoke test in `AliasTypeTests` declares all 6 + a `Customer` table using `[dbo].[AccountNumber]` / `[dbo].[Name]` / `[dbo].[NameStyle]` / `[dbo].[Phone]` and verifies `is_nullable=0` propagation for the three NOT-NULL aliases.

**Type-reference parsing** at every consumer site (CREATE TABLE column, DECLARE @v, ALTER TABLE ALTER COLUMN, CREATE PROCEDURE / FUNCTION / SEQUENCE parameter, OPENJSON column, sp_executesql parameter) now accepts 1- or 2-part dotted type names — was previously single-`Name` only. Each site routes through `Simulation.ResolveTypeReference(BatchContext, MultiPartName, Name leaf, …)` which checks `Schema.AliasTypes` first and falls back to `SqlType.GetByName` for built-in types. A length-parameter at the alias-usage site raises **Msg 2716 St 3** verbatim (probe-confirmed against SQL Server 2025; distinct from the State-1 form for built-ins).

**Nullability inheritance**: probe-confirmed semantics — bare `CREATE TYPE T FROM int` and explicit `FROM int NULL` both set the alias's `IsNullable=true`; `NOT NULL` sets false. When a column / variable references an alias without its own explicit `NULL` / `NOT NULL` marker, the alias's default propagates. Column-site explicit marker (`c MyAlias NULL`) overrides the alias default.

**Errors enforced verbatim**: Msg 219 (duplicate type name, alias-vs-alias or alias-vs-table-type), Msg 222 (`The base type "X" is not a valid base type for the alias data type.`), Msg 2716 St 3 (column width at alias-usage site), Msg 218 (DROP TYPE on missing alias without IF EXISTS).

**`sys.types`** rows for alias types ship via `BuiltInResources.cs::EnumerateSysTypes` — `system_type_id` from the **underlying** builtin (e.g. 231 for nvarchar-of-… , 56 for int-of-…), `user_type_id` from the alias's per-database allocation (starts at 256), `schema_id` from the owning schema, `is_user_defined=1`, `is_table_type=0`, `is_nullable` from the alias's stored marker.

**Known fidelity gaps** (deferred — not load-bearing for the bacpac baseline):
- `HeapColumn` doesn't retain a back-pointer to its declaring `AliasType`. Consequence: `sys.columns.user_type_id` surfaces the underlying built-in's id (not the alias's) when a column is alias-typed, and `DROP TYPE` on an alias type doesn't enforce **Msg 3732** (referenced-by-object). Real bacpac load never drops alias types during import, so this is acceptable for the baseline.
- Alias-type max-length surfaces on `sys.types.max_length` aren't emitted (the catalog view's shipped subset doesn't include `max_length` yet — pre-existing gap from before this bundle, not specific to alias types).
- Alias-of-alias not modeled — `CREATE TYPE T2 FROM T1` where T1 is itself an alias raises Msg 222 (matches probe: real SQL Server rejects alias-of-alias the same way).

### [x] Extended properties (shipped 2026-05-14)
Full sproc trio + `sys.extended_properties` catalog view + `fn_listextendedproperty` system TVF — pure metadata, no semantic effect on queries. 538 in AW (461 column + 69 table + 5 schema + 1 DB + 1 filegroup + 1 DDL-trigger; filegroup / DDL-trigger remain out of scope per the simulator's broader feature roster).

**Storage** lives on `Database.ExtendedProperties` — a `ConcurrentDictionary<ExtendedPropertyKey, SqlValue>` keyed by `(byte class, int major_id, int minor_id, string name)` with case-insensitive name comparison (the `ExtendedPropertyKey` readonly-struct overrides `Equals` / `GetHashCode` through `Collation.Default`). Per-DB flat dict mirrors `sys.extended_properties`'s catalog shape (not per-schema).

**Sproc trio** in `Simulation.ExtendedProperties.cs` (new partial). `Simulation.Exec.cs` gains three dispatch branches after the existing `sp_executesql` route, each forwarding to the shared `InvokeSpExtendedProperty(batch, ExtendedPropertyOp)` body which parses the named-arg list (8 recognized args: `@name`, `@value`, `@level0type` / `@level0name` / `@level1type` / `@level1name` / `@level2type` / `@level2name`), resolves the target via `ResolveExtendedPropertyTarget`, and performs the add/update/drop op against the dict. Argument-name comparison drops the `@` prefix (the `AtPrefixedString` token's `Value` is already `@`-stripped).

**Verbatim error wording** (probe-confirmed against SQL Server 2025 on 2026-05-14):
- **Msg 15233** for duplicate add: `"Property cannot be added. Property 'X' already exists for 'Y'."` — `Y` is `'object specified'` for DB-level, `'<schema>'` for schema, `'<schema>.<name>'` for table/view/proc/func, `'<schema>.<table>.<col>'` for column.
- **Msg 15217** for update/drop on missing: same target-label convention.
- **Msg 15135** for missing target object: `"Object is invalid. Extended properties are not permitted on '<target>', or the object does not exist."` — target label uses the failing-level's value.
- **Msg 15600** for invalid parameters (positional arg, unknown @-name, missing required arg, unknown level type).

**`sys.extended_properties`** catalog view in `BuiltInResources.cs::EnumerateSysExtendedProperties` emits the shipped 6-column subset: `class` (tinyint), `class_desc` (sysname — 0→`DATABASE`, 1→`OBJECT_OR_COLUMN`, 3→`SCHEMA`), `major_id` (int), `minor_id` (int — 0 for tables/views/procs/funcs, 1-based column ordinal for columns), `name` (sysname), `value` (nvarchar(MAX) — sql_variant isn't modeled, so the value coerces to nvarchar; AW's all-nvarchar workload is lossless).

**`fn_listextendedproperty`** in `Selection.ListExtendedProperty.cs` is a built-in system TVF dispatched alongside `OPENJSON` / `STRING_SPLIT` in `ParseSingleFromSource`. 7 args (`@name`, `@level0type`, `@level0name`, `@level1type`, `@level1name`, `@level2type`, `@level2name`), each may be NULL; returns 4 columns (`objtype`, `objname`, `name`, `value`). Filter pipeline: parse each arg expression; eval to nullable string; build `ExtendedPropertyListFilter` from the resolved target; walk `Database.ExtendedProperties` and project matches. The `'default'` wildcard at any level-name slot fans out across every object of that level-type under the parent (probe-confirmed). Missing target returns zero rows (distinct from the sproc path's Msg 15135). Unknown level0/1/2 type (other than `SCHEMA` / `TABLE` / `VIEW` / `PROCEDURE` / `FUNCTION` / `TYPE` / `COLUMN`) raises `NotSupportedException`.

21 new tests in `ExtendedPropertyTests.cs` cover: read-back via `sys.extended_properties` (schema / table / column / DB-level row shape + class+class_desc values), all four duplicate-add target-label variants for Msg 15233, update + drop happy paths, all three missing-target variants for Msg 15135, Msg 15217 / 15600 paths, plus 5 `fn_listextendedproperty` cases (table-level scalar read, name filter across levels, column-level filter, missing-target zero rows, `'default'` wildcard fanout). Full suite 4640 → 4661, all green Debug + Release.

**Known gaps deferred**: PARAMETER / INDEX / TRIGGER / CONSTRAINT level types (Msg 15600 / `NotSupportedException` rather than full target resolution) — AW doesn't exercise them in extended-property declarations, and the bacpac-loader baseline doesn't need them. `sql_variant`-typed values are surfaced as nvarchar via lossy coercion (probe-confirmed: AW's 538 properties are all nvarchar inputs, so this is invisible in practice; non-nvarchar inputs from app code would lose their original type-tag on read-back).

### [x] `hierarchyid` data type — AW-minimum-viable surface (shipped 2026-05-14)
Storage type, `hierarchyid::Parse(str)` / `hierarchyid::GetRoot()` static factories, and the five instance methods AW exercises in `[HumanResources].[Employee]` ship: `.GetLevel()` (returns `smallint`), `.GetAncestor(n)`, `.GetDescendant(child1, child2)` covering all four NULL/NULL × siblings × gap combinations probe-confirmed, `.IsDescendantOf(other)` (returns `bit`), `.ToString()` (returns `nvarchar(4000)`). Comparison and `ORDER BY` follow lexicographic path order (probe-confirmed against the canonical test set `/`, `/-1/`, `/1/`, `/1/1/`, `/1/1/1/`, `/1/2/`, `/2/`).

**Internal representation**: a `HierarchyIdValue` carries the path as `int[][]` — outer array is the segment sequence, inner arrays are the dot-joined label tuples (so `/1/2.5/3/` is `[[1], [2, 5], [3]]`). `HierarchyIdSqlType` (`Storage/HierarchyIdType.cs`) handles parse / format / compare / encode / decode. `SqlValue.FromHierarchyId(int[][])` boxes the array into the reference slot; `SqlValue.AsHierarchyId` returns it back.

**Parser wiring**: `Expression.cs` gains two new cases in the binary-operator loop:
- `Operator { Character: ':' }` — recognizes the `hierarchyid::` type-scope shape (requires a second `:` immediately after, plus the prior `Reference` token to be the bare 1-part name `hierarchyid` — case-insensitive via `Collation.Default`). Dispatches to `HierarchyIdStaticCall.Parse`.
- `Operator { Character: '.' }` extended — when the dotted-name component matches one of the closed accept-list method names (`GetLevel` / `GetAncestor` / `GetDescendant` / `IsDescendantOf` / `ToString`) AND the very next token is `(`, dispatch to `HierarchyIdMethodCall.Parse` instead of the existing multipart-Reference path. Uses `SaveCheckpoint` / `RestoreCheckpoint` to peek without committing.

Known gap from the closed-list approach: a column literally named `GetLevel` (etc.) followed by `.MethodName(...)` would route through hierarchyid dispatch instead of multipart-reference resolution. AW doesn't exercise this collision; accepted as a documented limitation.

**Errors**: `Msg 6522` verbatim wording covers `hierarchyid::Parse` on invalid input (empty, missing slash, double-slash, non-numeric, etc.), `.GetAncestor(-1)`, `.GetDescendant(self, x)` where `x` isn't a direct child of self, `.GetDescendant(c1, c2)` where `c1 >= c2`. The 4 deferred methods (`.GetReparentedValue`, `.Read`, `.Write`) raise `NotSupportedException` if encountered — AW doesn't reference them.

**CAST byte form**: the simulator's `CAST(@h AS varbinary)` produces simulator-native bytes (segment-count + per-segment label-count + each label as int32 LE), **not** SQL Server's documented variable-bit ordinal encoding. CAST round-trips within the simulator work; cross-engine byte transfer (BCP files, SqlClient UDT wire format) is intentionally deferred until the BACPAC loader bundle. Probing on 2026-05-14 confirmed the SQL Server encoding has a recursive Stern-Brocot-tree structure with embedded sub-tier markers. The probe data + cracked tier templates + remaining unknowns are documented below — picking up the byte-identical work should not require re-probing from zero.

#### hierarchyid byte-identical CAST — research notes (deferred follow-up)

These notes are sufficient to resume the byte-identical work cold. **Scope**: replace `HierarchyIdSqlType.Encode` / `Decode` with the SQL Server format. **Method semantics + parser wiring stay**; only the byte serialization changes. Once it lands, the BCP loader can pass `hierarchyid` payloads through as-is without an intermediate decode step (subject to confirming that the BCP wire format and `CAST AS varbinary` form are the same, which is itself an open question — a fresh probe via `bcp out` of a hierarchyid column will answer it).

##### Probe data (single-label paths)

Raw bytes for `hierarchyid::Parse('/N/')` cast to `varbinary`, probed against SQL Server 2025 on 2026-05-14. Bit columns shown MSB-first per byte.

| N | bytes | hex | bits |
|---|------:|---|---|
| -200 | 3 | `1BE044` | `00011011 11100000 01000100` |
| -120 | 3 | `1BEA44` | `00011011 11101010 01000100` |
| -100 | 3 | `1BEC64` | `00011011 11101100 01100100` |
| -80 | 3 | `1BEEC4` | `00011011 11101110 11000100` |
| -73 | 3 | `1BEEFC` | `00011011 11101110 11111100` |
| -72 | 2 | `2088` | `00100000 10001000` |
| -71 | 2 | `2098` | `00100000 10011000` |
| -50 | 2 | `24E8` | `00100100 11101000` |
| -20 | 2 | `2CC8` | `00101100 11001000` |
| -17 | 2 | `2CF8` | `00101100 11111000` |
| -16 | 2 | `2D88` | `00101101 10001000` |
| -15 | 2 | `2D98` | `00101101 10011000` |
| -8 | 2 | `3880` | `00111000 10000000` |
| -7 | 2 | `3980` | `00111001 10000000` |
| -4 | 2 | `3C80` | `00111100 10000000` |
| -3 | 2 | `3D80` | `00111101 10000000` |
| -2 | 2 | `3E80` | `00111110 10000000` |
| -1 | 2 | `3F80` | `00111111 10000000` |
| 0 | 1 | `48` | `01001000` |
| 1 | 1 | `58` | `01011000` |
| 2 | 1 | `68` | `01101000` |
| 3 | 1 | `78` | `01111000` |
| 4 | 1 | `84` | `10000100` |
| 5 | 1 | `8C` | `10001100` |
| 6 | 1 | `94` | `10010100` |
| 7 | 1 | `9C` | `10011100` |
| 8 | 1 | `A2` | `10100010` |
| 9 | 1 | `A6` | `10100110` |
| 10 | 1 | `AA` | `10101010` |
| 14 | 1 | `BA` | `10111010` |
| 15 | 1 | `BE` | `10111110` |
| 16 | 2 | `C110` | `11000001 00010000` |
| 17 | 2 | `C130` | `11000001 00110000` |
| 18 | 2 | `C150` | `11000001 01010000` |
| 31 | 2 | `C3F0` | `11000011 11110000` |
| 32 | 2 | `C910` | `11001001 00010000` |
| 47 | 2 | `CBF0` | `11001011 11110000` |
| 48 | 2 | `D110` | `11010001 00010000` |
| 63 | 2 | `D3F0` | `11010011 11110000` |
| 64 | 2 | `D910` | `11011001 00010000` |
| 79 | 2 | `DBF0` | `11011011 11110000` |
| 80 | 3 | `E00440` | `11100000 00000100 01000000` |
| 81 | 3 | `E004C0` | `11100000 00000100 11000000` |
| 100 | 3 | `E02640` | `11100000 00100110 01000000` |
| 200 | 3 | `E0EC40` | `11100000 11101100 01000000` |
| 500 | 3 | `E64640` | `11100110 01000110 01000000` |
| 1000 | 3 | `EE2C40` | `11101110 00101100 01000000` |
| 1100 | 3 | `EEEE40` | `11101110 11101110 01000000` |
| 1103 | 3 | `EEEFC0` | `11101110 11101111 11000000` |
| 1104 | 3 | `F00088` | `11110000 00000000 10001000` |
| 1105 | 3 | `F00098` | `11110000 00000000 10011000` |
| 2000 | 3 | `F1C088` | `11110001 11000000 10001000` |
| 5000 | 3 | `F78D88` | `11110111 10001101 10001000` |
| 10000 | 6 | `F80000254220` | `11111000 00000000 00000000 00100101 01000010 00100000` |
| 17487 | 6 | `F800009F77E0` | `11111000 00000000 00000000 10011111 01110111 11100000` |
| 17488 | 6 | `F80000A00220` | `11111000 00000000 00000000 10100000 00000010 00100000` |
| 100000 | 6 | `F80005A45220` | `11111000 00000000 00000101 10100100 01010010 00100000` |
| 1000000 | 6 | `F8003C9B7220` | `11111000 00000000 00111100 10011011 01110010 00100000` |

##### Probe data (multi-label + decimal segments)

| Path | bytes | hex | bits |
|---|------:|---|---|
| `/` | 0 | (empty) | (empty) |
| `/1/2/` | 2 | `5B40` | `01011011 01000000` |
| `/1/1/` | 2 | `5AC0` | `01011010 11000000` |
| `/1/100/` | 3 | `5F0132` | `01011111 00000001 00110010` |
| `/16/16/` | 3 | `C11C11` | `11000001 00011100 00010001` |
| `/-1/-1/` | 3 | `3F9FC0` | `00111111 10011111 11000000` |
| `/-1/1/` | 2 | `3FAC` | `00111111 10101100` |
| `/1/-1/` | 2 | `59FC` | `01011001 11111100` |
| `/0.1/` | 2 | `52C0` | `01010010 11000000` |
| `/0.2/` | 2 | `5340` | `01010011 01000000` |
| `/1.1/` | 2 | `62C0` | `01100010 11000000` |
| `/1.2/` | 2 | `6340` | `01100011 01000000` |
| `/1.3/` | 2 | `63C0` | `01100011 11000000` |
| `/2.1/` | 2 | `72C0` | `01110010 11000000` |
| `/2.10/` | 2 | `7550` | `01110101 01010000` |

##### Multi-label composition rule (cracked)

Each label's bit string ends with a `1` terminator bit (whether it's the last label or not). Labels concatenate end-to-end with **no separator**. After the last label, the bit stream is padded to byte boundary with `0`s.

Verified with `/1/2/`: label-1 = `0101` + terminator `1` = `01011`; label-2 = `0110` + `1` = `01101`; concat = `0101101101` (10 bits); pad with 6 `0`s = `01011011 01000000` = `0x5B40` ✓. Same rule on `/1/100/`: label-1 = `01011` (5 bits), label-100 = `111000000010011001` (18 bits, see tier-4 below), concat = `01011111000000010011001` (23 bits), pad 1 `0` = `01011111 00000001 00110010` = `0x5F0132` ✓.

##### Per-tier label templates (cracked: 0..3 / 4..7 / 8..15 / 16..79)

In each template, `V` marks a value-bit position (MSB → LSB), `1` and `0` mark fixed structural bits, and the final `1` is the label terminator.

**Tier P0 — positive N=0..3**: `01 VV 1` (5 bits) — value = N (2 bits, range 0..3).
- /0/ label = `01 00 1` → byte = `01001 000` = `0x48` ✓
- /3/ label = `01 11 1` → byte = `01111 000` = `0x78` ✓

**Tier P1 — positive N=4..7**: `100 VV 1` (6 bits) — value = N - 4 (2 bits, range 0..3).
- /4/ label = `100 00 1` → byte = `100001 00` = `0x84` ✓
- /7/ label = `100 11 1` → byte = `100111 00` = `0x9C` ✓

**Tier P2 — positive N=8..15**: `101 VVV 1` (7 bits) — value = N - 8 (3 bits, range 0..7).
- /8/ label = `101 000 1` → byte = `1010001 0` = `0xA2` ✓
- /15/ label = `101 111 1` → byte = `1011111 0` = `0xBE` ✓

**Tier P3 — positive N=16..79**: 12-bit label with embedded sub-tier markers. Template: `110 VV 0 V 1 VVV 1` where the 6 value bits split as 2 + 1 + 3 (weights 32, 16 || 8 || 4, 2, 1, MSB → LSB). Value = N - 16 (6 bits, range 0..63).
- /16/ label = `110 00 0 0 1 000 1` → bytes = `11000001 0001 0000` = `0xC110` ✓
- /79/ label = `110 11 0 1 1 111 1` → bytes = `11011011 1111 0000` = `0xDBF0` ✓
- The fixed `0` at position 5 and `1` at position 7 are sub-tier discriminators that distinguish tier 3 sub-ranges from each other (recursive Stern-Brocot structure).

**Tier P4 — positive N=80..1103**: 18-bit label. Value-bit positions (from probe XOR analysis): `4, 5, 6, 8, 9, 10, 12, 14, 15, 16` (10 bits). Fixed structural bits: `0` at positions 0..3 = `1110`, `1` at position 7, `1` at position 11, `0` at position 13, terminator `1` at position 17. **Bit-weight ordering is only partially confirmed**: bit 16 = LSB (weight 1, verified via /80/-/81/). Need 3-4 more probes (`/82/`, `/84/`, `/96/`, `/112/`) to lock weights of bits 15, 14, 12, 10, 9, 8, 6, 5, 4.

##### Per-tier label templates (unfinished work)

**Tier P5 — positive N=1104..17487**: 20-bit label (probably). Prefix `11110`. /1104/ = `1111000000000000 1000 1000` — last `1` at position 20, so label is 21 bits actually. Need to derive value-bit positions and weight ordering via XOR analysis on `/1105/`, `/2000/`, `/5000/`, `/17487/` (existing probe data has those — work just hasn't been done).

**Tier P6 — positive N=17488..(huge)**: ~42-bit label (6 bytes total). Prefix `1111100`. /17488/ has the new tier prefix — need to verify and derive.

**Negative-N tiers**: probe data shows negatives DON'T simply invert the positive bit pattern. /-1/ = `0x3F80` ≠ bitwise-not(/1/ = `0x58`). /-72/ = `0x2088` (2 bytes, mirrors tier-P3's 2-byte range 16..79). Hypothesis: negatives use a parallel set of tier prefixes starting with `0` instead of `1`, with the value bits possibly XOR'd against a tier-specific mask. Need an XOR analysis pass like the one done for positives. /-1/ through /-72/ map to 2-byte labels; /-73/ jumps to 3-byte = N5 territory in mirror.

**Decimal sub-ordinals**: probe shows `/0.1/` and `/0.2/` differ from `/0/` only in the lower bits AFTER the label-1 terminator. `/0/` = `0x48` = `01001000` (label `01001` + 3 pad). `/0.1/` = `0x52C0` = `01010010 11000000`. Diff: bits 4..9 in `/0.1/` = `010011`. Hypothesis: sub-ordinals extend the segment with a continuation marker bit somewhere, then add more label bits encoding the sub-ordinal's value with the same tier system. Need decimal-only probes like `/1.0/` (does this normalize to `/1/`?), `/1.0.0/`, `/1.1.1/`, `/1.4/`, `/1.16/`, `/1.80/` to derive the rule.

##### Implementation plan when resumed

1. **Pre-flight probe**: bcp-out a small hierarchyid column to confirm BCP wire format = CAST varbinary form (or document the divergence). The TDS UDT wire format is documented to be the same as CAST, but verify before committing.
2. **Finish tier P4 / P5 / P6 weight ordering**: 3-5 targeted probes per tier, XOR-analyze, lock the templates. Each probe is 2 minutes of work.
3. **Crack negative-N mirror**: probe `/-1/`, `/-3/`, `/-4/`, `/-8/`, `/-9/`, `/-15/`, `/-16/`, `/-17/`, `/-72/`, `/-73/`, `/-1103/`, `/-1104/`, `/-17487/`. Hypothesis from existing data: negative tier-N1 ≡ positive tier-P0 with prefix `00` instead of `01`; need to verify across boundaries.
4. **Crack decimal sub-ordinals**: probe `/1.0/`, `/1.1/`...`/1.79/`, `/1.80/`. Look for tier boundaries within the sub-ordinal.
5. **Replace `HierarchyIdSqlType.Encode` / `Decode`**: write a bit-level writer/reader that emits the per-tier template and concatenates labels with the trailing-`1` + zero-pad rule. The existing `int[][]` internal representation can stay; only the byte form changes.
6. **Switch the existing 22-test suite** to assert byte-level equality against the probe table above (currently only round-trip is asserted). Add 4-5 cross-engine fixture cases — bytes captured from real SQL Server, fed to `CAST(0x… AS hierarchyid)`, with the result asserted via `.ToString()`.
7. **Document the change** in CLAUDE.md (the "Not modeled" entry shrinks further) and remove the "simulator-native byte form" caveat from this section.

##### Probe scaffold (one-shot rebuild)

The original probe scaffold lived at `/tmp/hierarchyid-probe/` and was deleted with the rest of the bundle's working files. To rebuild: `dotnet new console`, add `Microsoft.Data.SqlClient` ref, connect to the reference SQL Server 2025, `SELECT CAST(hierarchyid::Parse('/N/') AS varbinary(900))` for each N. The full pre-deletion probe source was ~150 lines and is reconstructable in 5 minutes from this section's probe-table column structure alone.

22 new tests in `HierarchyIdTests.cs` cover the static factories, all five instance methods including AW's `GetDescendant` combinations, comparison via `ORDER BY` against the probe-canonical sequence, storage round-trip through a heap table, NULL handling (`Parse(NULL)`, `IsDescendantOf(NULL)`, `GetAncestor` past root, NULL hierarchyid round-trip), `DECLARE @h hierarchyid` + variable-method-call, and the `sys.types` row shape (system_type_id 240, user_type_id 128). Test count with DataRow inflation: ~50 distinct cases.

**Deferred follow-ups** (for the BCP loader bundle or beyond): byte-identical CAST encoding (reverse-engineer the recursive Stern-Brocot bit pattern for all 6+ positive tiers + negative-mirror + decimal-segment cases), `.GetReparentedValue(oldRoot, newRoot)`, `.Read(BinaryReader)` / `.Write(BinaryWriter)` (CLR-stream surface), `GetDescendant`'s sub-ordinal placement algorithm for paths already carrying decimal sub-ordinals (current rule is "extend c1 with [+1] at the deepest sub-ordinal position" which produces a valid `c1 < result` result but may not exactly match SQL Server's choice for the rare `/1/2.1/` + `/1/2.2/` style inputs — AW doesn't exercise this path).

### [x] DDL trigger (`CREATE TRIGGER … ON DATABASE`) (shipped 2026-05-14)
Parse-and-store-but-no-fire surface for database-scope DDL triggers. AW's `[ddlDatabaseTriggerLog]` (`FOR DDL_DATABASE_LEVEL_EVENTS`) loads end-to-end and surfaces in `sys.triggers` with the probe-confirmed shape: `parent_class=0`, `parent_class_desc='DATABASE'`, `parent_id=0`, `type_desc='SQL_TRIGGER'`, `is_instead_of_trigger=0`. The body source is captured verbatim for the eventual `sys.sql_modules` row (not yet modeled — see deferred work below).

**Storage**: new `DdlTrigger` class (`SqlServerSimulator/DdlTrigger.cs`) carries name + object_id + event-type list + body source + is_disabled flag. `Database.DdlTriggers` is the per-database `ConcurrentDictionary<string, DdlTrigger>` (case-insensitive keys); not per-schema because DDL triggers belong to the database itself. The class extends `SchemaObject` for the object-id + create-date pattern but doesn't participate in any schema's shared namespace except for name collision detection at CREATE time (probe-confirmed: a DDL trigger named `foo` collides with a same-named DML trigger / table / view / proc in the same schema).

**Parser**: `Simulation.CreateTrigger.cs::TryParseCreateTrigger` extended with a branch — after the `ON` keyword, if the next token is `DATABASE`, dispatch to a new `ParseDdlTriggerBody` helper that handles `[WITH options] {FOR|AFTER} <event_type_list> AS <body>`. Event types parse as bare identifiers (Name / UnquotedString) and store verbatim in `DdlTrigger.EventTypes`. The `DROP TRIGGER name ON DATABASE` form lives in `Simulation.Drop.cs::DropOneTrigger`, which peeks the next tokens via `SaveCheckpoint` / `RestoreCheckpoint` to decide between the DML-trigger and DDL-trigger paths.

**Catalog**: `sys.triggers` enumerator in `BuiltInResources.cs::EnumerateSysTriggers` now yields rows for `Database.DdlTriggers` after the per-schema DML trigger loop, with the probe-confirmed `parent_class=0` / `parent_class_desc='DATABASE'` / `parent_id=0` shape.

**Deferred**: trigger firing (the simulator doesn't dispatch DDL events to any trigger loop — accepted as a documented behavior gap; AW's trigger body is an audit-log writer, not a load-bearing dependency). `sys.sql_modules` to surface the body — not modeled yet, on the follow-up list. `DISABLE/ENABLE TRIGGER … ON DATABASE` not wired (the per-schema disable / enable path doesn't extend to the per-database dict).

7 new tests in `DdlTriggerTests.cs` cover CREATE / DROP / DROP IF EXISTS / multi-event / collision / CREATE OR ALTER plus the catalog-view row shape.

### [x] Permission statements (`GRANT` / `REVOKE` / `DENY` + principal DDL) (shipped 2026-05-14)
AW emits 2 GRANTs (`GRANT VIEW ANY COLUMN ENCRYPTION KEY DEFINITION TO public` + `GRANT VIEW ANY COLUMN MASTER KEY DEFINITION TO public`) — both database-scope, both to the built-in `public` role. No `CREATE USER` / `CREATE ROLE` in AW. The bundle ships the full parse-and-store surface so the model.xml round-trips: principal DDL, the GRANT/REVOKE/DENY trio, and three new catalog views.

**Storage**: `DatabasePrincipal` (`SqlServerSimulator/DatabasePrincipal.cs`) carries principal_id + name + type_code (`S` = SQL_USER, `R` = DATABASE_ROLE) + type_desc + is_fixed_role + create_date. `DatabasePermission` (`SqlServerSimulator/DatabasePermission.cs`) carries class + major_id + minor_id + grantee/grantor ids + permission_name + 4-char type code + state (`G`/`W`/`D`/`R`). Both live on `Database`: a `ConcurrentDictionary<string, DatabasePrincipal>` keyed by name, a `List<DatabasePermission>` for grants/denies, and a `List<(int RoleId, int MemberId)>` for role membership. Five fixed principals are pre-seeded at construction time matching real SQL Server's `sys.database_principals` ids (probe-confirmed 2026-05-14): public=0, dbo=1, guest=2, INFORMATION_SCHEMA=3, sys=4. User principals start at 5 via `Database.AllocatePrincipalId`.

**Parser** (`Simulation/Simulation.GrantRevokeDeny.cs` + `Simulation/Simulation.PrincipalDdl.cs`):
- `GRANT <perm_list> [ON <securable>] TO <principal_list> [WITH GRANT OPTION] [AS <grantor>]` — permission list eats word sequences ending at comma / ON / TO / AS / WITH (a sequence of bare identifiers fuses into one permission name); ON clause accepts `<name>`, `OBJECT::<name>`, `SCHEMA::<name>`, `DATABASE::<name>`, `TYPE::<name>` via a peek-restore pattern for the `::` operator pair. Grantee names accept either `Name` or `ReservedKeyword` raw text (so `public` — tokenized as `ReservedKeyword.Public` — works without special-casing).
- `REVOKE [GRANT OPTION FOR] <perm_list> [ON <securable>] FROM <principal_list> [CASCADE] [AS <grantor>]` — same shape; REVOKE GRANT OPTION FOR consumes-and-tracks (removes the W-state row only); CASCADE parses-and-discards.
- `DENY <perm_list> [ON <securable>] TO <principal_list> [AS <grantor>]` — same shape; produces D-state rows.
- `CREATE USER name [{FOR | FROM} ...] [WITH ...]` — parses the name, allocates a principal_id, stores with `type_code='S'` / `type_desc='SQL_USER'`. The full optional-clause grammar (FROM LOGIN / WITH PASSWORD / DEFAULT_SCHEMA / etc.) parses-and-discards through the next statement boundary via a shared `ConsumeToStatementBoundary` helper.
- `CREATE ROLE name [AUTHORIZATION owner]` — same shape; `type_code='R'` / `type_desc='DATABASE_ROLE'`. AUTHORIZATION clause parse-and-discards.
- `ALTER ROLE name { ADD MEMBER name | DROP MEMBER name | WITH NAME = newname }` — ADD/DROP MEMBER append/remove `(role_id, member_id)` to `Database.RoleMembers`. WITH NAME parses-and-discards.
- `DROP USER [IF EXISTS] name` and `DROP ROLE [IF EXISTS] name` — drop from `Database.Principals` and cascade-remove `Database.RoleMembers` entries that reference the removed id. The DROP USER / DROP ROLE paths are dispatched ahead of the generic DROP-target switch in `Simulation.Drop.cs` because principals don't live in a per-schema dict.

**Catalog views** in `BuiltInResources.cs`:
- `sys.database_principals` (12-col probe-confirmed shipped subset): name / principal_id / type / type_desc / default_schema_name (NULL — not tracked) / create_date / modify_date / owning_principal_id (NULL) / sid (NULL — no SID model) / is_fixed_role / authentication_type / authentication_type_desc (both NULL).
- `sys.database_permissions` (10-col probe-confirmed shipped subset): class / class_desc / major_id / minor_id / grantee_principal_id / grantor_principal_id / type (4-char code) / permission_name / state (1-char) / state_desc.
- `sys.database_role_members` (2-col, the full row): role_principal_id / member_principal_id.

**Permission type code derivation**: 4-char code is the first-letter-of-each-word right-padded with spaces (e.g. `VIEW ANY COLUMN MASTER KEY DEFINITION` → `VACM`). Approximate — real SQL Server's mapping uses a per-permission lookup that diverges for short names (`SELECT` → `SL`, `UPDATE` → `UP`); a polish pass would import the canonical table. Class_desc / state_desc are spelled out per the probe-confirmed enum (`DATABASE` / `OBJECT_OR_COLUMN` / `SCHEMA` / `DATABASE_PRINCIPAL`; `GRANT` / `GRANT_WITH_GRANT_OPTION` / `DENY` / `REVOKE`).

**Errors enforced verbatim**: `Msg 15151` for unknown principal in GRANT/REVOKE/DENY/ALTER ROLE; `Msg 15023` for duplicate CREATE USER / CREATE ROLE name. Both probe-confirmed against SQL Server 2025.

**Deferred**: server-scope grants (`GRANT … TO public ON SERVER`), schema-scope grants, column-scope grants, `WITH GRANT OPTION` cascading semantics (parses + records the W state, but doesn't propagate). The exact 4-char permission type codes for the full permission catalog (vs the simulator's first-letter heuristic). CREATE LOGIN / ALTER LOGIN / DROP LOGIN (server-scope, not database-scope). The full `CREATE USER … FROM EXTERNAL PROVIDER` / `WITH PASSWORD` semantic — currently parse-and-discard.

15 new tests in `PermissionStatementTests.cs` cover GRANT / REVOKE / DENY happy paths, WITH GRANT OPTION, multi-permission comma lists, object-scope ON, unknown-principal Msg 15151, CREATE USER / CREATE ROLE happy + duplicate Msg 15023, ALTER ROLE ADD MEMBER landing in role_members, DROP USER / DROP ROLE happy + IF EXISTS + cascade-drop-membership, and fixed-principal pre-seed visibility.

### [x] Full-text catalog + index (skip-with-diagnostic, shipped 2026-05-15)
DDL + catalog views ship; query-time predicates are explicit
`NotSupportedException`. AW model.xml's 1 `SqlFullTextCatalog`
(`[AW2025FullTextCatalog]`) + 3 `SqlFullTextIndex` elements load
end-to-end; the existing AW procedure `uspSearchCandidateResumes` (which
exercises `CONTAINSTABLE`) parses through CREATE PROCEDURE (proc bodies
are stored verbatim and only re-tokenized on EXEC) — calling the proc
fails loudly with the documented NotSupportedException, which is exactly
the skip-with-diagnostic stance the doc called for.

**Storage**: new `FullTextCatalog` class (`SqlServerSimulator/FullTextCatalog.cs`)
carries id + name + is_default + is_accent_sensitivity_on + principal_id +
create_date; `Database.FullTextCatalogs` is the per-database
`ConcurrentDictionary<string, FullTextCatalog>` (case-insensitive). New
`FullTextIndex` class (`SqlServerSimulator/FullTextIndex.cs`) carries
catalog_id + key_index_name + unique_index_id (resolved at CREATE) +
List<FullTextIndexColumn>; the index lives directly on
`HeapTable.FullTextIndex` as a single nullable slot (real SQL Server's
invariant: at most one FT index per table). `FullTextIndexColumn` carries
column_id (1-based storage ordinal) + language_id + nullable
type_column_id. The catalog-id counter starts at 5 — matches Microsoft
Learn's documented numbering convention for user catalogs (id 0..4 are
reserved internal slots).

**Parsers** in `Simulation/Simulation.FullText.cs`:
- `CREATE FULLTEXT CATALOG name [AS DEFAULT] [AUTHORIZATION owner]
  [WITH ACCENT_SENSITIVITY = {ON|OFF}] [ON FILEGROUP fg] [IN PATH '…']` —
  the filesystem-placement trailers (ON FILEGROUP / IN PATH) parse-and-
  discard. AS DEFAULT demotes any prior default before promoting this
  catalog. AUTHORIZATION owner resolves against `Database.Principals`
  (default `dbo`).
- `CREATE FULLTEXT INDEX ON table (col [TYPE COLUMN typeCol] [LANGUAGE n]
  [STATISTICAL_SEMANTICS] [, ...]) [KEY INDEX name] [ON catalog [, FILEGROUP fg] | ON (catalog [, FILEGROUP fg])]
  [WITH (option [, ...])]` — multi-column lists, the TYPE COLUMN nested
  reference (used for varbinary columns paired with a doc-extension
  column — AW's `[Production].[Document]` shape), the LANGUAGE LCID
  (integer literal — language-name literal parse-and-discards), the
  STATISTICAL_SEMANTICS flag (parse-and-discard), both paren and bare ON
  catalog forms, and the WITH (…) trailing options block (parse-and-
  discard via the shared `SkipBalancedParens` helper).
- `DROP FULLTEXT CATALOG name` and `DROP FULLTEXT INDEX ON table` —
  routed through dedicated `TryParseDropFullText` ahead of the generic
  DROP-target switch (DDL form sub-keywords have their own grammar).
- Statement dispatch: `Fulltext` added to `ContextualKeyword` enum;
  CREATE / DROP routes match `UnquotedString { ContextualKeyword:
  ContextualKeyword.Fulltext }` and dispatch through
  `Simulation.TryParseCreateFullText` / `Simulation.TryParseDropFullText`.

**Predicate / rowset rejection**:
- `WHERE CONTAINS(col, '…')` / `WHERE FREETEXT(col, '…')` —
  `BooleanExpression.ParseAtom` intercepts the `ReservedKeyword`s
  `Contains` and `FreeText` ahead of the comparison parse and raises
  `NotSupportedException` with the message `"Full-text search
  predicates (CONTAINS|FREETEXT) are not modeled."`.
- `FROM CONTAINSTABLE(...) AS t` / `FROM FREETEXTTABLE(...)` / the two
  SEMANTIC* variants — `Selection.ParseSingleFromSource` intercepts the
  rowset-function keywords ahead of the syntax-error default and raises
  `NotSupportedException` with `"Full-text rowset functions
  (CONTAINSTABLE|...) are not modeled."`.

**Catalog views** in `BuiltInResources.cs`:
- `sys.fulltext_catalogs` (9-col): fulltext_catalog_id / name / path
  (NULL — no on-disk storage) / is_default / is_accent_sensitivity_on /
  data_space_id (NULL) / file_id (NULL) / principal_id / is_importing
  (always false).
- `sys.fulltext_indexes` (14-col): object_id / unique_index_id /
  fulltext_catalog_id / is_enabled (true) / change_tracking_state ('A')
  / change_tracking_state_desc ('AUTO') / has_crawl_completed (true) /
  crawl_type ('F') / crawl_type_desc ('FULL') / crawl_start_date (NULL)
  / crawl_end_date (NULL) / stoplist_id (NULL) / data_space_id (NULL) /
  property_list_id (NULL).
- `sys.fulltext_index_columns` (5-col, full row): object_id / column_id /
  type_column_id / language_id / statistical_semantics (always false).

Column subset matches Microsoft Learn's documented surface for SQL
Server 2022+ (the reference instance doesn't have Full-Text installed —
the catalog views aren't registered without the FT service — so probe-
confirmation isn't available; column shapes are taken from
learn.microsoft.com/sql/relational-databases/system-catalog-views/).

21 new tests across `FullTextDdlTests.cs` (17) + `FullTextPredicateTests.cs`
(4): catalog AS DEFAULT semantics + accent-sensitivity / duplicate-name
Msg 2714 / drop happy + missing Msg 208 / default-promotion demotion; index
single-col / multi-col with TYPE COLUMN / default-catalog resolution /
missing table Msg 208 / duplicate-on-table Msg 2714 / unknown column
Msg 207 / unknown key-index Msg 208 / row shape (is_enabled / crawl_type) /
principal_id round-trip; predicate + rowset NotSupportedException
verification. Total: 4141 main (+21 new) / 227 internal / 328 EFCore / 58
analyzers, all green Debug + Release.

**Deferred**: query-time text search (tokenizer/stemmer/inverted-index
pipeline) — out of scope as documented. ALTER FULLTEXT CATALOG /
INDEX (REORGANIZE / REBUILD / START/STOP POPULATION / ADD/DROP column)
— `NotSupportedException` at parse. Filesystem-placement semantics
(`ON FILEGROUP` / `IN PATH`) parse-and-discard. The legacy
`sys.fulltext_languages` / `sys.fulltext_document_types` /
`sys.fulltext_stoplists` lookup catalogs aren't shipped — apps that
introspect the language enum hit a missing-view error.

### [x] `xml` data type + XML schema collections + XML methods + XML indexes (skip-with-diagnostic, shipped 2026-05-15)
DDL + catalog views + xml-typed columns + xml(schema_collection) bindings all ship; query-time XPath/XQuery methods raise NotSupportedException at execute. AW's 9 xml columns, 6 SqlXmlSchemaCollection, 8 SqlXmlIndex elements all round-trip through model.xml; the existing `uspGetEmployeeCandidate`-style procs that exercise `.value()` parse cleanly at CREATE and surface the documented NotSupportedException on EXEC.

**Storage**: `XmlSqlType : SqlType` (singleton, SqlServerName="xml", SystemTypeId=241, IsLob=true) — payload stored identically to `nvarchar(MAX)` (raw UTF-16 LE bytes). Type identity preserved through `sys.columns.user_type_id` / `sys.types`. `XmlSchemaCollection` class carries id + name + schema_id + nullable principal_id + xsdText + create_date / modify_date; `Schema.XmlSchemaCollections` is the per-schema dict (shares the type-namespace with TableTypes / AliasTypes — Msg 219 on duplicate). `Database.AllocateXmlCollectionId` returns 65536 first (probe-confirmed). `HeapColumn.XmlSchemaCollection` is a nullable ref linking xml columns to their collection — metadata only; the simulator does not validate xml payloads against the XSD. `HeapTable.XmlIndexes` is a `List<XmlIndex>`; `XmlIndex` carries name + columnOrdinal + isPrimary + UsingPrimaryIndexName (for secondary) + nullable SecondaryType (PATH / VALUE / PROPERTY) + ObjectId.

**Parsers** (`Simulation/Simulation.Xml.cs`):
- `CREATE XML SCHEMA COLLECTION [schema.]name AS '<xsd:schema>…'` — XSD text stored verbatim. No XSD parsing; AW's 6 schema-collection xsd-text payloads (with embedded namespaces, complex types, restrictions, sequences) round-trip as opaque strings.
- `DROP XML SCHEMA COLLECTION [schema.]name` — removes the entry.
- `CREATE PRIMARY XML INDEX name ON table(col) [WITH (…)]` — primary index for an xml column.
- `CREATE XML INDEX name ON table(col) USING XML INDEX primary_name FOR {PATH | VALUE | PROPERTY} [WITH (…)]` — secondary indexes that reference a primary index.
- `WITH (…)` trailing options block parse-and-discards via the shared SkipBalancedParens helper.
- xml column-type position: `xml`, `xml(name)`, `xml(CONTENT name)`, `xml(DOCUMENT name)` — the CONTENT / DOCUMENT discriminator parse-and-discards. Detection happens in `ParseOneColumnIntoLists` via a peek (`PeekIsXmlSchemaArgument`) that distinguishes the schema-collection-name form from a length / MAX spec; matched only when the bare 1-part type name is "xml". Unknown schema collection → Msg 208.
- Statement dispatch: `Xml` added to `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Xml }` and `ReservedKeyword { Keyword: Keyword.Primary }` (the PRIMARY XML INDEX form). SCHEMA is reserved, so the sub-keyword check uses `Keyword.Schema` — COLLECTION is a bare identifier.

**XML method execution rejection** (`Parser/Expressions/XmlMethodCall.cs`): instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`). Parses cleanly so CREATE VIEW / CREATE PROCEDURE bodies that reference XML methods can be stored verbatim; runtime evaluation raises `NotSupportedException` with `"XML instance method '.NAME()' is not modeled."`. Static result-type inference still applies (`.exist()`→bit, `.value()`→nvarchar(MAX) stub, others→xml) so projection-schema resolution works at the parser level.

**Catalog views** in `BuiltInResources.cs`:
- `sys.xml_schema_collections` (6-col, probe-confirmed against SQL Server 2025): xml_collection_id / schema_id / principal_id (NULL — AUTHORIZATION clause not modeled) / name / create_date / modify_date.
- `sys.xml_indexes` (9-col probe-derived shipped subset; real surface 26 cols): object_id / name / index_id / type (=3) / type_desc (='XML') / using_xml_index_id (NULL for primary) / secondary_type (char(1): P/V/R) / secondary_type_desc / is_primary_key (always false).

21 new tests in `XmlTests.cs` cover: xml column round-trip (text payload through INSERT / SELECT); NULL handling; sys.columns reports xml type identity; CREATE XML SCHEMA COLLECTION happy + qualified schema + duplicate Msg 219; xml(name) binding + CONTENT/DOCUMENT discriminators + unknown-collection Msg 208; DROP collection; CREATE PRIMARY XML INDEX + three secondary forms (PATH/VALUE/PROPERTY) + using_xml_index_id linkage; duplicate-name Msg 2714; XML method NotSupportedException at execute (`.value` / `.query` / `.exist`); CREATE VIEW with XML method succeeds-then-fails-at-execute; xml_collection_id starts at 65536; CAST xml→nvarchar round-trip. Total: 4162 main (+21 new) / 227 internal / 328 EFCore / 58 analyzers — all green Debug + Release.

**Deferred** (real feature work after the loader baseline ships): XPath/XQuery evaluation pipeline (`.value` / `.nodes` / `.query` / `.exist` / `.modify`), XSD validation against `xml(schema_collection)` bindings, `FOR XML` query-output clause, `ALTER XML SCHEMA COLLECTION ADD` for incremental schema additions, secondary index selectivity hints (the `SELECTIVE XML INDEX` variant from SQL Server 2014+).

### [x] `geography` / `geometry` data types (skip-with-diagnostic, shipped 2026-05-15)
DDL + catalog views + spatial-typed columns + WKT-form round-trip + spatial-index parse-and-discard all ship; OGC + Microsoft-extension methods raise NotSupportedException at execute except `.ToString()` which returns the stored WKT. Static constructors (`geography::Parse(wkt)` / `geography::STGeomFromText(wkt, srid)` / `geometry::Point(x, y, srid)`) construct spatial values that round-trip through column storage. The sole AW spatial column (`Person.Address.SpatialLocation`, geography) loads end-to-end as a first-class spatial-typed column rather than the originally-recommended `varbinary(MAX)` degradation.

**Storage**: `GeographySqlType` + `GeometrySqlType` (singletons, both inherit `SpatialSqlType : SqlType(SqlTypeCategory.String)`) in `Storage/SpatialType.cs`. `SqlServerName` = "geography" / "geometry", SystemTypeId=240 (shared with hierarchyid; CLR-UDT family), UserTypeId=130 / 129 respectively, IsLob=true. Payload encoding: raw UTF-16 LE of the constructed WKT string — the simulator's degraded-mode form. Round-trip via `CAST AS nvarchar(MAX)` returns the WKT verbatim; `geography::Parse('POINT(0 0)')` stores 'POINT(0 0)'. The byte form on disk is **not** SQL Server's documented binary CLR-UDT representation — same simulator-specific deferral as `hierarchyid`, to be replaced when the BACPAC loader bundle implements wire-format encoding. `SqlValue.FromGeography` / `FromGeometry` are the factory entry points; `SqlValue.FromString` routes the spatial branches when called with a SpatialSqlType target.

**Method-call surface** (`Parser/Expressions/SpatialMethodCall.cs`): broad closed accept-list (~70 names) covering every OGC predicate / accessor / constructor exposed on both geography and geometry, plus the common Microsoft extensions (`Lat` / `Long` / `MakeValid` / `Reduce` / `Filter` / `BufferWithTolerance` / `STSrid` / etc.). Parses cleanly so CREATE VIEW / CREATE PROCEDURE bodies that reference spatial methods store verbatim; runtime evaluation raises `NotSupportedException` with `"Spatial instance method '.NAME()' is not modeled."` except `.ToString()` which returns the stored WKT as `nvarchar(MAX)`. Static result-type inference still applies (`.STDistance` / `.STArea` / `.STLength` → float; `.STContains` / `.STIntersects` / `.STIsValid` → bit; `.STAsText` / `.STGeometryType` / `.ToString` → nvarchar(MAX); `.STAsBinary` → varbinary(MAX); `.STSrid` / `.STNumGeometries` / etc. → int; constructors → same spatial type as receiver) so projection-schema resolution works at the parser level. The `.ToString()` name collides with hierarchyid's `.ToString()` (which dispatches via the existing HierarchyIdMethodCall path); `HierarchyIdMethodCall.Run` was extended to detect a spatial receiver at runtime and return the WKT through the spatial path instead, avoiding a dispatch-order regression for the hierarchyid tests.

**Static-call surface** (`Parser/Expressions/SpatialStaticCall.cs`): `geography::` and `geometry::` type-scope dispatched alongside `hierarchyid::` in Expression.cs's `::` operator handling. `Parse(wkt)` / `STGeomFromText(wkt, srid)` accept a single WKT-string argument (the SRID is parsed-and-discarded — the simulator doesn't track per-value SRID). `Point(x, y, srid)` accepts numeric coordinates and synthesizes a `POINT (x y)` WKT. Every other static method raises `NotSupportedException` at Run.

**Parser** (`Simulation/Simulation.Spatial.cs`):
- `CREATE SPATIAL INDEX name ON table(col) [USING <i>scheme</i>] [WITH (BOUNDING_BOX = (xmin, ymin, xmax, ymax) | GRIDS = (level [, …]) | CELLS_PER_OBJECT = n | <i>any other index option</i>)]` — parses fully, stores in `HeapTable.SpatialIndexes`. Default tessellation_scheme when no USING clause is given: `GEOMETRY_AUTO_GRID` (geometry-typed col) / `GEOGRAPHY_AUTO_GRID` (geography-typed col), matching probed real-server behavior. GRIDS level arguments accept either numeric codes (1/2/3) or named levels (LOW / MEDIUM / HIGH). Unknown options inside the WITH clause skip via balanced-paren consumption. Non-spatial column → `NotSupportedException`; duplicate index name → Msg 2714.
- Statement dispatch: `Spatial` added to `ContextualKeyword` enum; CREATE SPATIAL routes via `UnquotedString { ContextualKeyword: ContextualKeyword.Spatial }` in TryParseCreate. INDEX is reserved, so the sub-keyword check uses `Keyword.Index`.

**Catalog views** in `BuiltInResources.cs`:
- `sys.spatial_indexes` (23-col, probe-confirmed against SQL Server 2025 on 2026-05-15): object_id / name / index_id / type (=4) / type_desc (='SPATIAL') / is_unique (=false) / data_space_id (=1) / ignore_dup_key / is_primary_key / is_unique_constraint / fill_factor / is_padded / is_disabled / is_hypothetical / is_ignored_in_optimization / allow_row_locks (=true) / allow_page_locks (=true) / spatial_index_type (3 for geometry / 4 for geography) / spatial_index_type_desc ('GEOMETRY' / 'GEOGRAPHY') / tessellation_scheme / has_filter / filter_definition / auto_created.
- `sys.spatial_index_tessellations` (16-col, probe-confirmed): object_id / index_id / tessellation_scheme / bounding_box_xmin/ymin/xmax/ymax / level_1_grid + level_1_grid_desc / ... / level_4_grid + level_4_grid_desc / cells_per_object. Unspecified GRIDS levels surface as NULL; level_*_grid_desc translates the 1/2/3 codes to 'LOW' / 'MEDIUM' / 'HIGH'.
- `sys.spatial_reference_systems` (6-col): empty by default (real SQL Server pre-seeds ~390 EPSG/ESRI SRID rows; the simulator surfaces the column shape but skips the WKT-laden seed payload). spatial_reference_id / authority_name / authorized_spatial_reference_id / well_known_text / unit_of_measure / unit_conversion_factor.

The `sys.types` row data was pre-existing (rows 33-34 in `BuiltInResources.cs:SystypesRowData` already carried geography and geometry with system_type_id=240 and user_type_id=130 / 129 respectively — the bundle just wired them into `ResolveSimpleKeyword` so `CREATE TABLE (g geography)` actually accepts them, and added the `SpatialSqlType => (-1, 0, 0)` arm to `GetSysColumnMetadata` so `sys.columns` reports the type identity).

23 new tests in `SpatialTypeTests.cs` cover: column round-trip (both geography and geometry); NULL storage; `sys.types` reports user_type_id=130 / 129 for geography / geometry sharing system_type_id=240; `sys.columns` reports spatial type identity + max_length=-1; geography::Parse from nvarchar literal; geometry::Point synthesizes a WKT from coordinates; `.ToString()` returns stored WKT; `.STDistance` / `.STAsText` / `.STIntersects` raise NotSupportedException at execute; CREATE VIEW with spatial method succeeds at CREATE and fails at execute (verifies the late-binding stance); CREATE SPATIAL INDEX populates sys.spatial_indexes (geometry with explicit bounding-box; geography with default GEOGRAPHY_AUTO_GRID); BOUNDING_BOX round-trips through sys.spatial_index_tessellations; GRIDS with LOW / HIGH level names parse to 1 / 3 codes + CELLS_PER_OBJECT; duplicate index name → Msg 2714; sys.spatial_reference_systems empty + column-shape reachable; CAST geography → nvarchar round-trip. Total: 4185 main (+23 new) / 227 internal / 328 EFCore / 58 analyzers — all green Debug + Release.

**Deferred** (real feature work after the loader baseline ships): WKT/WKB parsing for validation (currently any string is accepted as a "WKT" payload); OGC method evaluation pipeline (`.STDistance` / `.STIntersects` / `.STArea` / etc.); SRID tracking + transformation; spatial-index query-planner integration (the index parses cleanly but never accelerates anything); the `sys.spatial_reference_systems` seed data (~390 EPSG/ESRI rows); `ALTER SPATIAL INDEX` (REORGANIZE / REBUILD); and the documented byte-identical CAST encoding for cross-engine binary transfer.

## BCP wire format

The `Data/<schema>.<table>/TableData-NNN-NNNNN.BCP` files are the per-table data payload. The full type matrix below is probe-confirmed against AdventureWorks2025 hex-dumps on 2026-05-15.

| Type family | Wire layout | NULL sentinel |
|---|---|---|
| Fixed-width numeric (`int`, `bigint`, `smallint`, `tinyint`), temporal (`datetime`, `smalldatetime`, `date`) NOT NULL | raw bytes LE, no prefix | n/a |
| Same types NULLABLE | 1-byte length prefix (= type width) + raw bytes | `0xFF` |
| `bit` (NOT NULL or NULL, with-or-without UDDT alias) | 1-byte length prefix (= 1) + 1 raw byte | `0xFF` |
| Length-prefixed fixed (`uniqueidentifier`, `decimal`/`numeric`, `money`, `smallmoney`, `datetime2(N)`, `time(N)`, `datetimeoffset(N)`) — always 1-byte-prefix even NOT NULL | 1-byte length prefix (= width) + raw bytes | `0xFF` |
| UDDT-aliased columns of any base type (e.g. `dbo.Flag` over bit, `dbo.OrderNumber` over nvarchar) | shaped as if nullable (1-byte / 2-byte / 8-byte prefix per base family) regardless of declared nullability | per base-family NULL sentinel |
| Variable-length bounded (`nvarchar(N)`, `varchar(N)`, `nchar(N)`, `char(N)`, `varbinary(N)`, `binary(N)`) | 2-byte LE byte-length prefix + bytes | `0xFFFF` |
| MAX types (`varchar(MAX)`, `nvarchar(MAX)`, `varbinary(MAX)`), `xml`, CLR-UDT family (`hierarchyid`, `geography`, `geometry`) | 8-byte LE length prefix + N bytes inline (NOT the TDS-PLP chunked encoding) | `0xFFFFFFFFFFFFFFFF` (-1 signed) |

Probe-confirmed corrections from the original first-cut matrix:
- `money` / `smallmoney` / `time(N)` / `datetime2(N)` / `datetimeoffset(N)` were originally cataloged as "1-byte length prefix" but are actually fixed-raw with no prefix when NOT NULL (only nullable variants prefix). Reclassified during Phase G.
- `bit` was originally cataloged as 1-byte raw (matching other fixed-width numerics). Probe against AW's plain-bit `Production.Document.FolderFlag` (the only non-UDDT bit column in AW) revealed it's actually 1-byte-length-prefixed regardless of nullability — matches the UDDT-aliased bit shape.
- MAX types + xml + CLR-UDT (hierarchyid / geography / geometry) all share the **simple inline** 8-byte-prefix shape (NOT the chunked PLP form used in TDS network traffic). Probe-confirmed by ProductPhoto's 1077-byte ThumbNailPhoto flowing inline with no chunk markers / terminator, and by HumanResources.JobCandidate's 9086-byte Resume xml column likewise inline.

Encoding-edge items still without probe coverage (none seen in AW):
- `text` / `ntext` / `image` legacy LOB family — AW doesn't use any. Likely same 8-byte-prefix shape as the MAX family, but not confirmed.
- `sql_variant` envelope — AW doesn't use it. Probably type-tag byte + per-type encoding.
- TDS-PLP chunked form (8-byte length = `0xFFFFFFFFFFFFFFFE` "unknown total", chunks with 4-byte length markers terminating on 4-byte zero) — not seen in any bacpac shard; reserved for live TDS traffic only.

## Order of operations toward AW baseline

Rough sequence — work each bundle to completion, update this checklist, then revisit BACPAC scoping once the prerequisites land:

1. ~~**Database options expansion**~~ + ~~**UDDTs / alias types**~~ + ~~**Extended properties**~~ + ~~**`hierarchyid` (AW-minimum-viable)**~~ + ~~**DDL trigger + permission statements**~~ (all shipped 2026-05-14) + ~~**Full-text catalog + index (skip-with-diagnostic)**~~ + ~~**`xml` data type + schema collections + indexes (skip-with-diagnostic)**~~ + ~~**`geography` / `geometry` data types (skip-with-diagnostic)**~~ (latter three shipped 2026-05-15). **All prerequisites complete.**
2. ~~**Loader baseline implementation**~~ — shipped through Phase I (2026-05-15). `Simulation.FromBacpac(string, out BacpacLoadResult)` + Stream overload, internal until external API decisions land. All 8 model phases (schema + UDDTs + DB options → tables → constraints → FKs → indexes → views → programmable objects → extended properties) + BCP data pass ship in `SqlServerSimulator/Storage/Bacpac/`. AW coverage as of 2026-05-15: 5/5 schemas, 71/71 tables, 90/90 FKs, 89/89 CHECKs, 152/152 DEFAULTs, 89/95 indexes, 11/20 views, 8/10 procs, 10/11 functions, 10/10 DML triggers, 1/1 DDL trigger, 527/538 extended properties, and **760,167 / 760,167 rows (100%)**.
3. ~~**Resilient loader + sequences + roles + table types**~~ — shipped 2026-05-15. `RunPhase` catches per-element exceptions, records as `Skipped` with `"Load failed: …"` prefix, and continues. `EmitSequence` translates `SqlSequence` → `CREATE SEQUENCE [schema].[name] AS <type> START WITH N INCREMENT BY M` in phase 1. `EmitRole` translates `SqlRole` → `CREATE ROLE [name] AUTHORIZATION [owner]` in phase 1. `EmitTableType` translates `SqlTableType` → `CREATE TYPE [schema].[name] AS TABLE (col_list [, PRIMARY KEY (cols)])` in phase 1 (reuses `TranslateSimpleColumn` for `SqlTableTypeSimpleColumn` since the inner shape is identical; PK constraint translates the anonymous `SqlTableTypePrimaryKeyConstraint`'s `ColumnSpecifications` into a table-level `PRIMARY KEY (col1, …)`; `IsClustered` annotation dropped since table-variable storage is linear-scan regardless). Cascade benefit: dispatching table types unblocked 3 of 6 previously-failing WWI procedures whose bodies declared TVP parameters of those types. Together these unblock the WideWorldImporters-Standard bacpac: 48/48 tables, 26/26 sequences, 9/9 roles, 4/4 table types, 41/41 DEFAULTs (sequence-backed defaults previously cascade-failed), 98/98 FKs, 4.7M rows loaded. Permanent regression tests landed in `BacpacLoaderTests` (`Load_WWI_Element_Counts_Match_Probe`, `Load_WWI_Sequences_Land_In_sys_sequences`, `Load_WWI_Roles_Land_In_sys_database_principals`, `Load_WWI_Sequence_Backed_Defaults_Apply`, `Load_WWI_Table_Types_Land_In_sys_table_types`, `Load_WWI_Known_Gaps_Recorded_In_Skipped`, `Load_WWI_Most_Tables_Loaded`, `Load_AW_No_Per_Element_Failures`). WWI bacpac is `.vs/WideWorldImporters-Standard.bacpac` (gitignored alongside AW2025).
4. ~~**Deferred computed columns**~~ — shipped 2026-05-15. New phase 8 between programmable objects (phase 7) and extended properties (phase 9): `EmitDeferredComputedColumns(SqlTable element)` re-walks each table's `SqlComputedColumn` children and emits `ALTER TABLE [schema].[table] ADD [col] AS expr` per column. The `ExpressionScript` body arrives parenthesized from DACFx (e.g. `(concat([X],N' ',[Y]))`) and is emitted verbatim. The PERSISTED qualifier is intentionally dropped — the simulator's PERSISTED-column read path expects bytes from storage rather than recomputing, but BCP files don't carry data for computed columns (real `bcp.exe` / DACFx exclude them regardless of PERSISTED), so a persisted-computed column would have no stored bytes for existing rows. Recomputing on every read gives identical query semantics with the only cost being a per-read evaluation (no caching). The companion change in `BacpacReader.LoadRowsFromBcp` filters `table.Columns` to drop computed columns before calling `BcpRowReader.TryReadRow` + `RowEncoder.EncodeRow` — keeps the wire layout matching the BCP file's actual storage shape. Per-element failures inside the computed-column emit use `"Deferred: …"` rather than `"Load failed: …"` as the Skipped reason prefix, so the AW resilient-loader guard test (`Load_AW_No_Per_Element_Failures`) still meaningfully detects regressions on previously-working elements without firing on the known unmodeled-function gaps (AW's `dbo.ufnLeadingZeros` UDF-resolution gap in ALTER TABLE ADD AS, WWI's `json_query` unmodeled builtin). Results: 6/8 WWI computed columns succeed (the 2 referencing `json_query` remain deferred); 6 SqlExtendedProperty entries that hung off computed-column hosts also unblock (89 → 83). One known limitation: filtered indexes whose predicate references a computed column still fail in phase 5 (3 in WWI: `IX_*_IsFinalized`, `IX_*_ConfirmedDeliveryTime`) because computed columns don't exist until phase 8. Reordering indexes to a later phase tripped an unrelated UDF-resolution gap in the simulator's ALTER-TABLE-ADD-AS column-expression parser when AW's `dbo.ufnLeadingZeros`-referencing computed column tried to bind, so the reorder was rolled back and the filtered-index gap is deferred. Permanent regression tests added: `Load_WWI_Computed_Columns_Land_With_is_computed_Set` (counts via `sys.columns.is_computed=1`), `Load_WWI_Persisted_Computed_Column_Evaluates_On_Read` (verifies `Application.People.SearchName` recomputes correctly from `concat(PreferredName, ' ', FullName)` for BCP-loaded rows).
5. ~~**`sysname` keyword + BCP wire-format**~~ — shipped 2026-05-15. The simulator already carried `SystemNameSqlType` (a sys-schema alias over nvarchar(128) NOT NULL), but `SqlType.ResolveSimpleKeyword`'s length-7 branch didn't include it, so any column / parameter declared `sysname` raised Msg 243 ("Type sysname is not a defined system type."). Added `"SYSNAME" => SystemName` to the dispatch and a matching `SystemNameSqlType => ReadVarchar2(stream, type, ansi: false)` case + `SystemNameSqlType => SqlValue.FromSystemName(text)` materialization in `BcpRowReader` (same 2-byte-prefix UTF-16 wire layout as nvarchar). The bacpac loader's `NormalizeBuiltinName` previously expanded `[sys].[sysname]` to `nvarchar(128)` as a workaround — that's removed now, so `sys.columns` reports sysname for sysname-declared columns instead of nvarchar(128). All 3 previously-failing WWI procedures (`Application.AddRoleMemberIfNonexistent`, `Application.CreateRoleIfNonexistent`, `Sequences.ReseedSequenceBeyondTableValues`) — all carrying sysname parameters via `[sys].[sysname]` TypeSpecifier references — now CREATE successfully. AW unaffected (none of AW's columns were declared as sysname). Permanent regression test: `Load_WWI_Sysname_Procs_Land_In_sys_procedures`.
6. ~~**`JSON_QUERY` scalar function**~~ — shipped 2026-05-15. Added `JsonQuery` parallel to `JsonValue` in `SqlServerSimulator/Parser/Expressions/`; dispatched from `Expression.cs`'s length-10 switch (`"JSON_QUERY" => new JsonQuery(context)`). Reuses the existing `JsonPath.Parse` / `JsonDocument.Parse` / path-walk machinery; only the leaf materialization differs from JSON_VALUE: Object/Array match → raw JSON text via `JsonElement.GetRawText` (preserves input whitespace), Scalar match → SQL NULL in lax mode (Msg 13624 in strict, unreached by DACFx). Returns `nvarchar(MAX)` semantically; the runtime value lives on `SqlType.NVarchar` (the simulator's runtime `SqlValue` doesn't carry the max-length distinction — same convention as JSON_VALUE). Two WWI computed columns (`Application.People.OtherLanguages = json_query([CustomFields], N'$.OtherLanguages')`, `Warehouse.StockItems.Tags = json_query([CustomFields], N'$.Tags')`) now load — WWI computed columns are 8/8 (was 6/8); SqlExtendedProperty drops 83 → 81 because the 2 host-column extended properties also unblock. Permanent regression tests: 7 new entries in `SqlServerSimulator.Tests/JsonScalarTests.cs` covering object/array match, scalar-returns-NULL, missing-path, NULL inputs, and an OPENJSON round-trip. `docs/claude/json.md` updated: title gains JSON_QUERY, new paragraph documents the semantics, "Not modeled" line drops JSON_QUERY.
7. ~~**Long tail: ISJSON + DECOMPRESS + scalar-UDF SCHEMABINDING/EXECUTE AS + Filegroup skip + index-after-computed reorder**~~ — shipped 2026-05-15. Five small fixes whose combined effect closed most of the remaining WWI gap inventory and accidentally also unblocked the rest of AW's deferred elements. (i) New `IsJson` expression (`SqlServerSimulator/Parser/Expressions/IsJson.cs`) — wraps `System.Text.Json.JsonDocument.Parse` in try/catch and returns int 1/0; unblocks 1 of the 2 WWI CHECK constraints (`CK_Sales_Invoices_ReturnedDeliveryData_Must_Be_Valid_JSON` and any others using `isjson(col)<>(0)`). (ii) New `Decompress` expression — `System.IO.Compression.GZipStream` inflate of a varbinary argument; returns varbinary. Unblocks WWI's `Website.VehicleTemperatures` view (which casts DECOMPRESS output to nvarchar). Invalid gzip returns NULL pending a proper Msg 9803 factory. (iii) Scalar-UDF `ParseScalarTail` extended to accept `WITH SCHEMABINDING / ENCRYPTION / EXECUTE AS CALLER|SELF|OWNER|'name'` (comma-separated, multi-option) in addition to the prior single-option RETURNS NULL ON NULL INPUT. Unblocks WWI's `Website.CalculateCustomerPrice` (uses `WITH EXECUTE AS OWNER`) AND — critically — AW's `dbo.ufnLeadingZeros` (uses `WITH SCHEMABINDING`), which had been silently failing CREATE and cascade-blocking every AW computed column that referenced it. (iv) `SqlFilegroup` becomes a phase-1 no-op skip. (v) `SqlIndex` dispatch moves from phase 5 to phase 8, alongside computed columns. Element order within phase 8 puts SqlTable's deferred-computed-column ALTERs ahead of SqlIndex emissions, so filtered indexes that predicate on computed columns now resolve. Combined results: WWI gap census drops from 8 categories to 3 (SqlExtendedProperty 81, SqlPermissionStatement 2, SqlCheckConstraint 1 — the second JSON-CHECK constraint hits a separate boolean-parser gap with `(value_expr)=(1)` paren-wrapped value subexpressions, deferred to a follow-up bundle); AW SqlComputedColumn cascade-clears (89 → 93 user indexes land, scalar UDF count 10 → 11). Tests: `Load_AW_Indexes_Land` expected count 89 → 93; `Load_AW_Programmable_Counts` funcs 10 → 11; `Load_AW_Unhandled_Elements_Recorded_In_Skipped` drops the SqlComputedColumn `IsNotEmpty` assertion (cleared); `Load_WWI_Known_Gaps_Recorded_In_Skipped` rewritten to assert absence of SqlIndex / SqlView / SqlScalarFunction / SqlFilegroup categories.
8. ~~**Boolean-parser paren-wrapped value LHS**~~ — shipped 2026-05-16. The boolean parser's `ParseAtom` previously treated every leading `(` as a boolean group, so `WHERE (a + b) = 5` / `CHECK (((case_sum) = (1)))` / `HAVING (sum(v)) > 25` all surfaced as Msg 4145 syntax errors. Added `LookaheadValueLhs` — a token-only lookahead that scans to the matching `)`, peeks the next token, and routes to the value-LHS path (`Expression.Parse` + `ParseComparison`) when the post-`)` token is a comparison or arithmetic operator (= < > <> != !< !> + - * / % &amp; | ^ LIKE IS IN BETWEEN NOT). A top-level `,` inside the outer parens disqualifies the value-LHS path (preserves the row-constructor `(a, b) IN (...)` Msg 4145 wording). Uses `ParserContext.SaveCheckpoint` / `RestoreCheckpoint` — no exceptions for control flow, no side effects on aggregate / window collectors. Unblocks WWI's `CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required` (the parsed shape is `((case_sum) = (1))`). Permanent regression tests: 11 new direct-SQL tests (8 in `WhereTests`, 3 in `CheckConstraintTests`) covering WHERE / HAVING / CASE-WHEN / CHECK shapes plus the bacpac round-trip via `Load_WWI_ParenWrappedValueLhs_Check_Loaded_And_Enforces`.
9. ~~**SqlPermissionStatement dispatcher**~~ — shipped 2026-05-16. AW + WWI each carried 2 SqlPermissionStatement model elements that lacked a dispatcher entry. Added a phase-7 dispatcher slot plus `EmitPermissionStatement` (`ModelXmlReader.cs`): parses the element's `Name`-encoded `[Action.PermissionCamelCase.Scope].[grantee].[grantor]` triple, splits the camel-case permission identifier via `CamelToSpaceSeparatedUpper`, pulls the grantee from the `Grantee` relationship, and dispatches a `GRANT|REVOKE|DENY <permission> TO <grantee>` statement through the existing parser. The simulator's GRANT path already accepted the multi-word encryption-key permission keywords — only the loader-side translation was missing. Permanent regression test: `Load_WWI_Encryption_Key_Grants_Land_In_sys_database_permissions` verifies the 2 grants round-trip through `sys.database_permissions`. WWI gap census drops from 3 categories to 2 (SqlExtendedProperty + collation remained).
11. ~~**Collation metadata round-trip (per-DB + per-column whitelist)**~~ — shipped 2026-05-16. The simulator's previous behavior — hard-error on any non-default collation at ALTER DATABASE / CREATE TABLE — meant WWI's `Latin1_General_100_CI_AS` declaration landed on `Skipped` (last WWI category). The bundle adds: (i) `Database.CollationName : string` + `HeapColumn.Collation : string?` metadata fields (mutable on the DB, immutable per-column with null = inherit). (ii) `Collation.Recognized : FrozenDictionary<string, string>` whitelist (case-insensitive lookup, seeded with the two collation names we've encountered in AW + WWI plus a probe-confirmed description for each). (iii) Parser: `ALTER DATABASE name COLLATE name` validates against the whitelist, stores on `Database.CollationName`, raises `NotSupportedException` on unrecognized names; CREATE TABLE column-level `COLLATE name` works the same way (new switch arm in `ParseOneColumnIntoLists`). (iv) Catalog surface: `sys.databases` (new 9-column view), `sys.fn_helpcollations()` (new TVF emitting the whitelist), `DATABASEPROPERTYEX(db_name, property_name)` (new built-in scalar, dispatches the 9 properties common tooling queries), `sys.columns.collation_name` + `INFORMATION_SCHEMA.COLUMNS.COLLATION_NAME` updated to consult per-column override → DB default → NULL (non-string columns). (v) Loader: `EmitDatabaseOptions` emits ALTER DATABASE COLLATE when recognized + records on `Warnings` otherwise (graceful degradation; the load-best-effort contract); `TranslateSimpleColumn` emits per-column `COLLATE` clauses for whitelisted names. **Important non-goal**: comparison / sort / LIKE / `=` semantics are *not* extended — every string op still routes through `Collation.Default` (`SQL_Latin1_General_CP1_CI_AS` rules). The metadata is honest about what the BACPAC declares without claiming the matching collation algorithms are implemented. WWI's data is mostly Latin-script names where the two collations sort identically for >99% of rows; visible divergence is in ORDER BY of accented `varchar` strings + a few `LIKE` rules around Unicode expansion (e.g. German `ß ↔ ss`). Permanent regression tests: 15 in `CollationMetadataTests` + `Load_WWI_Database_Collation_Round_Trips`. Adds the simulator's first FrozenDictionary use.
10. ~~**SqlExtendedProperty host-routing for SqlIndexBase + SqlConstraint**~~ — shipped 2026-05-16. WWI's largest remaining gap (81 of 414 extended properties) — DACFx emits extended properties hosted on `SqlIndexBase` (76 in WWI: descriptions on indexes including FK-backing indexes named after the FK) and `SqlConstraint` (5 in WWI: descriptions on CHECK constraints). The previous `EmitExtendedProperty` switch only handled `SqlColumn` / `SqlTableBase` / `SqlSchema` / `SqlDatabaseOptions` hosts; the missing cases drove the entire residual count. Two-part fix: (i) `Simulation.ExtendedProperties.cs` extended `ResolveExtendedPropertyTarget` — CONSTRAINT-level2 walks `HeapTable.KeyConstraints` / `CheckConstraints` / `OutgoingForeignKeys` / `Columns[].DefaultConstraint`, reuses class=1 (OBJECT_OR_COLUMN) with the constraint's own object_id as major_id (matches real SQL Server); INDEX-level2 uses class=7 with `(major_id=table.object_id, minor_id=index_id)`, with a new `ComputeIndexId` helper that mirrors `sys.indexes`'s enumeration (PK=1, others sequential in ObjectId order). `BuiltInResources.EnumerateSysExtendedProperties` adds the class=7 → `"INDEX"` mapping for `class_desc`. (ii) `ModelXmlReader.EmitExtendedProperty` adds `SqlIndexBase` (3-part host → `@level2type=INDEX`) and `SqlConstraint` (2-part host + new `LookupConstraintParentTable` that queries `sys.objects.parent_object_id` to find the owning table → `@level2type=CONSTRAINT`) cases. Permanent regression tests: 7 new direct-SQL tests in `ExtendedPropertyTests` (CHECK / PK / FK constraint round-trip, INDEX round-trip + class/class_desc/minor_id assertion, PK index_id=1 case, missing-name error path × 2) + `Load_WWI_Extended_Properties_Cover_Index_And_Constraint_Hosts`. WWI gap census drops to 1 category (collation only).
12. **Geography WKB → WKT decoder** — Person.Address rows load with `SpatialLocation = NULL` (the 8-byte-prefix bytes are drained but not decoded). A future bundle could implement the simple-point case (4-byte SRID + 1-byte version + 1-byte properties + 16-byte X/Y doubles) which is all AW uses; full WKB shape support (LineString / Polygon / MultiPolygon / etc.) is a larger project.
13. **Hierarchyid byte-identical CAST encoding** — the BCP wire decoder ships (covers AW's [0..79] positive-ordinal envelope; throws `NotSupportedException` on negative ordinals + ordinals ≥ 80 + dotted sub-ordinals so a follow-up bundle can extend cleanly), but the simulator's own `HierarchyIdSqlType.Encode` / `Decode` still uses its segment-array-LE internal form. Replacing both with the documented OrdPath encoding makes cross-engine CAST byte equality hold round-trip; the wire decoder gives the read half for free, the symmetric encoder is the remaining work.
14. **WWI-discovered gap inventory** (remaining categories, ranked by Skipped count):
   - ~~`SqlExtendedProperty` (81)~~: shipped 2026-05-16 — `EmitExtendedProperty` extended with `SqlIndexBase` + `SqlConstraint` host-routing; the simulator's `ResolveExtendedPropertyTarget` gained CONSTRAINT and INDEX level2 acceptance (step 10 above).
   - ~~`SqlPermissionStatement` (2)~~: shipped 2026-05-16 — `EmitPermissionStatement` translates the `SqlPermissionStatement` element's `Name`-encoded `Action.PermissionCamelCase.Scope` triple plus the `Grantee` relationship into a `GRANT|REVOKE|DENY <perm> TO <grantee>` statement. The simulator's GRANT parser already accepted the multi-word encryption-key permission keywords; the loader just hadn't been routing the model elements through it. Same fix covers both AW and WWI.
   - ~~`SqlCheckConstraint` (1)~~: shipped 2026-05-16 — `BooleanExpression.ParseAtom` gained `LookaheadValueLhs` (step 8 above). WWI's `CK_Sales_SpecialDeals_Exactly_One_NOT_NULL_Pricing_Option_Is_Required` now loads.
   - ~~`SqlComputedColumn` / `SqlProcedure` / `SqlIndex` / `SqlView` / `SqlScalarFunction` / `SqlFilegroup`~~: all shipped (steps 4–7 above).
   - ~~`SqlDatabaseOptions` (1)~~: shipped 2026-05-16 — collation metadata round-trip (step 11 above). WWI's `Latin1_General_100_CI_AS` is on the recognized whitelist and lands on `Database.CollationName`. Comparison semantics are unchanged (still routed through default); fully modeling the comparison/sort/LIKE rules per collation is a deferred follow-on.
   - **Temporal-table wiring**: half of WWI's schema is system-versioned (every `*_Archive` table is a history sibling via the `TemporalSystemVersioningHistoryTable` relationship on the base SqlTable). `EmitTable` currently emits base tables as plain tables and ignores the relationship — the history sibling loads as a separate plain table, breaking SYSTEM_VERSIONING. Needs either CREATE TABLE WITH SYSTEM_VERSIONING handling (history table must exist first → topological ordering) or post-pass ALTER TABLE SET SYSTEM_VERSIONING ON (currently not in the alter-table grammar; see [`temporal-tables.md`](temporal-tables.md) — the OFF direction ships, the ON direction doesn't).
15. **Real XPath/XQuery + OGC method evaluation + full-text search** as separate post-baseline initiatives, each promoted from "parses cleanly, throws at execute" to first-class as bundles complete.

Loader code layout (target, when baseline lands):
- `SqlServerSimulator/Storage/Bacpac/BacpacReader.cs` — OPC zip walker, dispatches to model + data readers
- `SqlServerSimulator/Storage/Bacpac/ModelXmlReader.cs` — `model.xml` → DDL emitter
- `SqlServerSimulator/Storage/Bacpac/BcpRowReader.cs` — `*.BCP` → row decoder
- `SqlServerSimulator/Storage/Bacpac/BacpacLoadResult.cs` — diagnostics carrier (Skipped + Degraded lists)
- Public surface: `internal static Simulation Simulation.FromBacpac(string path, out BacpacLoadResult diagnostics)` + Stream overload, kept internal until baseline AW load works end-to-end

## Status

Loader baseline shipped (2026-05-15). `Simulation.FromBacpac` loads AdventureWorks2025 end-to-end with **100% row coverage** (760,167 / 760,167 rows, zero BCP-file failures). All four DDL/data type families ship: schema + constraints + indexes + programmable objects + extended properties + every BCP wire-format type (fixed-raw, 1-byte-prefix, 2-byte-prefix, 8-byte-prefix). Geography rows load with `SpatialLocation = NULL` (WKB → WKT decoding deferred); hierarchyid rows decode to canonical-string-equivalent segment arrays via the new `HierarchyIdWireDecoder`.

WideWorldImporters-Standard (Microsoft's modern sample DB) also loads end-to-end after the 2026-05-15+16 bundles: 48/48 tables, 26/26 sequences, 9/9 roles, 4/4 table types, 8/8 computed columns, 7/7 CHECK constraints (including the paren-wrapped `((value_expr) = (1))` shape that the 2026-05-16 boolean-parser fix unlocked), 42/42 procedures, 94/94 indexes, 3/3 views, 1/1 scalar function, 2/2 encryption-key GRANTs (the 2026-05-16 SqlPermissionStatement dispatcher), 414/414 extended properties (the 2026-05-16 SqlIndexBase + SqlConstraint host-routing bundle), **collation metadata round-trip** (`Latin1_General_100_CI_AS` lands on `Database.CollationName`, surfaces through `sys.databases.collation_name` + `DATABASEPROPERTYEX`), 4.7M rows. **Zero Skipped categories remain.** Comparison / sort / LIKE semantics still route through the simulator's default collation regardless of declared metadata — see step 11 below for the deferred-fidelity caveat.
