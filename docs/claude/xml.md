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

`Parser/Selection.ForXml.cs` — the trailing `FOR XML { RAW[('elem')] | AUTO | PATH[('row')] } [, ELEMENTS [XSINIL|ABSENT]] [, ROOT[('name')]]` clause, parsed in the same `SELECT`-tail slot as FOR JSON (`Selection.ParseOptionalForXml` runs right after `ParseOptionalForJson`; a non-XML `FOR` restores the cursor for the downstream Msg 102).
Mirrors the FOR JSON shape: a trailing-clause parser + a `StringBuilder` serializer over `SqlValue` rows.
The result is a single row, one column named `XML_F52E2B61-18A1-11d1-B105-00805F49916B`, typed `xml`.
An **empty input rowset yields NULL** (zero result rows), matching real.
Real chunks large XML across ~2033-char rows; the simulator returns the whole fragment in one row (documented approximation, shared with FOR JSON).

### Modes

- **RAW** — one `<row …/>` per row, attribute-centric by default; `RAW('elem')` renames the row element.
  `RAW, ELEMENTS` switches to element-centric (`<row><col>v</col></row>`).
  An unnamed column raises **Msg 6809**; a binary column raises **Msg 6829** (needs the unmodeled BINARY BASE64 option).
- **AUTO** — flat single-source only: the row element is named after the table/alias (`<t id="1"/>`), attribute-centric or `ELEMENTS`; unnamed column → Msg 6809, binary column → **Msg 6830**.
  Join-nesting (a secondary table nested under the first) raises `NotSupportedException` (use PATH).
- **PATH** — always element-centric; the column alias drives node placement (compiled once into a shared per-row element template, `ForXmlElement`):
  - `[@x]` → attribute `x` on the row element; `[name]` → child element; `[parent/child]` → nested elements at arbitrary depth (contiguous same-prefix steps share the parent).
  - `[text()]` / an **unnamed** column → the row element's text content; `[data()]` → text content, but adjacent `data()` atomic values are space-separated (`10 30 50`) where `text()` concatenates (`123`).
  - Consecutive same-name element columns concatenate their text into one element (`[x],[x]` → `<x>1020</x>`).
  - `PATH('')` suppresses the row wrapper (bare elements at document level); an attribute column under `PATH('')` raises **Msg 6864**.
  - An attribute column after a non-attribute sibling at the same level raises **Msg 6852**.

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

### Deferrals

EXPLICIT mode, the `TYPE` option (typed-node embedding — the untyped escaped-text nesting is real's default and works), AUTO join-nesting, `BINARY BASE64`/`HEX` / `XMLSCHEMA` / `WITH NAMESPACES`, and PATH node functions beyond `text()`/`data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`) all raise `NotSupportedException` (or the noted Msg).
See [`backlog.md`](backlog.md).

## Known gaps

- **`.modify()`** XML-DML (`insert` / `replace value of` / `delete`) + its `UPDATE … SET` statement integration.
- **XQuery features beyond the path subset** the evaluator models (FLWOR, comparison / boolean / arithmetic operators, value predicates like `[@x="1"]`, element constructors).
- **XSD validation** against `xml(schema_collection)` bindings.
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
