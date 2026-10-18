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
A unique nonclustered `GRAPH_UNIQUE_INDEX_<hex>` over `graph_id` is created with the table; it takes index id 2 ahead of the declaration's own nonclustered indexes, which is where its object id is allocated, and on a table placed on a partition scheme it stays unaligned, on PRIMARY.
A key or index naming `$node_id` / `$edge_id` keys the hidden `graph_id` it renders (`PRIMARY KEY ($node_id)` included), and a node or edge table can be neither memory-optimized (Msg 13910) nor temporal (Msg 13912); `ALTER TABLE … DROP | ALTER COLUMN $node_id` is Msg 102 at the name, and no `ALTER TABLE … AS` form exists (Msg 156) — all probed 2026-10-05 against SQL Server 2025.
An index, key, `INCLUDE` list or statistic naming `$from_id` / `$to_id` keys the hidden object id and graph id pair it renders from, in that order and the written direction, so it raises no Msg 1945 and a duplicate reports the two integers (probed 2026-10-11 against SQL Server 2025).

A view whose body reads one node or edge table — directly, through a derived table or CTE, or through a view that isn't row-limited, `DISTINCT`, `TOP`, a window or a `UNION ALL`'s first branch included, a `GROUP BY` or a second source not — lists hidden graph columns after its own wherever the catalog lists a view's columns (`sys.columns`, `INFORMATION_SCHEMA.COLUMNS`, `sp_columns`, `sp_help`, `COL_NAME`, `COLUMNPROPERTY`): over a node table the table's `graph_id` and an `obj_id_<hex>` of the view's own (`graph_type` 9, `GRAPH_IV_NODE_OBJ_ID`), over an edge table its `graph_id` and both endpoint pairs (`View.HiddenGraphColumns`, probed 2026-10-11 against SQL Server 2025).
A view over a view inherits its columns, `obj_id` name and all; a projected pseudo-column keeps its `graph_type` under any alias and describes as updatable, not computed.
The view's rows carry none of these columns — only the catalog lists them.

