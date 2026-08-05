# `xml` data type + XML schema collections + XML methods + XML indexes

DDL + catalog views + xml-typed columns + `xml(schema_collection)` bindings all ship.
`.value()` / `.nodes()` / `.query()` / `.exist()` execute against a bundled [XQuery-subset evaluator](#xquery-subset-evaluator) (`Storage/XmlQuery*.cs`), and `.modify()` mutates through the same expressions (`Parser/XmlDml.cs` + `Parser/XmlDmlParser.cs`).
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

### The value model: documents and fragments

SQL Server's `xml` is CONTENT-typed, so an instance is not required to be a document: `CAST('<a/><b/>' AS xml)`, `CAST('<a>1</a>tail' AS xml)` and `CAST('abc' AS xml)` are all legal, and a `FOR XML …, TYPE` result routinely carries several top-level elements.
`Storage/XmlInstance.cs` is the single parse seam both the read methods and `.modify()` enter through, and it admits every one of those shapes.

| instance | context item for a relative path | root of an absolute path |
|---|---|---|
| one top-level element, no top-level text | that element | the document node |
| anything else (several elements, top-level text, empty) | the fragment's root node | the same root node |

The fragment row is real's own rule — real's context item is the *document node* for every instance — so `/a` reaches a top-level element of `'<a/><b/>'`, `/text()` reaches the top-level text of `'<a>1</a>tail'`, and `/` and `/a/..` both serialize the whole content.
The document row is the simulator's, and it is what makes a `.nodes()` row work: each row is one node's serialized outer XML, re-parsed as its own instance, so a downstream relative `.value('@x')` has to resolve against that element rather than above it (real hands the row a node reference instead, and its relative reads land the same way).
The divergence shows only where a relative path is written against a single-root instance directly — `@x.query('a')` over `<r><a/></r>` selects the `a` where real selects nothing.

Whitespace-only text between top-level nodes is insignificant and dropped, and an XML declaration is dropped, both matching real; text carrying anything else keeps its surrounding spaces (`'<a/> x <b/>'` round-trips as written).
That normalization is the *evaluator's* — an `xml` payload is stored verbatim, so it becomes visible only on the `.modify()` round trip.

`.modify()` edits a mutable container (`XmlInstance.CreateMutableContainer`) whose children are the instance's top-level nodes, which is what lets an edit *produce* a fragment: `insert <b/> after (/r)[1]` on `<r/>` answers `<r/><b/>` and `insert <c/> into (/)[1]` on `<a/>` answers `<a/><c/>`, both as on real.

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

- XSD text stored verbatim; AW's 6 schema-collection payloads (with embedded namespaces, complex types, restrictions, sequences) round-trip byte-identically.
  The one thing read out of the text is each element declaration's occurrence, which is what an XQuery path's static cardinality depends on — see [A schema collection narrows the cardinality](#a-schema-collection-narrows-the-cardinality).
  Instance validation against the schema isn't modeled.
- `WITH (…)` trailing options block parse-and-discards via `SkipBalancedParens`.
- xml type positions: `xml`, `xml(name)`, `xml(CONTENT name)`, `xml(DOCUMENT name)` — the `CONTENT` / `DOCUMENT` discriminator parse-and-discards.
  Both a **column** declaration and a **`DECLARE @x`** take the form, off the same peek (`PeekIsXmlSchemaArgument`) that distinguishes the schema-collection-name form from a length / MAX spec; matched only when the bare 1-part type name is `xml`.
  Unknown schema collection → Msg 208.
- Statement dispatch: `Xml` added to `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Xml }` and `ReservedKeyword { Keyword: Keyword.Primary }` (the PRIMARY XML INDEX form).
  `SCHEMA` is reserved, so the sub-keyword check uses `Keyword.Schema`.
  `COLLECTION` is a bare identifier.

## XML method execution

`Parser/Expressions/XmlMethodCall.cs` — instance methods `.value()` / `.nodes()` / `.query()` / `.exist()` / `.modify()` are intercepted in `Expression.cs`'s dotted-name dispatch (closed accept-list, matched only when followed by `(`).

- **`.value(xquery, sqltype)`** — evaluates `xquery` against the target xml via `XmlQueryEngine.EvaluateScalar`, then casts the selected node's string value to `sqltype` through `Cast.ApplyCoercion`.
  The type literal (e.g. `'nvarchar(30)'`, `'money'`, `'decimal(9, 4)'`, `'integer'`) is resolved at parse time via `SqlType.GetByName`; `integer` maps to `int`.
  Empty selection → typed NULL, and an expression real doesn't type as at most one item is [Msg 2389](#static-cardinality-and-the-msg-2389-family) at parse.
  `GetSqlType` returns the resolved target type, so projection / view-output schemas are exact (not the old nvarchar(MAX) stub).
- **`.nodes(xquery)`** — rowset-producing, valid only in a FROM / APPLY source position.
  `Selection.cs::ParseLateralFromSource` detects the `xmlexpr.nodes(...) [AS] alias(column)` shape (the parsed object name's leaf is `nodes` with a following `(`), re-parses the target as an expression, and builds a correlated single-column (`xml`) lateral plan (`Selection.XmlNodes.cs`).
  Each row's value is the serialized outer XML of one matched node, so a downstream relative `.value()` / nested `.nodes()` re-parses the fragment.
  Reaching `XmlMethodCall.Run` for `.nodes()` means it appeared in scalar position — unsupported.
- **`.exist(xquery)`** — returns `bit`: 1 when the expression's **result sequence is non-empty**, 0 otherwise, NULL when the instance is NULL (`XmlQueryEngine.EvaluateExists`).
  That is emptiness, not an effective boolean value: `exist('false()')`, `exist('0')` and `exist('1=2')` all answer 1 because each yields one item, while `exist('()')` and `exist('/r/nope')` answer 0 (probe-confirmed).
- **`.query(xquery)`** — returns `xml`: the serialized concatenation of the matched nodes in document order (atomic items separated by a single space), empty string when nothing matches, NULL when the instance is NULL (`XmlQueryEngine.EvaluateQuery`, reusing `EvaluateNodes`).
  Serialization is the engine's own (`XmlQueryEngine.SerializeNode`), not `XPathNavigator.OuterXml` — the navigator's writer indents and writes ` />`, where real writes neither.
  A fragment's own root re-declares every namespace in scope, so a relative read against a `.nodes()` row resolves the same names; real renames a re-declared prefix (`p` → `p1`) and the simulator keeps the original.
- **`.modify()`** — the mutator, a separate sublanguage; see [`.modify()` — XML-DML](#modify--xml-dml) below.
  Reaching `XmlMethodCall` for it means it was written in a value position, which is **Msg 8137**.
- `GetSqlType`: `.value()`→resolved target type, `.exist()`→bit, `.nodes()` / `.query()`→xml.
- A non-literal `xquery` / type argument raises `NotSupportedException` (dynamic XQuery isn't modeled).

## XQuery-subset evaluator

`Storage/XmlQueryParser.cs` compiles the expression, `Storage/XmlQueryExpression.cs` is the tree it builds and evaluates, and `Storage/XmlQueryEngine.cs` is the front door the four read methods and `.modify()`'s target paths enter through.

The argument is a compile-time literal everywhere, so **an expression compiles once while the SQL statement parses** — which is also where SQL Server settles its static XQuery diagnostics, so those fire there too and over an empty rowset.
Evaluation walks an `XPathNavigator` over the parsed instance, positioned on the context item [the value model](#the-value-model-documents-and-fragments) picks: the **document element** of a single-root instance — so a relative path (`Edu.Level`) resolves against that element while an absolute path (`/Resume/…`) resolves from the document root, the dual behavior `.nodes()`-serialized node references rely on — and a fragment's own root node otherwise.
An empty or whitespace-only argument is **Msg 6306** (`Invalid XQuery expression passed to XML data type method.`) in every method, `.modify()` included.

### The XQuery subset

- **Prolog**: leading `declare default element namespace "uri";` (zero or one) and `declare namespace prefix="uri";` (zero or more).
  An unprefixed *element* name takes the default element namespace; an attribute never does (XQuery's scoping rule).
  An undeclared prefix — on a name test or a function name — is **Msg 2229**.
- **Location steps**: child (the default axis), attribute (`@x`), parent (`..`), self (`.`) and the descendant-or-self expansion of `//`.
  Name tests may be prefixed (`act:number`) or not and may contain `.`; `*`, `text()`, `node()`, `comment()` and `processing-instruction()` are the node tests.
  A named axis step (`child::a`) raises `NotSupportedException`.
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
- **`and` / `or` / `not()`** over effective boolean values, `and` binding tighter, parentheses available.
  Their operands take the [condition type gate](#conditions-and-the-msg-2204-gate).
- **Arithmetic** `+` `-` `*` `div` `idiv` `mod` and unary minus.
  XQuery's name grammar swallows a `-` that follows a name character, so `@n-1` reads the attribute named `n-1` and a subtraction needs a space — real's own behavior.
- **Sequences** `(a, b)`, the empty sequence `()`, and a positional predicate over either (`(act:telephoneNumber)[1]/act:number`).
  The comma form is the grammar's own `Expr`, so it is also what the body itself and an `if` condition take — `/r/a, /r/b` is a legal `.query()` argument — while a predicate, a function argument and every FLWOR clause take a single expression, which is why `/r/a[., .]` is **Msg 9303** (`Syntax error near ',', expected ']'.`).
- **[FLWOR, quantified and conditional expressions](#flwor-quantified-and-conditional-expressions)** and the `$`-variable references they bind.
- **Direct element constructors** `<out a="{…}">{…}</out>` with arbitrary nesting, in `.query()` and `.exist()`.
  An enclosed expression contributes its nodes' markup in element content and its atomized text in an attribute value; adjacent atomic items are space-separated and `{{` / `}}` are the literal-brace escapes.
  `.value()` and `.nodes()` refuse a constructed node with **Msg 2373**, worded differently for each: `data() is not supported with constructed XML` and `'nodes()' is not supported with constructed XML`.
  A constructor resolves its name through the [prolog](#the-xquery-subset) exactly as a path step does, so `declare default element namespace "urn:d"; <b/>` builds `<b xmlns="urn:d"/>`; a declared prefix the markup never writes isn't declared on the result, as real omits it.
- **The computed `element name {…}` constructor**, which nests and takes the same content a direct one does (`element n {element m {1}}`, `element n {/r/a}`), and whose name resolves through the prolog the same way (an undeclared prefix is **Msg 2229**).
  Real takes only that **constant-QName** form: a `{…}` name expression is **Msg 9315** (`Only constant expressions are supported for the name expression of computed element and attribute constructors.`) whatever it holds, a string literal included.
  The computed comment and processing-instruction forms are real's own refusals — **Msg 9326** and **Msg 9325**, `Computed comment constructors are not supported.` / `Computed processing instruction constructors are not supported.` — in every method, `.modify()`'s insert content included.
- **Functions**: `avg` `ceiling` `concat` `contains` `count` `data` `distinct-values` `empty` `false` `floor` `last` `local-name` `lower-case` `max` `min` `namespace-uri` `not` `number` `position` `round` `string` `string-length` `substring` `sum` `true` `upper-case`, reachable bare or through the predeclared `fn:` prefix.
  Anything else in the function namespace is **Msg 2395**, `There is no function '{http://www.w3.org/2004/07/xpath-functions}:starts-with()'` — which is what real answers for `starts-with` / `ends-with` / `normalize-space` / `translate` / `boolean` / `exists` / `abs` / `zero-or-one` too, since its library doesn't carry them either.
  Arity is part of the signature: too few arguments is **Msg 2236** (`There are not enough actual arguments in the call to function "contains()".`) and too many **Msg 2238** (`Too many arguments in call to function 'count()'` — real punctuates the two differently).

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

Three receivers carry a binding, and the third is what the AdventureWorks view needs:

| receiver | where the binding comes from |
|---|---|
| a column | `HeapColumn.XmlSchemaCollection`, resolved against the query scope's FROM sources |
| a variable | `VariableSlot.XmlSchemaCollection`, from `DECLARE @x xml(<collection>)` |
| a `.nodes()` row column | the binding of the `.nodes()` target, stamped on the produced column — each row is a node of that instance |

Everything else — a literal, a `CAST`, an expression — is untyped, as on real.

Only the cardinality is read out of the schema: instance **validation** against the XSD isn't modeled (see [Not modeled yet](#not-modeled-yet)), and neither is real's schema-derived static *type name*, so a Msg 2389 raised over a typed value still quotes `xdt:untypedAtomic` where real quotes (say) `xs:string`.

### Not modeled yet

- The computed **`attribute name {…}`** and **`text {…}`** constructors in a read method (`NotSupportedException`; both ship in `.modify()`'s insert content, and the computed `element name {…}` ships in both), and the direct comment / processing-instruction forms (`<!-- c -->`, `<?pi d?>`); the direct element form ships.
  An `attribute` constructor's value would have to be hoisted into the enclosing element's tag, which the splice-and-parse constructor model doesn't reach; real refuses one outside an element anyway (**Msg 2396**, not modeled).
  `.modify()`'s insert content has its own constructor set — see below.
- **An arbitrary XQuery expression inside `.modify()`'s insert content** — a `{…}` there is the value sublanguage (literals and the `sql:` accessors), so `insert <b>{for $i in /r/a return string($i)}</b>` doesn't parse.
  A mutator's *target path* takes the whole expression grammar, FLWOR included.
- `sql:variable()` / `sql:column()` accessors outside `.modify()`'s value terms, and the `xs:` constructor functions (`xs:integer(@a)`), both `NotSupportedException`.
- Named axis steps (`child::` / `descendant::` / …), `NotSupportedException`.
- **Msg 2396** — real refuses a `.query()` whose result is a top-level attribute (`/r/a/@x`) and **Msg 2390** the same for `.value()`; the simulator serializes the attribute instead.
  `.value()`'s 2390 can't be modeled while a `.nodes()` row is re-parsed as a document, since that is what makes the legitimate `n.ref.value('@x', …)` a top-level attribute read.
- **Instance validation against a bound schema collection**, and the schema-derived **static type name** — the binding is read for element occurrence alone (see [A schema collection narrows the cardinality](#a-schema-collection-narrows-the-cardinality)), so nothing checks an INSERT / UPDATE / `.modify()` edit against the XSD and a diagnostic over a typed value still quotes `xdt:untypedAtomic`.

### Divergences

- A **single-root instance's context item is its document element** rather than real's document node, so a relative path written directly against one resolves a level lower — see [the value model](#the-value-model-documents-and-fragments), which also covers what a fragment does instead.
- `.value()` casts go through the standard string→type coercion (`casting.md`'s flexible string→date-like parser), so the AdventureWorks `vJobCandidateEducation` / `vJobCandidateEmployment` / `vPersonDemographics` views — which wrap `.value()` date strings in `CONVERT(datetime, …, 101)` — resolve.
- Msg 2209's quoted token comes from the simulator's own recursive-descent cursor, so a malformed expression may name a different token than real's parser does.
  Real also splits its generic syntax errors further than the simulator does — a path that ends mid-step is its **Msg 9341** (`Syntax error near '<eof>', expected a step expression.`) where the simulator reports 2209 — and a construct keyword written without the token that identifies it (`for i in …`, `if 1=1 then …`) is real's 2209 near the *keyword* while the simulator names the token it stopped on.
- **`position()` / `last()` legality is lexical.** The simulator allows them anywhere inside a written predicate, so a FLWOR nested in one can read them; real's rule is its own binder's.
- `fn:min` / `fn:max` compare numerically; real compares by the operand's own type, so a string sequence orders differently.
- **A constructed node re-parses.** Each evaluation splices the enclosed sequences into the literal markup and parses the result, so a value carrying markup-significant text is escaped by position rather than kept as a node identity; the serialized answer matches real for every probed shape.

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
| the instance is NULL — an unassigned variable or a NULL cell in an updated row | **Msg 5302** — `Mutator 'modify()' on '@x' cannot be called on a null value.` (the name as written, `@` included for a variable) |

Real reaches Msg 8137 before the SET-option gate, so `.modify()` in a select list reports it even from a session holding `QUOTED_IDENTIFIER` the wrong way; the mutator positions take the gate (**Msg 1934**) like every other XML method, naming the statement's own verb — `SELECT` for `SET @x.modify(…)`, `UPDATE` for the UPDATE form.

### The three statements

```
insert <content> [as first | as last] {into | before | after} <target>
delete <target>
replace value of <target> with <value>
```

**`insert`** — content is one item or a parenthesized sequence.
Item forms: a direct element constructor with arbitrary nesting (`<n><m><o>3</o></m></n>`), a direct comment (`<!-- c -->`) or processing instruction (`<?pi data?>`), the computed `element n {…}` / `attribute n {…}` / `text {…}` constructors, and a bare `sql:variable("@v")` / `sql:column("c")` carrying `xml`.
A computed `element` takes a content sequence of the same items, so constructors nest (`element n {element m {1}}`, `element n {attribute a {1}}`) and adjacent atomic items join with a single space (`element n {"a","b"}` is `<n>a b</n>`); a `{…}` name expression is **Msg 9315** and the computed comment / processing-instruction forms are **Msg 9326** / **Msg 9325**, all three real's own refusals shared with the read methods.
An element constructor's `{…}` enclosed expressions are substituted with the value's XML text and escaped by position (element content vs attribute value); `{{` / `}}` are the literal-brace escapes.
A constructor resolves its **name through the prolog** exactly as a path step does — `declare default element namespace "urn:d"; insert <b/>` builds a `urn:d` element, and `declare namespace p="urn:x"; insert <p:b/>` a `urn:x` one (probe-confirmed) — while an already-serialized `sql:variable` / `sql:column` fragment brings its own scope.
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
| the argument parses as XQuery but isn't XML-DML (`'/r'`, `'count(/r)'`, `'<a/>'`, a FLWOR) | **Msg 6305** — `XQuery data manipulation expression required in XML data type method.` |
| the argument doesn't parse as XQuery either (`'('`, `'/r['`, a bare `'insert'`) | **Msg 2209** — `XQuery [modify()]: Syntax error near '<eof>'` |
| the argument is empty or whitespace | **Msg 6306** — `Invalid XQuery expression passed to XML data type method.` |
| an `insert` would duplicate an attribute name | **Msg 6308** — `XML well-formedness check: Duplicate attribute 'n'. Rewrite your XQuery so it returns well-formed XML.` |

The insert checks run in real's own order — target cardinality, then content type, then the attribute-position rule, then the target's node kind (probed one shape at a time), so `insert "abc" into (/r/a)` reports 2226 rather than 2207.
The Msg 6305 / 2209 split is real's too: text no XML-DML keyword opened is handed to the expression grammar, and only its failure reports 2209.

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

- **Typed xml is edited as untyped.** The `xml(collection)` binding is metadata only (no XSD parse anywhere in the simulator), so a `.modify()` on a typed column neither validates the result (real's **Msg 6923**) nor types the `with` value against the schema (real's **Msg 2247**), and `replace value of` still requires a `text()` / attribute target where real would accept the typed element itself.
- Real's **Msg 2209 quotes a token** the simulator's recursive-descent parser may name differently — `insert <b/> into /r extra` is real's `'r'` and the simulator's own stopping token.
- **`SET t.col.modify(…)`** reports Msg 102 near `'.'` where real reports it near `'modify'`.
- A **prolog prefix** used by a constructor is re-declared on the inserted element whether or not the insertion point already binds it; real omits the declaration when the prefix is already in scope.
- An `insert`'s content sequence mixing an atomic item with a node one isn't rejected (real's **Msg 2210** heterogeneous-sequence rule); the simulator writes the atomic as text inside a constructor and reports Msg 2207 for a top-level one.

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

`Parser/Selection.ForXml.cs` — the trailing `FOR XML { RAW[('elem')] | AUTO | PATH[('row')] | EXPLICIT } [, ELEMENTS [XSINIL|ABSENT]] [, BINARY BASE64] [, TYPE] [, ROOT[('name')]]` clause, parsed in the same `SELECT`-tail slot as FOR JSON (`Selection.ParseOptionalForXml` runs right after `ParseOptionalForJson`; a non-XML `FOR` restores the cursor for the downstream Msg 102), optionally scoped by a leading [`WITH XMLNAMESPACES`](#with-xmlnamespaces) prefix.
Mirrors the FOR JSON shape: a trailing-clause parser + a `StringBuilder` serializer over `SqlValue` rows.
The option list is order-free (`, TYPE, ROOT('r')` and `, ROOT('r'), TYPE` are the same clause) but each option may appear once — a repeated `TYPE` / `ROOT` / `ELEMENTS` / `BINARY BASE64` is **Msg 102** reported against the clause's own `XML` keyword rather than the repeated word, whatever the mode.
A `('name')` row-tag argument belongs to RAW and PATH alone; `AUTO('x')` / `EXPLICIT('x')` is **Msg 6859** severity 15.
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
  - The rest of the [node functions](#paths-node-functions) — `[comment()]`, `[processing-instruction(target)]`, `[node()]` and `[*]` — place their own node kinds.
  - Consecutive same-name element columns concatenate their text into one element (`[x],[x]` → `<x>1020</x>`).
  - `PATH('')` suppresses the row wrapper (bare elements at document level); an attribute column under `PATH('')` raises **Msg 6864**.
  - An attribute column after a non-attribute sibling at the same level raises **Msg 6852** — a comment or processing instruction counts as a non-attribute sibling for it.
  - A NULL drops the child element it would have filled, but the **row** element always stands: a row whose whole content is NULL is `<row/>`, not a missing row (RAW's included).
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

Divergences:

- **An embedded `xml` value carries no `xmlns=""`.**
  Real re-serializes an `xml`-typed column's fragment in EXPLICIT alone and stamps `xmlns=""` on its unprefixed top-level elements (`<a><b xmlns="">x</b></a>`); the simulator embeds the stored text the way RAW and AUTO do.
- **`idrefs` / `nmtokens` always raise Msg 6826.**
  Real admits one where the column's expression is statically nullable — the shape that feeds one value per row in and merges them into a space-joined attribute — and reports 6826 otherwise; the simulator has no expression-nullability model, so it reports what real gives the non-nullable shape.

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
Real settles the statement shape before any name (an INSERT source SELECT with an unusable alias reports 6819, not 6850) but after parsing, so a syntax error still wins; the simulator matches by checking once the clause has parsed, at nesting depth 0 only, off `ParserContext.InInsertSourceSelect` (set by the INSERT parser) plus the parsed selection's own `IntoTarget` / `IsAssignmentOnly`.

Real reaches its verdict before resolving the target table (`INSERT INTO nosuchtable SELECT … FOR XML` reports 6819); the simulator resolves the INSERT target first, so a missing table reports Msg 208 there.

### Options

- `ELEMENTS` → element-centric (RAW/AUTO; a no-op on always-element-centric PATH).
  `ELEMENTS XSINIL` → NULL columns emit `<col xsi:nil="true"/>` and the `xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"` declaration is hoisted to the `ROOT` element when present, else repeated on each top-level element (each row wrapper, or each bare element under `PATH('')`).
  `ELEMENTS ABSENT` (default) → NULL elements omitted; NULL attributes are always omitted.
- `ROOT` → wrap in `<root>…</root>` (default name `root`); `ROOT('rows')` renames; `ROOT('')` raises **Msg 6861**.
- `BINARY BASE64` → see [below](#binary-base64-and-autos-dbobject-references).

Each option may be written once — a repeat is **Msg 102** near `'XML'`, as noted at the top of this section.

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

The `XMLSCHEMA` directive raises `NotSupportedException` in RAW / AUTO / PATH (under a `WITH XMLNAMESPACES` prefix it raises real's own Msg 6868 first, and in EXPLICIT real's own Msg 3625).
`XMLDATA` isn't parsed at all, so it falls to Msg 102 without the prefix.
One-row chunking is the shared approximation noted above, and EXPLICIT's `idrefs` / `nmtokens` accept path is under [its divergences](#explicit--the-universal-table).

See [`backlog.md`](backlog.md).

## Leading byte-order mark

A string that becomes `xml` loses a leading U+FEFF, wherever the conversion happens — a literal INSERT, a parameter, an explicit `CAST`, `SqlBulkCopy` and a TVP row all behave the same, probe-confirmed against SQL Server 2025 (2026-07-30).
The same mark in an `nvarchar` column survives, so this belongs to the type conversion rather than to any input path; the strip therefore lives in `SqlValue.FromXml`, which every xml value funnels through.
A mark that isn't leading is content and stays.

## Known gaps

- **XQuery features beyond the expression subset** the evaluator models — see [its own list](#not-modeled-yet) (FLWOR, constructors in a read method's argument, `sql:` accessors, `xs:` constructor functions, named axes).
  `.modify()`'s paths run through the same evaluator, so the subset bounds the mutator too.
  [`OPENXML`](#openxml) is unaffected — its patterns are XPath 1.0 and run through the DOM's own engine.
- **XSD validation** against `xml(schema_collection)` bindings — nothing validates an INSERT, an UPDATE or a `.modify()` edit, and a typed instance's paths carry untyped static types (see [`.modify()`'s divergences](#divergences-1)).
- **`ALTER XML SCHEMA COLLECTION ADD`** — incremental schema additions.
- **`SELECTIVE XML INDEX`** variant (SQL Server 2014+).
- The `.modify()` residue listed under [its divergences](#divergences-1): a multi-root insert result, attribute placement, the Msg 6305 / 2209 split, and the computed `element {…}` constructor.
