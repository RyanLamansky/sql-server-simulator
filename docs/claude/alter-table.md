# ALTER TABLE

`ALTER TABLE` ships these modeled shapes: `SET (SYSTEM_VERSIONING = OFF | ON (HISTORY_TABLE = name [, DATA_CONSISTENCY_CHECK = ON|OFF]))` (see [`temporal-tables.md`](temporal-tables.md)), `SET (LOCK_ESCALATION = TABLE | DISABLE | AUTO)`, `{ ENABLE | DISABLE } TRIGGER { ALL | name [, …] }` (see [`triggers.md`](triggers.md)), `[WITH CHECK | WITH NOCHECK] ADD [CONSTRAINT name] (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT) [, …]` (multi-element constraint list — see [Multi-element ADD](#multi-element-add)), `DROP CONSTRAINT [IF EXISTS] name [, …]`, `[WITH CHECK | WITH NOCHECK] (CHECK | NOCHECK) CONSTRAINT (ALL | name [, …])` (trust toggling), `ADD [COLUMN] col TYPE [, …]` (multi-column add — see [Column ops](#column-ops)), `DROP COLUMN [IF EXISTS] col [, …]` (multi-column drop with dependency rejection), `ALTER COLUMN col TYPE[(prec[,scale])] [COLLATE coll] [NULL|NOT NULL]` (single-column type / nullability change — see [ALTER COLUMN](#alter-column)), `ALTER COLUMN col { ADD | DROP } { ROWGUIDCOL | SPARSE | HIDDEN }` (see [Column attributes](#column-attributes)), `ALTER COLUMN col { ADD MASKED WITH (…) | DROP MASKED }` (see [`data-masking.md`](data-masking.md#ddl)), `ALTER COLUMN col { ADD | DROP } PERSISTED` (see [PERSISTED](#persisted)), `DROP PERIOD FOR SYSTEM_TIME` (see [DROP PERIOD FOR SYSTEM_TIME](#drop-period-for-system_time)), `REBUILD` (see [REBUILD](#rebuild)), and `SWITCH [PARTITION n] TO target [PARTITION m]` (see [`partitioning.md`](partitioning.md#alter-table--switch)).
Probe-confirmed against SQL Server 2025.

## Grammar

```sql
ALTER TABLE [schema.]table
    [WITH CHECK | WITH NOCHECK]
    ADD [CONSTRAINT name] <body>

ALTER TABLE [schema.]table
    DROP CONSTRAINT [IF EXISTS] name [, name ...]

ALTER TABLE [schema.]table
    [WITH CHECK | WITH NOCHECK]
    (CHECK | NOCHECK) CONSTRAINT (ALL | name [, name ...])
```

`<body>` is one of:

```sql
PRIMARY KEY (col [ASC|DESC] [, ...])
UNIQUE      (col [ASC|DESC] [, ...])
FOREIGN KEY (col [, ...]) REFERENCES parent [(col [, ...])]
            [ON DELETE NO ACTION | CASCADE | SET NULL | SET DEFAULT]
            [ON UPDATE NO ACTION | CASCADE | SET NULL | SET DEFAULT]
CHECK (predicate)
DEFAULT expression FOR column        -- value parentheses optional
```

The DEFAULT value's parentheses are optional (probe-confirmed): both `ADD CONSTRAINT df DEFAULT (0) FOR b` and the bare `ADD CONSTRAINT df DEFAULT 0 FOR b` are accepted, bind the default to the column, and store an equivalent parenthesized definition; inserts that omit the column pick it up.

**A DEFAULT expression has no column scope at all.**
A name inside one is **Msg 128** (`The name "v" is not permitted in this context. …`) and a subquery is **Msg 1046**, whichever the left-to-right reading meets first — the same `ParserContext.ScalarOnlyOperand` arming `PRINT`'s operand takes (see [`control-flow.md`](control-flow.md#the-operand-admits-only-scalar-expressions)).
Real settles both while parsing, so they fire whatever the table holds: an empty table, a name that *is* a column of the table, and a name that is nothing at all all report the same Msg 128, at all three declaration sites (`CREATE TABLE`'s inline form, `ALTER TABLE … ADD <column> DEFAULT`, and the named-constraint `DEFAULT (v) FOR w`).

Anonymous ADD (no `CONSTRAINT name`) auto-generates a name with the same FNV-1a-based scheme as CREATE TABLE inline: `PK__<t8>__<hex>` / `UQ__<t8>__<hex>`, and `FK__` / `CK__` / `DF__` cut as [`foreign-keys.md`](foreign-keys.md#auto-generated-fk-name) describes.
`is_system_named` reflects the auto-name path on FK / CHECK / DEFAULT (KeyConstraint infers from the prefix — `PK__` / `UQ__` — since the existing storage doesn't carry an explicit flag).

## WITH CHECK / WITH NOCHECK

`WITH NOCHECK` applies only to FK and CHECK adds.
It bypasses the existing-row validation pass and sets `IsNotTrusted = true` on the new constraint.
`WITH CHECK` (the default) runs the validation pass.
PK / UQ / DEFAULT ignore the modifier — the grammar accepts it but validation is unconditional (PK / UQ always scan for duplicates; DEFAULT has no data to validate against).

`sys.foreign_keys.is_not_trusted` and `sys.check_constraints.is_not_trusted` reflect the flag; re-trusting is [below](#trust-toggling--bulk-import-recipe).

## Existing-data validation

Default (`WITH CHECK`) scans the live heap before mutating:

| Family | Check | Error |
|--------|-------|-------|
| `PRIMARY KEY` | column declared NOT NULL | Msg 8111 |
| `PRIMARY KEY` | table doesn't already have one | Msg 1779 |
| `PRIMARY KEY` / `UNIQUE` | column exists | Msg 1911 |
| `PRIMARY KEY` / `UNIQUE` | no duplicate key tuples in existing rows | Msg 1505 (`CREATE UNIQUE INDEX statement terminated …`) |
| `FOREIGN KEY` | child column exists | Msg 1769 |
| `FOREIGN KEY` | referenced columns form PK / UQ, or an enabled unfiltered unique index, on parent | Msg 1776 |
| `FOREIGN KEY` | a computed child column is PERSISTED, and its referential actions never write it | Msg 1764 / 1765 / 1715 — see [`foreign-keys.md`](foreign-keys.md#computed-columns-in-a-foreign-key) |
| `FOREIGN KEY` | cascade graph doesn't form a cycle / multiple paths | Msg 1785 |
| `FOREIGN KEY` | every non-NULL existing FK tuple matches a parent row | Msg 547 with `"ALTER TABLE statement"` prefix |
| `CHECK` | the predicate reads no non-persisted computed column | Msg 1764 — see [`constraints.md`](constraints.md#computed-columns-in-a-check-constraint); raised even under `WITH NOCHECK`, which only skips the data scan |
| `CHECK` | every existing row passes (UNKNOWN passes) | Msg 547 with `"ALTER TABLE statement"` prefix |
| `DEFAULT` | column exists | Msg 1752 |
| `DEFAULT` | column doesn't already have a DEFAULT | Msg 1781 |
| any | constraint name free in the table's schema — among its constraints and every other object, which share one namespace, and another schema may hold it | Msg 2714 state 5 |

A failure to create a constraint is followed by Msg 1750 (`"Could not create constraint or index. See previous errors."`), raised as one exception with it (`SimulatedSqlException.FollowedByConstraintNotCreated`), whose state varies with the failure; a `CATCH` reads the 1750, the last of the pair (probed 2026-09-24 against SQL Server 2025).

The Msg 547 verb difference is the only wording variance between INSERT-time CHECK / FK violations and ALTER-time existing-data violations.
The constraint name, table reference, and column suffix follow the same format.

## Trust toggling — bulk-import recipe

`NOCHECK CONSTRAINT name` disables enforcement on a specific FK / CHECK and sets both `IsDisabled = true` and `IsNotTrusted = true`.
While disabled:

- INSERT / UPDATE / MERGE skip the FK / CHECK validation.
- DELETE / UPDATE on the parent skips both the NO-ACTION reject **and** any CASCADE / SET NULL / SET DEFAULT action (probe-confirmed: disabled CASCADE FK leaves children orphaned when the parent is deleted).

`CHECK CONSTRAINT name` (bare, no `WITH CHECK` prefix) re-enables enforcement on subsequent rows but does **not** re-validate existing data — `IsDisabled = false` and `IsNotTrusted` stays `true`.
Common gotcha.

`WITH CHECK CHECK CONSTRAINT name` re-enables enforcement **and** re-validates existing data — raises Msg 547 with the `"ALTER TABLE statement"` prefix on the first conflicting row; on success, `IsDisabled = false` and `IsNotTrusted = false`.

`ALL` targets every FK + CHECK on the table at once.
The same toggle action applies uniformly to every constraint on the target.

| Shape | IsDisabled | IsNotTrusted | Revalidate existing? |
|-------|------------|--------------|----------------------|
| `NOCHECK CONSTRAINT name` | → true | → true | No |
| `CHECK CONSTRAINT name` | → false | unchanged | No |
| `WITH CHECK CHECK CONSTRAINT name` | → false | → false (on success) | Yes |
| `WITH NOCHECK CHECK CONSTRAINT name` | → false | unchanged | No |

Probe-confirmed error paths:

| Condition | Behavior |
|-----------|----------|
| Constraint name not found | Msg 4917 (`Constraint 'name' does not exist.`), then Msg 4916 |
| A key or default constraint named | Msg 11415 (`Object 'name' cannot be disabled or enabled. …`), then Msg 4916 (probed 2026-10-01) |
| Multi-name with one missing | Atomic — all names resolved first; Msg 4917 prevents all mutations |
| Trailing comma | Msg 102 |
| Revalidation failure (WITH CHECK CHECK) | Msg 547 with `"ALTER TABLE statement"` prefix |

The bulk-import recipe:

```sql
ALTER TABLE Orders NOCHECK CONSTRAINT ALL;
-- Push large batch; FK + CHECK ignored, even if rows would have violated.
ALTER TABLE Orders CHECK CONSTRAINT ALL;
-- Enforcement back on for subsequent DML; existing data marked is_not_trusted.
-- Optional: ALTER TABLE Orders WITH CHECK CHECK CONSTRAINT ALL;
-- if you want the optimizer to trust the data (and accept Msg 547 if any row
-- violates).
```

## DROP CONSTRAINT

```sql
ALTER TABLE t DROP CONSTRAINT name [, name ...] [WITH (index_option [, …])]
ALTER TABLE t DROP CONSTRAINT IF EXISTS name
```

The `WITH` options (`ONLINE`, `MAXDOP`, …) are parsed and discarded; a nonclustered key constraint refuses them with Msg 3748 then Msg 3727, while a clustered one or any other constraint takes them (probed 2026-09-25).

Name lookup walks all four families on the target table in order:

1. `KeyConstraints` (PK / UQ)
2. `CheckConstraints`
3. `OutgoingForeignKeys`
4. Each column's `DefaultConstraint`

First hit wins (collation-insensitive).
Probe-confirmed shapes:

| Condition | Behavior |
|-----------|----------|
| Name resolves | Remove from the matching container; FK additionally detaches from `parent.IncomingForeignKeys` |
| Name not found, no `IF EXISTS` | Msg 3728 (`'name' is not a constraint.`), or Msg 3733 (`Constraint 'name' does not belong to table 't'.`) when another table of the schema holds it (probed 2026-10-01) |
| Name not found, `IF EXISTS` | Silent no-op |
| PK / UQ referenced by an incoming FK | Msg 3725 (`The constraint 'X' is being referenced by table 'Y', foreign key constraint 'Z'.`) |
| Trailing comma | Msg 102 (probe-confirmed) |

**Multi-drop is atomic** — all names resolve and validate first; any failure (Msg 3728 / 3725) leaves the table's constraint state unchanged.
Probe-confirmed.

The list may go on to drop columns — `DROP CONSTRAINT df, COLUMN b` — which runs the constraint drop then the column drop (probed 2026-10-01 against SQL Server 2025).

## Multi-element ADD

`ADD` takes any number of comma-separated elements — column definitions and constraint bodies (every form, `DEFAULT … FOR` included) in any mix and order: `ADD x int, CONSTRAINT ck CHECK (x > 0), y int NOT NULL DEFAULT 5, UNIQUE (x)`.

Real applies the list as a whole rather than in writing order (probed 2026-09-25 against SQL Server 2025): every column is added first, so a constraint may name a column the list defines after it, and a table-level `PRIMARY KEY` over an added column makes that column NOT NULL, as `CREATE TABLE` does — which on a non-empty table is Msg 4901 for it.
`TryParseAlterTableAddConstraint` reads the list once, collecting column definitions and parsing each constraint element in skip mode with its start remembered, then adds the columns and re-parses each constraint element at its start to apply it.

The statement is **atomic**: an element that raises leaves none of the others behind (probe-confirmed for a binder error, a CHECK the existing rows violate, and a foreign key to a missing table).
`AlterTableAddUndo` captures the three constraint lists' lengths plus each existing column's `Default` / `DefaultConstraint` — a `DEFAULT … FOR` element writes onto the column instance rather than into a list — and `AddedColumnsUndo` the column array, the column-id watermark and the `Heap` the row rewrite replaced.

Two elements naming one constraint alike are Msg 8168 before anything is added (probed 2026-10-01 against SQL Server 2025).

**Divergence**: a list declaring two primary keys reports only the second's problem here, where real leads with Msg 8110 (`Cannot add multiple PRIMARY KEY constraints`) before carrying on to it.

## Column attributes

`ALTER COLUMN col { ADD | DROP } { ROWGUIDCOL | SPARSE }` toggles a marker.
Both are metadata here: the `$ROWGUID` pseudo-column isn't modeled, and the row encoder already omits a NULL from the row, so `SPARSE`'s storage bargain has nothing to buy.
`sys.columns.is_rowguidcol` / `is_sparse` and `COLUMNPROPERTY(…, 'IsSparse')` are what observe the toggle.
A column definition takes `SPARSE` too (`CREATE TABLE`, `ADD`), nullable by default, with the same refusals below plus **Msg 1919** state 3 (and Msg 1750) for a key over it and **Msg 1791** (and Msg 1750) for a DEFAULT on it (probed 2026-09-25).

### Sparse column sets

`xml COLUMN_SET FOR ALL_SPARSE_COLUMNS` declares the table's sparse column set (all probed 2026-10-06 against SQL Server 2025):

- It stores nothing: a read renders an element per non-NULL sparse column in column order, each value as `FOR XML` writes it (`<b />` for an empty string), and NULL when every sparse column is.
  The column is a non-persisted computed column underneath (`ColumnSetValue`), yet `sys.columns` reports it `is_computed` 0 and `is_column_set` 1, and `sys.computed_columns` leaves it out.
- It stands in for the sparse columns in `SELECT *`, an INSERT's implicit column list and `sp_columns`; naming a sparse column still reads it.
- Writing it — INSERT, UPDATE, MERGE — sets each named sparse column, matching element names without regard to case, and every other sparse column to NULL.
  A write naming both it and a sparse column is **Msg 360** as the statement compiles; content other than attribute-free elements holding text is **Msg 9524**, an attribute **Msg 9530**, a column named twice **Msg 9525**, an unknown one **Msg 1911** state 201, and text that won't convert **Msg 9532**.
- A second one is **Msg 1732**, a type other than nullable `xml` **Msg 1733** (`NOT NULL` ahead of `COLUMN_SET` is the syntax error at it), and adding one to a table already holding sparse columns **Msg 1734**; a sparse column added after it joins it.

Probed refusals:

| Statement | Refusal |
| --- | --- |
| Either form on a column the table doesn't have | **Msg 4924** State 1 (the PERSISTED form's is State 2) |
| `ADD ROWGUIDCOL` where the table already carries one | **Msg 4925** |
| `ADD <column> uniqueidentifier ROWGUIDCOL` where the table already carries one | **Msg 8196** State 16 (State 1 when the `ADD` list itself repeats it, as `CREATE TABLE` reports) |
| `ADD ROWGUIDCOL` on a non-`uniqueidentifier` column | **Msg 2761** |
| `DROP ROWGUIDCOL` where no column carries it | **Msg 4926** — and it names the table, not the column the statement asked about, so real drops *the* ROWGUIDCOL rather than the named column's |
| `ADD SPARSE` on a NOT NULL / IDENTITY / ROWGUIDCOL column, or one typed `text` / `ntext` / `image` / `geometry` / `geography` | **Msg 1731**, whose message carries the whole rule rather than naming which half was violated |
| `ADD SPARSE` on a computed column | **Msg 4928** |
| `ADD SPARSE` on a column carrying a DEFAULT | **Msg 11410** |

Both Msg 4925 and Msg 4926 are followed by **Msg 1750**, as a failed constraint is.

`ADD | DROP PERSISTED` is [PERSISTED](#persisted)'s, and `ADD | DROP MASKED` is [Dynamic Data Masking](data-masking.md#ddl)'s.

`ADD | DROP HIDDEN` toggles a period column's `is_hidden`, which `SELECT *` reads; repeating a toggle is a no-op, a column that isn't `GENERATED ALWAYS AS ROW START | END` is **Msg 13735**, and the history sibling's columns stay unhidden whatever the parent's (probed 2026-10-02 against SQL Server 2025; EF Core's migration converting a table to temporal emits `ADD HIDDEN`).

## PERSISTED

`ALTER COLUMN c { ADD | DROP } PERSISTED` converts a computed column between stored and evaluated-on-read in place, each form a no-op on a column already in that state, and `sys.computed_columns.is_persisted` follows (probed 2026-09-28 against SQL Server 2025).
The column keeps its position and `column_id`; what moves is a storage slot, which every row gains or loses, so the statement rewrites the heap and shifts the slots after it for the keys and indexes that name them (`AlterColumnPersisted`, undone slot by slot on a rollback).

`ADD` evaluates the expression for every row on the way in, so a row it fails on (a divide by zero, an overflow) ends the statement with its error and Msg 3621 and leaves the column as it was.
An index keying the column while it was evaluated-on-read keys the stored slot afterwards.

Probed refusals:

| Statement | Refusal |
| --- | --- |
| Either form on a column that isn't computed | **Msg 4919** state 0 |
| Either form on a column the table doesn't have | **Msg 4924** state 2 |
| `ADD` over a nondeterministic expression — `NEWID()`, `GETDATE()`, a non-schema-bound function | **Msg 4936** state 1 |
| `DROP` while a CHECK, key, index keying the column, statistics object, foreign key or schema-bound module depends on it | **Msg 5074** per dependent, then **Msg 4922** state 9 |
| `ADD PERSISTED NOT NULL` | **Msg 156** near `not` |

An index that only *includes* the column doesn't hold `DROP` back, and an imprecise (`float`) expression persists.

## ADD PERIOD FOR SYSTEM_TIME

`ALTER TABLE t ADD PERIOD FOR SYSTEM_TIME (startCol, endCol)` is `DROP PERIOD`'s inverse: both named columns become `GENERATED ALWAYS AS ROW START | END`, `sys.periods` gains its row, and the period is live immediately — an `INSERT` that omits the two columns has them filled the way a versioned table's does.
It is what `SET (SYSTEM_VERSIONING = ON)` needs, so the pair re-arms a table whose period was dropped, which is the deactivate-load-reactivate cycle WideWorldImporters' `DataLoadSimulation` procedures script.

Checks run in real's own probed order:

1. **Msg 13597** (state 2) when the table already carries a period, ahead of every column check.
2. Per column, start then end: **Msg 4924** when the column doesn't exist (a *computed* column counts as absent — real doesn't offer one as a candidate), then **Msg 13501** (state 3) when it isn't `datetime2`, then **Msg 13587** when it's nullable.
3. **Msg 13513** when the two `datetime2` precisions disagree (each precision on its own is fine).
4. **Msg 13575** (state 0) when an existing row's end-of-period value falls short of the maximum its declared precision holds; an empty table passes, and the check reads the end column alone — a start ahead of its end doesn't raise.
5. **Msg 13542** (state 0) when an open row's period starts after the statement's time (probed 2026-10-04 against SQL Server 2025).

Naming the same column twice is legal, as on real, and leaves that one column marked ROW START.

The period may also be declared in the `ADD` list that adds its `GENERATED ALWAYS AS ROW START | END` columns, as `CREATE TABLE` declares one — `ADD s datetime2 GENERATED ALWAYS AS ROW START NOT NULL DEFAULT …, e …, PERIOD FOR SYSTEM_TIME (s, e)` — the existing rows taking the columns' defaults before the checks above run; a generated column added without its period is Msg 13509 (probed 2026-10-04 against SQL Server 2025).

## DROP PERIOD FOR SYSTEM_TIME

`ALTER TABLE t DROP PERIOD FOR SYSTEM_TIME` removes the period row and turns its two columns into ordinary ones — they keep their type, nullability and `column_id` but lose `GENERATED ALWAYS AS ROW START | END`, so `sys.periods` empties and `sys.tables.temporal_type` reads `NON_TEMPORAL_TABLE`.
Storage is untouched, since both columns were stored either way.

Two refusals, both naming the table three-part (`db.schema.table`, unlike most of `ALTER TABLE`'s unqualified messages):

- **Msg 13592** while system versioning is still on — the period is what versioning reads, so `SET (SYSTEM_VERSIONING = OFF)` has to come first.
- **Msg 13593** when there is no period, which a table that never had one and one whose period was already dropped report alike.

## REBUILD

`ALTER TABLE t REBUILD [PARTITION = { ALL | <n> }] [WITH ( option = value [, …] )]` re-lays-out physical storage, which a flat page list has no notion of, so it validates and succeeds without touching a row.

- The option block takes `DATA_COMPRESSION` (`NONE` / `ROW` / `PAGE` / `COLUMNSTORE` / `COLUMNSTORE_ARCHIVE`), `MAXDOP`, `ONLINE`, `SORT_IN_TEMPDB` and `XML_COMPRESSION`; anything else is **Msg 155** in its ALTER TABLE wording, a bad compression level or an empty list is Msg 102.
- On an unpartitioned table a partition *number* splits three ways exactly as real's does: **Msg 7729** State 1 naming the table's key-backed index (in real's own "alter index statement" wording, whichever statement raised it), **Msg 7735** naming the table when it carries no index, and **Msg 7729** State 3 for the `ON PARTITIONS (…)` sub-clause — written with `PARTITION = ALL`, which it needs first (Msg 10737; probed 2026-10-05).
  A partitioned table takes a number it has — see [`partitioning.md`](partitioning.md#rebuilding-one-partition).
- A missing table is **Msg 4902**, as for every other ALTER TABLE shape.

## Storage

- `HeapTable.KeyConstraints` / `CheckConstraints` are `List<>` (the reference is `readonly`, contents mutable) so ADD / DROP can append / remove in place.
  Inline at CREATE TABLE still goes through the same lists.
- `HeapTable.OutgoingForeignKeys` / `IncomingForeignKeys` already carry the constraint lists.
- `ForeignKey.IsNotTrusted` / `CheckConstraint.IsNotTrusted` are mutable bool fields, false on CREATE-time inline / true on WITH-NOCHECK ALTER ADD / true after `NOCHECK CONSTRAINT`.
  Cleared by `WITH CHECK CHECK CONSTRAINT` on successful revalidation.
- `ForeignKey.IsDisabled` / `CheckConstraint.IsDisabled` are independent mutable bools — true after `NOCHECK CONSTRAINT`, false after either `CHECK CONSTRAINT` form.
  The enforcement loops (`EnforceCheckConstraints`, `EnforceOutgoingForeignKeys`, `EnforceIncomingForeignKeys`, `EnforceIncomingFkOnUpdate`) skip when `IsDisabled` — including suppressing cascade actions.
- `CheckConstraint.IsSystemNamed` flags auto-named CHECKs; `KeyConstraint` infers the same from its name prefix (no explicit flag).
- `HeapColumn.Default` is mutable (ALTER ADD DEFAULT sets, ALTER DROP CONSTRAINT clears).
- `HeapColumn.DefaultConstraint` is the named metadata wrapper alongside `Default` — populated at inline DEFAULT (auto-named, `IsSystemNamed = true`) and named ALTER ADD DEFAULT (explicit name, `IsSystemNamed = false`).

## Catalog views

Three catalog views cover constraint metadata:

- **`sys.check_constraints`** — one row per CHECK constraint, with `is_not_trusted`, `is_system_named`, `parent_column_id` (the attached column's stable `sys.columns.column_id` for inline column-level; `0` for table-level), `uses_database_collation` (1 — real reports 1 for every CHECK, numeric-only predicates included), and `definition` (the predicate in real's canonical form — see [Definition columns](#definition-columns)).
- **`sys.key_constraints`** — one row per PRIMARY KEY / UNIQUE constraint, with `type` = `PK` / `UQ`, `type_desc` = `PRIMARY_KEY_CONSTRAINT` / `UNIQUE_CONSTRAINT`, and `is_system_named` inferred from the auto-name prefix.
- **`sys.default_constraints`** — one row per named DEFAULT (inline + ALTER ADD), with `parent_column_id` (the bound column's stable `sys.columns.column_id`), `is_system_named`, and `definition` (the default expression in real's canonical form — see [Definition columns](#definition-columns)).

`sys.foreign_keys.is_not_trusted` / `is_disabled` read from `ForeignKey.IsNotTrusted` / `IsDisabled`; `sys.check_constraints.is_not_trusted` / `is_disabled` read from the corresponding `CheckConstraint` flags.

## Per-constraint dates

Each `KeyConstraint` / `CheckConstraint` / `ForeignKey` / `DefaultConstraint` carries its own `CreateDate` / `ModifyDate`, stamped from the declaring statement's frozen `UtcNow` rather than borrowed from the parent table.
Both `sys.objects` and the per-family catalog view project them.
Probe-confirmed rules:

- A constraint declared **inside `CREATE TABLE`** — inline column tail or table-level list — shares the table's instant, because it is the same statement.
- An **`ALTER TABLE … ADD CONSTRAINT`** carries the later instant; the parent table's own `modify_date` advances alongside.
- A **trust toggle** (`ALTER TABLE … {NOCHECK|CHECK} CONSTRAINT`, either direction, `ALL` included) advances the constraint's `modify_date` alone, leaving `create_date` put.

An `sp_rename` of a constraint advances its `modify_date` too (see [`catalog-views.md`](catalog-views.md)).
A **DEFAULT** constraint also has no `sys.objects` row here, so only `sys.default_constraints` carries its dates.

## Definition columns

`sys.check_constraints.definition`, `sys.default_constraints.definition` and `sys.computed_columns.definition` hold the expression in SQL Server's **canonical form**, rendered at CREATE / ALTER time by `Parser/CanonicalDefinition.cs` from the expression's tokens and wrapped in one paren pair (probed 2026-09-26 against SQL Server 2025).
The same text reaches every surface that reads it — `INFORMATION_SCHEMA.CHECK_CONSTRAINTS` / `COLUMNS.COLUMN_DEFAULT`, `sp_helptext`, `sp_helpconstraint`, the bacpac round trip.
The rules the renderer reproduces:

- names bracketed as written (`A` → `[A]`, `dbo.f` → `[dbo].[f]`); built-in names lowercased, except the few real keeps in a case of its own (`Trim`, `Compress`, `Decompress`, `Date_Bucket`, `Crypt_Gen_Random`) and `CONVERT` / `TRY_CAST` / `TRY_CONVERT`;
- numeric literals parenthesized — an integer by value (`0002` → `(2)`), a decimal with its written scale and a scale-0 one with a trailing point (`1.` → `(1.)`, `2147483648` → `(2147483648.)`), a float as `%.16e` of the **stored double** with a three-digit exponent (`1.5E-2` → `(1.4999999999999999e-002)`, probed 2026-10-02), money as `$` plus four places; string, binary and `NULL` bare;
- a minus takes a whole multiplicative term (`-a*b` is `-(a*b)`) and folds into a numeric literal, rendering ` -x` otherwise — a zero literal takes no sign but a float's (`-0.00` → `(0.00)`, `-0e0` → `(-0.0000000000000000e+000)`); `+` vanishes; `~` binds tightest;
- `NEXT VALUE FOR` keeps the sequence name as written, one part or two, each bracketed (`NEXT VALUE FOR [sq]`, `NEXT VALUE FOR [dbo].[sq]`), without the parentheses the source wrapped it in — and a database part is refused in a DEFAULT, Msg 11730 (probed 2026-10-02);
- written parentheses dropped, and put back by precedence: an arithmetic operand binding no tighter than its operator is parenthesized on either side (`a+b-c` → `([a]+[b])-[c]`), a comparison binds at the additive level, a negation is always parenthesized as an operand;
- `AND` / `OR` flatten a left operand of their own kind and parenthesize a right one; an `OR` under an `AND` is parenthesized;
- `IN` becomes an OR chain over its list **reversed**, `BETWEEN` a `>=` / `<=` pair, `NOT` over either parenthesizing the chain; `!=` / `!<` / `!>` become `<>` / `>=` / `<=`; `LIKE` stays lowercase and an `ESCAPE` leaves a trailing space;
- `CAST` → `CONVERT([type],x)`, `TRY_CONVERT` without a style → `TRY_CAST(x AS [type])`, the type name folded to its system name (`integer` → `int`, `rowversion` → `timestamp`, `national char varying` → `nvarchar`); `IIF` → `CASE`; a `CASE` with no `ELSE` leaves two spaces before `end`; `YEAR` / `MONTH` / `DAY` → `datepart`, and a date part's alias its full name; `CURRENT_TIMESTAMP` → `getdate()`, `CURRENT_USER` / `SESSION_USER` / `USER` → `user_name()`, `SYSTEM_USER` → `suser_sname()`; `<<` / `>>` → `left_shift` / `right_shift`;
- `COLLATE` parenthesizes its operand, `AT TIME ZONE` its whole expression;
- a method call keeps its name as written after its bracketed receiver (`[h].[GetLevel]()`), and a type's static method after its bracketed type (`[geography]::Point((1),(2),(4326))`, probed 2026-10-07).

A shape outside that grammar — an ODBC `{fn …}` escape — keeps its **source text**, wrapped in one paren pair (a computed column's body once, not twice), so the column is never empty.
Real renders an escape's arguments canonically and maps some escapes to their T-SQL function while keeping others (`{fn ucase(s)}` → `(upper([s]))`, `{fn concat(s, 'x')}` → `({fn concat([s],'x')})`, probed 2026-10-07 against SQL Server 2025).
The filtered-index `sys.indexes.filter_definition` has its own, narrower renderer (see [`indexes.md`](indexes.md#filtered-index-filter_definition)).
The text scans that read a stored definition — the determinism and precision checks behind persisted and indexed computed columns — accept both forms.

## Variables in a column definition

A declared variable in a column's `DEFAULT`, `CHECK` or computed expression is **Msg 112** as the batch compiles, so nothing in it runs: `Variables are not allowed in the CREATE TABLE statement.` for `CREATE TABLE` and a table variable's `DECLARE`, `… ALTER TABLE statement.` for anything `ALTER TABLE … ADD` adds — ahead of a missing table that would defer the rest — while a `SWITCH`'s partition number takes one and an undeclared variable is still Msg 137 (probed 2026-10-06 against SQL Server 2025; `ParserContext.VariablesRefusedIn`).
A table type's `CREATE TYPE … AS TABLE` accepts one.

## Fidelity gaps

- **An ODBC escape in a definition keeps its source text** rather than real's canonical rendering — see [Definition columns](#definition-columns).
- **`KeyConstraint.IsSystemNamed` is inferred from the name prefix** — `PK__` / `UQ__` → system-named.
  Custom names matching the prefix would report `is_system_named = true` incorrectly.
  Real SQL Server tracks the flag explicitly; the simulator inherits a no-flag pre-bundle storage layout and infers rather than adding a column-mutating change.
- **A sparse column set's client metadata**: SqlClient's `GetSchemaTable` reports `IsColumnSet` false, where real's COLMETADATA flags the column, and `OUTPUT … INTO`, `BULK INSERT` and a bulk load don't write through one (Msg 271 as a computed column).

## EF Core integration

EF Migrations emit FK adds via `ALTER TABLE` heavily (separate from `CREATE TABLE`).
The simulator accepts that emit shape; no EFCore-specific test covers it, because once the FK is in place (whether declared inline at CREATE TABLE or added via ALTER), EF Core sees the same database state either way, so the `EFCoreForeignKey` test already covers the LINQ surface.
The simulator-side `AlterTableConstraintTests` covers the parser / validation / catalog surface for ALTER directly.

## Column ops

`ALTER TABLE … ADD [COLUMN] col TYPE [, …]` and `ALTER TABLE … DROP COLUMN [IF EXISTS] col [, …]` ship, matching the shapes EF Migrations emits.
Probe-confirmed against SQL Server 2025.

### Grammar — ADD COLUMN

```sql
ALTER TABLE [schema.]table ADD [COLUMN] col TYPE [(N | MAX [, scale])]
    [NULL | NOT NULL]
    [DEFAULT expr [WITH VALUES]]
    [IDENTITY [(seed, increment)]]
    [CONSTRAINT name (CHECK (predicate) | UNIQUE | PRIMARY KEY | REFERENCES parent(cols))]
    [, col2 TYPE …]
```

Inline column-level constraints (CHECK / UNIQUE / PRIMARY KEY / REFERENCES, with or without `CONSTRAINT name`) all parse through the shared `ParseOneColumnIntoLists` helper that backs CREATE TABLE.
Computed columns via `col AS expr [PERSISTED [NOT NULL]] [inline constraints]` are supported and resolve against the combined (existing + new) column view — which is also the view the added column's inline CHECK is validated against, so a predicate reaching a non-persisted computed column already on the table raises Msg 1764 (see [`constraints.md`](constraints.md#computed-columns-in-a-check-constraint)).


An inline `UNIQUE` / `PRIMARY KEY` may be the batch's last token — CREATE TABLE always has a closing paren after the clause, this form doesn't, and `ALTER TABLE t ADD c int NULL UNIQUE` is what Django's schema editor emits for a `unique=True` field a migration adds.

### Backfill semantic

Existing rows are re-encoded against the new schema.
Per-column backfill values:

| Column kind | Backfill for existing rows |
|-------------|----------------------------|
| Nullable | NULL — DEFAULT only applies to future INSERTs, unless it says `WITH VALUES` |
| Nullable with `DEFAULT … WITH VALUES` | DEFAULT expression, as for NOT NULL |
| NOT NULL with DEFAULT | DEFAULT expression evaluated once at ALTER time, snapshotted to every row |
| NOT NULL IDENTITY | Sequential allocation: seed, seed+increment, seed+2·increment, … in heap-scan order |
| NOT NULL ROWVERSION / TIMESTAMP | Per-row from the database-scoped rowversion counter |
| NOT NULL without DEFAULT/IDENTITY/ROWVERSION on non-empty table | Msg 4901 (probe-confirmed) |
| Computed (non-persisted) | No backfill — evaluated on read |

`WITH VALUES` belongs to an added column's DEFAULT alone: on a column without one, in CREATE TABLE and in a table variable's declaration real reads `VALUES` as Msg 156 (probed 2026-09-24 against SQL Server 2025).
After `ADD … DEFAULT … FOR column` it parses and changes nothing, the column's rows keeping their values (probed 2026-10-01).

The DEFAULT-evaluated-once rule is a probe-confirmed SQL Server quirk: `ALTER TABLE t ADD created datetime NOT NULL DEFAULT GETUTCDATE()` produces a single timestamp for every existing row, not a per-row evaluation.
The simulator matches.

### Error paths — ADD COLUMN

| Msg | Trigger |
|-----|---------|
| **2744** | Adding a second IDENTITY column to a table that already has one (existing-identity count tracked from `HeapTable.IdentityOrdinal`). |
| **2705** | Duplicate column name — against any existing column or another column in the same multi-column ADD. |
| **4901** | NOT NULL without DEFAULT / IDENTITY / ROWVERSION on a non-empty table. |
| **8111** | Existing PrimaryKeyOnNullableColumn for inline `PRIMARY KEY` on an explicit-`NULL` column (inherited from CREATE TABLE shared parser). |
| **8183** | Inline `CHECK` / `REFERENCES` / `NOT NULL` on a computed column added without `PERSISTED`. |
| **1764** | An added column's inline `CHECK` reading a non-persisted computed column, its own or one already on the table. |
| **1505** | Inline `UNIQUE` constraint when the filled rows hold duplicate values — NULLs a nullable column filled every row with included (probed 2026-10-01). |
| **547** | Inline FK / CHECK rejecting the filled rows — a NOT NULL default its own CHECK refuses included (probed 2026-10-01). |
| **1701** | The columns' smallest row passes 8060 bytes, as at CREATE TABLE. |

### Grammar — DROP COLUMN

```sql
ALTER TABLE [schema.]table DROP COLUMN [IF EXISTS] col [, col2, …]
```

Two-pass apply: every name is resolved + dependency-checked before any mutation, so a single Msg 5074 or Msg 4924 leaves the table unchanged.

### Dependency rejection — DROP COLUMN

Probe-confirmed: dropping a column referenced by ANY of the following raises one **Msg 5074** per blocker, then **Msg 4922** state 9 (`ALTER TABLE DROP COLUMN col failed because one or more objects access this column.`), each its own error:

- `PRIMARY KEY` / `UNIQUE` constraint (`KeyConstraint` storage ordinals)
- Outgoing `FOREIGN KEY` (child side — the FK's child column references the to-be-dropped column)
- Incoming `FOREIGN KEY` (parent side — another table's FK references this column)
- `CHECK` constraint (inline `InlineColumn` match OR table-level predicate walked structurally for column refs by name)
- `DEFAULT` constraint attached to the column
- A `WITH SCHEMABINDING` view or function whose body names the column (see [`programmable.md`](programmable.md#schema-binding-with-schemabinding))
- `INDEX` (`CREATE INDEX`-declared — either KEY column or INCLUDE column)
- A computed column whose expression names it (`The column 'X' …`)

Each blocker emits its line with the appropriate prefix: `The object 'X' is dependent on column 'col'.` for constraints and schema-bound modules, `The index 'X' is dependent on column 'col'.` for indexes.
Real orders them by kind — the DEFAULT, computed columns, CHECKs, schema-bound modules, key constraints, indexes, outgoing then incoming foreign keys — and by creation within a kind, not by creation overall (probed 2026-09-25); `CollectColumnBlockers` walks in that order.

`IF EXISTS` suppresses Msg 4924 (column doesn't exist) but does NOT suppress Msg 5074 (dependencies block) — matches real SQL Server.

### Storage rewrite

DROP COLUMN walks every surviving `KeyConstraint` / `Index` / `ForeignKey` (outgoing + incoming) and in-place remaps their storage / full ordinals through an `oldStorageToNew[]` / `oldFullToNew[]` map.
The mutation patterns:

- `KeyConstraint.StorageOrdinals[i]` — array element reassignment
- `Index.KeyColumns[i]` — slot replacement with new `IndexKeyColumn(newOrdinal, oldDescending)`
- `Index.IncludedColumns[i]` — array element reassignment
- `ForeignKey.ChildColumnOrdinals[i]` — array element reassignment (outgoing)
- `ForeignKey.ReferencedColumnOrdinals[i]` — array element reassignment (incoming, since this table is the referenced side)

The heap is re-encoded: each row is decoded under the old `StoredColumns` layout, projected through the surviving ordinals, and re-encoded against the new `StoredColumns`.
The old `Heap` is replaced wholesale (via the mutable `HeapTable.Heap` field).

### Fidelity gaps — Column ops

- **Eager row rewrite vs metadata-only**: Real SQL Server 2012+ optimizes many ADD COLUMN cases (nullable adds, NOT NULL constant-default adds) to metadata-only — no physical row updates.
  The simulator always rewrites every row.
  Behavior is identical; performance differs (acceptable for simulator workload sizes).
- **Table variable column ops**: `DECLARE @t TABLE` then `ALTER TABLE @t ADD …` raises Msg 102 at parse — real SQL Server's grammar also doesn't allow ALTER on table variables.

## ALTER COLUMN

### Grammar — ALTER COLUMN

```sql
ALTER TABLE [schema.]table
    ALTER COLUMN col TYPE[(precision[, scale])] [COLLATE collation] [SPARSE] [MASKED WITH (…)] [NULL | NOT NULL]
```

Single-column shape only (real SQL Server's grammar doesn't accept comma-separated multi-column ALTER COLUMN).
Routed from `TryParseAlterTable` via `Keyword.Alter` into `TryParseAlterTableAlterColumn`.
The trailing `NULL`/`NOT NULL` keyword is optional — omitting it leaves the column nullable, a NOT NULL one included and under `SET ANSI_NULL_DFLT_ON OFF` too, unless an alias type declares otherwise (probed 2026-10-01 against SQL Server 2025).
`COLLATE` sets the column's collation, which a CHECK, DEFAULT or index on it refuses as a type change (see [Blockers](#blockers-msg-5074)); without one the column takes the database default, a declared collation reset included (probed 2026-10-02 against SQL Server 2025).
`SPARSE` makes the column sparse and a restatement without it makes it non-sparse; it comes after `COLLATE` and before `MASKED WITH`, either other order being a syntax error at `SPARSE`, and its refusals are the [column attribute](#column-attributes)'s, raised ahead of the conversion check — `int` to `text SPARSE` is Msg 1731, not Msg 206 (probed 2026-10-02).

The `ALTER COLUMN col ADD/DROP {ROWGUIDCOL|SPARSE}` sub-clauses are [column attributes](#column-attributes), `MASKED` is [Dynamic Data Masking](data-masking.md#ddl)'s, and `PERSISTED` is [PERSISTED](#persisted)'s.
A type change drops the column's mask unless the clause restates one (`ALTER COLUMN c varchar(20) MASKED WITH (…) NULL`).

### Conversion fidelity

Type and length changes flow per-row through `SqlValue.CoerceTo`, which is the same conversion path CAST / CONVERT use.
Real SQL Server's error codes surface verbatim:

| Path | Trigger | Code |
|------|---------|------|
| Integer narrowing overflow | `int → tinyint` with value 500 | Msg 220 (`Arithmetic overflow error for data type tinyint, value = 500.`) |
| String → integer with non-numeric data | `varchar → int` with `'hello'` | Msg 245 (`Conversion failed when converting the varchar value 'hello' to data type int.`) |
| String → date/time with bad format | `varchar → date` with `'not-a-date'` | Msg 241 (`Conversion failed when converting date and/or time from character string.`) |
| Decimal precision narrow | `decimal(10,2) → decimal(4,2)` with 999.99 | Msg 8115 (`Arithmetic overflow error converting expression to data type numeric.`) |
| Bounded-string narrow | `varchar(50) → varchar(10)` with 30-char value | Msg 2628 (`String or binary data would be truncated…`) |
| `NULL → NOT NULL` with existing NULL | `varchar(10) null → varchar(10) not null` on a row with NULL | Msg 515 (`Cannot insert the value NULL into column 'X', table 'Y'; column does not allow nulls.`) |

Widening within the same family (`varchar(50) → varchar(100)`, `int → bigint`, `tinyint → smallint`) succeeds when nothing below blocks it; bounded-string narrowings succeed when every existing value fits the new length.

Each value then meets the new column as a write does ([`dml.md`](dml.md#writing-a-value-into-a-column), `ConvertForAlteredColumn`, probed 2026-10-01 against SQL Server 2025): only trailing spaces beyond the length are cut silently, a number into a string converts as a `CAST` would (`12345` into `varchar(2)` stores `*`), the Msg 2628 names no value, and under `SET ANSI_WARNINGS OFF` a truncation cuts and an overflow stores NULL without Msg 3606.
A refused value ends the statement with Msg 3621, the Msg 515 naming the table three-part.
A change no `CAST` could make — `date → int`, `int → date`, `int → uniqueidentifier` — is **Msg 206** before any row is read (`Cast.IsIllegalExplicitConversion`).

### Blockers (Msg 5074)

`CollectColumnBlockers` walks the same surface as DROP COLUMN, with most sources blocking only some changes (probed 2026-09-25, the key, foreign-key and statistics rows 2026-09-30, against SQL Server 2025).
A **type change** is a different type family (`int → bigint`, `varchar → nvarchar`), a different collation, or a move to or from MAX.
A **size change** is any other change to the declaration — a length, precision or scale; **growth** is a `varchar` / `nvarchar` / `varbinary` length growing.
Restating the column exactly as it stands passes every row but the always-blocking ones, which is what lets an ORM re-issue `ALTER COLUMN` over a key or foreign-key column to change only its comment or nullability:

| Source | Blocks ALTER COLUMN? | Prefix in Msg 5074 |
|--------|----------------------|--------------------|
| PRIMARY KEY on this column | A type change, a size change other than growth, or NOT NULL to NULL | `The object 'X' is dependent…` |
| UNIQUE constraint on this column | A type change, a size change other than growth, or NULL to NOT NULL | `The object 'X' is dependent…` |
| Index whose key or include columns reference this column, and a statistic on it | As a UNIQUE constraint — `char(10) → char(20)`, `datetime2(3) → datetime2(7)` and a decimal precision change block, `varchar(10) → varchar(20)` passes | `The index 'X' is dependent…` / `The statistics 'X' is dependent…` |
| FOREIGN KEY at either end — this column as a child or as the referenced column | A type or size change, growth included; no nullability change | `The object 'X' is dependent…` |
| Computed column that references this column in its expression | Always | `The column 'X' is dependent…` |
| `WITH SCHEMABINDING` view or function whose body names this column | Always — probe-confirmed that a widening an index waves past still fails here | `The object 'X' is dependent…` |
| CHECK constraint that references this column | On a type change (a length change passes and the constraint keeps enforcing) | `The object 'X' is dependent…` |
| DEFAULT constraint on this column | On a type change | `The object 'X' is dependent…` |

One Msg 5074 per blocker then Msg 4922 naming `ALTER COLUMN`, in DROP COLUMN's order.

### Rejection paths (other than Msg 5074)

| Condition | Code | Notes |
|-----------|------|-------|
| Column doesn't exist on the table | Msg 4924 | Shares the code with DROP COLUMN's missing-column path, distinct wording (`ALTER TABLE ALTER COLUMN failed because column 'X' does not exist…`). |
| Column is a computed column | Msg 4928 | Phrasing: `Cannot alter column 'X' because it is 'COMPUTED'.` |
| Column is rowversion / timestamp | Msg 4928 | Phrasing: `Cannot alter column 'X' because it is 'timestamp'.` |
| New type is rowversion / timestamp | Msg 4927 | `Cannot alter column 'X' to be data type timestamp.` (probed 2026-10-01) |
| The table is a view | Msg 4909 | `Cannot alter 'v' because it is not a table.`, for every `ALTER TABLE` action (probed 2026-10-01). |
| Column is `GENERATED ALWAYS AS ROW START/END` — a period column | Msg 13599 | `Period column 'X' in a system-versioned temporal table cannot be altered.` Checked ahead of the type reference, so a period column with an unparseable target type still reports 13599. |
| ALTER COLUMN of an IDENTITY column to a non-integer type | Msg 2749 state 3 | `Identity column 'X' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0…` Checked after the type resolves, since it reads the new type. The gate is `SqlType.IsIntegerCategory`, so the `decimal` / `numeric` scale-0 half of the message real honours isn't accepted yet — the same narrowing `CREATE TABLE` applies at state 2. |

Adding or removing identity itself never reaches either check: real's `ALTER COLUMN` grammar has no IDENTITY slot, so it is a parse-level Msg 156.

### Preservation through the column instance swap

`HeapColumn` instances are immutable for most fields; ALTER COLUMN constructs a fresh `HeapColumn` for the target ordinal and inherits identity / default / generated-as / hidden state from the prior instance.
Specifically:

- **Identity counter**: `existingCol.Identity` (the `IdentityState` reference) carries over verbatim.
  The high-water mark survives, so the next INSERT after `int identity` → `bigint not null` keeps incrementing from where it left off.
- **DEFAULT expression + constraint name**: `existingCol.Default` and `existingCol.DefaultConstraint` both carry over.
  Probe-confirmed: a named DEFAULT keeps its name through the alter; sys.default_constraints shows the same entry post-ALTER.
- **`is_hidden` / `GeneratedAs`**: Inherited but currently rejected up front (see table above).
- **Inline CHECK constraints**: live on `HeapTable.CheckConstraints` keyed by `InlineColumn` name, not on the HeapColumn — so they remain wired to the column by name and continue to enforce after the rebuild.

### Storage rewrite

`RewriteHeapForAlterColumn` walks every row, decoding the target column under the pre-alter `HeapColumn` and re-encoding the whole row against the post-alter `StoredColumns`.
The non-altered columns are decoded then re-encoded as-is (no coercion).
Strategy: build the candidate post-alter Columns array, swap it onto the table (so `StoredColumns` / `Schema` reflect the new shape before re-encoding writes), walk rows; on any failure restore the original Columns + recompute.
The Heap field replaces wholesale at the end.

Like ADD / DROP COLUMN, the rewrite is unconditional — even pure length widening (`varchar(50) → varchar(100)`) walks every row through decode + re-encode, because the singleton `SqlType` reference differs between lengths and the StoredColumns / Schema arrays must mirror that.
Storage cost is negligible at simulator workload sizes.

### Fidelity gaps — ALTER COLUMN

- **Eager rewrite even when bytes are identical**: As above — pure length widening within the same family rewrites every row, even though the encoded bytes are byte-for-byte identical between varchar(50) and varchar(100).
  Performance only; behavior matches.