A pseudo-column is written `$node_id` (the tokenizer's `$`-word, like `$action`), any case, qualified or not, and resolves to the internal column whose name it prefixes — through a derived table, view or CTE that projects it too, which is why the match is on the name and not on the column's kind.
A projection of one takes the internal column's full name as its own.
A read of one asks `SELECT` on the pseudo-column itself, as a computed column's does, and Msg 230 names it by that full name — never the hidden columns it renders from, which only `MATCH`'s desugared equalities read (probed 2026-10-11 against SQL Server 2025).
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
A single-table `UPDATE` or `DELETE` reads a pseudo-column in its `WHERE` (`DELETE Knows WHERE $from_id = …`) through the same prefix match.

## Edge constraints

`[CONSTRAINT name] CONNECTION (A TO B [, …]) [ON DELETE CASCADE | NO ACTION]`, in `CREATE TABLE` or `ALTER TABLE … ADD`, lists as an `EC` object with its clauses in `sys.edge_constraint_clauses`.
Its name shares the schema's object namespace (Msg 2714 state 5 then 1750 for a taken one), and a clause resolves its target before its source, so `X TO Y` with neither existing names `Y` in its Msg 13931 (probed 2026-10-05 against SQL Server 2025).
Each constraint must admit every edge — some clause names its pair of node tables, and both nodes exist — and the constraints on one table are checked independently.
Deleting a node that a constrained edge still reaches is refused, or deletes those edges under `ON DELETE CASCADE`, which fires no trigger on the edge table (a foreign key's cascade does); an edge table with no constraint keeps its dangling edges.
A node table a constraint names can't be dropped or truncated.
`ALTER TABLE … NOCHECK | CHECK CONSTRAINT name | ALL` toggles one as it does a foreign key: `NOCHECK` sets `is_disabled` and `is_not_trusted`, a bare `CHECK` clears only `is_disabled`, and `WITH CHECK CHECK` re-validates the existing edges (Msg 547 as an `ALTER TABLE` statement) before clearing both; `OBJECTPROPERTY`'s `CnstIsDisabled` / `CnstIsNotTrusted` and `sp_helpconstraint`'s status read the same flags.
A disabled constraint checks no edge write and neither refuses nor cascades a node delete, yet still keeps its node tables from `TRUNCATE` and `DROP`.
`sp_helpconstraint` lists an edge constraint with `No Action` whatever its delete action, and a node table closes with the constraints naming it.

## MATCH

`MATCH` is recognized only inside a WHERE, a `MERGE`'s `ON` (and, to be refused, a join's ON); anywhere else `match(…)` parses as a call and the pattern's arrows are the syntax error.
It binds while it parses, against the query scope's sources and the joins between them — `JoinSpec.IsComma` tells a comma apart from a written `CROSS JOIN`, which MATCH refuses like any other join.
An identifier the query's own sources don't answer binds against an enclosing query's (`ParserContext.EnclosingScopes`), so a correlated subquery can match from an outer node, as real's does (probed 2026-10-05 against SQL Server 2025); its desugared equality then reads the outer row like any correlated reference.
An `APPLY`'s left side is no enclosing query (`ParserContext.ApplyLeftScopes`): an identifier bound there is Msg 13920, as is a source joined to its neighbor.

The refusals come in real's order (probed 2026-10-11 against SQL Server 2025): each hop binds its edge before its two nodes, and the first hop to fail ends the binding, so a later hop's misbound names go unreported; a derived table, view or CTE real can't read through — one with `TOP`, `DISTINCT`, `GROUP BY`, a `UNION`, a second source or no table at all — is Msg 13940 once per position, its edges ahead of its nodes, after the rest; and the joined-source check runs ahead of a name that binds nowhere.
A derived table on an `APPLY`'s right side is Msg 13920 at state 3 and the check goes on to the next name, while any other joined source's state 1 ends it.
Every MATCH refusal is a compile error, so one met as a statement over tables its own batch created runs ends the batch (`Simulation.IsDeferredCompileError`).
Each hop desugars into equalities: over two base tables `e.from_obj_id = <node table's object id>` and `e.from_id = n.graph_id`, which the comma-join rewrite hands to the hash join like any written equi-join; over a derived table, view or CTE, the same pair read off the hidden identity columns its body carries (below), else the rendered `$from_id = $node_id` text.

A node table is read as an **outer join** from the first edge endpoint naming it, as real NULL-extends a node its table no longer holds — or a row-level security filter hides — rather than dropping the edge's row, its `$node_id` NULL too; every other endpoint naming the node compares with that first one, so `n1-(e)->n2-(f)->n3` still joins `e` to `f` through a missing `n2` (probed 2026-10-16 against SQL Server 2025).
`ApplyMatchOuterNodes` moves each such node behind the query's other sources as a `LEFT JOIN` on its anchor, ahead of the paths, and switches each MATCH to that form (`MatchScope.OuterNodes`); a node a derived edge, a `LAST_NODE`, an enclosing query, a path's start or a `MERGE` reaches keeps the inner equalities, and so does a derived table, view or CTE standing for a node, whose filter drops the edge's row as real's does.

A derived table, view or CTE whose body reads one node or edge table — or a body that does in turn — without a row limit, `DISTINCT`, aggregate or set operation carries that table's identity as hidden trailing columns (`Selection.AppendGraphIdentityColumns`: the graph id, or the edge's two endpoint pairs), so a `MATCH` reads through a body that projects no pseudo-column, or projects one under another name, as real's does (probed 2026-10-16 against SQL Server 2025).
The columns ride only a batch whose text names `MATCH` (`ParserContext.GraphIdentityPassthrough`) and a view body such a batch's reference parses, and like a browse statement's hidden columns they stay out of `SELECT *` and a declared column list.
The predicate object keeps its desugared conjuncts behind `CollectConjuncts`, which is all the planner sees.

