# `xml` data type + XML schema collections + XML methods + XML indexes

DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship.
`.value()` / `.nodes()` / `.query()` / `.exist()` execute against a bundled XQuery-subset evaluator (`Storage/XmlQueryEngine.cs`); `.modify()` (XML-DML) remains skip-with-diagnostic (`NotSupportedException` at execute).

## Storage

**`XmlSqlType`** (singleton in `Storage/XmlType.cs`): `SqlServerName="xml"`, `SystemTypeId=241`, `IsLob=true`.
Payload stored identically to `nvarchar(MAX)` (raw UTF-16 LE bytes).
Type identity preserved through `sys.columns.user_type_id` / `sys.types`.

**`XmlSchemaCollection`** carries id + name + schema_id + nullable principal_id + xsdText + create_date / modify_date.

**`Schema.XmlSchemaCollections`** — per-schema dict; shares the type-namespace with `TableTypes` / `AliasTypes` (Msg 219 on duplicate).

**`Database.AllocateXmlCollectionId`** seeds at 65536 (probe-confirmed).

**`HeapColumn.XmlSchemaCollection`** — nullable ref linking xml columns to their collection.
Metadata only; the simulator does **not** validate xml payloads against the XSD.

**`HeapTable.XmlIndexes`** — `List<XmlIndex>`.
`XmlIndex` carries name + columnOrdinal + isPrimary + `UsingPrimaryIndexName` (for secondary) + nullable `SecondaryType` (PATH / VALUE / PROPERTY) + ObjectId + `InternalTableObjectId` (allocated per **primary** index at CREATE — see the internal node-table surface below; 0 for secondaries).

## Parsers — `Simulation/Simulation.Xml.cs`

```
CREATE XML SCHEMA COLLECTION [schema.]name AS '<xsd:schema>…'
DROP XML SCHEMA COLLECTION [schema.]name

CREATE PRIMARY XML INDEX name ON table(col) [WITH (…)]
CREATE XML INDEX name ON table(col)
    USING XML INDEX primary_name
    FOR {PATH | VALUE | PROPERTY}
    [WITH (…)]
```

- XSD text stored verbatim.
  No XSD parsing; AW's 6 schema-collection payloads (with embedded namespaces, complex types, restrictions, sequences) round-trip as opaque strings.
- `WITH (…)` trailing options block parse-and-discards via `SkipBalancedParens`.
- xml column-type positions: `xml`, `xml(name)`, `xml(CONTENT name)`, `xml(DOCUMENT name)` — the `CONTENT` / `DOCUMENT` discriminator parse-and-discards.
  Detection happens in `ParseOneColumnIntoLists` via a peek (`PeekIsXmlSchemaArgument`) that distinguishes the schema-collection-name form from a length / MAX spec; matched only when the bare 1-part type name is `xml`.
  Unknown schema collection → Msg 208.
- Statement dispatch: `Xml` added to `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Xml }` and `ReservedKeyword { Keyword: Keyword.Primary }` (the PRIMARY XML INDEX form).
  `SCHEMA` is reserved, so the sub-keyword check uses `Keyword.Schema`.
  `COLLECTION` is a bare identifier.

## XML method execution

