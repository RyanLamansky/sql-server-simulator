# Full-text catalog, index and query pipeline

The catalog and index DDL, the catalog views, the property scalars, the BACPAC round-trip, and the **query pipeline** — `CONTAINS` / `FREETEXT` and the `CONTAINSTABLE` / `FREETEXTTABLE` rowsets — all ship.
The two `SEMANTIC*` rowsets still raise `NotSupportedException`.

The bacpac-loaded AW procedure `uspSearchCandidateResumes` — which runs `CONTAINSTABLE` over `HumanResources.JobCandidate`'s `xml` resume column — executes and returns rows.

## Storage

**`FullTextCatalog`** (`src/SqlServerSimulator/Schemas/FullTextCatalog.cs`) carries id + name + is_default + is_accent_sensitivity_on + principal_id + create_date.

**`Database.FullTextCatalogs`** — per-database `ConcurrentDictionary<string, FullTextCatalog>` (case-insensitive).
The catalog-id counter starts at 5 — matches Microsoft Learn's documented numbering convention (ids 0..4 are reserved internal slots).

**`FullTextIndex`** (`src/SqlServerSimulator/Schemas/FullTextIndex.cs`) carries catalog_id + key_index_name + unique_index_id (resolved at CREATE) + `List<FullTextIndexColumn>`.

