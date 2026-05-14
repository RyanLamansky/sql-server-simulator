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

### [ ] DDL trigger (`CREATE TRIGGER … ON DATABASE`) (small if scoped to parse-and-discard)
1 in AW: `[ddlDatabaseTriggerLog]` — fires on `DDL_DATABASE_LEVEL_EVENTS`, writes to `dbo.DatabaseLog`. Surface: `CREATE TRIGGER … ON DATABASE … FOR <event_type_group> AS …` parser + storage + dispatch. Could legitimately be parse-and-store-but-never-fire for the baseline — the trigger only fires on DDL events the simulator may not even dispatch to a trigger loop in the first place. Worth a probe to confirm AW apps actually depend on its side effects.

### [ ] Permission statements (`GRANT` / `REVOKE` / `DENY`) (medium — needs principal model)
2 in AW. Real surface needs:
- `CREATE USER` / `CREATE ROLE` / `ALTER ROLE … ADD MEMBER` (or accept-as-no-op for the principals AW references — `public` and the schema authorizers)
- `GRANT <perm> ON <object> TO <principal>` / `REVOKE` / `DENY`
- `sys.database_principals`, `sys.database_permissions`, `sys.database_role_members`

For the loader's "baseline AW load" goal, parse-and-discard is probably enough — the simulator has no permission enforcement, so GRANT/REVOKE are no-ops semantically. The catalog views surface as empty/synthesized. Real feature work deferred.

### [ ] Full-text catalog + index (large — likely skip-with-diagnostic)
1 catalog (`[AW2025FullTextCatalog]`) + 3 indexes in AW. Full surface: `CREATE FULLTEXT CATALOG` / `CREATE FULLTEXT INDEX … ON tbl(col LANGUAGE 1033) KEY INDEX <pk> ON <catalog>`, `CONTAINS()` / `FREETEXT()` predicates, `CONTAINSTABLE` / `FREETEXTTABLE` rowset functions, `sys.fulltext_catalogs` / `sys.fulltext_indexes`.

The query-time predicates (`CONTAINS`, `FREETEXT`) are the hard part — they need a tokenizer/stemmer/inverted-index/relevance-rank pipeline. Recommend **skip-with-diagnostic** for the loader; AW data still loads, full-text-using queries fail at parse with `NotSupportedException("Full-text search is not modeled")`. Real feature deferred indefinitely unless an application needs it.

### [ ] `xml` data type + XML schema collections + XML methods + XML indexes (very large)
9 column uses in AW (`Production.Document.DocumentSummary`, `Person.Person.AdditionalContactInfo`, `HumanResources.JobCandidate.Resume`, etc.), 6 `SqlXmlSchemaCollection` (with embedded XSD schemas in `SchemaExpression`), 8 `SqlXmlIndex` (`PrimaryXmlIndexUsage` 3, secondary index types). Surface:
- Storage type + `xml(SchemaCollection)` parametrization
- `CREATE XML SCHEMA COLLECTION` (with XSD payload)
- XML methods: `.value('xpath', 'sqltype')`, `.nodes('xpath')`, `.query('xpath')`, `.exist('xpath')`, `.modify('xml dml')`
- Implicit/explicit cast between `xml` and `[n]varchar`
- XML primary + secondary indexes (PATH / VALUE / PROPERTY)
- `FOR XML` query-output clause (separate but related)
- `sys.xml_schema_collections`, `sys.xml_indexes`

Genuinely large — XPath + XML DML are independent sub-languages. Recommend **skip-with-diagnostic** in the loader for the baseline (load xml columns as `nvarchar(MAX)` containing the raw XML — preserves application read-back via `.ToString()`, breaks XPath methods). Real feature could be one or several bundles down the road.

### [ ] `geography` / `geometry` data types (large — likely skip-with-diagnostic)
1 column in AW (`Person.Address.SpatialLocation`). Spatial types have their own large surface (WKT/WKB parsing, OGC methods, spatial indexes). Recommend **skip-with-diagnostic**; load as `varbinary(MAX)` or `nvarchar(MAX)` in degraded mode, application queries that call `.STDistance` etc. fail at parse.

## BCP wire format

The `Data/<schema>.<table>/TableData-NNN-NNNNN.BCP` files are the per-table data payload. Probed against `Production.ProductCategory` (4 rows, 192 bytes, schema `int IDENTITY NOT NULL, nvarchar(50) NOT NULL, uniqueidentifier NOT NULL, datetime NOT NULL` — verified row 1 = `(1, 'Bikes', <guid>, 2019-04-30 00:00:00)`):