`Parser/Expressions/XmlMethodCall.cs` — instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`).

- **`.value(xquery, sqltype)`** — evaluates `xquery` against the target xml via `XmlQueryEngine.EvaluateScalar`, then casts the selected node's string value to `sqltype` through `Cast.ApplyCoercion`.
  The type literal (e.g. `'nvarchar(30)'`, `'money'`, `'decimal(9, 4)'`, `'integer'`) is resolved at parse time via `SqlType.GetByName`; `integer` maps to `int`.
  Empty selection → typed NULL.
  `GetSqlType` returns the resolved target type, so projection / view-output schemas are exact (not the old nvarchar(MAX) stub).
- **`.nodes(xquery)`** — rowset-producing, valid only in a FROM / APPLY source position.
  `Selection.cs::ParseLateralFromSource` detects the `xmlexpr.nodes(...) [AS] alias(column)` shape (the parsed object name's leaf is `nodes` with a following `(`), re-parses the target as an expression, and builds a correlated single-column (`xml`) lateral plan (`Selection.XmlNodes.cs`).
  Each row's value is the serialized outer XML of one matched node, so a downstream relative `.value()` / nested `.nodes()` re-parses the fragment.
  Reaching `XmlMethodCall.Run` for `.nodes()` means it appeared in scalar position — unsupported.
- **`.exist(xquery)`** — returns `bit`: 1 when the path selects ≥1 node (true boolean / non-empty string / non-zero number also count), 0 otherwise, NULL when the instance is NULL (`XmlQueryEngine.EvaluateExists`).
- **`.query(xquery)`** — returns `xml`: the serialized concatenation of the matched nodes in document order, empty string when nothing matches, NULL when the instance is NULL (`XmlQueryEngine.EvaluateQuery`, reusing `EvaluateNodes`).
  Output serialization is .NET `XPathNavigator.OuterXml`, which may differ from SQL Server's normalization (namespace-declaration placement, self-closing-tag spacing).
- **`.modify()`** — XML-DML; parses cleanly (CREATE VIEW / PROCEDURE bodies store verbatim) but `Run` raises `NotSupportedException`.
  It's statement-level (`UPDATE … SET col.modify(…)`), a separate sublanguage from the path-evaluation methods.
- `GetSqlType`: `.value()`→resolved target type, `.exist()`→bit, `.nodes()` / `.query()` / `.modify()`→xml.
- A non-literal `xquery` / type argument raises `NotSupportedException` (dynamic XQuery isn't modeled).

## XQuery-subset evaluator — `Storage/XmlQueryEngine.cs`

Backs `.value()` / `.nodes()` / `.query()` / `.exist()`.
Covers the subset SQL Server's sample databases (AdventureWorks / WideWorldImporters) exercise:

- **Prolog**: leading `declare default element namespace "uri";` (zero or one) and `declare namespace prefix="uri";` (zero or more).
- **Path body**: absolute (`/Resume/Name/Name.Prefix`) and relative (`Address/Addr.Type`) child steps; prefixed (`act:number`) and unprefixed names; element names containing `.`; attribute axis (`@LocationID`); `text()` node test; `string(.)`; parenthesized sub-path with a positional predicate (`(…)[1]`) and trailing continuation (`(act:telephoneNumber)[1]/act:number`).

**Mechanism**: the body is translated to XPath 1.0 and evaluated through `XPathNavigator`.
Each name test becomes a `*[local-name()='…' and namespace-uri()='…']` (attributes: `@*[…]`) predicate, so the default-element-namespace binding — which XPath 1.0 has no syntax for — is resolved at translation time without a namespace manager.
Attributes are never in the default element namespace (XQuery's scoping rule).
The navigator is positioned on the document element of the parsed input, so a relative path resolves against that element while an absolute path resolves from the document root — the dual behavior `.nodes()`-serialized node references rely on.
`string(.)` is special-cased ahead of translation (its XQuery `[1]` postfix has no XPath 1.0 form).

### Divergences

- Only the path subset above is modeled.
  FLWOR, arithmetic / comparison / boolean XQuery operators, `local-name()`-style functions in the source text, and constructors are not — they'd surface as malformed XPath or wrong results rather than a clean error.
- The evaluator parses its input as a **document**, so a multi-root fragment (`'<a/><b/>'`, or the typical `FOR XML …, TYPE` result) raises `XmlException` where real accepts it — real's `xml` is CONTENT-typed and admits several top-level elements.
- `.value()` casts go through the standard string→type coercion (`casting.md`'s flexible string→date-like parser), so the AdventureWorks `vJobCandidateEducation` / `vJobCandidateEmployment` / `vPersonDemographics` views — which wrap `.value()` date strings in `CONVERT(datetime, …, 101)` — now resolve.

## Catalog views in `BuiltInResources.cs`

**`sys.xml_schema_collections`** (6-col, probe-confirmed): `xml_collection_id` / `schema_id` / `principal_id` (NULL — AUTHORIZATION clause not modeled) / `name` / `create_date` / `modify_date`.

**Internal node-table + statistics surface (for DacFx export).**
DacFx's XML-index reverse-engineering query doesn't read `sys.xml_indexes` alone — it INNER JOINs `sys.index_columns` (one row per XML index: the indexed xml column, `index_column_id` 1, `key_ordinal` 0) *and* an internal "node table" per **primary** index (`sys.objects` type `IT` / `INTERNAL_TABLE`, named `xml_index_nodes_<tableObjectId>_<primaryIndexObjectId>`, parent = base table, `schema_id` = sys, `is_ms_shipped` = 1) joined to `sys.stats` (one row per XML index, `name` = the index name, on the node table's `object_id`; a primary owns its node table, secondaries share their primary's — `stats_id` sequential within a node table).
Modeled from probe (SQL Server 2025); without them DacFx NREs client-side (`SqlFullTextIndexColumnSpecifierPopulator`-style orphaned-parent) and emits no `SqlXmlIndex` elements.
A primary XML index allocates its node-table object id at CREATE (`XmlIndex.InternalTableObjectId`); `EnumerateXmlIndexStats` resolves each index (primary or secondary) to its owning node table.
This is the only place the simulator surfaces a type-`IT` object.

**`sys.xml_indexes`** (full 26-col shape, probe-confirmed against SQL Server 2025 WWI).
The load-bearing core keeps its original positions: `object_id` / `name` / `index_id` / `type` (=3) / `type_desc` (`XML`) / `using_xml_index_id` (NULL for primary) / `secondary_type` (char(1): `P`/`V`/`R`) / `secondary_type_desc` / `is_primary_key` (always false).
Appended after them (real orders these interleaved; the simulator appends since consumers read by name): `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` (false) / `is_unique_constraint` (false) / `fill_factor` (0) / `is_padded` (false) / `is_disabled` (false) / `is_hypothetical` (false) / `is_ignored_in_optimization` (false) / `allow_row_locks` (true) / `allow_page_locks` (true) / `has_filter` (false) / `filter_definition` (NULL) / `xml_index_type` (0 primary, 1 secondary) / `xml_index_type_description` (`PRIMARY_XML` / `SECONDARY_XML`) / `path_id` (0) / `auto_created` (false).
Values are the fresh-index defaults.
DacFx's XML-index reverse-engineering query reads the `fill_factor` / `is_padded` / `allow_*_locks` / `is_disabled` / `xml_index_type` / `path_id` tail.

**`XML_SCHEMA_NAMESPACE(relational_schema, collection_name)`** (`Parser/Expressions/XmlSchemaNamespaceFunction.cs`): returns the collection's XSD as `xml` — the simulator returns the raw `CREATE XML SCHEMA COLLECTION … AS '…'` source text, where real reconstructs a normalized XSD from component metadata (divergence).
Unresolved pair → Msg 6314 at execution (probe-confirmed wording incl. the space before the colon; real raises 6314 even for the built-in `sys` collection, which the simulator doesn't register — the natural miss matches).
NULL argument → Msg 8116.
The three-argument namespace-filtering form → `NotSupportedException`.
DacFx's bacpac export calls this per user collection while scripting `sys.xml_schema_collections`.

## FOR XML result serialization

`Parser/Selection.ForXml.cs` — the trailing `FOR XML { RAW[('elem')] | AUTO | PATH[('row')] } [, ELEMENTS [XSINIL|ABSENT]] [, TYPE] [, ROOT[('name')]]` clause, parsed in the same `SELECT`-tail slot as FOR JSON (`Selection.ParseOptionalForXml` runs right after `ParseOptionalForJson`; a non-XML `FOR` restores the cursor for the downstream Msg 102).
Mirrors the FOR JSON shape: a trailing-clause parser + a `StringBuilder` serializer over `SqlValue` rows.
The option list is order-free (`, TYPE, ROOT('r')` and `, ROOT('r'), TYPE` are the same clause).
Real chunks large XML across ~2033-char rows; the simulator returns the whole fragment in one row (documented approximation, shared with FOR JSON).

### The result column, and the `TYPE` option

| | column name | column type | empty input rowset |
|---|---|---|---|
| without `TYPE` | `XML_F52E2B61-18A1-11d1-B105-00805F49916B` | `nvarchar(max)` | zero rows |
| with `TYPE` | `""` (unnamed) | `xml` | **one row, NULL** |

Probe-confirmed against SQL Server 2025 through `GetSchemaTable` over SqlClient.
The row-count asymmetry is real's: a top-level untyped `FOR XML` over no rows returns an empty result set, while the typed form returns one NULL `xml` value — as a scalar subquery both read SQL NULL either way.

`TYPE` is what makes a **nested** `FOR XML` embed as nodes rather than escaped text, and that falls out of the column *type* rather than any marker: the serializer emits any `xml`-typed value verbatim, so a stored `xml` column, a `CAST(… AS xml)`, and a `(SELECT … FOR XML …, TYPE)` subquery all embed as markup while every other type is escaped.

```
select p.id, (select c.cnm from cc c where c.pid = p.id for xml path('c'), type) as kids
from pp p for xml path('p')
    → <p><id>1</id><kids><c><cnm>a1</cnm></c></kids></p>

-- the same without TYPE
    → <p><id>1</id><kids>&lt;c&gt;&lt;cnm&gt;a1&lt;/cnm&gt;&lt;/c&gt;</kids></p>
```

An **unnamed** nested TYPE column (the `(SELECT … FOR XML PATH, TYPE)` idiom with no alias) inlines its child nodes directly into the parent element, since PATH maps an unnamed column to the row element's content.
An `xml`-typed column in an **attribute** position raises **Msg 6851** in PATH (an attribute can't hold nodes); in RAW / AUTO's attribute-centric default it silently becomes a child element named after the column instead.

Real's untyped result column reports `ntext` (max length 1073741823) in its wire metadata while typing the same expression `nvarchar(max)` in subquery position; the simulator carries one type for both and picks `nvarchar(max)`, so the string-building idioms real supports (`STUFF((SELECT … FOR XML PATH('')), 1, 1, '')`, concatenation) work rather than tripping the legacy-LOB restrictions.

### Modes

- **RAW** — one `<row …/>` per row, attribute-centric by default; `RAW('elem')` renames the row element.
  `RAW, ELEMENTS` switches to element-centric (`<row><col>v</col></row>`).
  An unnamed column raises **Msg 6809**; a binary column raises **Msg 6829** (needs the unmodeled BINARY BASE64 option).
- **AUTO** — one element per FROM source, nested (see below); the row element is named after the table/alias (`<t id="1"/>`), attribute-centric or `ELEMENTS`; unnamed column → Msg 6809, binary column → **Msg 6830**, no FROM clause at all → **Msg 6800**.
- **PATH** — always element-centric; the column alias drives node placement (compiled once into a shared per-row element template, `ForXmlElement`):
  - `[@x]` → attribute `x` on the row element; `[name]` → child element; `[parent/child]` → nested elements at arbitrary depth (contiguous same-prefix steps share the parent).
  - `[text()]` / an **unnamed** column → the row element's text content; `[data()]` → text content, but adjacent `data()` atomic values are space-separated (`10 30 50`) where `text()` concatenates (`123`).
  - Consecutive same-name element columns concatenate their text into one element (`[x],[x]` → `<x>1020</x>`).
  - `PATH('')` suppresses the row wrapper (bare elements at document level); an attribute column under `PATH('')` raises **Msg 6864**.
  - An attribute column after a non-attribute sibling at the same level raises **Msg 6852**.

### AUTO nesting (shared with `FOR JSON AUTO`)

Each FROM source becomes one nesting level, built in `Parser/Selection.AutoNesting.cs` (`BuildAutoLevels`) from the per-column source binding `Selection.AutoColumnSource` / `AutoSourceNames` that `BuildSelectionCore` records — `Selection.ForXml.cs` renders the levels as nested elements, `Selection.ForJson.cs` as nested arrays, off the same level model.
The rules are heuristic and were probed one at a time against SQL Server 2025; the whole matrix below is byte-identical between the simulator and real.

| rule | behavior |
|---|---|
| what makes a level | a FROM source contributing at least one **bare column reference** to the select list; a source no column reads contributes no level |
| level order | order of each source's **first** column in the select list — not FROM order — and always a linear chain, whatever the join topology (two tables both joined to the first still nest one inside the other) |
| level name | the alias, else the object name **as written** (`FROM dbo.t` → `<dbo.t>`, `FROM dbo.t AS x` → `<x>`) |
| column placement | a column joins its own source's level even when another table's columns intervene, keeping its relative order there — so `p.id, c.cnm, p.nm` puts `id` and `nm` on `p` and `cnm` on the nested `c` |
| computed columns | any expression that isn't a bare column reference — including a CAST or function call **over another table's column**, and aggregates — joins the level of the nearest *preceding* table column; one that precedes every table column joins the first level, ahead of that level's own columns |
| all-computed projection | one level, named after the first FROM source (`select 1 as a from t` → `<t a="1"/>`) |
| no FROM clause | **Msg 6800** (FOR XML) / **Msg 13600** (FOR JSON) |
| row grouping | an outer level collapses **consecutive** rows whose values for that level are all equal (two NULLs count as equal); the same values after an intervening different row open a **new** element |
| innermost level | never collapses — one element / object per row, even for two identical rows |
| `xml` column in a level | that level never collapses at all: SQL Server can't compare `xml`, so every row opens a fresh element (`AutoLevel.AlwaysRestarts`) |
| NULL-filled outer-join side | still emits its element (`<c/>`) / object (`[{}]`) |

`ELEMENTS`, `ROOT`, `XSINIL` and the value formatting apply per level unchanged; the `xmlns:xsi` declaration lands on the outermost element (or the ROOT).

Divergences:

- A **set-operation** result raises `NotSupportedException` (no per-column source binding survives the union); real names every element after the *first branch's* table.
- A source with no written object name (derived table, CTE, table variable, `OPENJSON` / `STRING_SPLIT`) is named after its alias.
  That matches real for derived tables and CTEs; for the rowset functions real instead raises Msg 6800 (they aren't tables), which the simulator doesn't.
- Grouping compares values through `SqlValue.Equals`, so it is **collation-aware** — under a case-insensitive collation `'A'` and `'a'` group together.

### Options

- `ELEMENTS` → element-centric (RAW/AUTO; a no-op on always-element-centric PATH).
  `ELEMENTS XSINIL` → NULL columns emit `<col xsi:nil="true"/>` and the `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"` declaration is hoisted to the `ROOT` element when present, else repeated on each top-level element (each row wrapper, or each bare element under `PATH('')`).
  `ELEMENTS ABSENT` (default) → NULL elements omitted; NULL attributes are always omitted.
- `ROOT` → wrap in `<root>…</root>` (default name `root`); `ROOT('rows')` renames; `ROOT('')` raises **Msg 6861**.

### Value formatting + escaping (probe-confirmed, SQL Server 2025)

Numeric/date formatting matches FOR JSON (scientific `float`/`real`, the all-zero-fraction drop) **except** `bit` → `1`/`0` (not `true`/`false`), `uniqueidentifier` uppercases, `binary`/`varbinary` base64-encodes (PATH only — RAW/AUTO raise 6829/6830), and values are XML-escaped rather than JSON-escaped.
Escaping is position-dependent:

| position | escaped |
|---|---|
| element text | `&`→`&amp;`, `<`→`&lt;`, `>`→`&gt;`, CR→`&#x0D;` (`"` and `'` stay literal) |
| attribute value | the above plus `"`→`&quot;`, tab→`&#x09;`, LF→`&#x0A;` (`'` stays literal) |

### Not modeled yet

EXPLICIT mode, `BINARY BASE64`/`HEX` / `XMLSCHEMA` / `WITH NAMESPACES`, and PATH node functions beyond `text()`/`data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`) all raise `NotSupportedException`.
**Msg 6819** — real rejects a `FOR XML` clause inside an `INSERT … SELECT` (`The FOR XML clause is not allowed in a INSERT statement.`); the simulator accepts it.
One-row chunking is the shared approximation noted above.

