# `xml` data type + XML schema collections + XML methods + XML indexes

DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship.
`.value()` / `.nodes()` / `.query()` / `.exist()` execute against a bundled [XQuery-subset evaluator](#xquery-subset-evaluator) (`Storage/XmlQuery*.cs`), and `.modify()` mutates through the same expressions (`Parser/XmlDml.cs` + `Parser/XmlDmlParser.cs`).
The separate legacy rowset — [`OPENXML`](#openxml) over a `sp_xml_preparedocument` handle — reads XPath 1.0 through the DOM instead.

## Storage

**`XmlSqlType`** (singleton in `Storage/XmlType.cs`): `SqlServerName="xml"`, `SystemTypeId=241`, `IsLob=true`.
Payload stored identically to `nvarchar(MAX)` (raw UTF-16 LE bytes).
Type identity preserved through `sys.columns.user_type_id` / `sys.types`.

`xml` carries no comparison, so it is refused wherever a value is sorted, grouped or deduped — its own **Msg 305** in `ORDER BY` / `GROUP BY`, and the family-blind **Msg 421** / **Msg 5335** / **Msg 8117** everywhere else, tabulated beside the other non-comparable types in [`legacy-lob.md`](legacy-lob.md#where-the-types-cant-go).

**`XmlSchemaCollection`** carries id + name + schema_id + nullable principal_id + xsdText + create_date / modify_date.

**`Schema.XmlSchemaCollections`** — per-schema dict; shares the type-namespace with `TableTypes` / `AliasTypes` (Msg 219 on duplicate).

**`Database.AllocateXmlCollectionId`** seeds at 65536 (probe-confirmed).

**`HeapColumn.XmlSchemaCollection`** — nullable ref linking xml columns to their collection; a write through it is validated and canonicalized — see [Typed writes](#typed-writes--validation-and-canonical-form).

### The value model: documents and fragments

SQL Server's `xml` is CONTENT-typed, so an instance is not required to be a document: `CAST('<a/><b/>' AS xml)`, `CAST('<a>1</a>tail' AS xml)` and `CAST('abc' AS xml)` are all legal, and a `FOR XML …, TYPE` result routinely carries several top-level elements.
`Storage/XmlInstance.cs` is the single parse seam both the read methods and `.modify()` enter through, and it admits every one of those shapes.

Every instance's context item is its **document node**, so a relative path starts above the top-level element: `@x.query('a')` over `<r><a/></r>` selects nothing, `@x.value('(r/a/@x)[1]', …)` reads the attribute, and `@x.value('text()[1]', …)` over `<r>t</r>` is NULL (probed 2026-09-28 against SQL Server 2025).
`/a` reaches a top-level element of `'<a/><b/>'`, `/text()` reaches the top-level text of `'<a>1</a>tail'`, and `/` and `/a/..` both serialize the whole content.
`.modify()` resolves its paths from the same node, so `delete a` over `<r><a/></r>` deletes nothing and `insert <b/> into .` appends a top-level sibling.

A `.nodes()` row is a **node reference**, not a copy: the row carries the batch's number for the document (`BatchContext.XmlNodeDocuments`) and the node's child ordinals from the document node down, so a method on the row starts at that node while `..` and an absolute path still reach the rest of the document — `c.query('..')` is the parent, `c.query('/r')` the whole instance, `c.value('count(../a)', …)` the node's siblings, all as on real.
The reference can't outlive the batch that made it, since only the four methods may read it, and a run of siblings encodes and decodes by counting on from the previous row, which keeps shredding a wide instance linear.

Whitespace-only text between top-level nodes is insignificant and dropped, and an XML declaration is dropped, both matching real; text carrying anything else keeps its surrounding spaces (`'<a/> x <b/>'` round-trips as written).
A converted value is already stored that way — see [Well-formedness](#well-formedness) for the canonical form every conversion to `xml` produces — while a payload that arrives already typed `xml` (a bacpac row, a `FOR XML …, TYPE` result) is stored as it came.

`.modify()` edits a mutable container (`XmlInstance.CreateMutableContainer`) whose children are the instance's top-level nodes, which is what lets an edit *produce* a fragment: `insert <b/> after (/r)[1]` on `<r/>` answers `<r/><b/>` and `insert <c/> into (/)[1]` on `<a/>` answers `<a/><c/>`, both as on real.

**`HeapTable.XmlIndexes`** — `List<XmlIndex>`.
`XmlIndex` carries name + columnOrdinal + isPrimary + `UsingPrimaryIndexName` (for secondary) + nullable `SecondaryType` (PATH / VALUE / PROPERTY) + ObjectId + `InternalTableObjectId` (allocated per **primary** index at CREATE — see the internal node-table surface below; 0 for secondaries).

## Parsers — `Simulation/Simulation.Xml.cs`

```
CREATE XML SCHEMA COLLECTION [schema.]name AS '<xsd:schema>…'
ALTER XML SCHEMA COLLECTION [schema.]name ADD '<xsd:schema>…'   -- or a variable
DROP XML SCHEMA COLLECTION [schema.]name

CREATE PRIMARY XML INDEX name ON table(col) [WITH (…)]
CREATE XML INDEX name ON table(col)
    USING XML INDEX primary_name
    FOR {PATH | VALUE | PROPERTY}
    [WITH (…)]
```

- XSD text stored verbatim; AW's 6 schema-collection payloads (with embedded namespaces, complex types, restrictions, sequences) round-trip byte-identically.
  What is read out of the text: each element declaration's occurrence and each element's and attribute's simple type, which type an XQuery expression over a bound value (see [A schema collection narrows the cardinality](#a-schema-collection-narrows-the-cardinality)), and the compiled schema set a typed write validates against (see [Typed writes](#typed-writes--validation-and-canonical-form)).
- The identity constraints `xsd:unique`, `xsd:key` and `xsd:keyref` are real's own refusal at `CREATE` and `ADD`: **Msg 9336** `The XML Schema syntax 'unique' is not supported.`, naming the first one written (probed 2026-09-28 against SQL Server 2025).
- A text that doesn't compile is refused at `CREATE` and `ADD` in real's order (`XmlSchemaCollection.RejectUncompilableSchema`, probed 2026-10-06): a global element, attribute, type, group or attribute group declared twice in a namespace is **Msg 2302**, then the first reference in document order to a name nothing defines is **Msg 2307**, or **Msg 2308** naming its namespace — `xml:` included, since real predefines none of its attributes, while the `sqltypes` namespace an import names resolves — then a length or digits facet whose value isn't a number is **Msg 2309**, and a facet the base type doesn't take **Msg 2319** with its location.
  Each acts as under `XACT_ABORT`, as Msg 9336 does.
- Before any of those the text is read as XML, its parse errors real's own (Msg 9400 family, through `XmlWellFormedness`), and `include` and `notation` are Msg 9336 named as written (`'<xs:include>'`) and `redefine` **Msg 2391**.
  A reference that isn't an XML name is **Msg 2379** in place of 2307.
  Once every name resolves, real reads the documents in order (`XmlSchemaCollection.FirstStructuralError`, probed 2026-10-07): an element the XSD namespace lacks (**Msg 2297**), an attribute its element doesn't take (**Msg 2298**, by real's own lists — `abstract` and `mixed` on a `simpleType` are taken — and located at the complex type for its `complexContent` / `simpleContent`; `fixed` beside `default` on an element too), a boolean or enumerated attribute given another value (**Msg 2312** / **2313**, `block` and `final` included), an occurrence value that isn't a number (2309), a `totalDigits` of 0 (**Msg 2386**), a global declaration without its name (**Msg 2299**), a `name` beside a `ref` (**Msg 2360**), `minOccurs` above `maxOccurs` (**Msg 2382**), an empty `choice` that isn't optional (**Msg 2293**), a type named and declared inline at once (**Msg 2305**), and an attribute declared twice (**Msg 2310**).
  After Msg 2319, a named simple type's own restriction is judged: a base chain returning to it (**Msg 2366**), `fractionDigits` over `totalDigits` (**Msg 6950**), `minLength` over `maxLength` (**Msg 6946**), and a numeric lower bound over its upper (**Msg 6951** / **6952**).
  Real refuses more, which is accepted here and leaves the collection's values untyped where .NET's compiler refuses it too — see [Divergences](#divergences-1).
- `ALTER … ADD` appends schema documents to the collection's text and rolls back with the transaction, as `CREATE` does.
  A variable typed by a collection altered after its batch began — in the batch's own text or an `EXEC` it ran — refuses every assignment, a NULL and a `DECLARE` initializer included, with **Msg 6323**, transaction-aborting and past any `TRY`; declaring one is fine, and a later batch assigns freely (probed 2026-10-07 against SQL Server 2025).
  It admits only components the collection lacks: a global element, type or attribute the collection already declares in that namespace is **Msg 6310** (`… component namespace: '' component name: 'r' component kind:ELEMENT`, two spaces after the first sentence), though a component of another kind by the same name is fine.
  Text holding no `xsd:schema` document is **Msg 2378**, an empty string is a no-op, and a collection that doesn't resolve or that the session can't alter is **Msg 6347** either way (probed 2026-09-28).
- The `WITH (…)` list sets `FILLFACTOR`, `PAD_INDEX` and the two lock options the catalog reports, and `DROP_EXISTING = ON` rebuilds the named index — a primary taking the secondaries built on it, a secondary given the next index id — or is **Msg 6333** when there is none; what the statement can't take is refused as the batch compiles, in real's split between a name no index takes and an option this statement refuses (`Simulation.ParseXmlIndexOptions`, probed 2026-10-07 against SQL Server 2025).
- xml type positions: `xml`, `xml(name)`, `xml(CONTENT name)`, `xml(DOCUMENT name)`.
  A **column** declaration, a **`DECLARE @x`** and a **procedure parameter** take the form, off the same peek (`PeekIsXmlSchemaArgument`) that distinguishes the schema-collection-name form from a length / MAX spec, matched only when the bare 1-part type name is `xml`, and so does a **`CAST` / `CONVERT`** target (`Simulation.TryParseXmlCastTarget`).
  `DOCUMENT` is honored on every write: anything but one top-level element is **Msg 6901**, state 2 when it holds no element at all and 1 otherwise (probed 2026-10-06), and `sys.columns` / `sys.parameters` report it as `is_xml_document`.
  A typed procedure parameter validates its argument as the body begins, so a refusal names the procedure at line 0.
  The collection resolves while the batch compiles: an unknown one, or one the batch itself creates first, is **Msg 6314** quoting the name as written (`dbo.nosuch`), which stops the whole batch (probed 2026-10-06).
- Statement dispatch: `Xml` added to `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Xml }` and `ReservedKeyword { Keyword: Keyword.Primary }` (the PRIMARY XML INDEX form).
  `SCHEMA` is reserved, so the sub-keyword check uses `Keyword.Schema`.
  `COLLECTION` is a bare identifier.

## XML method execution

`Parser/Expressions/XmlMethodCall.cs` — instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`).

- **`.value(xquery, sqltype)`** — evaluates `xquery` against the target xml via `XmlQueryEngine.EvaluateScalar`, then casts the selected node's string value to `sqltype` through `Cast.ApplyCoercion`.
  The type literal (e.g. `'nvarchar(30)'`, `'money'`, `'decimal(9, 4)'`, `'integer'`) is resolved at parse time via `SqlType.GetByName`; `integer` maps to `int`.
  A type the method can't produce — `xml`, `text` / `ntext` / `image`, `sql_variant`, the CLR types, an unknown name, anything after the type (`'int, 1'`) — is **Msg 9500** naming the literal as written; a character type without a length is one character long; a binary target reads the value as base64, NULL when it isn't, padding a `binary(n)` or `timestamp` (all probed 2026-10-02 against SQL Server 2025).
  Empty selection → typed NULL, and an expression real doesn't type as at most one item is [Msg 2389](#static-cardinality-and-the-msg-2389-family) at parse.
  `GetSqlType` returns the resolved target type, so projection / view-output schemas are exact (not the old nvarchar(MAX) stub).
- **`.nodes(xquery)`** — rowset-producing, valid only in a FROM / APPLY source position.
  `Selection.FromClause.cs::ParseLateralFromSource` detects the `xmlexpr.nodes(...) [AS] alias(column)` shape (the parsed object name's leaf is `nodes` with a following `(`), re-parses the target as an expression, and builds a correlated single-column (`xml`) lateral plan (`Selection.XmlNodes.cs`).
  A variable or parameter target — `FROM @x.nodes(…)`, `CROSS APPLY @x.nodes(…)` — takes the same plan through a `.nodes(` lookahead on the `@` token.
  The row column is a node reference only the four methods and `IS [NOT] NULL` may read: anything else is **Msg 493**, and a `CAST` / `CONVERT` of it **Msg 525** naming the target's base type — inside an `IS NULL` test too — both ahead of the type rules its `xml` type would otherwise break, in the select list, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY` and `ON` alike (probed 2026-09-25 and 2026-09-28).
  Each row references its node in place — see [the value model](#the-value-model-documents-and-fragments).
  In scalar position it is no method at all to real: **Msg 227** (`"nodes" is not a valid function, property, or field.`).
  Without its `alias(column)` the rowset is **Msg 318** (class 15, state 0), with two columns **Msg 8159**, and an expression real types as atomic values (`nodes('1')`, `nodes('data(/a)')`) **Msg 2374** while it compiles.
  A three-part `t.x.nodes(…)` in a subquery's plain `FROM` shreds the enclosing query's column, as an APPLY does its left side's (probed 2026-10-02).
- **`.exist(xquery)`** — returns `bit`: 1 when the expression's **result sequence is non-empty**, 0 otherwise, NULL when the instance is NULL (`XmlQueryEngine.EvaluateExists`).
  That is emptiness, not an effective boolean value: `exist('false()')`, `exist('0')` and `exist('1=2')` all answer 1 because each yields one item, while `exist('()')` and `exist('/r/nope')` answer 0 (probe-confirmed).
- **`.query(xquery)`** — returns `xml`: the serialized concatenation of the matched nodes in document order (atomic items separated by a single space), empty string when nothing matches, NULL when the instance is NULL (`XmlQueryEngine.EvaluateQuery`).
  Serialization is `Storage/XmlResultSerializer.cs`, not `XPathNavigator.OuterXml` — the navigator's writer indents and writes ` />`, where real writes neither — and it re-binds the namespaces a node out of its document needs; see [Serializing a node out of its document](#serializing-a-node-out-of-its-document).
- **`.modify()`** — the mutator, a separate sublanguage; see [`.modify()` — XML-DML](#modify--xml-dml) below.
  Reaching `XmlMethodCall` for it means it was written in a value position, which is **Msg 8137**.
- `GetSqlType`: `.value()`→resolved target type, `.exist()`→bit, `.query()`→xml.
- **The call's own shape**, settled while compiling (probed 2026-10-02 against SQL Server 2025): `value` takes two arguments and the other three one, else **Msg 174**; the five names match case-sensitively, so an `xml` receiver's `.Value(…)` or `.foo(…)` is **Msg 227**; a receiver named in three or more parts (`dbo.t.x.value(…)`) is **Msg 344**, read as a remote function.
- **`WITH XMLNAMESPACES`** binds the statement's xml methods as if their prologs declared the prefixes and the default element namespace; a prolog's own declaration wins.
- **Where a method may not appear**, each settled while compiling so nothing earlier in the batch runs (probed 2026-09-28 against SQL Server 2025): a `PRINT` operand is **Msg 2722**, a `CHECK` constraint **Msg 423** + 1750, a computed column **Msg 435** — **Msg 424** on a table variable or a multi-statement function's return table.
  Each wants a scalar UDF wrapping the call, which is accepted everywhere.
  `SET`, `DECLARE`'s initializer, `IF` / `WHILE`, `RETURN`, `TOP`, `OFFSET` and a `DEFAULT` all take one; `RAISERROR` / `THROW` / `EXEC` arguments, `EXEC (…)` and `WAITFOR` refuse the dotted call as Msg 102 on both engines.
- A non-literal `xquery` / type argument is Msg 8172 while the batch compiles — see [Arguments, outer references and the context node](#arguments-outer-references-and-the-context-node).
- **XML runtime errors abort as under `XACT_ABORT`**: a well-formedness refusal (Msg 6307 / 6308), a `replace value of` one (Msg 6320 / 6325) and every typed-validation failure end the batch and roll the transaction back uncaught, and doom it caught, whatever the option says (probed 2026-09-28) — the same class as the [parsing family](#well-formedness).

## XQuery-subset evaluator

`Storage/XmlQueryParser.cs` compiles the expression, `Storage/XmlQueryExpression.cs` is the tree it builds and evaluates, and `Storage/XmlQueryEngine.cs` is the front door the four read methods and `.modify()`'s target paths enter through.

The argument is a compile-time literal everywhere, so **an expression compiles once while the SQL statement parses** — which is also where SQL Server settles its static XQuery diagnostics, so those fire there too and over an empty rowset.
Evaluation walks an `XPathNavigator` over the parsed instance, positioned on the context item [the value model](#the-value-model-documents-and-fragments) picks: the document node, or the node a `.nodes()` row references.
An empty or whitespace-only argument is **Msg 6306** (`Invalid XQuery expression passed to XML data type method.`) in every method, `.modify()` included.

### The XQuery subset

- **Prolog**: an optional `xquery version "1.0";`, then `declare default element namespace "uri";`, `declare namespace prefix="uri";` and `declare default function namespace "uri";` (which changes nothing the library resolves).
  An unprefixed *element* name takes the default element namespace; an attribute never does (XQuery's scoping rule).
  An undeclared prefix — on a name test or a function name — is **Msg 2229**.
  `declare function` and `declare variable` are **Msg 9335**, and any other declaration (`declare boundary-space …`) Msg 2209 near `declare` (probed 2026-09-28).
- **Location steps**: child (the default axis), attribute (`@x`), parent (`..`), self (`.`) and the descendant-or-self expansion of `//`.
  Name tests may be prefixed (`act:number`) or not and may contain `.`; `*`, `*:local` (the local name in any namespace), `prefix:*` (any name in the prefix's namespace), `text()`, `node()`, `comment()` and `processing-instruction()` are the node tests (the two wildcards probed 2026-10-02).
  The named forms of the same six axes — `child::`, `attribute::`, `self::`, `parent::`, `descendant::`, `descendant-or-self::` — evaluate; real parses the reverse and sibling axes (`ancestor`, `following-sibling`, …) only to refuse them with **Msg 9335**, reports any other word before `::` (`namespace::` included) as **Msg 2392**, and reads `child :: x` as Msg 2209 near the word.
  A `self::x` after a step real types as an element of another name is **Msg 2261** (`There is no element named 'x' in the type 'element(r,xdt:untyped) *'.`), all probed 2026-09-28.
  A step runs once per context node — which is what scopes a predicate, so `a[1]` is the first `a` under *each* parent — and `XmlStep.SortIntoDocumentOrder` then folds the per-context-node sequences into one **document-ordered, duplicate-free** sequence, as `/` requires.
  Two axes need it.
  `//` expands to `descendant-or-self::node()`, putting a node and its own descendants in the same context, so the following step interleaves: over `<r><a><b>a1</b><c><b>c1</b></c></a><b>r1</b><a><b>a2</b></a></r>`, `//b` is `a1, c1, r1, a2` and not the `r1, a1, c1, a2` that step-evaluation order gives.
  That is a value difference rather than a presentation one, since `(//b)[1]` narrows over the sorted sequence.
  `..` reaches one parent once per child, so `/r/a/..` is a single `r`.
  The ordered-and-distinct case is the common one (a child or attribute step over an ordered context), so a linear check precedes the sort.
- **Predicates**, in any number and on any step or parenthesized expression.
  What one *means* comes from its static type, not its runtime value:

  | predicate's static type | meaning |
  |---|---|
  | numeric (`[2]`, `[1.0]`, `[count(b)]`, `[position()=2]`) | **positional** — the item at that 1-based position |
  | boolean (a comparison, `and` / `or`, `not()`, `true()`) | a filter |
  | node sequence (`[@x]`, `[b]`, `[b/c]`) | an existence test — non-empty selects |
  | anything else (`["a"]`, `[string(@x)]`, `[data(@x)]`) | **Msg 2203**, quoting the type |

  Chained predicates filter in written order, each seeing what the previous left: `[@x="1"][2]` is the second match, `[2][@x="1"]` tests the second item.
- **General comparisons** `=` `!=` `<` `<=` `>` `>=` — existential over both operand sequences, and the untyped-atomic rule decides the comparison type from the *other* operand:

  | shape | comparison |
  |---|---|
  | untyped vs a numeric literal (`[@x=1]`) | numeric — `"01"` equals `1` |
  | untyped vs a string literal (`[@x="1"]`) | by **code point** — `"01"` doesn't equal `"1"`, and the ordering is case-sensitive whatever the database collation |
  | untyped vs untyped (`[b=c]`) | by code point |
  | two typed operands of different kinds (`["a"=1]`) | **Msg 2234** at compile time |

  A value that won't cast to the number it's compared against matches nothing and raises nothing — `[@x=1]` and `[@x!=1]` are both empty where `@x` is `"abc"` (probe-confirmed).
  Because the rule is existential, `!=` is **not** the complement of `not(=)`: over `<p><b>1</b><b>2</b></p>`, `[b!=1]` selects (the `2` differs) and `[not(b=1)]` doesn't.
- **Value comparisons** `eq` `ne` `lt` `le` `gt` `ge` — the same type rules over singletons, with real's **static** cardinality check in front (below).
  An empty operand answers the empty sequence, which a predicate reads as no match.
- **Node comparisons** `is`, `<<`, `>>` — identity and document order between two statically single nodes; a plural operand is Msg 2389 and `()` Msg 2234 naming the type `empty`.
- **`and` / `or` / `not()`** over effective boolean values, `and` binding tighter, parentheses available.
  Their operands take the [condition type gate](#conditions-and-the-msg-2204-gate).
- **Arithmetic** `+` `-` `*` `div` `mod` and unary minus, over statically single operands (Msg 2389 naming the operator otherwise); `idiv` is real's **Msg 9335**, and a string or boolean operand **Msg 9308**.
  The result type — and with it the rendering — follows [the arithmetic types](#arithmetic-types).
  XQuery's name grammar swallows a `-` that follows a name character, so `@n-1` reads the attribute named `n-1` and a subtraction needs a space — real's own behavior.
- **Sequences** `(a, b)`, the empty sequence `()`, and a positional predicate over either (`(act:telephoneNumber)[1]/act:number`).
  The comma form is the grammar's own `Expr`, so it is also what the body itself and an `if` condition take — `/r/a, /r/b` is a legal `.query()` argument — while a predicate, a function argument and every FLWOR clause take a single expression, which is why `/r/a[., .]` is **Msg 9303** (`Syntax error near ',', expected ']'.`).
- **[FLWOR, quantified and conditional expressions](#flwor-quantified-and-conditional-expressions)** and the `$`-variable references they bind.
- **[Node constructors](#node-constructors)** — direct elements, comments and processing instructions, and the computed `element` / `attribute` / `text` forms.
- **[Constructor functions, `cast as` and `instance of`](#constructor-functions-cast-as-and-instance-of)**, and the [`sql:variable()` / `sql:column()` accessors](#sqlvariable-and-sqlcolumn).
- `typeswitch`, `validate`, `ordered` and `unordered` are real's **Msg 9335**, as are `treat as`, `castable as`, `to`, `union` / `|`, `intersect` and `except`.
- **Functions**: `avg` `ceiling` `concat` `contains` `count` `data` `distinct-values` `empty` `false` `floor` `last` `local-name` `lower-case` `max` `min` `namespace-uri` `not` `number` `position` `round` `string` `string-length` `substring` `sum` `true` `upper-case`, reachable bare or through the predeclared `fn:` prefix.
  `number()` takes nodes only — `number("12")` is **Msg 2374** — and `round()` takes a half toward positive infinity (`round(-2.5)` is `-2`).
  A string parameter (`concat`, `contains`, `upper-case`, `lower-case`, `string-length`, `substring`'s first) refuses a number or boolean argument, and `substring`'s positions a string, with **Msg 2364** naming the argument's type and `xs:string` / `xs:decimal`; an untyped value or a node converts (probed 2026-10-02 against SQL Server 2025).
  Anything else in the function namespace is **Msg 2395**, `There is no function '{http://www.w3.org/2004/07/xpath-functions}:starts-with()'` — which is what real answers for `starts-with` / `ends-with` / `normalize-space` / `translate` / `boolean` / `exists` / `abs` / `zero-or-one` too, since its library doesn't carry them either.
  Arity is part of the signature: too few arguments is **Msg 2236** (`There are not enough actual arguments in the call to function "contains()".`) and too many **Msg 2238** (`Too many arguments in call to function 'count()'` — real punctuates the two differently).

### Node constructors

| constructor | notes |
|---|---|
| direct element `<out a="{…}">{…}</out>` | arbitrary nesting; an enclosed expression contributes its nodes in element content and its atomized text in an attribute value, adjacent atomics space-separated within one expression but not across two (`<a>{1}  {2}</a>` is `<a>12</a>`); `{{` / `}}` are the literal-brace escapes |
| direct comment `<!-- c -->` / processing instruction `<?t d?>` | literal text, braces included, at top level or inside an element; a comment with `--` or a trailing `-` is **Msg 9322**, a target of `xml` in any case **Msg 2294**, whitespace ahead of the target **Msg 2278**, and the whitespace between target and data isn't data |
| computed `element name {…}` | the content a direct constructor takes |
| computed `attribute name {…}` | the content atomized and space-joined, an empty body the empty string; `xmlns` is **Msg 9316** |
| computed `text {…}` | a single expression (a comma is **Msg 2205**, an empty body Msg 2209), atomized and space-joined; `()` builds no node, `""` an empty one |

Real takes only the **constant-QName** form of a computed name: a `{…}` name expression is **Msg 9315** whatever it holds, a string literal included.
The computed comment and processing-instruction forms are **Msg 9326** and **Msg 9325**, in every method, `.modify()`'s insert content included.
A constructor resolves its name through the [prolog](#the-xquery-subset) exactly as a path step does, so `declare default element namespace "urn:d"; <b/>` builds `<b xmlns="urn:d"/>`; a declared prefix the markup never writes isn't declared on the result, as real omits it.

Inside a direct element, **boundary whitespace** — content that is only whitespace, between tags and enclosed expressions — is dropped (`<a>  {1}  </a>` is `<a>1</a>` while `<a>  x  {1}</a>` keeps its spaces), an attribute value is either literal text or exactly one enclosed expression (**Msg 9313** otherwise), and a CDATA section is text (a top-level one is Msg 2209 near `<!`).
A string literal reads the five predefined entity references and character references: an `&` opening no name, or a name XML doesn't predefine, is **Msg 2282**, a name that runs into another character before its `;` **Msg 2283** naming that character, and a numeric reference that isn't a number **Msg 2285** (probed 2026-10-02 against SQL Server 2025).

An **attribute item in element content** — a computed attribute, or an attribute a path selected (`<e>{/r/@a}</e>`) — is hoisted onto the enclosing element in order, after its literal attributes; one already there is **Msg 6308**, and one that follows an element, comment or processing-instruction child is **Msg 6307**, while text or an atomic value ahead of it is fine.
The splice-and-parse model carries it as a marker processing instruction the constructor replaces after parsing (`XmlAttributeHoisting`).
A `.query()` whose result real types as attributes alone (`/r/@a`, `attribute x {1}`, a FLWOR returning attributes) is **Msg 2396**, and one that reaches an attribute among other nodes (`/r/@a, /r/x`) Msg 6307.

**Constructed XML is opaque**: real returns it, nests it in another constructor, tests it with `.exist()`, and reads it as a condition (`if (<a/>)`, `not(<a/>)`, a predicate), and every other operation over it is **Msg 2373** naming the operation — `'/'`, `'[]'`, `'for'` / `'let'` / `'some'` / `'every'`, `'instance of'`, the function (`count()`, `string()`, `empty()`), or `data()` for anything that atomizes (a comparison, arithmetic, a cast, an `order by` key, a computed attribute's or text's content).
`.value()` and `.nodes()` refuse a constructed result the same way, as `data()` and `'nodes()'`.
All probed 2026-09-28 against SQL Server 2025.

### Constructor functions, `cast as` and `instance of`

`xs:type(expr)` is the cast to that built-in atomic type, and `expr cast as xs:type?` the same written as an operator binding tighter than arithmetic; `xdt:untypedAtomic(expr)` casts to untyped (`xs:untypedAtomic` is Msg 2395).
Validation and the result's canonical text ride .NET's built-in datatypes and `XsdCanonical`, the pair a typed write uses, so `xs:decimal("1.50")` is `1.5`, `xs:double("1e20")` `1.0E20` and `xs:time("24:00:00")` `00:00:00`.

| shape | answer |
|---|---|
| a literal the type doesn't admit (`xs:integer("abc")`, `"300" cast as xs:byte?`, and `NaN` or a number past `xs:double` / `xs:float`'s range — `INF` and `-INF` are admitted) | **Msg 9319** while compiling |
| a value read from the instance that doesn't convert | the empty sequence |
| a plural or empty operand | **Msg 2365** (`Cannot explicitly convert from 'xdt:untypedAtomic *' to 'xs:integer'`, `'empty'` for `()`) |
| `cast as xs:type` without the `?` | **Msg 9301** |
| an unknown type name | **Msg 2232** in `cast as`, Msg 2395 as a function |
| `xs:QName` from a string | Msg 2365 |
| wrong arity | Msg 2236 / 2238, naming the function without its parentheses |

A number casts to an integer type by truncating (`xs:integer(1.7)` is 1) and to `xs:boolean` by its non-zero test.
`expr instance of SequenceType` takes `empty()`, an atomic type, and the kind tests `item()`, `node()`, `element([name])`, `attribute([name])`, `text()`, `comment()` and `processing-instruction()` with an occurrence indicator; `document-node()` is Msg 9335, and the operand must be statically single whatever the indicator says (Msg 2389).
An atomic item's type is its static one, so `1 instance of xs:decimal` is true and `1.5 instance of xs:integer` false.

### Arithmetic types

An arithmetic result takes XQuery's promotion of its operands' static types — integer below decimal below float below double, an untyped operand counting as `xs:double`, and `div` over two integers `xs:decimal` — and renders by it (`XmlAtomicTypes`):

| result type | rendering | example |
|---|---|---|
| `xs:double` / `xs:float` | [`fn:string`'s approximate form](#canonical-form-per-primitive) at 15 / 7 digits | `data(/r/z)[1] + 1` over `1234567` is `1.234568E6` |
| `xs:decimal` | plain digits; a quotient cut and a product rounded to six fractional digits, a zero divisor the empty sequence | `2 div 3` is `0.666666`, `1.23456789 * 1.1` is `1.358025`, `1 div 0` is empty |
| `xs:integer` | plain digits | `1234567 + 1` is `1234568` |

`sum`, `min`, `max`, `ceiling`, `floor` and `round` keep their argument's type (untyped counts as double), `avg` of integers is decimal cut to ten fractional digits, and `number()` is double.
`.value()` reads the rendered text, so `.value('sum(/r/z) * 10', 'int')` is real's own Msg 245 over `'1.234567E7'`.
All probed 2026-09-28 against SQL Server 2025.

### `sql:variable()` and `sql:column()`

Both accessors read the SQL side's value in every method (`XmlMethodCall.BuildAccessorScope`): a variable of the batch, or a column of the query scope the method is in (a multi-part name binds against its qualifier, Msg 107 when that names no source, Msg 209 when unqualified and ambiguous, Msg 207 when missing).
The value is typed by its SQL type — `int` is `xs:int`, the character types and `uniqueidentifier` `xs:string`, `decimal` / `money` `xs:decimal`, `float` `xs:double`, `real` `xs:float`, `bit` `xs:boolean`, the binaries `xs:base64Binary`, the date-time family `xs:dateTime` / `xs:date` / `xs:time` — which is what a comparison or arithmetic over it checks (`sql:variable("@v") = "5"` over an `int` is Msg 2234 quoting `xs:int ?`), and it renders in that type: a `decimal` 1.50 is `1.5`, a `float` `1.5E10`, a `bit` `true`, a `datetime` `2020-01-02T03:04:05.000` and a `datetime2(3)` / `time(2)` / `datetimeoffset` at its own fractional precision.
A NULL is the empty sequence.

| refusal | error |
|---|---|
| a name without `@` | **Msg 9519** (no method bracket) |
| a variable never declared | **Msg 9501** state 2 (no method bracket) |
| an `xml` value anywhere but a `.modify()` insert's bare content | **Msg 9342** |
| `sql_variant`, `hierarchyid` and the other unmapped types | **Msg 9344** |
| an argument that isn't a string literal | **Msg 2225** |
| a name the `sql` namespace doesn't have | Msg 2395 over `urn:schemas-microsoft-com:xml-sql` |

The same typing and rendering apply in `.modify()`, whose insert content and `with` value read the accessors through the same conversion (`XmlAtomicTypes.FromSql`); there a `sql:column` the statement can't type while its SET list parses — an UPDATE's FROM clause comes after it — reads untyped and takes no part in deciding an arithmetic result's type.
All probed 2026-09-28 against SQL Server 2025.

### Serializing a node out of its document

A node `.query()` returns is written out of its document, so a namespace declared on an ancestor is out of scope; real re-binds it where it is used, choosing the prefix (probed 2026-09-28 against SQL Server 2025):

1. a declaration inside the written subtree, kept as written;
2. the query's own prolog — a `declare namespace` prefix, or the default element namespace for an element name — and then the prefixes every XQuery context predeclares (`xs`, `xsi`, `xdt`, `sql`);
3. a generated `p1`, `p2`, … counted across the whole result and skipping any prefix already bound — `p10`, `p11`, … once `p` itself is bound.

So `/r/a` over `<r xmlns:p="urn:p"><a><p:b/></a></r>` is `<a><p1:b xmlns:p1="urn:p"/></a>`, a namespace declared but never used on the way down isn't written at all, an element's own binding precedes its attributes and an attribute's lands just ahead of it.
A `.nodes()` row's `c.query('.')` has no prolog of its own, which is why it writes `<p1:a xmlns:p1="urn:p"/>` for an element whose prefix the document declared above it.

### FLWOR, quantified and conditional expressions

```
for $v in <expr> [, $v in <expr>]…        let $v := <expr> [, $v := <expr>]…
[where <expr>] [[stable] order by <expr> [ascending | descending] [, …]] return <expr>

some  $v in <expr> [, …] satisfies <expr>
every $v in <expr> [, …] satisfies <expr>

if (<expr>) then <expr> else <expr>
```

The grammar SQL Server 2025 accepts, probed one form at a time:

| form | |
|---|---|
| `for` / `let`, in any number and interleaved in either order | ships |
| several bindings in one clause (`for $a in X, $b in Y`), a binding reading an earlier one (`$b in $a/c`) | ships |
| `where`, `order by` with `ascending` / `descending` / several items / a leading `stable` | ships |
| nested FLWOR, a FLWOR as a binding source, as a predicate's expression, as a `.modify()` target | ships |
| `for $i at $p in …` (positional variable) | **Msg 9335** `'at'` |
| `for $i as xs:string in …` (typed binding) | **Msg 9335** `'as'` |
| `order by … empty greatest` / `empty least` / `collation "…"` | **Msg 9335** naming the whole modifier |

Clause order is enforced: what follows the bindings must be `where`, `(stable) order by` or `return` — anything else is **Msg 9332** (`Syntax error near '$i', expected 'where', '(stable) order by' or 'return'.`) — and after those, anything but `return` is **Msg 9303**, which is also what a missing `then` / `else` / `satisfies` reports.
A missing binding separator splits by construct: a quantified expression reports **Msg 9303** (`… near '/', expected 'in'.`) where a FLWOR reports **Msg 2205** (`"in" was expected.`, `":="` for a `let`).
A `$`-reference no binding introduced is **Msg 2227**, `The variable '$nope' was not found in the scope in which it was referenced.`

Semantics:

- The result keeps **iteration order and every duplicate** — it is not folded into document order the way a path step's output is, so `for $i in /r/a return /r/b` answers the same `b` once per `a`.
  Multiple bindings nest like loops (`for $a in X, $b in Y` and two consecutive `for` clauses are the same thing).
- An **`order by` key compares by code point** unless real types it as a number, so `order by $i/@x` puts `"10"` before `"2"` while `order by number($i/@x)` doesn't.
  An empty key sorts first (real's default is `empty least`) and `descending` reverses the comparison, putting it last; ties keep stream order.
- `some` over an empty binding sequence is false and `every` is true; `satisfies` reads the effective boolean value.
- XQuery's `else` is mandatory, and the branches must agree on nodes-versus-atomics (**Msg 2210** otherwise, below).
- `position()` and `last()` read the sequence a predicate is filtering, so real refuses them anywhere else — a FLWOR's return clause included — with **Msg 2371**, `'position()' can only be used within a predicate or XPath selector`.

Static cardinality follows the shape: a `for` multiplies its binding sequence's cardinality into the return clause's, a `let` binds the whole sequence once and contributes none, and a `where` narrows neither.
So `.value('for $i in (/r/a)[1] return $i', …)` reads while `.value('for $i in /r/a return $i', …)` is Msg 2389 — and the type it quotes is the return clause's, `'xs:string *'` for `return "x"`.
A `let` variable carries its binding's own static type, which is what makes `let $i := /r/a return string($i)` Msg 2389 quoting `'element(a,xdt:untyped) *'`.

### Conditions and the Msg 2204 gate

A **condition** — an `if` test, a `where`, a `satisfies` body, an `and` / `or` operand, a `not()` argument — admits only a boolean or a node sequence.
Unlike a *predicate*, where a numeric expression is a position, a numeric condition is refused, and so is a string or an already-atomized `data(…)`:

```
XQuery [query()]: Only 'http://www.w3.org/2001/XMLSchema#boolean?' or 'node()*'
expressions allowed in conditions and with logical operators, found 'xs:integer'
```

Real settles it statically, so it fires while the SQL statement parses and over an empty rowset, like the rest of the family.

### Static cardinality, and the Msg 2389 family

Real types an expression off its **shape**, never the instance, and refuses a construct that admits at most one item but got a sequence.
Cardinality multiplies along a path, and only a *positional* predicate narrows a step — a filtering one leaves it plural.
So `(/r/a)[1]` and `/r[1]/a[1]` are singular while `/r/a[1]`, `/r/a[@x="1"]` and `/r/a[@x="1"][1]` are not (probe-confirmed one shape at a time).

Five constructs take the check, and each reports **Msg 2389** naming the method that carried the expression:

| construct | message |
|---|---|
| a value comparison | `XQuery [query()]: 'eq' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'` |
| a function whose parameter is atomic (`contains`, `substring`, `string-length`, `upper-case`, …) | the same, naming `'contains()'` |
| a function whose parameter is `item()?` (`string`, `local-name`, `namespace-uri`) | the same, but quoting the **node** type: `'element(b,xdt:untyped) *'` |
| an `order by` key | the same, naming `'order by'` |
| `.value()` itself | `XQuery [value()]: 'value()' requires a singleton (or empty sequence), found operand of type 'xdt:untypedAtomic *'` |

That last row is why the `(…)[1]` wrapper is idiomatic in every `.value()` call: real refuses `/r/a[@x="1"]` even when the instance holds exactly one match.

**Msg 2210** rides the same static typing: a sequence — a comma list or an `if`'s two branches — may not put nodes beside atomic values, and the message names the atomic type first whichever side wrote it (`Heterogeneous sequences are not allowed: found 'xs:string' and 'element(a,xdt:untyped) *'`).
Two atomic types are fine, so `(1, "a")` reads.

### The receiver names the diagnostic

Every XQuery diagnostic real raises is bracketed with the method that carried the expression, and a **column** receiver puts its own dotted path in front of it: `XQuery [dbo.xr.d.value()]`, `XQuery [sx.xr2.d.modify()]`.
A **variable** receiver contributes nothing and reads as the bare `XQuery [value()]`.
The rule is one lookup — the receiver's source and column — and it holds for all five methods, for `.modify()`'s XML-DML errors as much as the read methods', and wherever the call appears (a select list, a WHERE, a subquery, an UPDATE's SET list).

The source half is the object **as the FROM clause wrote it**, with any alias ignored:

| receiver's source | named |
|---|---|
| base table or view | as written — `xr`, `dbo.xr`, `ProbeScratch.dbo.xr`, `dbo.vx`; brackets stripped, alias ignored |
| synonym | the synonym, never the object behind it |
| temp table / table variable | `#tt` / `@t` |
| CTE | the CTE's own name, even under an alias (`FROM c AS q` names `c`) |
| derived table, `VALUES` constructor | the alias, there being no object name |
| the row column a `.nodes()` produced | the **originating** receiver — `dbo.xr CROSS APPLY d.nodes(…) n(c)` names `dbo.xr.d`, and a chain of them keeps naming the first |
| variable, literal, expression | nothing |

That is `FromSource.WrittenObjectName`'s rule exactly, which the **GROUP BY containment** diagnostics read for the same reason (see [`query.md`](query.md)) — the two were probed independently and agree on every source kind, views and aliased CTEs included.
An UPDATE mutator names its write target the same way: the table-name form directly, and the alias form through the FROM clause it resolved against, which is why the alias form's body compiles only once that clause has parsed.

### A schema collection narrows the cardinality

Binding a value to an XML schema collection changes exactly one thing about how an expression compiles: a **named child step whose element the collection declares at most once is a singleton**, where the same step over untyped `xml` is plural.
That is what lets real read `(act:telephoneNumber)[1]/act:number` — the trailing step would otherwise make the path a sequence, and `.value()` would be Msg 2389.
AdventureWorks' own `Person.vAdditionalContactInfo` is written that way, so the view doesn't create at all without this.

`XmlSchemaCollection.GetSingletonElementNames()` reads the names out of the stored XSD (one `XmlReader` pass, cached until the text is reassigned, and an empty set for a text the reader can't get through — which leaves the value untyped rather than failing the query).
The rule is name-keyed rather than type-aware:

- Only a **local** declaration — an `xsd:element` inside a content model — carries an occurrence.
  A **global** one, a direct child of `xsd:schema`, says nothing, because its cardinality comes from wherever it is referenced; in AdventureWorks' contact schema that is an unbounded `xsd:any` wildcard, and real accordingly types `/ci:AdditionalContactInfo/act:telephoneNumber` as a sequence.
- A name declared plural **anywhere** in the collection loses singleton status everywhere.
  That errs narrower than real, which resolves the declaration through the containing type: it can leave a path real accepts refused, never the reverse.

Three receivers carry a binding beside a `CAST` / `CONVERT` to `xml(collection)`, and the third is what the AdventureWorks view needs:

| receiver | where the binding comes from |
|---|---|
| a column | `HeapColumn.XmlSchemaCollection`, resolved against the query scope's FROM sources |
| a variable | `VariableSlot.XmlSchemaCollection`, from `DECLARE @x xml(<collection>)` |
| a `.nodes()` row column | the binding of the `.nodes()` target, stamped on the produced column — each row is a node of that instance |

Everything else — a literal, a `CAST` to plain `xml`, an expression — is untyped, as on real.

A **`CAST` / `CONVERT` to `xml(collection)`** is typed the same way, so `CAST(… AS xml(sc)).value('/r/d', …)` is Msg 2389 where the untyped CAST would be too — but quoting the schema type.

### A schema collection types the expression

The binding also gives each element and attribute name the collection declares with a simple type that type (`XmlStaticTyping`, keyed by local name like the singleton rule, and left untyped where two declarations disagree): a step to an `xs:decimal` element reports `element(d,xs:decimal)` and atomizes as `xs:decimal`.
That is what real quotes (`'value()' requires a singleton …, found operand of type 'xs:decimal *'`), what makes a typed decimal compare as a number and a typed string refuse a numeric comparison (**Msg 2234** quoting both types), what makes `(/r/d)[1] + 1` render `1234568` rather than the untyped `1.234568E6`, and what makes `data((/r/d)[1]) instance of xs:decimal?` true.
A `text()` step under a simply typed element is **Msg 9312** (`'text()' is not supported on simple typed or 'http://www.w3.org/2001/XMLSchema#anyType' elements, found 'element(d,xs:decimal) *'.`), in `.modify()`'s paths too, and a typed element marked `xsi:nil` reads as NULL through `.value()`.
All probed 2026-09-28 against SQL Server 2025.

### Arguments, outer references and the context node

Every XQuery argument, and `value()`'s type, is a string literal — parenthesized or not — and anything else (a variable, an expression, `NULL`) is **Msg 8172** naming the argument's position and the method, which stops the batch as it compiles, a `TRY` around it included (probed 2026-10-06 against SQL Server 2025).

A `.nodes()` row column is read only through a method or `IS [NOT] NULL` from the queries nested in its own as well: an enclosing query's or an `APPLY`'s left side's column named bare in a select list, a `WHERE`, an `EXISTS` or a derived table is **Msg 493**, and converted **Msg 525** (`RejectDirectNodesColumnRead`, through `ParserContext.EnclosingScopes`).

Inside a predicate on a step the collection types, the context item atomizes as that type, so `/r/s[. = 1]` over an `xs:string` element is **Msg 2234** quoting `xs:string` and `xs:integer`.
A `.nodes()` row standing on an attribute is typed as one (`attribute(*,xdt:untypedAtomic)`, or the attribute's own name), from which an attribute step is **Msg 2219**, a child step **Msg 2261** and `text()` **Msg 2377** — `.` and `..` read on (all probed 2026-10-06).

### Not modeled yet

- **User-derived simple types** report the built-in they restrict rather than their own name in a static type, and a complex type with simple content stays untyped.

### Divergences

- `.value()` casts go through the standard string→type coercion (`casting.md`'s flexible string→date-like parser), so the AdventureWorks `vJobCandidateEducation` / `vJobCandidateEmployment` / `vPersonDemographics` views — which wrap `.value()` date strings in `CONVERT(datetime, …, 101)` — resolve.
- Msg 2209's quoted token comes from the simulator's own recursive-descent cursor, so a malformed expression may name a different token than real's parser does.
  Real also splits its generic syntax errors further than the simulator does — a path that ends mid-step is its **Msg 9341** (`Syntax error near '<eof>', expected a step expression.`) where the simulator reports 2209 — and a construct keyword written without the token that identifies it (`for i in …`, `if 1=1 then …`) is real's 2209 near the *keyword* while the simulator names the token it stopped on.
- **`position()` / `last()` legality is lexical.** The simulator allows them anywhere inside a written predicate, so a FLWOR nested in one can read them; real's rule is its own binder's.
- `fn:min` / `fn:max` compare numerically; real compares by the operand's own type, so a string sequence orders differently.
- **Exact numbers are `numeric(38, 10)`, as real's are** (probed 2026-10-06 against SQL Server 2025): a decimal literal keeps ten fractional digits rounded half away from zero, more than 28 integer digits is **Msg 2342**, a result past that range is the empty sequence, and an integer keeps every digit (`Storage/XmlExactDecimal.cs`) — a value a `double` can't carry travels as its digits.
  The [arithmetic types](#arithmetic-types) decide the rendering and the decimal scale rules, and real's decimal scale follows each operand's own digits where the simulator applies the six-digit rule to every quotient and product.
- **A constructed node re-parses.** Each evaluation splices the enclosed sequences into the literal markup and parses the result, so a value carrying markup-significant text is escaped by position rather than kept as a node identity; the serialized answer matches real for every probed shape.
## Typed writes — validation and canonical form

A write to an `xml(<collection>)` target isn't stored as written.
Real validates the instance against the collection and stores it **canonicalized**, so `<Total>1.00</Total>` comes back as `<Total>1</Total>` — and the canonical text is what everything downstream reads, a trigger's `INSERTED` and an `OUTPUT` projection included, because the rewrite happens on the way in.
Probed against SQL Server 2025 on 2026-08-08.

It runs **per assigned value**, not per row: an `UPDATE` that never names the xml column neither re-reads the schema nor re-checks what is already stored.
`Simulation.CoerceForInsert(SqlValue, HeapColumn)` is the seam — the coercion every write already performs per column — and `VariableSlot.Assign` is its sibling for a `DECLARE @x xml(c)` initializer and every later `SET`.
The third target, a `CAST` / `CONVERT` to `xml(collection)`, validates in `Cast.ValidateTypedXml` (`TRY_CAST` doesn't soften the failure), and the bacpac loader sends every typed column's value through the same method, so an import is held to the schema a write is.
The two halves live in `Storage/XmlSchemaValidation.cs` (the walk) and `Storage/XsdCanonical.cs` (the rendering).

### Canonical form per primitive

| type | written | stored |
|---|---|---|
| `decimal` and the whole integer family | `1.00`, `.5`, `+5.`, `-0.0`, `+007` | `1`, `0.5`, `5`, `0`, `7` |
| `double` / `float` | `1.0E2`, `0.50`, `1000000`, `0.0000005`, `0`, `-0` | `100`, `0.5`, `1.0E6`, `5.0E-7`, `0.0E0`, `-0.0E0` |
| `boolean` | `1`, `0` | `true`, `false` |
| `string` | `  a  b  ` | `  a  b  ` (whiteSpace `preserve`) |
| `normalizedString` | `  a <tab> b  ` | `  a   b  ` (`replace`) |
| `token`, `anyURI`, `Name`, every non-string primitive | `  a  b  ` | `a b` (`collapse`) |
| `dateTime` / `date` / `time` / the `g*` family | `…05.000+02:00`, `…05+00:00`, `…05-00:00`, `2020-01-02T24:00:00`, `24:00:00` | `…05+02:00`, `…05Z`, `…05Z`, `2020-01-03T00:00:00`, `00:00:00` |
| `duration` | `P1Y2M3DT4H5M6.000S`, `P0Y` | `P1Y2M3DT4H5M6S`, `PT0S` |
| `hexBinary` / `base64Binary` | `ABcd`, `YW Jj` | `ABCD`, `YWJj` |
| a `list` | `  1.50   2.00  ` | `1.5 2` (each item canonicalized) |
| a `union` | `2.50`, `1` under `decimal \| boolean` | `2.5`, `1` — the first member type that accepts it wins, so `1` doesn't become `true` |

The approximate pair is the one rule that isn't XSD's own canonical form.
Real renders `double` / `float` by XQuery's `fn:string` rule — plain notation inside `[1e-6, 1e6)`, scientific outside it with a mantissa in `[1, 10)` carrying at least one fractional digit and an exponent with no `+` and no padding — and caps the digits at SQL Server's own **15** for a `double` and **7** for a `float` rather than taking a shortest round trip, so `1234567890123456789` stores as `1.23456789012346E18` and `1.234568E18` respectively.
Zero is always scientific (`0.0E0`), signed when it was written signed.
`NaN` is refused outright, which XSD itself permits.

### The validation errors

Nine messages, each carrying real's own location trail — `/*:r[1]/*:a[1]` per element with same-named siblings numbered from one, `/*:r[1]/@*:k` for an attribute, and a namespaced name written `{uri}local`.

| condition | error |
|---|---|
| a value its declared type doesn't admit — a facet violation and an out-of-range integer included | **Msg 6926** `Invalid simple type value: 'zz'. Location: …` |
| an element the model didn't want here — undeclared, out of order, or in a namespace a wildcard doesn't name | **Msg 6965** `Invalid content. Expected element(s): '…'. Found: element '…' instead. Location: ….` (the only member ending in a period) |
| a leftover child the model has nothing left to offer for | **Msg 6923** `Unexpected element(s): {uri}b. Location: …` |
| the parent ended with the model still requiring an element | **Msg 6908** `Invalid content. Expected element(s): '…'. Location: …` (named against the parent) |
| an attribute the type doesn't declare | **Msg 6905** state 3, `Attribute 'q' is not permitted in this context. Location: …` |
| a `use="required"` attribute left out | **Msg 6906** `Required attribute 'k' is missing. Location: …` |
| a root element the collection declares nowhere — including one written in no namespace against a qualified schema, which real refuses rather than skipping — or a `strict` wildcard's child that nothing declares | **Msg 6913** `Declaration not found for element '…'. Location: …` |
| character data inside an element-only type | **Msg 6909** `Text node is not allowed at this location, …` |
| a second child an `xsd:all` member already took | **Msg 6911** `Found duplicate element 'b' in all content model. Location: …` (against the repeat); a required member left untaken is Msg 6908 naming only the missing ones |

The expected-element list names what the model would have taken **at the position the walk stopped at**, not every name in the content particle — and a wildcard writes itself into it as `{uri}*` per namespace it names.
Its names run together as `'x','y'`, no space after the comma (probed 2026-09-25).
**Divergence**: real also lists the optional elements a sequence skipped on the way — `<s/>` against `m?, n` is `'n','m'` there and `'n'` here.
That list is also what splits the two leftover-child errors: a model still willing to take something is Msg 6965 naming it, while one whose every particle has reached its `maxOccurs` has nothing to offer and reports Msg 6923.
So against `dec?`, `<v><nope/></v>` is 6965 and `<v><dec>1</dec><nope/></v>` is 6923 (each probed on its own).

The value model stays CONTENT-typed under validation: several top-level elements are as legal as they are for untyped `xml`, provided the collection declares each of them — unless a `CAST` wrote `DOCUMENT`.
Every one of these failures aborts as under `XACT_ABORT`, ending the batch and rolling the transaction back (probed 2026-09-28).

### The `xsi:` attributes

Every attribute in the `xsi` namespace is exempt from the attribute check, a made-up `xsi:bogus` included; two of them mean something (probed 2026-09-28 against SQL Server 2025):

| attribute | effect | refusals |
|---|---|---|
| `xsi:nil="true"` (or `1`) | the element holds no value and isn't validated past its attributes | **Msg 6917** on an element not declared `nillable` (or with a fixed value), **Msg 6918** when it still has content |
| `xsi:type="xs:int"` | the element validates against the named type | **Msg 6914** for a type nothing defines, **Msg 6936** for one not derived from the declared type |

A nillable element's `replace value of … with ()` sets `xsi:nil="true"`, declaring the prefix on the element — see [the three statements](#the-three-statements).

### How the walk is built

Compilation is .NET's `XmlSchemaSet` — the post-compilation infoset (`ElementSchemaType`, `ContentTypeParticle`, `AttributeUses`) is exactly the resolved shape needed, and hand-rolling XSD would be the whole of it.
The **walk** is the simulator's own, because real's diagnostics distinguish cases .NET separates only by English prose: an out-of-order child and an over-occurring one are both `invalid child element` there, where real splits them into Msg 6965 and Msg 6923.
.NET also skips a no-namespace root entirely where real raises Msg 6913.

An `xsd:any` admits only the namespaces it names (`##any` / `##other` / `##targetNamespace` / `##local` and explicit URIs), and a child it admits is then typed against its own **global declaration** — real's `strict` processing, and what types AdventureWorks' `ContactRecord` inside the `xsd:any` its `AdditionalContactInfo` declares.
Under `strict` (the default, and what an unspecified `processContents` means) a name the wildcard admits but nothing declares is Msg 6913, the same error an undeclared root takes; `lax` and `skip` let it through unvalidated.
Everywhere the matcher can't place a particle it lets the children through, which is the direction that can only lose fidelity rather than reject an instance real accepts.

An edited instance is written back through `XmlDml.Serialize` — the same probe-confirmed serializer `.modify()` uses, which self-closes an empty element with no space before the slash where `XDocument`'s own writer adds one.

Evidence beyond the tests: **1,038 values** across all six AdventureWorks schema collections round-trip byte-identically through the validating write path.
They were exported by real SQL Server and are therefore already in its canonical form, which makes them a free differential oracle for the renderer.

### Divergences

- **No precision cap on `decimal`.** Real applies an internal one that rejects a 29-digit integer part and truncates a long fractional part (`0.1234567890123456789012345678` stores as `0.123456789`); the simulator canonicalizes the digits as written. Values of the size real data carries are unaffected.
- A collection that exists but whose `EXECUTE` the principal lacks is Msg 229 on both, where a missing one is the Msg 6314 above.
- **An XSD error outside the refusals [the parser section lists](#parsers--simulationsimulationxmlcs)** is accepted here (probed 2026-10-07): a content model violating unique particle attribution (Msg 6916), a default, fixed or enumeration value its type rejects (Msg 6926), a facet that loosens its base's (Msg 6944), complex content derived from a simple type (Msg 6941), `use="prohibited"` with no base attribute to prohibit (Msg 6920), a known element in the wrong place (Msg 2297), complex types deriving from each other (Msg 6953), and a `documentation` element's `xml:lang` checked but an `id` attribute's value not.
  An invalid pattern (real's Msg 572), a loosened facet and an invalid enumeration value are Msg 2319 here, the facet .NET's compiler refuses.
- Real's expected-element list in a Msg 6965 isn't in declaration order (`'n','f','s','i','dt','b'` for a sequence declared `d, i, s, b, dt, n, f`); the simulator lists the names as declared.

## `.modify()` — XML-DML

`Parser/XmlDmlParser.cs` parses the sublanguage into a `Parser/XmlDml.cs` statement, and `XmlDml.Apply` runs it over a LINQ-to-XML tree selected through the same compiled [XQuery expression](#xquery-subset-evaluator) the read methods evaluate — so a value predicate reaches a mutator target too (`delete /r/a[@x="1"]`, `insert <c/> into (/r/a[@x="2"])[1]`).
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
| the instance is NULL — an unassigned variable or a NULL cell in an updated row | **Msg 5302** — `Mutator 'modify()' on '@x' cannot be called on a null value.` (the name as written, `@` included for a variable); it ends the batch and rolls the transaction back as under `SET XACT_ABORT ON`, and dooms it inside `TRY` (probed 2026-10-02) |

Real reaches Msg 8137 before the SET-option gate, so `.modify()` in a select list reports it even from a session holding `QUOTED_IDENTIFIER` the wrong way; the mutator positions take the gate (**Msg 1934**) like every other XML method, naming the statement's own verb — `SELECT` for `SET @x.modify(…)`, `UPDATE` for the UPDATE form.

### The three statements

```
insert <content> [as first | as last] {into | before | after} <target>
delete <target>
replace value of <target> with <value>
```

**`insert`** — the content is an **XQuery expression** like any other, compiled through the read methods' engine and evaluated against the instance as it stands before the edit: the [node constructors](#node-constructors), a path copying nodes out of the instance (`insert /r/b into (/r)[1]` doubles the `b`, `insert (/r/b)[1] before (/r/b)[1]` too), a FLWOR or conditional producing nodes, and enclosed expressions of any shape inside a constructor (`<a>{count(/r/b)}</a>`, `<a x="{/r/b}"/>`) — all probed 2026-09-28 against SQL Server 2025.
A selected attribute lands on the target element as a computed one does.
The one form of its own is a bare `sql:variable("@v")` / `sql:column("c")` (or a parenthesized list of them) carrying `xml`, the only place an `xml` accessor is legal; it brings its own namespace scope.
A constructor resolves its **name through the prolog** exactly as a path step does — `declare default element namespace "urn:d"; insert <b/>` builds a `urn:d` element, and `declare namespace p="urn:x"; insert <p:b/>` a `urn:x` one — and a binding it took from the prolog is dropped again wherever the insertion point already makes it, while one the constructor wrote itself (`<p:b xmlns:p="urn:x"/>`) stays (probe-confirmed).
The serializer re-declares whatever the insertion point doesn't already bind, so an unqualified constructed element landing under a namespaced parent comes back as `<b xmlns=""/>`, byte-identical to real.
`into` appends (as does `as last`), `as first` prepends, `before` / `after` place a sibling.
`before` / `after` on the outermost element, and `into` the document node, both produce a [fragment](#the-value-model-documents-and-fragments) — `insert <b/> after (/r)[1]` on `<r/>` is `<r/><b/>`.

An attribute item always attaches to the target element whatever the `as first` / `as last` keyword says, and threads into real's **internal node order** rather than landing at the end of the list.
An instance's own attributes sit at the odd ordinals 1, 3, 5, … and the *i*-th attribute one statement adds takes ordinal 2*i*, so a single insert lands right after the first attribute and a sequence interleaves one per gap before spilling to the end:

```
<a m="1" n="2" o="3" p="4"/> + attribute z          → <a m="1" z="9" n="2" o="3" p="4"/>
                             + (attribute z, attribute y)
                                                    → <a m="1" z="9" n="2" y="8" o="3" p="4"/>
```

Namespace declarations count as attributes for that ordering, and a second statement renumbers against what the first left — which reproduces real's own answer for repeated single inserts (`m n o p` plus `z`, then `y`, then `w`, is `m w y z n o p` on both).
Every shape above was probed one at a time.

**`delete`** — no cardinality restriction: every matched node goes, elements / attributes / text alike.
Deleting the top-level element leaves an empty instance (`''`).

**`replace value of`** — writes the target's string value.
The `with` clause is a whole **XQuery expression**, compiled through the same engine a read method's path takes: a literal, a path, arithmetic over them, a function call, and the two `sql:` accessors, which the compiler resolves to slots the SQL side fills before each evaluation.
AdventureWorks' `Sales.iduSalesOrderDetail` is the shape that turns on it — `replace value of (/IndividualSurvey/TotalPurchaseYTD)[1] with data(/IndividualSurvey/TotalPurchaseYTD)[1] + sql:column("inserted.LineTotal")`.
A sequence atomizes to its items' text joined by a single space, and replacing a text node's value with the empty string removes the node, so its element comes back self-closing.
An **empty result** replaces nothing (probed 2026-09-28): a text node takes it only when the expression was written `()` — which removes the text — and is **Msg 6325** otherwise (a NULL accessor, a path that matched nothing); an attribute or element is **Msg 6320**, unless the receiver's collection declares the element `nillable`, which empties it and marks it `xsi:nil="true"`.

Over a **typed** receiver the `with` value must be of the target's declared type or one derived from it, checked statically: **Msg 2247** (`The value is of type "xs:string", which is not a subtype of the expected type "xs:decimal".`) refuses `xs:integer` into an `xs:int` attribute and `xs:decimal` into an `xs:double` element alike, while an `int` `sql:variable` into an `xs:decimal` element is taken; an untyped value and the empty sequence are left to the write.
The value computes in the types [the schema gives the expression](#a-schema-collection-types-the-expression), so `data((/r/d)[1]) + 1` over an `xs:decimal` 1234567 writes `1234568` (probed 2026-09-28).

`sql:column` takes the **multi-part** form (`sql:column("inserted.LineTotal")`, brackets optional), and it binds against the **whole statement's scope** rather than the write target alone — an UPDATE's SET list parses ahead of its FROM clause, so the reference is resolved at the statement's own compile-time bind, where the FROM sources are in scope.
`sql:variable` is checked while the modify text parses, since a variable is batch-scoped (Msg 9501 for one never declared, as in the read methods).

An **element target** is legal when the receiver's `xml(collection)` binding types that element with simple content — real refuses it over untyped `xml` with Msg 2356 and accepts it over a typed instance, whether the receiver is a variable or a column.
The write replaces the element's content and leaves its attributes standing.
An element counts as simply typed when its `type` attribute names a built-in XSD type or a named `xsd:simpleType` the collection declares, or when its own first child is an inline `xsd:simpleType`; anything the reader can't place that way stays complex, which keeps real's Msg 2356 rather than admitting a write real refuses.

The mutator's **target** is the write target, never whatever the FROM clause also happens to call by that name: `UPDATE Person.Person SET Demographics.modify(…) FROM inserted` writes `Person.Person`'s column even though `inserted` carries a `Demographics` of its own (AdventureWorks' `Person.iuPerson`), because the re-read carries the leading name exactly as the statement wrote it.

Across all three, a path that matches nothing is a **no-op**, not an error.

### Static target and content checks

Real types the target off the path's **shape**, before reading a single node — so `/r/a/text()` is `text *` and refused as a `replace value of` target even when the instance holds exactly one match, while `(/r/a/text())[1]` is `text ?` and accepted.
Only a positional predicate over the *whole* path makes it singular: `(/r/a)[1]/text()` and `/r/a[1]/text()` are both still `text *`.

The messages quote that static type, and the simulator reproduces the notation: `text *` / `text ?`, `element(a,xdt:untyped) *`, `attribute(n,xdt:untypedAtomic) ?`, and for the context node `document { (element(*,xdt:untyped) ? & text ? & comment ? & processing-instruction ?) * }`.

| check | error |
|---|---|
| `insert` target not statically single | **Msg 2226** — `XQuery [modify()]: The target of 'insert' must be a single node, found 'element(a,xdt:untyped) *'` |
| `insert` content is an atomic value | **Msg 2207** — `XQuery [modify()]: Only non-document nodes can be inserted. Found "xs:string".` (a sequence mixing atomics with nodes is Msg 2210 first) |
| an attribute item with `before` / `after` | **Msg 2258** — `… The position may not be specified when inserting an attribute node, found 'attribute(n,xdt:untypedAtomic)'` |
| `insert … into` a non-element / non-document | **Msg 2240** — `… The target of 'insert into' must be an element/document node, found 'text ?'` |
| `insert … before/after` an attribute or the document | **Msg 2249** — `… The target of 'insert before/after' must be an element/PI/comment/text node, found '…'` |
| `replace value of` target not statically at-most-one | **Msg 2337** — `… The target of 'replace' must be at most one node, found 'text *'` |
| `replace value of` an untyped element (a typed one whose collection gives it simple content is accepted) | **Msg 2356** — `… must be a non-metadata attribute or an element with simple typed content, found 'element(a,xdt:untyped) ?'` |
| `replace value of … with <b/>` | **Msg 9310** — `… The 'with' clause of 'replace value of' cannot contain constructed XML.` |
| `replace value of` with no `with` | **Msg 2205** — `XQuery [modify()]: "with" was expected.` |
| `delete .` or a `delete` of an atomic value | **Msg 2264** — `… Only non-document nodes may be deleted, found '…'` |
| the argument parses as XQuery but isn't XML-DML (`'/r'`, `'count(/r)'`, `'<a/>'`, a FLWOR) | **Msg 6305** — `XQuery data manipulation expression required in XML data type method.` |
| the argument doesn't parse as XQuery either (`'('`, `'/r['`, a bare `'insert'`) | **Msg 2209** — `XQuery [modify()]: Syntax error near '<eof>'` |
| the argument is empty or whitespace | **Msg 6306** — `Invalid XQuery expression passed to XML data type method.` |
| an `insert` would duplicate an attribute name | **Msg 6308** — `XML well-formedness check: Duplicate attribute 'n'. Rewrite your XQuery so it returns well-formed XML.` |

The insert checks run in real's own order — target cardinality, then content type, then the attribute-position rule, then the target's node kind (probed one shape at a time), so `insert "abc" into (/r/a)` reports 2226 rather than 2207.
The Msg 6305 / 2209 split is real's too: text no XML-DML keyword opened is handed to the expression grammar, and only its failure reports 2209.

Msg 2207's type names come from the content's static type: a written literal reports no occurrence indicator (`xs:string`, and an integer literal is `xs:integer`), while a `sql:` accessor reports one off the SQL type (`xs:string ?` / `xs:int ?` / `xs:long ?` / `xs:decimal ?`).
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

- **A `.nodes()` over a derived table's pass-through column names the derived table**, where real resolves it back to the base column the projection forwards: `FROM (SELECT d FROM dbo.xr) dt CROSS APPLY dt.d.nodes(…) n(c)` makes a downstream `.value()` report `dt.d` against real's `dbo.xr.d`. A derived column with no base of its own (`(VALUES(@x)) v(z)`) names the source on both, as does a direct `.value()` on `dt.d` — the tracing is what `.nodes()` alone does.
- Real's **Msg 2209 quotes a token** the simulator's recursive-descent parser may name differently — `insert <b/> into /r extra` is real's `'r'` and the simulator's own stopping token.
- **`SET t.col.modify(…)`** reports Msg 102 near `'.'` where real reports it near `'modify'`.
- **An insert's position keyword is found textually**: a path whose element is named `into`, `after`, `before` or `as` in the content (`insert /r/into into …`) splits at the wrong word.

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
The selected text then routes through the ordinary string→type coercion, so a non-numeric attribute read as `int` is Msg 245; an `xml` column reads a matched element's markup instead (probed 2026-10-02 against SQL Server 2025).

A colpattern beginning `@mp:` reads a metaproperty of the row node instead: `id`, `localname`, `prefix`, `namespaceuri`, `prev`, `parentid`, `parentlocalname`, `parentprefix`, `parentnamespaceuri`, and `xmltext`.
Anything else after the prefix raises `NotSupportedException` naming it.

### The rowpattern dialect

OPENXML's patterns are XPath 1.0 — the dialect MSXML gives it — so the simulator runs both the rowpattern and every colpattern straight through `XmlNode.SelectNodes`, rather than the XQuery-subset translation the `xml` type's own methods take.
Descendant shorthand (`//a`, `/root//a`), value and positional predicates (`a[@t="p"]`, `a[1]`, `a[b="bb"]`), named axes (`descendant::a`, `child::a`), wildcards, unions and a relative path all follow from that; an attribute rowpattern (`/root/a/@id`) makes each attribute a row.
A pattern the engine refuses is **Msg 6603** state 2, whose text is the parser's complaint, a blank line, and the pattern carrying a `-->x<--` marker.

### The edge table

With no `WITH` clause the rowset is real's nine-column edge table (types probe-confirmed): `id` / `parentid` / `prev` `bigint`, `nodetype` `int`, `localname` / `prefix` / `namespaceuri` / `datatype` `nvarchar(4000)`, `text` `ntext`.
It carries the matched nodes' **whole subtrees** — not the whole document — in document order: the node, then each attribute followed by its value text node, then each child's subtree.
A node is listed once however many matched subtrees hold it, so `//*` is the document's nodes rather than each element's subtree in turn (probed 2026-10-02 against SQL Server 2025).
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
  The simulator numbers them eagerly in document order at prepare, which matches real for the first read of most handles and stays stable after.
  Real can also hold an element's own text back past them: in `<root><c id="1" nm="a"><o n="10">x</o><o n="11"/></c><c id="2" nm="b &amp; c"><nm>inner</nm></c></root>` the first read numbers `x` 7 but `inner` 20, after the six attribute texts, where the simulator gives `inner` 14 (probed 2026-10-02 against SQL Server 2025).
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
A new index takes one past the table's highest surviving XML id, so a dropped top id is reused while a dropped middle one isn't (probed 2026-09-26: with 256000–256002 on a table, dropping the primary at 256000 with its secondary at 256002 gives the next index 256002).

`DROP INDEX name ON table` drops an XML index, and a primary takes the secondaries built over it along (probed 2026-09-26).
The deprecated `DROP INDEX table.name` form is refused for XML and spatial indexes alike with Msg 3749 — while compiling, so nothing ahead of it in the batch runs, and even under `IF EXISTS`.
While either kind is on a table its primary key can't be dropped (Msg 3734 then Msg 3727).
A `#temp` table takes XML indexes as a permanent one does; a full-text index refuses it (Msg 208 state 48, where a missing table is state 49).
Appended after them (real orders these interleaved; the simulator appends since consumers read by name): `is_unique` (false) / `data_space_id` (1) / `ignore_dup_key` (false) / `is_unique_constraint` (false) / `fill_factor` (0) / `is_padded` (false) / `is_disabled` (false) / `is_hypothetical` (false) / `is_ignored_in_optimization` (false) / `allow_row_locks` (true) / `allow_page_locks` (true) / `has_filter` (false) / `filter_definition` (NULL) / `xml_index_type` (0 primary, 1 secondary) / `xml_index_type_description` (`PRIMARY_XML` / `SECONDARY_XML`) / `path_id` (NULL — the column names the promoted path a *selective* XML index tracks, and an ordinary primary or secondary index reports NULL; probe-confirmed) / `auto_created` (false).
Values are the fresh-index defaults.
DacFx's XML-index reverse-engineering query reads the `fill_factor` / `is_padded` / `allow_*_locks` / `is_disabled` / `xml_index_type` / `path_id` tail.

**`XML_SCHEMA_NAMESPACE(relational_schema, collection_name)`** (`Parser/Expressions/XmlSchemaNamespaceFunction.cs`): returns the collection's XSD as `xml` — the simulator returns the raw `CREATE XML SCHEMA COLLECTION … AS '…'` source text, where real reconstructs a normalized XSD from component metadata (divergence).
Unresolved pair → Msg 6314 at execution (probe-confirmed wording incl. the space before the colon; real raises 6314 even for the built-in `sys` collection, which the simulator doesn't register — the natural miss matches).
NULL argument → Msg 8116.
The three-argument namespace-filtering form → `NotSupportedException`.
DacFx's bacpac export calls this per user collection while scripting `sys.xml_schema_collections`.

## FOR XML result serialization

`Parser/Selection.ForXml.cs` — the trailing `FOR XML { RAW[('elem')] | AUTO | PATH[('row')] | EXPLICIT } [, ELEMENTS [XSINIL|ABSENT]] [, BINARY BASE64] [, TYPE] [, ROOT[('name')]]` clause, parsed in the same `SELECT`-tail slot as FOR JSON (`Selection.ParseOptionalForXml` runs right after `ParseOptionalForJson`; a non-XML `FOR` restores the cursor for the downstream Msg 102), optionally scoped by a leading [`WITH XMLNAMESPACES`](#with-xmlnamespaces) prefix.
Mirrors the FOR JSON shape: a trailing-clause parser + a `StringBuilder` serializer over `SqlValue` rows.
The option list is order-free (`, TYPE, ROOT('r')` and `, ROOT('r'), TYPE` are the same clause) but each option may appear once — a repeated `TYPE` / `ROOT` / `ELEMENTS` / `BINARY BASE64` is **Msg 102** reported against the clause's own `XML` keyword rather than the repeated word, whatever the mode.
A `('name')` row-tag argument belongs to RAW and PATH alone; `AUTO('x')` / `EXPLICIT('x')` is **Msg 6859** severity 15.
A SELECT statement's own untyped `FOR XML` streams in 2033-unit rows and counts the rows it serialized, exactly as [FOR JSON's](json.md#for-json-result-serialization) does.

### The result column, and the `TYPE` option

| | column name | column type | empty input rowset |
|---|---|---|---|
| without `TYPE` | `XML_F52E2B61-18A1-11d1-B105-00805F49916B` | `ntext` as a SELECT statement's own result, else `nvarchar(max)` | zero rows |
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
  An unnamed column raises **Msg 6809** — unless `ELEMENTS` is in force or it is `xml`, when it is the row element's bare content; a binary or `vector` column without [`BINARY BASE64`](#binary-base64-and-autos-dbobject-references) raises **Msg 6829**; two columns writing one attribute raise **Msg 6810** (all probed 2026-10-02).
- **AUTO** — one element per FROM source, nested (see below); the row element is named after the table/alias (`<t id="1"/>`), attribute-centric or `ELEMENTS`; unnamed column → Msg 6809, no FROM clause at all → **Msg 6800**, and a binary column without `BINARY BASE64` becomes a [`dbobject` reference](#binary-base64-and-autos-dbobject-references).
- **PATH** — always element-centric; the column alias drives node placement (compiled once into a shared per-row element template, `ForXmlElement`):
  - `[@x]` → attribute `x` on the row element; `[name]` → child element; `[parent/child]` → nested elements at arbitrary depth (contiguous same-prefix steps share the parent).
  - `[text()]` / an **unnamed** column → the row element's text content; `[data()]` → text content, but adjacent `data()` atomic values are space-separated (`10 30 50`) where `text()` concatenates (`123`).
  - The rest of the [node functions](#paths-node-functions) — `[comment()]`, `[processing-instruction(target)]`, `[node()]` and `[*]` — place their own node kinds.
  - Consecutive same-name element columns concatenate their text into one element (`[x],[x]` → `<x>1020</x>`).
  - `PATH('')` suppresses the row wrapper (bare elements at document level); an attribute column under `PATH('')` raises **Msg 6864**.
  - An attribute column after a non-attribute sibling at the same level raises **Msg 6852** naming the whole alias (`a/@b`) — a comment or processing instruction counts as a non-attribute sibling for it — and one written twice on an element **Msg 6810**.
  - A NULL drops the child element it would have filled, and a nested element whose every attribute and leaf is NULL goes too (`[d/@x]`, `[d/f]` both NULL leave no `<d/>`), but the **row** element always stands: a row whose whole content is NULL is `<row/>`, not a missing row (RAW's included).
    An element with an attribute keeps it where its own leaf is NULL (`<a x="1"/>`), the nil marker behind the attributes under `XSINIL`.
  - An element closes itself only when nothing was written into it: an empty string or an empty `xml` value still opens and closes it (`<a></a>`), RAW's `ELEMENTS` included (probed 2026-10-02).
- **EXPLICIT** — the universal table, built from the projection's own column names; see [below](#explicit--the-universal-table).

### PATH's node functions

The last step of a PATH alias may be a node function instead of a name.
All six ship, and all six are matched **ordinally with no namespace prefix** — `TEXT()`, `a:comment()` and `comment (  )` are all Msg 6850, since anything the classifier doesn't recognize falls through to the [XML-name rules](#xml-names--escaped-in-raw--auto-rejected-everywhere-else) and trips on its own `(` or `*` (a prefix that isn't declared reports Msg 6846 first, as any step would).

| step | places |
|---|---|
| `text()` | the value as escaped text content |
| `data()` | the same, as an atom a space separates from an adjacent one |
| `node()` / `*` | text content that takes an `xml` value as **nodes** rather than refusing it |
| `comment()` | `<!--value-->` |
| `processing-instruction(target)` | `<?target value?>` |

Each takes a path prefix (`[a/comment()]` nests under `<a>`) and keeps its position among its siblings.
Neither constructor escapes its value — real writes it raw, so a `?>` inside a processing instruction closes it early and produces XML that won't re-parse, and a `<` inside a comment stays a `<`.
The one thing real does check is the dashes a comment can't carry: an interior `--` is **Msg 9322 state 2** and a trailing `-` is **state 3**, both raised while serializing the row.
A processing instruction's separator is exactly one space, so an empty value is `<?p ?>` and a value of `' x '` is `<?p  x ?>`.

A NULL under either constructor writes nothing at all, `ELEMENTS XSINIL` included — the nil marker is for an element that would have held a value, which `text()` / `data()` / `node()` still get.

The remaining rules, all probe-confirmed:

- **Msg 6853** — an `xml`-typed column under `text()`, `data()`, `comment()` or `processing-instruction(…)`, none of which has a text form to write it as: `Column 'comment()': the last step in the path can't be applied to XML data type or CLR type in FOR XML PATH.`, quoting the whole alias.
  `node()`, `*` and a plain element step embed its nodes instead.
- **Msg 6854** — `processing-instruction()` names no target.
- **Msg 6879** — the target is `xml`, which would construct an XML declaration.
  The check is ordinal, so `XML` and `XmL` pass.
- **Msg 6850** — the target isn't an XML name, with **no `:` allowance** unlike an element or attribute step.
  Real leaves the message's name-kind word *empty* here, so it reads `" name 'processing-instruction(1a)' contains an invalid XML identifier…"` with a leading space — probe-confirmed, not a rendering slip.

`@*` is not a node function: real reads it as an attribute named `*` and reports Msg 6850 on the `*`.
RAW and AUTO have no node functions at all — they escape the alias like any other name, so `[comment()]` becomes the attribute `comment_x0028__x0029_`.

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

### EXPLICIT — the universal table

`Parser/Selection.ForXmlExplicit.cs`.
The mode carries no shape of its own: the projection *is* the shape.
`ForXmlExplicitPlan.Build` compiles the column names into one `ForXmlExplicitTag` template per tag number at parse time (so every name diagnostic fires over an empty rowset too), and `SerializeForXmlExplicit` walks the rows once, keeping a stack of the elements still open.

**The row protocol.**
Column 1 is `Tag`, column 2 is `Parent`, and every row opens exactly **one** element — the one its `Tag` value names — beneath whichever open element its `Parent` value names, `NULL` and `0` both meaning document level.
Everything below the named parent closes first, so a row for an outer tag ends the inner elements the preceding rows opened.
Nothing is reordered and nothing collapses: two consecutive rows with identical values open two elements (unlike AUTO's levels), and a child row ahead of its parent is **Msg 6833** rather than a re-sort.
A row whose tag is already its own ancestor is **Msg 6805** state 2.

| check | error |
|---|---|
| fewer than three columns | **Msg 6801** |
| column 1 / 2 not typed `int` (`bigint`, `smallint`, a string — all rejected) | **Msg 6803** / **Msg 6804** state **1**, at parse |
| column 1 / 2 not named `Tag` / `Parent` (case-insensitively) | **Msg 6820**, naming the position and the upper-cased expectation |
| a row's `Tag` is NULL or not positive / its `Parent` is negative | **Msg 6803** / **Msg 6804** state **2** |
| a row's `Tag` / `Parent` names a tag number no column declared | **Msg 6806** / **Msg 6807** state 2 |
| a row's `Parent` names a declared tag no open element holds | **Msg 6833** |
| a row would open a tag that is already open | **Msg 6805** state 2 |

**The column-name convention** is `ElementName!TagNumber[!AttributeName[!Directive…]]`.
The tag number is decimal digits denoting a positive value with no upper bound (255, 100000 alike); a missing `!`, an empty element name, an unnamed column or a non-positive / non-numeric tag number is **Msg 6802** quoting the name as written.
Two columns giving one tag number different element names is **Msg 6812**, compared **ordinally** — `e` and `E` collide.
An absent or empty attribute name puts the value in the element's own text; several such columns concatenate.
Names reach the output **verbatim** — EXPLICIT neither escapes them the way RAW / AUTO do nor rejects them the way PATH does, so `[e f!1!a b]` emits `<e f a b="1"/>` and duplicate attribute names pass straight through.
Attributes always precede content whatever the written order (they belong to the start tag); content keeps select-list order.

| directive | effect |
|---|---|
| *(none)* | an attribute on the tag's element — an `xml`-typed column becomes a child element instead, as in RAW / AUTO |
| `element` | a child element holding the value; with an empty attribute name it is the element's text |
| `elementxsinil` | as `element`, but a NULL emits `<name xsi:nil="true"/>`; any such column puts the `xsi` declaration on the outermost element (the ROOT when there is one) |
| `xml` | the value's own markup, unescaped and unchecked — a passthrough |
| `cdata` | a CDATA section, wrapped in a child element when the column is named |
| `xmltext` | the overflow element: unnamed, its attributes and content fold onto the tag's own element; named, it becomes a child element with that name |
| `hide` | the column declares its tag and emits nothing |
| `id` / `idref` / `nmtoken` | an ordinary attribute (they only mean anything to an inline schema) |
| `idrefs` / `nmtokens` | **Msg 6826** |

Directive words are case-insensitive, and a column may carry several.
The combination rules fire in real's own order (probed one pair at a time): a repeated `hide` is **Msg 6835**, two identity directives **Msg 6813**, two of `element` / `elementxsinil` / `xml` / `xmltext` / `cdata` **Msg 6817**, `hide` beside an identity directive **Msg 6815**, and a word that is no directive at all — the empty string included — **Msg 6824**.

NULL follows the rest of FOR XML: attributes, elements, text, CDATA and the overflow all vanish, and only `elementxsinil` marks it.
A CDATA section can't escape, so real breaks it apart at every `]]>`, splitting after the **first** `]` — `a]]>b` comes back as `<![CDATA[a]]]><![CDATA[]>b]]>` — and the simulator matches.
An `xmltext` value comes back as it was written — the content byte for byte (insignificant whitespace and all), and each attribute value's source text with only the delimiter normalized to `"`, so a `>` stays literal, an entity stays an entity, and a `"` out of a single-quoted value comes back unescaped (ill-formed markup real writes too).
An overflow attribute whose name the row already wrote is dropped, a second `xmltext` on a tag is **Msg 6827**, and a value that isn't a document with a root element is **Msg 6834** — state 1 for text that parses but holds no element, state 2 for markup that doesn't parse.
A materialized overflow keeps its element open even when it contributed nothing, so `<e a="1"></e>` rather than `<e a="1"/>`.

Value formatting, escaping, `TYPE`, `ROOT`, `BINARY BASE64` and the empty-rowset asymmetry are the shared ones.
`ELEMENTS` is **Msg 6825** (placement comes from the column names), a binary column without `BINARY BASE64` is **Msg 6829** — the same message RAW gets, raised from a scan that precedes every other check, so it beats even Msg 6801 — and `XMLSCHEMA` is real's own **Msg 3625** state 17, `'Inline XSD for FOR XML EXPLICIT' is not yet implemented.`

An `xml`-typed value, in EXPLICIT alone, gets `xmlns=""` on each unprefixed top-level element that declares no default namespace of its own, after the element's attributes (`<a><i v="9" xmlns=""/></a>`; probed 2026-10-02 against SQL Server 2025).

Divergences:

- **`idrefs` / `nmtokens` always raise Msg 6826.**
  Real admits some shapes — `cast(null as int) as [e!1!k!nmtokens]` alone emits `<e></e>` — and refuses others that look alike: a nullable column (`r varchar(10) null`), a literal, and a `UNION ALL` feeding the column `NULL` in its element's own branch are all Msg 6826 (probed 2026-10-06 against SQL Server 2025), so the rule is narrower than the column's nullability and isn't pinned yet.

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

A PATH alias is a path, so each `/`-separated step is validated on its own while the message quotes the whole alias (`[x/y z]` faults on the space); the last step's leading `@` is stripped first (a bare `[@]` reports the `@` itself) and a [node function](#paths-node-functions) there is exempt from the name rules, its `processing-instruction` target taking its own.
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
Real settles the statement shape before any name (an INSERT source SELECT with an unusable alias reports 6819, not 6850) but after parsing, so a syntax error still wins; the simulator matches by checking once the clause has parsed, for a statement's own query only, off its `QueryScope` (an `INSERT` source's position) plus the parsed selection's own `IntoTarget` / `IsAssignmentOnly`.

Real reaches its verdict before resolving the target table (`INSERT INTO nosuchtable SELECT … FOR XML` reports 6819); the simulator resolves the INSERT target first, so a missing table reports Msg 208 there.

### Options

- `ELEMENTS` → element-centric (RAW/AUTO; a no-op on always-element-centric PATH).
  `ELEMENTS XSINIL` → NULL columns emit `<col xsi:nil="true"/>` and the `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"` declaration is hoisted to the `ROOT` element when present, else repeated on each top-level element (each row wrapper, or each bare element under `PATH('')`).
  `ELEMENTS ABSENT` (default) → NULL elements omitted; NULL attributes are always omitted.
- `ROOT` → wrap in `<root>…</root>` (default name `root`); `ROOT('rows')` renames; `ROOT('')` raises **Msg 6861**.
- `BINARY BASE64` → see [below](#binary-base64-and-autos-dbobject-references).

Each option may be written once — a repeat, or a second mode word (`FOR XML PATH, AUTO`), is **Msg 102** near `'XML'`, as noted at the top of this section.

### `WITH XMLNAMESPACES`

`Parser/ForXmlNamespaces.cs` — the `WITH XMLNAMESPACES ('uri' AS prefix | DEFAULT 'uri', …)` prefix.
It parses through the same seam the CTE list does (`Simulation.ParseCteBindings`, so `Simulation.ParseBodyQuery` picks it up too) and registers on `ParserContext.XmlNamespaces`, which the statement loop clears alongside `CteBindings`.
Real accepts it only in **first** position: `WITH XMLNAMESPACES (…), c AS (…) SELECT …` works, `WITH c AS (…), XMLNAMESPACES (…)` is Msg 102 near `'xmlnamespaces'`.
The bindings also reach the statement's [xml methods](#xml-method-execution).
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

Numeric/date formatting matches FOR JSON (scientific `float`/`real`, the all-zero-fraction drop, a `datetime`'s rounded milliseconds) **except** `bit` → `1`/`0` (not `true`/`false`), `uniqueidentifier` uppercases, `binary` / `varbinary` / `image` base64-encodes (always in PATH, under [`BINARY BASE64`](#binary-base64-and-autos-dbobject-references) in RAW / AUTO) as `rowversion` always does, and values are XML-escaped rather than JSON-escaped.
A `json` value goes in as its text with nothing escaped, quotes and `<` included, and a spatial or CLR user-defined column is **Msg 6865** while binding (probed 2026-10-02 against SQL Server 2025).
Escaping is position-dependent:

| position | escaped |
|---|---|
| element text | `&`→`&amp;`, `<`→`&lt;`, `>`→`&gt;`, CR→`&#x0D;` (`"` and `'` stay literal) |
| attribute value | the above plus `"`→`&quot;`, tab→`&#x09;`, LF→`&#x0A;` (`'` stays literal) |
| either | any other control character, an unpaired surrogate and U+FFFE / U+FFFF as a character reference (`&#x01;`, `&#xD83D;`) |

### Not modeled yet

The `XMLSCHEMA` directive raises `NotSupportedException` in RAW / AUTO / PATH (under a `WITH XMLNAMESPACES` prefix it raises real's own Msg 6868 first, and in EXPLICIT real's own Msg 3625).
`XMLDATA` isn't parsed at all, so it falls to Msg 102 without the prefix.
EXPLICIT's `idrefs` / `nmtokens` accept path is under [its divergences](#explicit--the-universal-table).

What real sends for the two, probed 2026-10-06 against SQL Server 2025, for whoever builds them:

- **`XMLSCHEMA`** prefixes the rows with an inline `xsd:schema` whose `targetNamespace` is `urn:schemas-microsoft-com:sql:SqlRowSet<n>` — `n` counting the session's `XMLSCHEMA` queries that name no URI, so `XMLSCHEMA('urn:a')` takes `urn:a` and leaves the count alone — importing the `sqltypes` namespace, and puts each row element in it with `xmlns`.
  A column maps to `sqltypes:<type>` (`int`, `real`, `date`, `image` …); a string column restricts `sqltypes:varchar` and the like with its collation's `sqltypes:localeId`, `sqltypes:sqlCompareOptions` and, under a SQL collation, `sqltypes:sqlSortId` (`1033`, `IgnoreCase IgnoreKanaType IgnoreWidth`, `52` for `SQL_Latin1_General_CP1_CI_AS`), plus `maxLength` unless `max`; `decimal` takes `totalDigits` / `fractionDigits`, a binary type `maxLength`, `xml` an element of `sqltypes:xml`.
  A non-nullable attribute is `use="required"`, an element-centric one has no `minOccurs="0"`, `ELEMENTS XSINIL` makes each `nillable="1"`, and AUTO nests a child level as `<xsd:element ref="schema:c" minOccurs="0" maxOccurs="unbounded"/>` declared after its parent, under an extra `xmlns:schema` prefix.
  An empty rowset still sends the schema; `TYPE` returns it as one `xml` value; PATH is Msg 6855, and a `sql_variant` attribute Msg 6847.
- **`XMLDATA`** prefixes an XDR `Schema` named `Schema<n>` by its own count, with `ElementType` / `AttributeType` declarations carrying `dt:type` (`i4`, `string` …), and puts the rows in `x-schema:#Schema<n>`; a type XDR has no mapping for (`datetime2`, `date`) is Msg 6848, and a row tag name or `ROOT` Msg 6860.

## Leading byte-order mark

A string that becomes `xml` loses a leading U+FEFF, wherever the conversion happens — a literal INSERT, a parameter, an explicit `CAST`, `SqlBulkCopy` and a TVP row all behave the same, probe-confirmed against SQL Server 2025 (2026-07-30).
The same mark in an `nvarchar` column survives, so this belongs to the type conversion rather than to any input path; the strip therefore lives in `SqlValue.FromXml`, which every xml value funnels through.
A mark that isn't leading is content and stays.

## Well-formedness

Every conversion of a value to `xml` parses it — `CAST` / `CONVERT`, a variable or column assignment, an `INSERT` / `UPDATE`, a procedure or `sp_executesql` argument, an `xml` parameter, and a binary source — so a malformed value raises there rather than at its first read (`Storage/XmlWellFormedness.cs`, whose remarks carry the position and precedence rules).
Real raises its XML parsing family, Msg 9400–9465, as `XML parsing: line L, character C, <detail>`, and a `DOCTYPE` is Msg 6359 (probed 2026-09-23 against SQL Server 2025).
The family behaves as any error does under `SET XACT_ABORT ON`, whatever the option says: uncaught it ends the batch and rolls the transaction back, caught it dooms the transaction — see [`transactions.md`](transactions.md#set-xact_abort).
`TRY_CAST` / `TRY_CONVERT` answer NULL for it, and an argument bound to a parameter reports it at line 0, as real does.

An instance holds at most 128 levels of elements: an element below them is **Msg 6335** state 102, and an attribute or text node of an element at the 128th state 101 (probed 2026-10-02 against SQL Server 2025).

A `CONVERT` style against an `xml` target is whitespace and DTD handling, not a text layout, so a binary source is parsed rather than rendered as hex; a style other than 0 to 3 is **Msg 6358**, raised once the value is non-NULL and absorbed by `TRY_CONVERT` (probed 2026-10-02).

The other direction is refused rather than cut: an instance longer than a sized string target is **Msg 6354** state 10, counting UTF-16 units, and a character an ANSI target's code page has no best fit for **Msg 6355** (`Ā` best-fits to `A`, `日` refuses), both absorbed by `TRY_CAST`.
A binary target holds the byte-order mark and the UTF-16 bytes (`0xFFFE3C00…`), counted against its length the same way (probed 2026-10-02).
A binary source is decoded in the encoding its bytes announce — a byte-order mark, an unmarked UTF-16 `<`, or the declaration — and as UTF-8 otherwise, so a stray Latin-1 byte is Msg 9420 there (probed 2026-09-23; `SqlValue.DecodeXmlBytes`).

The same pass answers the **canonical form** real serializes the stored value as, and that text is what the column or variable holds — so `CAST('<a b=''x''></a>' AS xml)` reads back as `<a b="x"/>` through a text cast, and SqlClient's own re-rendering of the value on the wire (`<a b="x" />`) matches what it renders for real (probed 2026-09-23; the rules are in the class's remarks).
The declaration is dropped, which also keeps SqlClient from refusing a value whose declaration names an 8-bit encoding; whitespace-only text is dropped unless `CONVERT` style 1 or `xml:space="preserve"` keeps it.

### Not modeled yet

- **`CONVERT` styles 2 and 3** — real's limited internal-subset DTD support, which strips the DTD, expands its entities and raises informational Msg 6338; a `DOCTYPE` under either raises `NotSupportedException`.

## Known gaps

- **XQuery features beyond the expression subset** the evaluator models — see [its own list](#not-modeled-yet).
  `.modify()`'s paths, content and values run through the same evaluator, so the subset bounds the mutator too.
  [`OPENXML`](#openxml) is unaffected — its patterns are XPath 1.0 and run through the DOM's own engine.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
- The typed-write residue under [its divergences](#divergences-1): the XSD errors outside the ones refused.
