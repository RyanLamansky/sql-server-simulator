# Graph tables: nodes, edges, MATCH and SHORTEST_PATH

`CREATE TABLE … AS NODE | AS EDGE`, the `$node_id` / `$edge_id` / `$from_id` / `$to_id` pseudo-columns, edge constraints, `MATCH`, `SHORTEST_PATH` with its graph path aggregates, and the six identifier functions.
Everything below was probed 2026-09-27 against SQL Server 2025 unless it says otherwise; `GraphTableTests` is the behavior contract.

The pieces: `GraphColumns` (`Storage/GraphTables.cs`) builds the internal columns and reads and writes the JSON identifiers; `GraphIdentifier` is the pseudo-columns' computed expression; `Simulation.GraphTables.cs` holds the DDL, the graph-id allocation, the INSERT settle step and edge-constraint enforcement; `MatchPredicate` parses and binds `MATCH`; `Selection.ShortestPath.cs` turns a `SHORTEST_PATH` into a lateral source.

## The table shape

A node table leads with `graph_id` (hidden `bigint`) and `$node_id`; an edge table with `graph_id`, `$edge_id`, `from_obj_id`, `from_id`, `$from_id`, `to_obj_id`, `to_id`, `$to_id`, each name suffixed `_` plus 32 hex digits.
The declared columns follow, so every ordinal the column list records is taken after the internal columns are in place — `CREATE TABLE` looks ahead past the list for `AS NODE | EDGE` before parsing it.
Real's suffix is a fresh GUID; here it is a hash of the table name and the column's kind, stable from run to run (the constraint auto-names make the same trade).
A node table needs a column list; an edge table may leave it out.

The four pseudo-columns are non-persisted computed columns whose expression renders the identifier from the hidden columns when the row is read, so a renamed table's rows render the new name and a dropped endpoint table's edges render `$from_id` / `$to_id` as NULL.
The catalog reports them as ordinary columns: `sys.columns.is_computed` 0 and `graph_type` / `graph_type_desc` set, `sys.computed_columns` leaving them out, `sp_describe_first_result_set` calling them updatable — while `COLUMNPROPERTY(…, 'IsComputed')` and `sp_help` say computed, as real's do.
The hidden ones stay out of `SELECT *` like any hidden column; `SELECT … INTO` copies the pseudo-columns as plain `nvarchar(1000)` columns and makes no graph table.
A unique nonclustered `GRAPH_UNIQUE_INDEX_<hex>` over `graph_id` is created with the table; it takes index id 2 ahead of the declaration's own nonclustered indexes, which is where its object id is allocated.