**XML-name encoding** — RAW / AUTO run every element and attribute name through SQL Server's `_xHHHH_` escaping so the output is well-formed whatever the SQL identifier was, while PATH rejects an unusable name outright.
Probed against SQL Server 2025:

| written name | RAW / AUTO output |
|---|---|
| `[a b]` | `a_x0020_b` |
| `[1a]` (leading digit) | `_x0031_a` |
| `[a$b]` | `a_x0024_b` |
| `[a_x0020_b]` (already-escaped text) | `a_x005F_x0020_b` |
| `[a-b]` / `[a.b]` / `[é]` | unchanged (valid XML name characters) |
| `FROM #tmp` / `FROM @v` (AUTO element name) | `_x0023_tmp` / `_x0040_v` |

The simulator emits every name verbatim, so `SELECT … FROM #tmp FOR XML AUTO` yields `<#tmp …>` — not well-formed XML.
PATH's counterpart is **Msg 6850** (`Row name 'r x' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.`, with `Column name` / `ROOT name` variants), which the simulator doesn't raise either.

See [`backlog.md`](backlog.md).

## Leading byte-order mark

A string that becomes `xml` loses a leading U+FEFF, wherever the conversion happens — a literal INSERT, a parameter, an explicit `CAST`, `SqlBulkCopy` and a TVP row all behave the same, probe-confirmed against SQL Server 2025 (2026-07-30).
The same mark in an `nvarchar` column survives, so this belongs to the type conversion rather than to any input path; the strip therefore lives in `SqlValue.FromXml`, which every xml value funnels through.
A mark that isn't leading is content and stays.

## Known gaps

- **`.modify()`** XML-DML (`insert` / `replace value of` / `delete`) + its `UPDATE … SET` statement integration.
- **XQuery features beyond the path subset** the evaluator models (FLWOR, comparison / boolean / arithmetic operators, value predicates like `[@x="1"]`, element constructors).
- **XSD validation** against `xml(schema_collection)` bindings.
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