Under a MATCH, a bare `SELECT *` expands the sources MATCH doesn't name first, in FROM order, then its edges, then its nodes, each in pattern order with every hop read from its `from` end.

In a `MERGE`'s `ON`, `MATCH` binds the target and the `USING` side's sources (probed 2026-10-11 against SQL Server 2025).
The `USING` side may be a table source with joins — `USING a JOIN b ON …`, `USING a CROSS JOIN b`, `USING (a CROSS JOIN b)` — whose sources' columns read under their own qualifiers (`Simulation.MergeSourceOrdinal`), a bare name two of them answer to being Msg 209; a pseudo-column reads through a table there but not through a derived table, view or CTE (Msg 207).
Its nodes may be inner- or cross-joined, but an outer-joined node, and an edge joined to anything, is Msg 13920; the `MATCH` may sit under `OR` or `NOT` there.
A target alias spelled like the target's own name is an alias still (`MERGE K k USING …`).

## SHORTEST_PATH

`MATCH(SHORTEST_PATH(start(-(edge)->node …)+))`, the `<-(edge)-` direction, and the `{1, n}` quantifier, over edges and nodes marked `FOR PATH` in the FROM clause, with graph path aggregates — `STRING_AGG`, `COUNT`, `SUM`, `AVG`, `MIN`, `MAX`, `LAST_VALUE` followed by `WITHIN GROUP (GRAPH PATH)` — reading the path, and `LAST_NODE(node)` continuing an ordinary pattern from the path's end.
A recursive section may repeat a chain of hops (`n1(-(e1)->n2-(e2)->n3)+`), through other node tables too; one repetition is one step of the path, which each aggregate reads once, and the walk reaches the section's last node.
A step repeats only from a node of the start's own table, as real's recursion filters it, so a section ending in another table is one step deep (probed 2026-10-11 against SQL Server 2025).

A WHERE may hold several `SHORTEST_PATH`s, in one MATCH or several; each is a path source of its own, and `LAST_NODE(x) = LAST_NODE(y)` joins two paths' ends on their node identity.
A path may start at another's last node (`SHORTEST_PATH(LAST_NODE(n2)(-(e2)->n4)+)`), whichever the MATCH writes first.
An aggregate reads one path: one reading two is Msg 13953.
`LAST_NODE` names a path's last node and nothing else, refused with real's own number and state per position (Msg 13962 at a hop's start or end, at a path's start, on an equality's left; Msg 13965 on its right and for a path's start written without it), Msg 13964 joins two different tables' ends, and a recursive section's node named outside it without `LAST_NODE` is Msg 13906 — every one probed 2026-10-11 against SQL Server 2025.
Inside a recursive section a hop's node is a bare name (`LAST_NODE` there is Msg 102), and one node alias serves one section (Msg 13950).
A MATCH holding only `SHORTEST_PATH`s may sit under `OR`, which leaves the path applied and the MATCH true.
Under a `NOT` — any number of them — real applies none of its paths: the `FOR PATH` sources read as ordinary ones and the predicate leaves only each hop's edge keeping to its two node tables, un-negated (`MatchPredicate.WithdrawPaths`), so a graph path aggregate over it is Msg 13952 (probed 2026-10-16 against SQL Server 2025).
A query with no other MATCH then takes its `FOR PATH` sources as ordinary ones, as one with no MATCH at all does; beside an ordinary MATCH they are Msg 13949.

