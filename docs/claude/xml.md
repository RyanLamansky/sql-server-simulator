# `xml` data type + XML schema collections + XML methods + XML indexes

DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship. `.value()` / `.nodes()` / `.query()` / `.exist()` execute against a bundled XQuery-subset evaluator (`Storage/XmlQueryEngine.cs`); `.modify()` (XML-DML) remains skip-with-diagnostic (`NotSupportedException` at execute).

## Storage

**`XmlSqlType`** (singleton in `Storage/XmlType.cs`): `SqlServerName="xml"`, `SystemTypeId=241`, `IsLob=true`. Payload stored identically to `nvarchar(MAX)` (raw UTF-16 LE bytes). Type identity preserved through `sys.columns.user_type_id` / `sys.types`.

**`XmlSchemaCollection`** carries id + name + schema_id + nullable principal_id + xsdText + create_date / modify_date.

**`Schema.XmlSchemaCollections`** — per-schema dict; shares the type-namespace with `TableTypes` / `AliasTypes` (Msg 219 on duplicate).

**`Database.AllocateXmlCollectionId`** seeds at 65536 (probe-confirmed).

**`HeapColumn.XmlSchemaCollection`** — nullable ref linking xml columns to their collection. Metadata only; the simulator does **not** validate xml payloads against the XSD.

**`HeapTable.XmlIndexes`** — `List<XmlIndex>`. `XmlIndex` carries name + columnOrdinal + isPrimary + `UsingPrimaryIndexName` (for secondary) + nullable `SecondaryType` (PATH / VALUE / PROPERTY) + ObjectId.

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

- XSD text stored verbatim. No XSD parsing; AW's 6 schema-collection payloads (with embedded namespaces, complex types, restrictions, sequences) round-trip as opaque strings.
- `WITH (…)` trailing options block parse-and-discards via `SkipBalancedParens`.
- xml column-type positions: `xml`, `xml(name)`, `xml(CONTENT name)`, `xml(DOCUMENT name)` — the `CONTENT` / `DOCUMENT` discriminator parse-and-discards. Detection happens in `ParseOneColumnIntoLists` via a peek (`PeekIsXmlSchemaArgument`) that distinguishes the schema-collection-name form from a length / MAX spec; matched only when the bare 1-part type name is `xml`. Unknown schema collection → Msg 208.
- Statement dispatch: `Xml` added to `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Xml }` and `ReservedKeyword { Keyword: Keyword.Primary }` (the PRIMARY XML INDEX form). `SCHEMA` is reserved, so the sub-keyword check uses `Keyword.Schema`. `COLLECTION` is a bare identifier.

## XML method execution

