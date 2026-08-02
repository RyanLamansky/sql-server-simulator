# `xml` data type + XML schema collections + XML methods + XML indexes

DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship.
`.value()` / `.nodes()` / `.query()` / `.exist()` execute against a bundled XQuery-subset evaluator (`Storage/XmlQueryEngine.cs`), and `.modify()` mutates through the same paths (`Parser/XmlDml.cs` + `Parser/XmlDmlParser.cs`).
The separate legacy rowset — [`OPENXML`](#openxml) over a `sp_xml_preparedocument` handle — reads XPath 1.0 through the DOM instead.

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
- **`.modify()`** — the mutator, a separate sublanguage; see [`.modify()` — XML-DML](#modify--xml-dml) below.
  Reaching `XmlMethodCall` for it means it was written in a value position, which is **Msg 8137**.
- `GetSqlType`: `.value()`→resolved target type, `.exist()`→bit, `.nodes()` / `.query()`→xml.
- A non-literal `xquery` / type argument raises `NotSupportedException` (dynamic XQuery isn't modeled).

## XQuery-subset evaluator — `Storage/XmlQueryEngine.cs`

Backs `.value()` / `.nodes()` / `.query()` / `.exist()`, and supplies the prolog parsing + XPath translation `.modify()`'s target paths run through.
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

## `.modify()` — XML-DML

`Parser/XmlDmlParser.cs` parses the sublanguage into a `Parser/XmlDml.cs` statement, and `XmlDml.Apply` runs it over a LINQ-to-XML tree selected through the same `XmlQueryEngine` translation the read methods use.
The `.modify()` argument is a compile-time literal, so the whole statement — path, content constructors, every static check — is parsed once; only the value terms are read per row.

### Where a mutator is legal

Real accepts `.modify()` in exactly two positions, and the simulator parses both away from the expression parser:

| position | parsed by | notes |
|---|---|---|
| `SET @x.modify('…')` | `Simulation.Set.cs::TryParseSetInstanceMember` | assigns the edited instance back to the slot; sets `@@ROWCOUNT` to 1 |
| `UPDATE t SET col.modify('…')` | `Simulation.Update.cs::ParseXmlMutatorSetClause` | desugars to `col = <XmlModify>(col, dml)` |

The desugaring is what makes the UPDATE integration fall out: the expression re-reads the column's **pre-update** value and answers the edited one, so `OUTPUT inserted.col` / `deleted.col`, AFTER triggers, constraint enforcement, the undo log and `@@ROWCOUNT` all see an ordinary new value.
A modify clause composes with ordinary SET clauses in either order (`SET x.modify(…), n = 1` and `SET n = 1, x.modify(…)`), and works through a temp table, a table variable and an updatable view.

Everywhere else:

| shape | error |
|---|---|
| `.modify()` in a select list / predicate / `SET @x = @x.modify(…)` | **Msg 8137** — `Incorrect use of the XML data type method 'modify'. A non-mutator method is expected in this context.` |
| `SET @x.value(…)` / `SET col.query(…)` — a non-mutator in mutator position | **Msg 8113** — `Incorrect use of the XML data type method 'value'. A mutator method is expected in this context.` |
| the target isn't `xml` (`SET @s.modify(…)` on `nvarchar`, `SET n.modify(…)` on `int`) | **Msg 258** sev **15** — `Cannot call methods on nvarchar.` |
| the target column doesn't exist | **Msg 207** |
| a qualified column (`SET t.col.modify(…)`) or a chained call (`SET @x.query('/r').modify(…)`) | **Msg 102** |
| the instance is NULL — an unassigned variable or a NULL cell in an updated row | **Msg 5302** — `Mutator 'modify()' on '@x' cannot be called on a null value.` (the name as written, `@` included for a variable) |

Real reaches Msg 8137 before the SET-option gate, so `.modify()` in a select list reports it even from a session holding `QUOTED_IDENTIFIER` the wrong way; the mutator positions take the gate (**Msg 1934**) like every other XML method, naming the statement's own verb — `SELECT` for `SET @x.modify(…)`, `UPDATE` for the UPDATE form.

### The three statements

```
insert <content> [as first | as last] {into | before | after} <target>
delete <target>
replace value of <target> with <value>
```

**`insert`** — content is one item or a parenthesized sequence.
Item forms: a direct element constructor with arbitrary nesting (`<n><m><o>3</o></m></n>`), a direct comment (`<!-- c -->`) or processing instruction (`<?pi data?>`), the computed `attribute n {…}` / `text {…}` / `comment {…}` / `processing-instruction n {…}` constructors, and a bare `sql:variable("@v")` / `sql:column("c")` carrying `xml`.
An element constructor's `{…}` enclosed expressions are substituted with the value's XML text and escaped by position (element content vs attribute value); `{{` / `}}` are the literal-brace escapes.
A constructor resolves its **name through the prolog** exactly as a path step does — `declare default element namespace "urn:d"; insert <b/>` builds a `urn:d` element, and `declare namespace p="urn:x"; insert <p:b/>` a `urn:x` one (probe-confirmed) — while an already-serialized `sql:variable` / `sql:column` fragment brings its own scope.
The serializer re-declares whatever the insertion point doesn't already bind, so an unqualified constructed element landing under a namespaced parent comes back as `<b xmlns=""/>`, byte-identical to real.
`into` appends (as does `as last`), `as first` prepends, `before` / `after` place a sibling.
An attribute item always attaches to the target element whatever the `as first` / `as last` keyword says.

**`delete`** — no cardinality restriction: every matched node goes, elements / attributes / text alike.
Deleting the top-level element leaves an empty instance (`''`).

**`replace value of`** — writes the target's string value.
The `with` expression is a value: a literal, a `sql:variable` / `sql:column`, or a parenthesized sequence of those, which atomizes to the terms' text joined by a single space.
Replacing a text node's value with the empty string removes the node, so its element comes back self-closing.

Across all three, a path that matches nothing is a **no-op**, not an error.

### Static target and content checks

Real types the target off the path's **shape**, before reading a single node — so `/r/a/text()` is `text *` and refused as a `replace value of` target even when the instance holds exactly one match, while `(/r/a/text())[1]` is `text ?` and accepted.
Only a positional predicate over the *whole* path makes it singular: `(/r/a)[1]/text()` and `/r/a[1]/text()` are both still `text *`.

The messages quote that static type, and the simulator reproduces the notation: `text *` / `text ?`, `element(a,xdt:untyped) *`, `attribute(n,xdt:untypedAtomic) ?`, and for the context node `document { (element(*,xdt:untyped) ? & text ? & comment ? & processing-instruction ?) * }`.

| check | error |
|---|---|
| `insert` target not statically single | **Msg 2226** — `XQuery [modify()]: The target of 'insert' must be a single node, found 'element(a,xdt:untyped) *'` |
| `insert` content is an atomic value | **Msg 2207** — `XQuery [modify()]: Only non-document nodes can be inserted. Found "xs:string".` |
| an attribute item with `before` / `after` | **Msg 2258** — `… The position may not be specified when inserting an attribute node, found 'attribute(n,xdt:untypedAtomic)'` |
| `insert … into` a non-element / non-document | **Msg 2240** — `… The target of 'insert into' must be an element/document node, found 'text ?'` |
| `insert … before/after` an attribute or the document | **Msg 2249** — `… The target of 'insert before/after' must be an element/PI/comment/text node, found '…'` |
| `replace value of` target not statically at-most-one | **Msg 2337** — `… The target of 'replace' must be at most one node, found 'text *'` |
| `replace value of` an untyped element | **Msg 2356** — `… must be a non-metadata attribute or an element with simple typed content, found 'element(a,xdt:untyped) ?'` |
| `replace value of … with <b/>` | **Msg 9310** — `… The 'with' clause of 'replace value of' cannot contain constructed XML.` |
| `replace value of` with no `with` | **Msg 2205** — `XQuery [modify()]: "with" was expected.` |
| `delete .` or a `delete` of an atomic value | **Msg 2264** — `… Only non-document nodes may be deleted, found '…'` |
| the argument isn't XML-DML at all (`'/r'`) | **Msg 6305** — `XQuery data manipulation expression required in XML data type method.` |
| the XML-DML fails to parse | **Msg 2209** — `XQuery [modify()]: Syntax error near '<eof>'` |
| an `insert` would duplicate an attribute name | **Msg 6308** — `XML well-formedness check: Duplicate attribute 'n'. Rewrite your XQuery so it returns well-formed XML.` |

The insert checks run in real's own order — target cardinality, then content type, then the attribute-position rule, then the target's node kind (probed one shape at a time), so `insert "abc" into (/r/a)` reports 2226 rather than 2207.

Msg 2207's type names come from the argument: a written literal reports no occurrence indicator (`xs:string`, and an integer literal is `xs:integer`), while a `sql:` accessor reports one off the SQL type (`xs:string ?` / `xs:int ?` / `xs:long ?` / `xs:decimal ?`).
A `sql:variable`'s type is known while the modify text parses, so its 2207 fires at compile time; a `sql:column`'s resolves through the UPDATE SET list's target-table scope, and where no column scope exists the check falls to execution.

### Serialization after an edit

An edited instance comes back **normalized**, which is what real does and the simulator matches byte for byte:

| input | after any `.modify()` |
|---|---|
| `<r>  <a>1</a>   <b   c = "2"  />  </r>` | `<r><a>1</a><b c="2"/></r>` |
| `<?xml version="1.0"?><r><a>1</a></r>` | `<r><a>1</a></r>` |
| `<r><a><![CDATA[x<y]]></a></r>` | `<r><a>x&lt;y</a></r>` |
| `<r><a></a></r>` | `<r><a/></r>` |

Empty elements self-close with no space before the slash, the XML declaration and insignificant whitespace are dropped, CDATA folds into escaped text, and values take the same position-dependent escaping `FOR XML` applies (`Selection.AppendForXmlText` is shared, so element text escapes `&` `<` `>` CR and an attribute value adds `"` tab LF).
Namespace declarations ride along as attributes and re-emit with their prefixes.
This is the only place an `xml` payload is re-serialized — an unmodified value keeps the text it was stored with, so the normalization is visible only after an edit.

### Divergences

- **A multi-root result isn't representable.** `insert <b/> after (/r)[1]` — where `/r` is the instance's own top-level element — answers `<r/><b/>` on real; the simulator raises `NotSupportedException` naming the shape, since the evaluator parses an instance as a document throughout (the same reason a multi-root instance can't be read, noted under the evaluator's divergences).
  A comment or processing instruction beside the top-level element is fine and matches real.
- **Attribute insert position.** `insert attribute z {…} into (/r/a)[1]` appends to the attribute list; real threads the new attribute into its internal node order, which can land it mid-list (`<a b="1" d="2"/>` → `<a b="1" z="9" d="2"/>`, probed).
  The set of attributes matches; the written order doesn't.
- **Typed xml is edited as untyped.** The `xml(collection)` binding is metadata only (no XSD parse anywhere in the simulator), so a `.modify()` on a typed column neither validates the result (real's **Msg 6923**) nor types the `with` value against the schema (real's **Msg 2247**), and `replace value of` still requires a `text()` / attribute target where real would accept the typed element itself.
- **Msg 6305 vs Msg 2209.** Real reports 6305 for an argument that parses as XQuery but isn't XML-DML and 2209 for text that isn't XQuery at all; the simulator splits on the leading statement keyword, so anything not starting with `insert` / `delete` / `replace` reports 6305 and only a failure *after* the keyword reports 2209.
  Real's 2209 also quotes a token the simulator's recursive-descent parser may name differently.
- **`SET t.col.modify(…)`** reports Msg 102 near `'.'` where real reports it near `'modify'`.
- A computed `element {…}` constructor in insert content raises `NotSupportedException`; the direct form covers the same ground.
- A **prolog prefix** used by a constructor is re-declared on the inserted element whether or not the insertion point already binds it; real omits the declaration when the prefix is already in scope.

## `OPENXML`

The pre-`OPENJSON` XML rowset, and the two system procedures that stock the document store it reads:

```
EXEC @rc = sp_xml_preparedocument @hdoc OUTPUT, @xmltext [, @xpath_namespaces]
SELECT … FROM OPENXML(@hdoc, '<rowpattern>' [, flags]) [WITH (<schema> | <table>)]
EXEC @rc = sp_xml_removedocument @hdoc
```

`Simulation/Simulation.OpenXml.cs` holds the two procedures, `Parser/Selection.OpenXml.cs` the rowset source, and `Storage/PreparedXmlDocument.cs` the parsed document plus its edge-table numbering.
The rowset is a `Selection` factory attached through the same `FromSource.LateralPlan` seam `OPENJSON` uses, so alias / qualifier / join handling is shared; `OPENXML` reaches it from the reserved-keyword arm of `ParseSingleFromSource` rather than the name arm, since the tokenizer already reserved the word.

### The handle store

Handles live on `SimulatedDbConnection.PreparedXmlDocuments`, session-scoped like `TempTables` and dropped at close.
Probe-confirmed against SQL Server 2025:

| | behavior |
|---|---|
| values | `1, 3, 5, 7, …` — odd, two apart, restarting at 1 per session |
| reuse | never: releasing handle 3 still leaves the next allocation at 7 |
| lifetime | survives batch boundaries and a `ROLLBACK` (the store isn't transactional) |
| visibility | one session only — another session reading the same number is Msg 8179 |
| NULL / omitted `@xmltext` | still allocates a handle, return code 0 |

A handle the session never held — including one it already released — is **Msg 8179** state 5, `Could not find prepared statement with handle 99.` (real's shared wording with the cursor-handle family), and a NULL handle reports handle `0`.
A document that won't parse is **Msg 6602** state 2 attributed to `sp_xml_preparedocument`, message `The error description is '…'.`; real emits its second line (`The XML parse error 0x… occurred on line number 1, near the XML text "…"`) as a separate info message, so it isn't part of `ERROR_MESSAGE()`.

The optional third argument is a wrapper element whose `xmlns` attributes declare the prefixes the patterns may use — they need not be the prefixes the document itself wrote, and a prefix bound to a URI reaches a document that declared it as the *default* namespace:

```
exec sp_xml_preparedocument @h output, '<r xmlns:p="urn:x"><p:a p:id="1"/></r>', '<root xmlns:q="urn:x"/>';
select id from openxml(@h, '/r/q:a') with (id int '@q:id')   → 1
```

### Grammar

Real admits exactly one token per argument, so an expression combiner anywhere in the list is Msg 102 — and the handle must be a *variable* (`OPENXML(99, …)` is Msg 102 near `'99'`, probe-confirmed).
The rowpattern is a string literal or a variable, the flags an integer literal or a variable, and the rowpattern is required.

### Flags

The low two bits pick the default column mapping and bit 8 governs the overflow column; the default (flags omitted) is attribute-centric.

| flags | mapping | `@mp:xmltext` |
|---|---|---|
| 0 / 1 | attribute-centric — a column with no colpattern name-matches an attribute | whole row node |
| 2 | element-centric — it name-matches a child element | whole row node |
| 3 | attribute first, child element as fallback | whole row node |
| 8 / 9 / 10 / 11 | as the low bits say | **minus every node another column consumed** |

```
-- <root><a id="1" nm="x"><b>bb</b><nm>elemnm</nm></a></root>, columns (id, nm, b)
flags (default) → 1,    x,       NULL
flags 2         → NULL, elemnm,  bb
flags 3         → 1,    x,       bb
```

### The `WITH` clause

`WITH (col type ['colpattern'], …)` declares the shape; `WITH <table>` copies a table's column list (Msg 208 when the name doesn't resolve) and name-matches each column the same way.

A colpattern is XPath 1.0 evaluated **relative to the row node**, and every form the engine accepts works — an attribute step (`@id`), a child path (`c/d`), `text()`, a parent step (`../@p`), a descendant step (`.//d`), and the context node itself (`.`, whose value is the concatenated descendant text).
A pattern matching several nodes takes the first; one matching nothing is NULL, not an error.
The selected text then routes through the ordinary string→type coercion, so a non-numeric attribute read as `int` is Msg 245.

A colpattern beginning `@mp:` reads a metaproperty of the row node instead: `id`, `localname`, `prefix`, `namespaceuri`, `prev`, `parentid`, `parentlocalname`, `parentprefix`, `parentnamespaceuri`, and `xmltext`.
Anything else after the prefix raises `NotSupportedException` naming it.

### The rowpattern dialect

OPENXML's patterns are XPath 1.0 — the dialect MSXML gives it — so the simulator runs both the rowpattern and every colpattern straight through `XmlNode.SelectNodes`, rather than the XQuery-subset translation the `xml` type's own methods take.
Descendant shorthand (`//a`, `/root//a`), value and positional predicates (`a[@t="p"]`, `a[1]`, `a[b="bb"]`), named axes (`descendant::a`, `child::a`), wildcards, unions and a relative path all follow from that; an attribute rowpattern (`/root/a/@id`) makes each attribute a row.
A pattern the engine refuses is **Msg 6603** state 2, whose text is the parser's complaint, a blank line, and the pattern carrying a `-->x<--` marker.

### The edge table

With no `WITH` clause the rowset is real's nine-column edge table (types probe-confirmed): `id` / `parentid` / `prev` `bigint`, `nodetype` `int`, `localname` / `prefix` / `namespaceuri` / `datatype` `nvarchar(4000)`, `text` `ntext`.
It carries the matched nodes' **whole subtrees** — not the whole document — in document order: the node, then each attribute followed by its value text node, then each child's subtree.
`nodetype` is the DOM's own code (1 element, 2 attribute, 3 text, 7 processing instruction, 8 comment), `datatype` is always NULL for an untyped document, and only character data carries `text` (an element's content and an attribute's value both live on their own text child).
A namespace declaration surfaces as an attribute with prefix `xmlns` and no namespace URI, whichever half of `xmlns:p` / `xmlns` it is.
A rowpattern of `/` matches the document node, which contributes no row of its own — the edge table starts at the document element, and a `WITH` schema over it gets one all-NULL row.

Node ids follow real's numbering, which is not plain document order (probe-confirmed one shape at a time):

- the document element is **0**, and still consumes the counter slot it would otherwise have taken — which is why a document with no prolog numbers its next node **2**;
- nodes preceding the document element are numbered from **1**;
- an element's attributes are numbered immediately after it, before its children;
- a text node that would be numbered immediately after its own parent element **swaps places with the node numbered next** (`<root>t1<b/>t2<c/></root>` gives `b` 2 and `t1` 3, while `t2` and `c` stay in order at 4 and 5);
- attribute value text nodes are numbered last, in document order.

### Divergences

- **Real's numbering of attribute value text nodes is lazy**, assigned when a query first materializes the node and stable thereafter, so two `OPENXML` reads of one handle in different orders give the same node different ids.
  The simulator numbers them eagerly in document order at prepare, which matches real for the first read of any handle and stays stable after.
- **The XML declaration and the DTD are not edge-table nodes.**
  Real reports a declaration as a nodetype-7 node named `xml` holding its pseudo-attributes as attribute children (numbering from 1, ahead of the document element's descendants); the simulator drops both, so a document with a prolog numbers as if it had none.
- **Msg 6602's and Msg 6603's detail sentences come from .NET's XML reader and XPath engine**, not MSXML, so the quoted complaint differs from real's while the message shape, number, severity, state and procedure attribution match.
  Msg 6603's `-->x<--` marker also sits at the pattern's end rather than at the offending token, since .NET reports no position.
- **An uncaught `sp_xml_preparedocument` / `sp_xml_removedocument` failure leaves the return code unwritten.**
  Real reports Msg 6602 / 8179 without aborting the batch and still returns 1; the simulator raises, matching what real leaves behind when the error is *caught* (probe-confirmed: `@rc` and the handle both stay NULL inside `TRY` / `CATCH`).
- **XPath 1.0 is .NET's, not MSXML's.**
  The two agree across everything probed, but neither the function library nor the collation-sensitive string comparisons are byte-compared.

## Catalog views in `BuiltInResources.cs`

**`sys.xml_schema_collections`** (6-col, probe-confirmed): `xml_collection_id` / `schema_id` / `principal_id` (NULL — AUTHORIZATION clause not modeled) / `name` / `create_date` / `modify_date`.

**Internal node-table + statistics surface (for DacFx export).**
DacFx's XML-index reverse-engineering query doesn't read `sys.xml_indexes` alone — it INNER JOINs `sys.index_columns` (one row per XML index: the indexed xml column, `index_column_id` 1, `key_ordinal` 0) *and* an internal "node table" per **primary** index (`sys.objects` type `IT` / `INTERNAL_TABLE`, named `xml_index_nodes_<tableObjectId>_<primaryIndexId>` (the index's own 256000-range `index_id`, probe-confirmed), parent = base table, `schema_id` = sys, `is_ms_shipped` = 1) joined to `sys.stats` (one row per XML index, `name` = the index name, on the node table's `object_id`; a primary owns its node table, secondaries share their primary's — `stats_id` sequential within a node table).
Modeled from probe (SQL Server 2025); without them DacFx NREs client-side (`SqlFullTextIndexColumnSpecifierPopulator`-style orphaned-parent) and emits no `SqlXmlIndex` elements.
A primary XML index allocates its node-table object id at CREATE (`XmlIndex.InternalTableObjectId`); `EnumerateXmlIndexStats` resolves each index (primary or secondary) to its owning node table.
This is the only place the simulator surfaces a type-`IT` object.

**`sys.xml_indexes`** (full 26-col shape, probe-confirmed against SQL Server 2025 WWI).
The load-bearing core keeps its original positions: `object_id` / `name` / `index_id` / `type` (=3) / `type_desc` (`XML`) / `using_xml_index_id` (NULL for primary) / `secondary_type` (char(1): `P`/`V`/`R`) / `secondary_type_desc` / `is_primary_key` (always false).

`index_id` comes from real's dedicated **256000+** XML range, sequenced **per table** in creation order — a table's first XML index is 256000, its second 256001, and the first on a second table is 256000 again (all probe-confirmed against SQL Server 2025).
A secondary's `using_xml_index_id` is its primary's value from that same range, `sys.index_columns` keys the index's row on it, and the primary's internal node table is named after it.
Ordinary indexes keep the small ids starting at 1; spatial indexes have their own 384000+ range (see [`spatial.md`](spatial.md)).
The allocation is a per-table watermark rather than a reused slot, matching real (probed: dropping the second XML index and creating another gives 256003, not 256001) — the simulator gets that for free because `DROP INDEX` doesn't remove XML indexes.
Appended after them (real orders these interleaved; the simulator appends since consumers read by name): `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` (false) / `is_unique_constraint` (false) / `fill_factor` (0) / `is_padded` (false) / `is_disabled` (false) / `is_hypothetical` (false) / `is_ignored_in_optimization` (false) / `allow_row_locks` (true) / `allow_page_locks` (true) / `has_filter` (false) / `filter_definition` (NULL) / `xml_index_type` (0 primary, 1 secondary) / `xml_index_type_description` (`PRIMARY_XML` / `SECONDARY_XML`) / `path_id` (NULL — the column names the promoted path a *selective* XML index tracks, and an ordinary primary or secondary index reports NULL; probe-confirmed) / `auto_created` (false).
Values are the fresh-index defaults.
DacFx's XML-index reverse-engineering query reads the `fill_factor` / `is_padded` / `allow_*_locks` / `is_disabled` / `xml_index_type` / `path_id` tail.

**`XML_SCHEMA_NAMESPACE(relational_schema, collection_name)`** (`Parser/Expressions/XmlSchemaNamespaceFunction.cs`): returns the collection's XSD as `xml` — the simulator returns the raw `CREATE XML SCHEMA COLLECTION … AS '…'` source text, where real reconstructs a normalized XSD from component metadata (divergence).
Unresolved pair → Msg 6314 at execution (probe-confirmed wording incl. the space before the colon; real raises 6314 even for the built-in `sys` collection, which the simulator doesn't register — the natural miss matches).
NULL argument → Msg 8116.
The three-argument namespace-filtering form → `NotSupportedException`.
DacFx's bacpac export calls this per user collection while scripting `sys.xml_schema_collections`.

## FOR XML result serialization

`Parser/Selection.ForXml.cs` — the trailing `FOR XML { RAW[('elem')] | AUTO | PATH[('row')] } [, ELEMENTS [XSINIL|ABSENT]] [, BINARY BASE64] [, TYPE] [, ROOT[('name')]]` clause, parsed in the same `SELECT`-tail slot as FOR JSON (`Selection.ParseOptionalForXml` runs right after `ParseOptionalForJson`; a non-XML `FOR` restores the cursor for the downstream Msg 102), optionally scoped by a leading [`WITH XMLNAMESPACES`](#with-xmlnamespaces) prefix.
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
  An unnamed column raises **Msg 6809**; a binary column without [`BINARY BASE64`](#binary-base64-and-autos-dbobject-references) raises **Msg 6829**.
- **AUTO** — one element per FROM source, nested (see below); the row element is named after the table/alias (`<t id="1"/>`), attribute-centric or `ELEMENTS`; unnamed column → Msg 6809, no FROM clause at all → **Msg 6800**, and a binary column without `BINARY BASE64` becomes a [`dbobject` reference](#binary-base64-and-autos-dbobject-references).
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

A **set-operation** result flattens to a single level, whatever either branch's join topology: it is named after the first branch's *first* FROM source (its alias when it has one) and every column lands on that one element, so `SELECT t.id, u.a FROM t JOIN u … UNION ALL SELECT id, a FROM u` emits a flat `<t id nm/>` per row rather than nesting.
A first branch with no FROM clause is still Msg 6800 / 13600.
The columns count as computed there — no source binding survives the union — so an AUTO binary column in a set-op result reports **Msg 6830** for want of an owning table.
`CombineSetOps` folds the binding down to that one name, and a longer chain keeps naming its leftmost source because the left operand already carries the folded array.

Divergences:

- A source with no written object name (derived table, CTE, table variable, `OPENJSON` / `STRING_SPLIT`) is named after its alias.
  That matches real for derived tables and CTEs; for the rowset functions real instead raises Msg 6800 (they aren't tables), which the simulator doesn't.
- Grouping compares values through `SqlValue.Equals`, so it is **collation-aware** — under a case-insensitive collation `'A'` and `'a'` group together.

### XML names — escaped in RAW / AUTO, rejected everywhere else

A SQL identifier is not an XML name, and FOR XML settles the mismatch two different ways (probe-confirmed, SQL Server 2025).
RAW and AUTO **escape** every column, table and alias name they emit — each character an XML name can't carry becomes `_xHHHH_` — while PATH's column aliases and the *explicit* names written into the clause (`RAW('elem')` / `PATH('row')` / `ROOT('name')`, whatever the mode) are **rejected** instead.
`ForXmlName.Encode` and `ForXmlName.ValidateSimpleName` / `ValidatePathColumn` in `Parser/ForXmlName.cs` are the two halves.

The character classification is the XML 1.0 **fourth-edition** `Name` production, which `XmlConvert.IsStartNCNameChar` / `IsNCNameChar` implement and real matches character for character (verified across the Latin-1, combining-mark, extender and fullwidth boundaries, so the wider fifth-edition ranges are out).
Two SQL-Server-specific rules ride on top: `:` is a name character in every position but the first, and an `_` followed by `x` escapes itself whatever comes after — which is what keeps the encoding round-trippable.

| written name | RAW / AUTO output | rule |
|---|---|---|
| `[a b]` / `[a#b]` / `[a$b]` | `a_x0020_b` / `a_x0023_b` / `a_x0024_b` | not a name character |
| `[1a]` / `[-a]` / `[.a]` / `[:a]` | `_x0031_a` / `_x002D_a` / `_x002E_a` / `_x003A_a` | legal later, not first |
| `[a-b]` / `[a.b]` / `[a1]` / `[a:b]` / `[_a]` | unchanged | legal in a non-first position |
| `[a_x0020_b]` / `[a_xzzzz_b]` / `[_x]` | `a_x005F_x0020_b` / `a_x005F_xzzzz_b` / `_x005F_x` | `_` before a lowercase `x`, valid escape or not |
| `[a_Xzzzz_b]` / `[x_]` | unchanged | only a lowercase `x` triggers it |
| `[xmlfoo]` / `[XMLfoo]` / `[xml]` | unchanged | the XML-reserved name prefix is not escaped |
| `[aé]` / `[漢字]` / `[a·b]` / `[aͅb]` | unchanged | base character / ideographic / extender / combining mark |
| `[a«b]` / `[a×b]` / `[a€b]` / `[aͶb]` / `[a℘b]` / `[aＡb]` / `[aͥb]` | `a_x00AB_b` / `a_x00D7_b` / `a_x20AC_b` / `a_x0376_b` / `a_x2118_b` / `a_xFF21_b` / `a_x0365_b` | outside the fourth-edition ranges (uppercase hex) |
| `[a𝐀b]` (U+1D400) | `a_x01D400_b` | one **six**-hex-digit escape per supplementary code point, not one per surrogate |
| `FROM #tmp` / `FROM @v` / `FROM t AS [a b]` (AUTO level) | `_x0023_tmp` / `_x0040_v` / `a_x0020_b` | a level name escapes like a column name |

The rejections, in the order the validator applies them:

- **Msg 6867** — the name is `xmlns` or carries it as a prefix (`[xmlns]`, `[xmlns:a]`, `[@xmlns]`, `ROOT('xmlns')`): `'xmlns' is invalid in XML tag name in FOR XML PATH, or when WITH XMLNAMESPACES is used with FOR XML.`
- **Msg 6846** state 4 — a namespace prefix that is neither the predefined `xml` nor one a [`WITH XMLNAMESPACES`](#with-xmlnamespaces) prefix declared: `XML name space prefix 'a' declaration is missing for FOR XML column name 'a:b'.`
  The check precedes the character rules (`[a b:c]` reports the prefix `a b`, not the space) and the prefix comparison is ordinal in both directions — the predefined `xml:` passes where `XML:` doesn't, and a clause declaring `p` still refuses `P:a`.
  The message says `column` / `row` / `ROOT` for the three positions.
- **Msg 6850** — a character an XML name can't carry there: `Column name 'a b' contains an invalid XML identifier as required by FOR XML; ' '(0x0020) is the first character at fault.`, with `Row name` / `ROOT name` variants.
  A **supplementary** character passes here though RAW would escape it (`[a𝐀]` → `<a𝐀>`), the one place the two halves disagree.
- **Msg 6849** — a PATH alias with an empty step: `FOR XML PATH error in column '/a' - '//' and leading and trailing '/' are not allowed in simple path expressions.`

A PATH alias is a path, so each `/`-separated step is validated on its own while the message quotes the whole alias (`[x/y z]` faults on the space); the last step's leading `@` is stripped first (a bare `[@]` reports the `@` itself) and its `text()` / `data()` node function is exempt.
An explicit row / ROOT name is a single name, so a `/` in one is simply an invalid character (`PATH('a/b')` → Msg 6850 on `/`).
The row tag is checked before the ROOT name.
`RAW('')` is row-tag omission like `PATH('')`, which only element-centric serialization can carry: `RAW(''), ELEMENTS` emits the bare elements and the attribute-centric default raises **Msg 6864**.

`FOR JSON` shares none of this — a JSON property name is a quoted string, so an alias reaches the output as written (`[a b]` → `"a b"`).

### FOR XML on a SELECT that doesn't return to the client

**Msg 6819** — the clause is refused on the SELECT an `INSERT … SELECT` or a `SELECT … INTO` writes from, and on a variable-assigning `SELECT @v = …`:

| statement | error |
|---|---|
| `INSERT z SELECT … FOR XML` | Msg 6819 state 1 — `The FOR XML clause is not allowed in a INSERT statement.` |
| `SELECT … INTO z … FOR XML` | Msg 6819 state 1 — `… in a SELECT INTO statement.` |
| `SELECT @v = … FOR XML` | Msg 6819 state **3** — `… in a ASSIGNMENT statement.` |
| `INSERT z SELECT … FOR JSON` / `SELECT … INTO z … FOR JSON` | **Msg 13602** state 1, same sentence with `FOR JSON` |
| `SELECT @v = … FOR JSON` | **Msg 6819** state 3 — real reports the *FOR XML* wording for the JSON clause too |

The rejection is about the statement's own SELECT, so every nested position stays legal: a scalar subquery (`INSERT z SELECT (SELECT … FOR XML RAW)`), a derived table (`… FROM (SELECT … FOR XML RAW) d(v)`) and `SET @v = (SELECT … FOR XML RAW)` all work.
Real settles the statement shape before any name (an INSERT source SELECT with an unusable alias reports 6819, not 6850) but after parsing, so a syntax error still wins; the simulator matches by checking once the clause has parsed, at nesting depth 0 only, off `ParserContext.InInsertSourceSelect` (set by the INSERT parser) plus the parsed selection's own `IntoTarget` / `IsAssignmentOnly`.

Real reaches its verdict before resolving the target table (`INSERT INTO nosuchtable SELECT … FOR XML` reports 6819); the simulator resolves the INSERT target first, so a missing table reports Msg 208 there.

### Options

- `ELEMENTS` → element-centric (RAW/AUTO; a no-op on always-element-centric PATH).
  `ELEMENTS XSINIL` → NULL columns emit `<col xsi:nil="true"/>` and the `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"` declaration is hoisted to the `ROOT` element when present, else repeated on each top-level element (each row wrapper, or each bare element under `PATH('')`).
  `ELEMENTS ABSENT` (default) → NULL elements omitted; NULL attributes are always omitted.
- `ROOT` → wrap in `<root>…</root>` (default name `root`); `ROOT('rows')` renames; `ROOT('')` raises **Msg 6861**.
- `BINARY BASE64` → see [below](#binary-base64-and-autos-dbobject-references).

An option written twice is accepted; real reports Msg 102 near `'XML'` for a repeated `TYPE` / `ROOT` / `BINARY BASE64`.

### `WITH XMLNAMESPACES`

`Parser/ForXmlNamespaces.cs` — the `WITH XMLNAMESPACES ('uri' AS prefix | DEFAULT 'uri', …)` prefix.
It parses through the same seam the CTE list does (`Simulation.ParseCteBindings`, so `Simulation.ParseBodyQuery` picks it up too) and registers on `ParserContext.XmlNamespaces`, which the statement loop clears alongside `CteBindings`.
Real accepts it only in **first** position: `WITH XMLNAMESPACES (…), c AS (…) SELECT …` works, `WITH c AS (…), XMLNAMESPACES (…)` is Msg 102 near `'xmlnamespaces'`.
The word is a keyword there — `WITH XMLNAMESPACES AS (…)` is a syntax error while the delimited `WITH [XMLNAMESPACES] AS (…)` is an ordinary CTE — so only the unquoted spelling enters the clause.
A URI must be written out (a variable is Msg 102).

The clause does two things.

**It makes a prefixed name legal.** `[p:col]`, `[@p:attr]`, `[p:a/q:b]`, `RAW('p:e')`, `PATH('p:row')` and `ROOT('p:r')` all pass Msg 6846's prefix gate once the prefix is declared.
Names are still written verbatim — nothing rewrites them — so RAW's undeclared-prefix leniency (a `:` is an ordinary name character there) is unchanged.

**It emits `xmlns` attributes** on whatever element is outermost, in **reverse** declaration order, the `xsi` binding XSINIL needs coming first:

| shape | where the declarations land |
|---|---|
| RAW / AUTO / PATH with a row tag, no ROOT | every row element |
| any mode with `ROOT` | the ROOT element only |
| `PATH('')` / `RAW(''), ELEMENTS` | every top-level element the row content produces; bare `[text()]` content carries none |
| AUTO with nesting | the outermost level only |
| a nested `FOR XML …, TYPE` subquery | re-declared on the inner fragment's own outermost element |

The last row is why the bindings live on the parser context rather than on one clause: the prefix scopes the whole statement, and real writes the declarations again on each serialized fragment.
`ForXmlOptions.Declarations` precomputes the attribute text once per plan; `Selection.ForXml.cs`'s serializers append it to the root or thread it to the top-level elements (the seam the `xsi` declaration already used).

```
with xmlnamespaces ('urn:x' as p, default 'urn:d') select id, a from t for xml path
    → <row xmlns="urn:d" xmlns:p="urn:x"><id>1</id><a>10</a></row>…
```

The `DEFAULT` binding emits an unprefixed `xmlns`, which the unprefixed element names then inherit by ordinary XML scoping — the serializer doesn't rewrite them.
The predefined `xml` prefix binds only to `http://www.w3.org/XML/1998/namespace` and emits no declaration at all; a *different* prefix bound to that URI is refused.

Rejections, in the order real applies them per binding (the whole clause validates at parse, so a statement with no `FOR XML` at all still raises):

| written | error |
|---|---|
| `'urn:x' AS xmlns` | **Msg 6871** — `Prefix 'xmlns' used in WITH XMLNAMESPACES is reserved and cannot be used as a user-defined prefix.` |
| `'urn:x' AS [p q]` | **Msg 6870** — `Prefix 'p q' used in WITH XMLNAMESPACES clause contains an invalid XML identifier. ' '(0x0020) is the first character at fault.` |
| `'urn:x' AS xml` | **Msg 6872** state 1 — `XML namespace prefix 'xml' can only be associated with the URI http://www.w3.org/XML/1998/namespace. This URI cannot be used with other prefixes.` |
| `'http://www.w3.org/XML/1998/namespace' AS p` | **Msg 6872** state **2**, same sentence |
| `'' AS p` / `DEFAULT ''` | **Msg 6874** — `Empty URI is not allowed in WITH XMLNAMESPACES clause.` |
| `'urn:x' AS p, 'urn:y' AS p` | **Msg 6869** — `Attempt to redefine namespace prefix 'p'` (no sentence-final period) |
| `DEFAULT 'urn:d', DEFAULT 'urn:e'` | **Msg 6869** naming the literal `default` |
| `'urn:x' AS xsi` with `ELEMENTS XSINIL` | **Msg 6873** — `Redefinition of 'xsi' XML namespace prefix is not supported with ELEMENTS XSINIL option of FOR XML.` |
| the clause with `FOR XML EXPLICIT` / `, XMLSCHEMA` / `, XMLDATA` | **Msg 6868** — `The following FOR XML features are not supported with WITH XMLNAMESPACES list: EXPLICIT mode, XMLSCHEMA and XMLDATA directives.` |

Msg 6873 and 6868 belong to the `FOR XML` clause rather than the declaration list, so they fire only when the statement actually carries one; 6868 beats the simulator's own unmodeled-feature rejection for EXPLICIT and XMLSCHEMA.
Two prefixes may share a URI, and a declared-but-unused prefix still emits.
`FOR JSON` ignores the clause entirely, as do `INSERT` / `UPDATE` / `DELETE`.

### `BINARY BASE64` and AUTO's `dbobject` references

`BINARY BASE64` is the only encoding the grammar admits — `BINARY HEX` is Msg 102 near `'HEX'`, as is a bare `BINARY`.
Under it, RAW and AUTO base64-encode a binary column exactly as PATH always does; PATH is unaffected either way.
The rule covers `binary` / `varbinary` and the legacy `image`.

Without the option each mode takes its own path:

| mode | behavior |
|---|---|
| PATH | base64, whatever the option says |
| RAW | **Msg 6829** |
| AUTO | a `dbobject/TABLE[@PK='V']/@COLUMN` reference — SQL Server's legacy SQLXML addressing form |

The AUTO reference is assembled once per plan (`BuildForXmlBinaryUrl`, keyed per result column into `ForXmlOptions.BinaryUrls`) and needs both halves of the addressing:

```
select id, bin from bt b for xml auto
    → <b id="1" bin="dbobject/bt[@id='1']/@bin"/><b id="2"/>

select k1, k2, bin from bc for xml auto        -- composite key, value needing escaping
    → <bc k1="1" k2="a&amp;b" bin="dbobject/bc[@k1='1'%20and%20@k2='a&amp;b']/@bin"/>
```

- The reference is written from **base** names: the owning table's object name and the base column names, so a select-list alias on the element (`FROM bt b` → `<b>`) or on a key column (`id AS zz`) doesn't show through.
  Names take the same `_xHHHH_` escaping AUTO's level names do, so a table variable addresses `dbobject/_x0040_t[…]`.
- A composite key joins its terms with the URL-escaped `%20and%20`; each value is its plain text form, and the finished reference then takes the position's ordinary XML escaping.
- A NULL binary value omits the attribute / element as usual, and two aliases of one column produce the same reference twice.
- No owning table — an expression, a derived table's column, a set-operation result — is **Msg 6830**.
- An owning table whose primary key is missing or not wholly projected is **Msg 6831** (`FOR XML AUTO requires primary keys to create references for 'bin'. …`).

The base-column half of the addressing is why `Selection` records `AutoColumnOrdinal` beside `AutoColumnSource`: the level model only needs the source, the reference needs the column within it.

### Value formatting + escaping (probe-confirmed, SQL Server 2025)

Numeric/date formatting matches FOR JSON (scientific `float`/`real`, the all-zero-fraction drop) **except** `bit` → `1`/`0` (not `true`/`false`), `uniqueidentifier` uppercases, `binary` / `varbinary` / `image` base64-encodes (always in PATH, under [`BINARY BASE64`](#binary-base64-and-autos-dbobject-references) in RAW / AUTO), and values are XML-escaped rather than JSON-escaped.
Escaping is position-dependent:

| position | escaped |
|---|---|
| element text | `&`→`&amp;`, `<`→`&lt;`, `>`→`&gt;`, CR→`&#x0D;` (`"` and `'` stay literal) |
| attribute value | the above plus `"`→`&quot;`, tab→`&#x09;`, LF→`&#x0A;` (`'` stays literal) |

### Not modeled yet

EXPLICIT mode and the `XMLSCHEMA` directive raise `NotSupportedException` (under a `WITH XMLNAMESPACES` prefix they raise real's own Msg 6868 first), as do PATH node functions beyond `text()` / `data()` (`comment()`, `processing-instruction()`, `node()`, `*`, `@*`).
`XMLDATA` isn't parsed at all, so it falls to Msg 102 without the prefix.
One-row chunking is the shared approximation noted above.

See [`backlog.md`](backlog.md).

## Leading byte-order mark

A string that becomes `xml` loses a leading U+FEFF, wherever the conversion happens — a literal INSERT, a parameter, an explicit `CAST`, `SqlBulkCopy` and a TVP row all behave the same, probe-confirmed against SQL Server 2025 (2026-07-30).
The same mark in an `nvarchar` column survives, so this belongs to the type conversion rather than to any input path; the strip therefore lives in `SqlValue.FromXml`, which every xml value funnels through.
A mark that isn't leading is content and stays.

## Known gaps

- **XQuery features beyond the path subset** the evaluator models (FLWOR, comparison / boolean / arithmetic operators, value predicates like `[@x="1"]`, constructors in a read method's argument).
  `.modify()`'s paths run through the same evaluator, so the subset bounds the mutator too.
  [`OPENXML`](#openxml) is unaffected — its patterns are XPath 1.0 and run through the DOM's own engine.
- **XSD validation** against `xml(schema_collection)` bindings — nothing validates an INSERT, an UPDATE or a `.modify()` edit, and a typed instance's paths carry untyped static types (see [`.modify()`'s divergences](#divergences-1)).
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
- The `.modify()` residue listed under [its divergences](#divergences-1): a multi-root insert result, attribute placement, the Msg 6305 / 2209 split, and the computed `element {…}` constructor.