A start, edge or node may be a derived table, view or CTE real reads through, `FOR PATH` written after its closing parenthesis or name: the walk reads its rows, so a filtered edge source leaves its edges out, and a filtered node source's nodes are walked through but never reported.
Such a source has to project the pseudo-column its role reads (`$node_id`, `$from_id` and `$to_id`) by its own name.
The result is one row per node the start reaches, the start itself included when a cycle leads back to it, carrying the path to it.
The walk follows edges whatever their endpoints hold: an edge into a node its table no longer holds leads on, the node reading NULL to the aggregates, and as the path's last node it is a row whose columns are NULL but for its graph id, which a `LAST_NODE` hop reads on.
A node its table no longer holds starts a path too, as real starts one from every node the first hop's edges leave (walking backwards, enter), its columns and `$node_id` all NULL (probed 2026-10-16 and 2026-10-17 against SQL Server 2025: a deleted `c` with `c->d` reports `NULL|d`).
`StartPathsFromMissingNodes` adds those starts as a source `FULL JOIN`ed to the start's node table on a false condition, whose rows the path reads its start's graph id from, moving the start ahead of the comma-listed sources before it, which real crosses with every missing start, and reading the first hop's edges however their source is, a view included.
A start read through a view, a CTE or a derived table starts from no missing node on real (probed 2026-10-17 against SQL Server 2025), and none here.

`ApplyShortestPath` runs once the WHERE is parsed: each path's `FOR PATH` sources give way to one lateral source applied after every other source, whose rows the breadth-first walk produces per start row — the reached node's columns, its identity, then one column per graph path aggregate, all hidden, so `SELECT *` expands the other sources only.
Each aggregate is bound to its column, and computes its value from an ordinary `Aggregator` fed the path's steps in order, so the result types are the ordinary aggregates' (`LAST_VALUE`'s is its operand's).
Over base tables the walk reads a snapshot of each hop's edge adjacency and node rows keyed on the heaps' mutation generations, so the start rows of one query, and repeated executions of a cached plan, share one read until a table changes; a derived source's rows are read per start row.
A `SHORTEST_PATH` in a subquery is refused, as real refuses it; a derived table or CTE may hold one.
A path column outside a graph path aggregate is Msg 13961 for every one, reported clause by clause — `ORDER BY`, `GROUP BY`, `HAVING`, `WHERE`, then the select list — each clause's in the order written (probed 2026-10-11 against SQL Server 2025).

Each depth's newly reached nodes come out in graph id order, which is the order real's plan writes them, so a query from one start row returns real's rows in real's order.
Real settles each depth in a `DISTINCT` sort over the candidates, the frontier walked in graph id order and each node's edges in scan order; the simulator keeps the first candidate, which is the path real reports when the depth reaches one node by several (a diamond's `p;q;p>t`) and for the depth's lowest node by graph id, but not always for the others (see [Divergences](#divergences)).

## Divergences

- Among several shortest paths of one length, the simulator reports the first one found; real's distinct sort keeps a duplicate that is neither the first nor the last in any order a probe of 2026-10-11 against SQL Server 2025 could fit (no heap, quick or merge sort variant over 136 tie shapes), so where a depth reaches more than one node, real can name another path to any but its lowest (`f>c>a;g>e>b` where the simulator says `f>c>a;f>c>b`).
- An unordered `SHORTEST_PATH` query over several start rows returns each start's rows together; real's returns them depth by depth across every start.
- Of real's several errors for one misused `FOR PATH` source (Msg 13949 then Msg 4104 and Msg 13952), only the first is raised; a `GROUP BY` over a graph path aggregate names the aggregate's column in its Msg 8120 where real names an internal one.
- The walk over base tables reads their current rows without the reading session's locks or row versions.
- Several `CONNECTION` constraints in one `ALTER TABLE … ADD` are bound and validated one at a time, where real binds them all first: a later constraint's missing table is real's Msg 13931 where the simulator raises the earlier one's Msg 547 against the existing edges.

## Not modeled yet

- **`NOT MATCH(SHORTEST_PATH(… {1, n}))`**: real fails the batch with an error carrying no message (sqlcmd reports SqlState 42000 alone); the simulator reads it as the `+` form under a `NOT` (probed 2026-10-16 against SQL Server 2025).
