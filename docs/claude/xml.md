# `xml` data type + XML schema collections + XML methods + XML indexes

Skip-with-diagnostic for queries. DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship; query-time XPath / XQuery methods raise `NotSupportedException` at execute.

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

## XML method execution rejection

`Parser/Expressions/XmlMethodCall.cs` — instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`).

- **Parses cleanly** so CREATE VIEW / CREATE PROCEDURE bodies that reference XML methods can be stored verbatim.
- **Runtime** raises `NotSupportedException` with `"XML instance method '.NAME()' is not modeled."`
- **Static result-type inference still applies** so projection-schema resolution works at the parser level: `.exist()`→bit, `.value()`→nvarchar(MAX) stub, others→xml.

## Catalog views in `BuiltInResources.cs`

**`sys.xml_schema_collections`** (6-col, probe-confirmed): `xml_collection_id` / `schema_id` / `principal_id` (NULL — AUTHORIZATION clause not modeled) / `name` / `create_date` / `modify_date`.

**`sys.xml_indexes`** (9-col probe-derived subset; real surface is 26 cols): `object_id` / `name` / `index_id` / `type` (=3) / `type_desc` (`XML`) / `using_xml_index_id` (NULL for primary) / `secondary_type` (char(1): `P`/`V`/`R`) / `secondary_type_desc` / `is_primary_key` (always false).

## Known gaps

- **XPath / XQuery evaluation pipeline** (`.value` / `.nodes` / `.query` / `.exist` / `.modify`).
- **XSD validation** against `xml(schema_collection)` bindings.
- **`FOR XML`** query-output clause.
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