A pseudo-column is written `$node_id` (the tokenizer's `$`-word, like `$action`), any case, qualified or not, and resolves to the internal column whose name it prefixes — through a derived table, view or CTE that projects it too, which is why the match is on the name and not on the column's kind.
A projection of one takes the internal column's full name as its own.
The match runs only after an ordinary lookup misses, so it costs nothing on the scan path.
Only the bare token matches: a delimited `[$node_id]` or `"$node_id"` is an ordinary name, Msg 207 unless a column is called that (`MultiPartName.LeafDelimited`).
A hidden internal column named in a query by its full name — in a select list, `WHERE`, `ORDER BY` or an aggregate — is Msg 13908 state 1, while a pseudo-column's full name reads it; the check sits in `FindSourceColumn` and fires only for a name read from the statement's text (`MultiPartName.FromText`), since MATCH's desugared equalities name those columns too.

## Identifiers

The rendered form is `{"type":"node","schema":"dbo","table":"T","id":N}` with the names JSON-escaped.
Two readers accept it back, and they differ:
- The INSERT reader (`GraphColumns.TryParseWritten`) takes any JSON object holding exactly the four keys, in any order, any whitespace, keys and the `type` value in any case, a duplicate key's last value winning, the table and schema resolved as names are — but the id must be an integer literal (`3.0`, `1e2` and `"3"` are refused).
- The functions' reader (`GraphColumns.TryParseStrict`) takes only the rendered shape: the keys in order and no whitespace anywhere, though key and `type` case and string escapes are still free.

## DML

`graph_id` counts from 0 per table: an explicit `$node_id` / `$edge_id` above the counter raises it, a rolled-back row's id isn't reissued, and `TRUNCATE TABLE` doesn't reset it.
An INSERT without a column list fills an edge's `$from_id` and `$to_id` first, then the declared columns; a node's list is its declared columns.
`SettleGraphColumns` runs after the values are coerced: an explicit `$node_id` / `$edge_id` must name this table (else Msg 13921, NULL included), and a `$from_id` / `$to_id` that doesn't read as a node of an existing node table leaves its hidden pair NULL, so the ordinary NOT NULL check names `from_obj_id` / `to_obj_id` in its Msg 515 — the endpoint node needn't exist.
MERGE's insert arm and a `SqlBulkCopy` into a node table settle the same way.
UPDATE of a pseudo-column is the computed-column Msg 271, and naming a hidden column in an INSERT list or a SET is Msg 13908.

## Edge constraints

`[CONSTRAINT name] CONNECTION (A TO B [, …]) [ON DELETE CASCADE | NO ACTION]`, in `CREATE TABLE` or `ALTER TABLE … ADD`, lists as an `EC` object with its clauses in `sys.edge_constraint_clauses`.
Each constraint must admit every edge — some clause names its pair of node tables, and both nodes exist — and the constraints on one table are checked independently.
Deleting a node that a constrained edge still reaches is refused, or deletes those edges under `ON DELETE CASCADE`, which fires no trigger on the edge table (a foreign key's cascade does); an edge table with no constraint keeps its dangling edges.
A node table a constraint names can't be dropped or truncated.
`ALTER TABLE … NOCHECK | CHECK CONSTRAINT name | ALL` toggles one as it does a foreign key: `NOCHECK` sets `is_disabled` and `is_not_trusted`, a bare `CHECK` clears only `is_disabled`, and `WITH CHECK CHECK` re-validates the existing edges (Msg 547 as an `ALTER TABLE` statement) before clearing both; `OBJECTPROPERTY`'s `CnstIsDisabled` / `CnstIsNotTrusted` and `sp_helpconstraint`'s status read the same flags.
A disabled constraint checks no edge write and neither refuses nor cascades a node delete, yet still keeps its node tables from `TRUNCATE` and `DROP`.
`sp_helpconstraint` lists an edge constraint with `No Action` whatever its delete action, and a node table closes with the constraints naming it.

## MATCH

`MATCH` is recognized only inside a WHERE (and, to be refused, an ON); anywhere else `match(…)` parses as a call and the pattern's arrows are the syntax error.
It binds while it parses, against the query scope's sources and the joins between them — `JoinSpec.IsComma` tells a comma apart from a written `CROSS JOIN`, which MATCH refuses like any other join.
Each hop desugars into equalities: over two base tables `e.from_obj_id = <node table's object id>` and `e.from_id = n.graph_id`, which the comma-join rewrite hands to the hash join like any written equi-join; over a derived table, view or CTE, the rendered `$from_id = $node_id` text.
The predicate object keeps its desugared conjuncts behind `CollectConjuncts`, which is all the planner sees.

Under a MATCH, a bare `SELECT *` expands the sources MATCH doesn't name first, in FROM order, then its edges, then its nodes, each in pattern order with every hop read from its `from` end.

## SHORTEST_PATH

`MATCH(SHORTEST_PATH(start(-(edge)->node)+))`, the `<-(edge)-` direction, and the `{1, n}` quantifier, over an edge and node marked `FOR PATH` in the FROM clause, with graph path aggregates — `STRING_AGG`, `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, `LAST_VALUE` followed by `WITHIN GROUP (GRAPH PATH)` — reading the path, and `LAST_NODE(node)` continuing an ordinary pattern from the path's end.
Inside the `MATCH`, `LAST_NODE(x) = LAST_NODE(x)` holds for every row; anything but another `LAST_NODE` after the `=` is Msg 102 near it.
The result is one row per node the start reaches, the start itself included when a cycle leads back to it, carrying the first-found shortest path to it.

`ApplyShortestPath` runs once the WHERE is parsed: the two `FOR PATH` sources give way to one lateral source applied after every other source, whose rows the breadth-first walk produces per start row — the reached node's stored columns plus one column per graph path aggregate, all hidden, so `SELECT *` expands the other sources only.
Each aggregate is bound to its column, and computes its value from an ordinary `Aggregator` fed the path's steps in order, so the result types are the ordinary aggregates' (`LAST_VALUE`'s is its operand's).
The walk reads a snapshot of the edge adjacency and the node rows keyed on the two heaps' mutation generations, so the start rows of one query, and repeated executions of a cached plan, share one read until either table changes; a step's columns are decoded only when an aggregate reads them.
A `SHORTEST_PATH` in a subquery is refused, as real refuses it; a derived table or CTE may hold one.

## Divergences

- `MATCH` over an `APPLY`'s right side reports the first Msg 13920 at state 1 alone, where real sends state 3 then a second one for the start node.
- Among several shortest paths of one length, the path reported is the first the edge scan finds, and an unordered `SHORTEST_PATH` query's row order is the walk's; real's are plan-dependent.
- Of real's several errors for one misused `FOR PATH` source (Msg 13949 then Msg 4104 and Msg 13952), only the first is raised; a `GROUP BY` over a graph path aggregate names the aggregate's column in its Msg 8120 where real names an internal one.
- The walk reads the tables' current rows without the reading session's locks or row versions.

## Not modeled yet

- A `SHORTEST_PATH` repeating more than one hop (`n1(-(e1)->n2-(e2)->n3)+`), a second `SHORTEST_PATH` in one WHERE — and with it `LAST_NODE(x) = LAST_NODE(y)` over two paths — and one starting from anything but a node table → `NotSupportedException` or a syntax error.
- **A column-level permission recorded against a pseudo-column read**: real asks for SELECT on the pseudo-column's own `$node_id_<guid>` column and names it in Msg 230 — under `GRANT SELECT (id)` alone `SELECT $node_id` and `SELECT $node_id, id` are both denied, `SELECT *` names `$node_id_<guid>` and then `name`, and under a table-level grant with `DENY SELECT (name)` the pseudo-column still reads — where the simulator names the hidden `graph_id_<hex>` it renders from, lets `SELECT $node_id, id` through and refuses the last shape on `name` (probed 2026-09-30 against SQL Server 2025; an ordinary computed column already checks itself the way real does).