| Type family | Wire layout | Notes |
|---|---|---|
| Fixed-width numeric (`int`, `bigint`, `smallint`, `tinyint`, `bit`) | raw bytes, little-endian, no prefix | `int` = 4 bytes LE |
| Fixed-width temporal (`datetime`, `smalldatetime`, `date`) | raw bytes, no prefix | `datetime` = 4-byte int32 days + 4-byte uint32 ticks-of-day (1/300 sec) |
| Variable-length text/binary (`nvarchar`, `varchar`, `varbinary`) | 2-byte LE byte-length prefix + bytes | nvarchar = UTF-16 LE; `0xFFFF` likely = NULL (needs probe confirm) |
| Length-prefixed fixed (`uniqueidentifier`, `decimal`/`numeric`, `money`, `smallmoney`, `datetime2`, `datetimeoffset`, `time`) | 1-byte length-prefix (= type width) + bytes | guid = `0x10` + 16 bytes; `0x00` likely = NULL |
| MAX types (`varchar(MAX)`, `nvarchar(MAX)`, `varbinary(MAX)`, `text`, `ntext`, `image`) | length prefix likely 8-byte for full size + bytes (or chunked) | needs probe — not in ProductCategory |
| `hierarchyid` | variable-length binary, length-prefixed | covered by hierarchyid feature work |
| `xml` | variable-length text/binary, length-prefixed | covered by xml feature work; if xml is loaded as nvarchar(MAX) in degraded mode, falls through to the MAX-types row |
| `sql_variant` | special envelope (type byte + value) | not yet investigated, AW may or may not use |

Encoding-edge probes needed (carve out tiny custom BACPACs locally via `SqlPackage`):
- NULL sentinel for each prefix class (1-byte / 2-byte / fixed-no-prefix)
- `decimal(p, s)` precision/scale layout (probably sign byte + LE mantissa)
- `datetime2(N)` and `datetimeoffset(N)` precision dependence
- MAX-type encoding when row >> 8KB
- `varbinary(N)` for `rowversion` columns (auto-generated server-side — does BACPAC export them or skip?)
- `IDENTITY` reseed: confirm `LastValue` annotation matches the actual max-allocated rather than max-inserted

## Order of operations toward AW baseline

Rough sequence — work each bundle to completion, update this checklist, then revisit BACPAC scoping once the prerequisites land:

1. ~~**Database options expansion**~~ + ~~**UDDTs / alias types**~~ + ~~**Extended properties**~~ + ~~**`hierarchyid` (AW-minimum-viable)**~~ (all shipped 2026-05-14)
2. **DDL trigger + permission statements** as parse-and-store-but-no-enforce (smallest scope; both end up as catalog-view-visible no-ops)
3. **Loader baseline implementation**, with `xml` / `geography` / full-text **loaded in degraded mode** (xml/geography → nvarchar(MAX), full-text indexes → parse-and-discard). Diagnostics report which features were degraded. **Re-probe the hierarchyid BCP wire format** at the start of this bundle and either confirm CAST-byte-identical or implement a separate BCP decoder; replace the current simulator-native CAST encoder/decoder with the documented variable-bit ordinal encoding at the same time.
4. **Real xml + spatial + full-text** as separate post-baseline initiatives, each promoted from degraded-mode-via-diagnostic to first-class as bundles complete.

Loader code layout (target, when baseline lands):
- `SqlServerSimulator/Storage/Bacpac/BacpacReader.cs` — OPC zip walker, dispatches to model + data readers
- `SqlServerSimulator/Storage/Bacpac/ModelXmlReader.cs` — `model.xml` → DDL emitter
- `SqlServerSimulator/Storage/Bacpac/BcpRowReader.cs` — `*.BCP` → row decoder
- `SqlServerSimulator/Storage/Bacpac/BacpacLoadResult.cs` — diagnostics carrier (Skipped + Degraded lists)
- Public surface: `internal static Simulation Simulation.FromBacpac(string path, out BacpacLoadResult diagnostics)` + Stream overload, kept internal until baseline AW load works end-to-end

## Status

Pre-implementation. Scoping done 2026-05-14. Implementation paused until prerequisite features land; resume by reopening the session named `bacpac`.
