# `CREATE DEFAULT` / `CREATE RULE` and their binding procedures

The pre-constraint way to give a column a default or a check: a named schema object holding an expression, attached to a table column or an alias type by `sp_bindefault` / `sp_bindrule` and detached by `sp_unbindefault` / `sp_unbindrule`.
Everything below was probed 2026-09-26 against SQL Server 2025.

## The objects

`DefaultObject` / `RuleObject` (`Schemas/BindableObject.cs`) live in `Schema.Defaults` / `Schema.Rules`, share the object-name namespace (Msg 2714 state 3 on a collision) and list in `sys.objects` as `D ` / `DEFAULT_CONSTRAINT` and `R ` / `RULE` with no parent.
Each stores its whole batch as its definition, as a module does, so it answers `OBJECT_DEFINITION`, `sys.sql_modules`, `sp_helptext`, and — for a bound default — `INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` and `INFORMATION_SCHEMA.DOMAINS.DOMAIN_DEFAULT`, which carry that whole text rather than the expression.
`OBJECTPROPERTY` answers `IsDefault` / `IsRule`, and a constant 0 for `IsQuotedIdentOn`, as a CHECK or DEFAULT constraint does.

Either statement must lead its batch (Msg 111, state 13 for DEFAULT and 12 for RULE), runs to the end of it (a trailing statement is Msg 156), and names itself as the procedure of every error it raises.
Neither expression has a column scope (Msg 128 for a name, 1046 for a subquery).
A rule's predicate reads exactly one variable (Msg 160 for none, 161 for more), which stands for the value being written; a schema-qualified function call in it is Msg 4105 whether or not the function exists.
The parser reads that variable as a `Reference` (`ParserContext.RuleVariables`), so enforcement answers it through the resolver the way a CHECK constraint reads its columns.

`DROP DEFAULT` / `DROP RULE` take `IF EXISTS` and a comma list.
An object still bound anywhere is Msg 3716 (state 3 for a default, 1 for a rule), a DEFAULT constraint's name under `DROP DEFAULT` is Msg 3717, and a name held by another kind is Msg 3705 in both directions.

## Binding

`@objname` with a dot names a column (`table.column`, schema optional); without one it names an alias type, a built-in type being Msg 4185 and anything else Msg 15148.
A bound default sets `HeapColumn.Default` to its expression, which every insert path already reads, alongside `HeapColumn.BoundDefault`; `DefaultConstraint` stays null, and the two kinds exclude each other (Msg 15103 binding over a constraint, Msg 1781 adding a constraint over a binding).
A default refuses identity columns (Msg 15102) and computed, sparse, rowversion, MAX, xml and CLR columns (Msg 15101); a rule refuses the same kinds plus text / ntext / image (Msg 15107).
Binding again replaces the previous binding silently.

Binding to an alias type rebinds the columns declared with it whose binding is still the type's previous one, unless `@futureonly` is `'futureonly'`; unbinding the type likewise unbinds the columns carrying its binding.
A column declared with the type later takes the type's bindings at `CREATE TABLE` / `ALTER TABLE ADD`, while a table variable, a table type and a multi-statement function's return table refuse such a type outright (Msg 1710, which ends the batch while it compiles).
A bound column blocks `ALTER COLUMN` / `DROP COLUMN` (Msg 5074 naming the object, then 4922), as a DEFAULT constraint does.

Each procedure prints real's confirmation from the line of real's source that prints it, and its refusals report their own line (`Simulation.SystemProcedureErrorSite`); binding and unbinding roll back with the transaction.
`sp_helpconstraint` lists a binding as `DEFAULT on column c (bound with sp_bindefault)` / `RULE on column c (bound with sp_bindrule)`, its keys the whole definition.

## Enforcement

A rule judges the value an INSERT writes to every bound column — a default and an implicit NULL included — and the value an UPDATE or MERGE sets, never a column the statement doesn't set, so rebinding a stricter rule leaves existing rows alone until they're written.
A false answer is Msg 513 followed by Msg 3621; UNKNOWN passes.
NOT NULL is checked before the rule.

## Not modeled yet

- `ALTER SCHEMA … TRANSFER` of a default or rule.
- A second `CREATE DEFAULT` / `CREATE RULE` later in a batch whose first one was misplaced: real reports a Msg 111 for each.