**`HeapTable.FullTextIndex`** — single nullable slot (real SQL Server's invariant: at most one FT index per table).

**`FullTextIndexColumn`** carries column_id (1-based storage ordinal) + language_id + nullable type_column_id.

## Parsers — `Simulation/Simulation.FullText.cs`

```
CREATE FULLTEXT CATALOG name
    [AS DEFAULT]
    [AUTHORIZATION owner]
    [WITH ACCENT_SENSITIVITY = {ON | OFF}]
    [ON FILEGROUP fg]
    [IN PATH '…']

CREATE FULLTEXT INDEX ON table (col
        [TYPE COLUMN typeCol]
        [LANGUAGE n]
        [STATISTICAL_SEMANTICS]
        [, …])
    [KEY INDEX name]
    [ON catalog [, FILEGROUP fg] | ON (catalog [, FILEGROUP fg])]
    [WITH (option [, …]) | WITH CHANGE_TRACKING {MANUAL | AUTO | OFF} [, NO POPULATION]]

DROP FULLTEXT CATALOG name
DROP FULLTEXT INDEX ON table
```

- Filesystem-placement trailers (`ON FILEGROUP` / `IN PATH`) parse-and-discard.
- `AS DEFAULT` demotes any prior default before promoting the new catalog.
- `AUTHORIZATION owner` resolves against `Database.Principals` (default `dbo`).
- Multi-column lists supported; the `TYPE COLUMN` nested reference handles AW's `[Production].[Document]` shape (varbinary doc + extension-column pairing).
- `LANGUAGE` accepts an integer LCID literal; language-name literal parse-and-discards.
- `STATISTICAL_SEMANTICS` flag parse-and-discards.
- Both paren and bare `ON catalog` forms work.
- Trailing `WITH` options parse-and-discard in both spellings real accepts: the parenthesized list via `SkipBalancedParens`, and the bare `WITH CHANGE_TRACKING {MANUAL | AUTO | OFF} [, NO POPULATION]` most scripts write.
  The tracking mode carries no behavior — the simulator searches the live rows rather than a crawled index (see [the query pipeline](#no-index--the-rows-are-read-not-crawled)).

Statement dispatch: `Fulltext` is added to the `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Fulltext }`.
DROP is routed through `TryParseDropFullText` ahead of the generic DROP-target switch.

## The query pipeline

`Parser/FullText/` holds the whole search side; `Parser/Expressions/FullTextPredicate.cs` is the `CONTAINS` / `FREETEXT` predicate and `Parser/Selection.FullTextTable.cs` the two rowsets.

Everything below was probed against a live SQL Server 2025 (17.0.4065.4) instance with Full-Text Search installed.
`tests/SqlServerSimulator.Tests/FullTextQueryTests.cs` carries the graduated expectations, each one the reference's own answer to the same statement over the same seed rows.

### No index — the rows are read, not crawled

The simulator has **no persisted inverted index**.
A search word-breaks each candidate row's indexed columns while scanning, and matches the parsed condition against the resulting positional term list (`FullTextDocument`).

That is the load-bearing design choice here, and it buys correctness rather than speed: a row is searchable exactly when the reading transaction can see it, so rollback, MVCC snapshots, triggers, cross-database writes and temp tables all need no separate bookkeeping, and no maintenance hook can drift from the data.

The divergence it creates is **timing, and only in the simulator's favour**.
Real crawls asynchronously under `CHANGE_TRACKING AUTO`: a probe inserting a row and searching for it in the same batch found nothing, and found it about five seconds later.
The simulator answers immediately.
Both reach the same answer; real takes seconds to get there.
(A `CREATE FULLTEXT INDEX` over already-populated rows *is* synchronous on real — the full crawl completes before the statement returns — so only incremental DML lags.)

### Word breaking

`FullTextWordBreaker` models the English (LCID 1033) breaker as a rule set.
A term is a maximal run of Unicode letters and digits, plus these joins:

| Rule | Example | Terms |
| --- | --- | --- |
| Interior apostrophe joins | `O'Brien`, `don't`, `rock'n'roll` | `o'brien` — so `obrien` and `brien` match nothing |
| Interior hyphen / underscore compounds | `red-hot` | `red-hot`@1, `red`@1, `hot`@2 |
| | `under_score` | `under_score`@3, `under`@3, `score`@4 |
| Interior period / comma between digits joins | `3.14`, `1,000` | one term each |
| Everything else breaks | `a.b.c`, `end.`, `..dots` | `a` `b` `c`; `end`; `dots` |

A compound's composite shares its first part's position, and each part advances one — real's own numbering, readable from `sys.dm_fts_parser`'s `occurrence` column.
Positions matter: they are what a phrase and `NEAR` measure over.

Two folds apply to every term:

- **Case**, always.
  Matching is case-insensitive whatever the column's collation says — probe-confirmed against a `Latin1_General_CS_AS` column, where `apple` and `APPLE` both matched both `Apple Banana` and `apple cherry`.
- **Accents**, only when the backing catalog was created `WITH ACCENT_SENSITIVITY = OFF`.
  The default is ON, so `café` and `cafe` are distinct terms.

An **`xml` column** contributes its content and not its markup, which is what real indexes: probing `<r kind="cv"><skill>Engineer</skill></r>` found `Engineer` and the attribute *value* `cv`, but neither the element name `skill` nor the attribute name `kind`.
A **`varbinary` column paired through `TYPE COLUMN`** contributes nothing — real filters the document into text first, and the simulator has no filter, so the column is searchable but empty rather than word-broken as bytes.

**Stopwords** come from `FullTextLexicon.EnglishStopwords` — the exact 154 entries `sys.fulltext_system_stopwords` reports for `language_id = 1033`, single letters and single digits included.
That list is why `CONTAINS(col, '7')` and `CONTAINS(col, 'o')` match nothing while `CONTAINS(col, '42')` matches.
A stopword still **occupies a position** in the term list, which is what makes `"over the lazy dog"` match text reading exactly that while `"jumps over lazy"` matches nothing in `jumps over the lazy dog`.

An ignored word doesn't merely fail to match — it collapses the clause holding it, matching real: `the AND quick` and `quick AND NOT the` both return nothing, while `the OR quick` returns `quick`'s rows.
Any ignored word in the condition also raises real's severity-10 **Msg 9927** (`Informational: The full-text search condition contained noise word(s).`) through the `InfoMessage` surface, once per statement.

### The `contains_search_condition` grammar

```
or_expr    ::= and_expr { (OR | '|') and_expr }
and_expr   ::= near_expr { (AND | '&') near_expr | (AND NOT | '&!') near_expr }
near_expr  ::= primary { (NEAR | '~') primary }
primary    ::= '(' or_expr ')' | generic_near | formsof | isabout | term
term       ::= word | '"' phrase '"'
generic_near ::= NEAR '(' ( term { ',' term }
                         | '(' term { ',' term } ')' [ ',' (int | MAX) [ ',' (TRUE | FALSE) ] ] ) ')'
formsof    ::= FORMSOF '(' (INFLECTIONAL | THESAURUS) ',' word { ',' word } ')'
isabout    ::= ISABOUT '(' term [WEIGHT '(' number ')'] { ',' … } ')'
```

**Prefix** is the star, and it has meaning only *inside* the quotes, where it applies per whitespace-separated word: `"al* be*"` asks for two prefixes and matches `alpha beta`.
Unquoted `ch*` is the ordinary word `ch` (real matches nothing for it), and a star anywhere but a word's end is a break character, so `"*quick"` is the plain term `quick` and `"c*i"` is the two stopwords `c` and `i`.

**`NEAR`**'s distance counts the terms lying *between* the operands, so `0` means adjacent; the count includes stopwords.
The infix `a NEAR b`, the generic `NEAR(a, b)` with no distance, and `MAX` all mean "in the same row" — probed over rows holding 0 through 12 intervening terms, every one matched.
A third argument of `TRUE` additionally requires the written order, `MAX` included.

**`FORMSOF(INFLECTIONAL, …)`** expands through the stemmer below.
**`FORMSOF(THESAURUS, …)`** matches only the written word, which is what real's shipped (empty) thesaurus files give.
**`ISABOUT`** is an OR for row matching; its weights steer `RANK` only.

`, LANGUAGE n` parses on all four members and is discarded — the simulator models English and applies it whatever LCID a column carries.

### `FREETEXT`

The whole string word-breaks, stopwords drop out, and what survives is OR-ed together after inflectional expansion.
Punctuation and quotes carry no operator meaning: `FREETEXT(body, '"quick brown"')` is `quick OR brown`.
Probe-confirmed: `FREETEXT(body, 'quick geese')` returns the rows holding either, and `FREETEXT(body, 'mouse')` finds a row holding `mice`.

### The stemmer

`FullTextLexicon.Stem` reduces both the query term and the indexed term to one key, so they match when the keys agree.
Rules: strip a possessive `'s` / `'`, then one of `-ies` / `-ied` → `y`, the `-sses` / `-shes` / `-ches` / `-xes` / `-zes` and `-oes` / `-ies` plurals, plain `-s`, or verbal `-ing` / `-ed` with the doubled-consonant undo and the silent-`e` restore.
An irregular table sits ahead of the rules, carrying the strong verbs, the irregular plurals (`child` / `children`, `mouse` / `mice`, `foot` / `feet`), the Latin and Greek pairs (`analysis` / `analyses`, `index` / `indices`, `datum` / `data`, `matrix` / `matrices`), and the `-f` / `-ves` family.

A 68-word differential — one word per row, `FREETEXT` for each — put the simulator on real's answer for every word but one class, and `Inflectional_Equivalence_Classes_Match_Reference` pins twenty of them.

### `CONTAINSTABLE` / `FREETEXTTABLE`

`(table, column_spec, condition [, LANGUAGE n] [, top_n_by_rank])`, projecting `KEY` and `RANK`.
`KEY` carries the type of the column the index's `KEY INDEX` names — `int` for the usual identity primary key, `varchar(20)` for a string key — and `RANK` is always `int`.
Rows come back ordered by rank descending, and `top_n_by_rank` cuts the list there; `0` yields nothing and a negative literal is Msg 102 from the expression grammar, as on real.
Both compose as ordinary FROM sources (alias, JOIN back to the base table on `[KEY]`, APPLY), because they ride the same synthesized-plan seam as `OPENJSON` and `STRING_SPLIT`.

#### `RANK`

**`RANK` values are the simulator's own and do not match real's.**
Real's come from the engine's relevance scorer, and probing found them quantized and corpus-dependent in ways no published formula reproduces: the same term at the same frequency in a same-length document scored `32` in one table and `112` in another, and a doc-frequency sweep that moved the rank across `112 / 80 / 64 / 32` in one corpus left it flat at `32` across doc frequencies 1 through 58 in another.
What *is* reproducible about real is the structure, and that is matched exactly: the column names and types, the ordering, `top_n_by_rank`, and rank determinism (the same query twice gives the same values).

The simulator computes a BM25-shaped score over the condition's leaf terms — monotone in term frequency, falling with document length, rising with term rarity, scaled into real's 0–1000 band and clamped to at least 1.
Consumers that order by `RANK` or filter `RANK > n` behave; consumers that assert an exact value will not.

### Errors

| Case | Error |
| --- | --- |
| Table (or indexed view) carries no full-text index | **Msg 7601** sev 16 state 2, `Cannot use a CONTAINS or FREETEXT predicate on table or indexed view '<t>' because it is not full-text indexed.` |
| Column isn't one of the indexed columns | **Msg 7601** sev 16 state 3, `… on column '<c>' because it is not full-text indexed.` |
| Column doesn't exist | **Msg 207** |
| NULL, empty or all-whitespace condition | **Msg 7645** sev 15 state 1, `Null or empty full-text predicate.` |
| Condition ran out mid-parse (`'(quick'`, `'"quick" NEAR'`) | **Msg 7630** sev 15 state 1, near `<end of input>` |
| Punctuation where a term belonged (`'ISABOUT()'`; an unterminated quote reports near `"`) | **Msg 7630** state 2 |
| A word where an operator or the end belonged (`'NOT x'`, `'quick AND AND fox'`, `'FORMSOF(BOGUS, run)'`) | **Msg 7630** state 3 |
| The predicate where only a scalar may stand (CHECK constraint) | **Msg 1046**, real's subquery-not-allowed wording |

Msg 7630's message quotes the condition whole.
State 3 is what an operator keyword standing in *operand* position produces — real reads `AND` there as an ordinary word, which is why `'quick AND AND fox'` reports near `fox` and `'NOT x'` reports near `x`.

**When each error fires** follows real's split:

- The **column and table gates** (7601 / 207) bind at parse time, so a `CREATE PROCEDURE` naming an unindexed table fails to create.
- A **literal condition** parses at statement compile, so `IF 1 = 0 SELECT … CONTAINS(body, '(bad')` still raises 7630 — real rejects it too.
- A **module body** is the one place real defers: `CREATE PROCEDURE … CONTAINS(body, '(bad')` creates happily and raises at `EXEC`.
  The simulator skips the condition parse while `BatchContext.CreateTimeBinding` is set to match.
- A **variable or parameter** condition parses per execution, as on real.

### Divergences

Everything here is the word breaker's or the stemmer's lexicon, which is a data set rather than a rule and is tracked in [`backlog.md`](backlog.md).

- **Real's word breaker keeps some tokens whole that the rules break**: `.net`, `c#`, `at&t`, `u.s.a.`, `foo@bar.com` and `http://x.com` are each one term on real (alongside their parts), and real emits normalized companions beside numbers and dates (`42` → `42` + `nn42`, `2026-08-02` → the date plus `dd20260802` plus each field). The simulator breaks at every one of those punctuation marks, so `CONTAINS(col, 'net')` matches a row holding `.NET` on the simulator and not on real. Real is lexicon-driven here rather than rule-driven — `c#` is one term but `f#` breaks to `f` — so no rule set reproduces it.
- **The stemmer holds one lemma per surface form.**
  Real's expansion can span two: `leaves` reaches `leaf` *and* `leave`, where the simulator picks `leaf`.
  Every other class in the 68-word differential agreed.
- **Only English is modeled.** A column declared with another LCID is broken and stemmed by the English rules and reads the English stoplist.
- **`FORMSOF(THESAURUS, …)`** matches only the written word.
  That is what real's out-of-the-box empty thesaurus files give, so the two agree until a thesaurus is populated.
- **`RANK` values** — see above.
- **No crawl lag** — see above.
- A phrase or `NEAR` can't span two columns of a multi-column index in either engine; the simulator gets that by leaving a wide position gap between columns rather than by tracking column identity.

## Catalog views in `BuiltInResources.cs`

**`sys.fulltext_catalogs`** (9-col): `fulltext_catalog_id` / `name` / `path` (NULL — no on-disk storage) / `is_default` / `is_accent_sensitivity_on` / `data_space_id` (NULL) / `file_id` (NULL) / `principal_id` / `is_importing` (always false).

**`sys.fulltext_indexes`** (14-col): `object_id` / `unique_index_id` / `fulltext_catalog_id` / `is_enabled` (true) / `change_tracking_state` (`A`) / `change_tracking_state_desc` (`AUTO`) / `has_crawl_completed` (true) / `crawl_type` (`F`) / `crawl_type_desc` (`FULL`) / `crawl_start_date` (NULL) / `crawl_end_date` (NULL) / `stoplist_id` (**0** = system stoplist) / `data_space_id` (**1** = PRIMARY) / `property_list_id` (NULL).
`stoplist_id` and `data_space_id` are **non-NULL by design** (probe-confirmed against the reference's AW database): DacFx's `SqlFullTextIndex` reverse-engineering INNER JOINs `sys.data_spaces` on `data_space_id` (a NULL drops the parent index element, orphaning its column specifiers → client-side NRE in `SqlFullTextIndexColumnSpecifierPopulator`) and reads `stoplist_id` to choose `DoUseSystemStopList` (0 = system) vs `IsStopListOff` (NULL = disabled) — a NULL there scripts the wrong stoplist mode.

**`sys.fulltext_index_columns`** (5-col, full row): `object_id` / `column_id` / `type_column_id` / `language_id` / `statistical_semantics` (always false).

**`sys.fulltext_languages`** (2-col): `lcid` / `name` — the 59 languages a stock SQL Server 2025 instance ships (probed from the reference; static reference data).
DacFx's full-text-index-column populator INNER JOINs it by `language_id`, so an empty view NREs the column-specifier build; AW's indexes use LCID 1033 (English).

Column shapes are probe-confirmed against the local SQL Server 2025 (CU7) reference, which has Full-Text installed.

## `FULLTEXTSERVICEPROPERTY('property_name')`

`Parser/Expressions/FullTextServiceProperty.cs`.
Returns a plain `int` — probe-confirmed against SQL Server 2025 (unlike `SERVERPROPERTY`, which is `sql_variant`), so the result type is always `int` regardless of whether the argument is a compile-time constant (no constant-detection branch, unlike `ServerProperty`).

`IsFullTextInstalled` returns `1`, matching a reference with Full-Text installed and the simulator's own `SERVERPROPERTY('IsFullTextInstalled')`.
The resource-tuning properties carry the values that reference reports: `ConnectTimeout`, `LoadOSResources` and `ResourceUsage` are `0`, and `VerifyResourceUsage` is **NULL** — real singles that one out.
An unrecognized property name returns NULL `int` (probe-confirmed convention); names are case-insensitive.

## `FULLTEXTCATALOGPROPERTY('catalog_name', 'property')`

`Parser/Expressions/FullTextCatalogProperty.cs`.
Returns an `int` property of a full-text catalog resolved by name against `Database.FullTextCatalogs` (probe-confirmed return type).

Two properties are computed from the data the catalog's indexes cover, the same way a search reads it: **`ItemCount`** is the number of indexed rows and **`UniqueKeyCount`** the number of distinct non-stopword terms in them.
Both are non-zero on a populated catalog on real (a probe catalog covering 307 rows reported `ItemCount` 307 and `UniqueKeyCount` 271).
**`AccentSensitivity`** reflects the catalog's DDL-captured `ACCENT_SENSITIVITY` option (`FullTextCatalog.IsAccentSensitive`, defaulting `1` / accent-sensitive).
The remaining properties report the idle answers real gives a settled catalog, since nothing here is crawled in the background — `IndexSize`, `PopulateStatus`, `PopulateCompletionAge`, `MergeStatus`, `ImportStatus`, `LogSize` all `0`, which is what the same probe read back for the ones it could reach.
An unknown catalog name or unrecognized property returns NULL; property names are case-insensitive.

## Not modeled yet

- **The `SEMANTIC*` rowsets** (`SEMANTICKEYPHRASETABLE`, `SEMANTICSIMILARITYTABLE`, `SEMANTICSIMILARITYDETAILSTABLE`) — `NotSupportedException` at parse, naming the function. `STATISTICAL_SEMANTICS` on a column parses and is discarded.
- **`ALTER FULLTEXT CATALOG` / `INDEX`** (REORGANIZE / REBUILD / START/STOP POPULATION / ADD/DROP column) — `NotSupportedException` at parse.
- **Filesystem-placement semantics** (`ON FILEGROUP` / `IN PATH`) — parse-and-discard.
- **`sys.fulltext_document_types` / `sys.fulltext_stoplists`** — shipped empty (the stoplist registry is inert since only the system stoplist is modeled), and a custom `STOPLIST` isn't read.
- **`sys.dm_fts_parser`** — real's word-breaker inspection DMV, the probe instrument behind the [word breaking](#word-breaking) rules above.
- **`TYPE COLUMN` document extraction** — a `varbinary` column paired with an extension column is stored and projected through the catalog views, but its bytes are not filtered into text, so a search over one matches nothing. `xml` columns *are* indexed, by content — see [word breaking](#word-breaking).
- The linguistic residue — real's word-breaker token list, the thesaurus, and languages other than English — is in [Divergences](#divergences) and [`backlog.md`](backlog.md).

## BACPAC round-trip

`ModelXmlReader` dispatches `SqlFullTextCatalog` (phase 1) → `CREATE FULLTEXT CATALOG name WITH ACCENT_SENSITIVITY = {ON|OFF} [AS DEFAULT] AUTHORIZATION owner` and `SqlFullTextIndex` (phase 8) → `CREATE FULLTEXT INDEX ON t (col [TYPE COLUMN c] LANGUAGE n, …) KEY INDEX key ON catalog`.
AW's catalog + 3 indexes (incl. `Production.Document`'s multi-column `TYPE COLUMN` pairing) load skip-free and re-export/re-import cleanly against a real full-text-enabled SQL Server.
See [`bacpac-loader.md`](bacpac-loader.md).