`Parser/Expressions/XmlMethodCall.cs` — instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`).

- **`.value(xquery, sqltype)`** — evaluates `xquery` against the target xml via `XmlQueryEngine.EvaluateScalar`, then casts the selected node's string value to `sqltype` through `Cast.ApplyCoercion`. The type literal (e.g. `'nvarchar(30)'`, `'money'`, `'decimal(9, 4)'`, `'integer'`) is resolved at parse time via `SqlType.GetByName`; `integer` maps to `int`. Empty selection → typed NULL. `GetSqlType` returns the resolved target type, so projection / view-output schemas are exact (not the old nvarchar(MAX) stub).
- **`.nodes(xquery)`** — rowset-producing, valid only in a FROM / APPLY source position. `Selection.cs::ParseLateralFromSource` detects the `xmlexpr.nodes(...) [AS] alias(column)` shape (the parsed object name's leaf is `nodes` with a following `(`), re-parses the target as an expression, and builds a correlated single-column (`xml`) lateral plan (`Selection.XmlNodes.cs`). Each row's value is the serialized outer XML of one matched node, so a downstream relative `.value()` / nested `.nodes()` re-parses the fragment. Reaching `XmlMethodCall.Run` for `.nodes()` means it appeared in scalar position — unsupported.
- **`.exist(xquery)`** — returns `bit`: 1 when the path selects ≥1 node (true boolean / non-empty string / non-zero number also count), 0 otherwise, NULL when the instance is NULL (`XmlQueryEngine.EvaluateExists`).
- **`.query(xquery)`** — returns `xml`: the serialized concatenation of the matched nodes in document order, empty string when nothing matches, NULL when the instance is NULL (`XmlQueryEngine.EvaluateQuery`, reusing `EvaluateNodes`). Output serialization is .NET `XPathNavigator.OuterXml`, which may differ from SQL Server's normalization (namespace-declaration placement, self-closing-tag spacing).
- **`.modify()`** — XML-DML; parses cleanly (CREATE VIEW / PROCEDURE bodies store verbatim) but `Run` raises `NotSupportedException`. It's statement-level (`UPDATE … SET col.modify(…)`), a separate sublanguage from the path-evaluation methods.
- `GetSqlType`: `.value()`→resolved target type, `.exist()`→bit, `.nodes()` / `.query()` / `.modify()`→xml.
- A non-literal `xquery` / type argument raises `NotSupportedException` (dynamic XQuery isn't modeled).

## XQuery-subset evaluator — `Storage/XmlQueryEngine.cs`

Backs `.value()` / `.nodes()` / `.query()` / `.exist()`. Covers the subset SQL Server's sample databases (AdventureWorks / WideWorldImporters) exercise:

- **Prolog**: leading `declare default element namespace "uri";` (zero or one) and `declare namespace prefix="uri";` (zero or more).
- **Path body**: absolute (`/Resume/Name/Name.Prefix`) and relative (`Address/Addr.Type`) child steps; prefixed (`act:number`) and unprefixed names; element names containing `.`; attribute axis (`@LocationID`); `text()` node test; `string(.)`; parenthesized sub-path with a positional predicate (`(…)[1]`) and trailing continuation (`(act:telephoneNumber)[1]/act:number`).

**Mechanism**: the body is translated to XPath 1.0 and evaluated through `XPathNavigator`. Each name test becomes a `*[local-name()='…' and namespace-uri()='…']` (attributes: `@*[…]`) predicate, so the default-element-namespace binding — which XPath 1.0 has no syntax for — is resolved at translation time without a namespace manager. Attributes are never in the default element namespace (XQuery's scoping rule). The navigator is positioned on the document element of the parsed input, so a relative path resolves against that element while an absolute path resolves from the document root — the dual behavior `.nodes()`-serialized node references rely on. `string(.)` is special-cased ahead of translation (its XQuery `[1]` postfix has no XPath 1.0 form).

### Divergences

- Only the path subset above is modeled. FLWOR, arithmetic / comparison / boolean XQuery operators, `local-name()`-style functions in the source text, and constructors are not — they'd surface as malformed XPath or wrong results rather than a clean error.
- `.value()` casts go through the standard string→type coercion (`casting.md`'s flexible string→date-like parser), so the AdventureWorks `vJobCandidateEducation` / `vJobCandidateEmployment` / `vPersonDemographics` views — which wrap `.value()` date strings in `CONVERT(datetime, …, 101)` — now resolve.

## Catalog views in `BuiltInResources.cs`

**`sys.xml_schema_collections`** (6-col, probe-confirmed): `xml_collection_id` / `schema_id` / `principal_id` (NULL — AUTHORIZATION clause not modeled) / `name` / `create_date` / `modify_date`.

**`sys.xml_indexes`** (9-col probe-derived subset; real surface is 26 cols): `object_id` / `name` / `index_id` / `type` (=3) / `type_desc` (`XML`) / `using_xml_index_id` (NULL for primary) / `secondary_type` (char(1): `P`/`V`/`R`) / `secondary_type_desc` / `is_primary_key` (always false).

## Known gaps

- **`.modify()`** XML-DML (`insert` / `replace value of` / `delete`) + its `UPDATE … SET` statement integration.
- **XQuery features beyond the path subset** the evaluator models (FLWOR, comparison / boolean / arithmetic operators, value predicates like `[@x="1"]`, element constructors).
- **XSD validation** against `xml(schema_collection)` bindings.
- **`FOR XML`** query-output clause.
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
