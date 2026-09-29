# Full-text catalog, index and query pipeline

The catalog and index DDL, the catalog views, the property scalars, the BACPAC round-trip, and the **query pipeline** — `CONTAINS` / `FREETEXT` and the `CONTAINSTABLE` / `FREETEXTTABLE` rowsets — all ship.
The two `SEMANTIC*` rowsets still raise `NotSupportedException`.
`sys.dm_fts_parser` and `sys.fulltext_system_stopwords` report what the pipeline does.

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
    [WITH [(] option [, …] [)]]

ALTER FULLTEXT CATALOG name {REBUILD [WITH ACCENT_SENSITIVITY = {ON | OFF}] | REORGANIZE | AS DEFAULT}
ALTER FULLTEXT INDEX ON table {ENABLE | DISABLE
    | SET CHANGE_TRACKING [=] {MANUAL | AUTO | OFF}
    | SET STOPLIST [=] {OFF | SYSTEM | name} [WITH NO POPULATION]
    | SET SEARCH PROPERTY LIST [=] {OFF | name} [WITH NO POPULATION]
    | ADD (col [TYPE COLUMN typeCol] [LANGUAGE n] [STATISTICAL_SEMANTICS] [, …]) [WITH NO POPULATION]
    | DROP (col [, …]) [WITH NO POPULATION]
    | ALTER COLUMN col {ADD | DROP} STATISTICAL_SEMANTICS [WITH NO POPULATION]
    | START {FULL | INCREMENTAL | UPDATE} POPULATION
    | {STOP | PAUSE | RESUME} POPULATION}

DROP FULLTEXT CATALOG name
DROP FULLTEXT INDEX ON table
```

`ALTER FULLTEXT INDEX` lives in `Simulation/Simulation.AlterFullText.cs`.

- Filesystem-placement trailers (`ON FILEGROUP` / `IN PATH`) parse-and-discard.
- `AS DEFAULT` demotes any prior default before promoting the new catalog.
- `AUTHORIZATION owner` resolves against `Database.Principals` (default `dbo`).
- Multi-column lists supported; the `TYPE COLUMN` nested reference handles AW's `[Production].[Document]` shape (varbinary doc + extension-column pairing).
- `LANGUAGE` accepts an LCID or a language name (`'German'`), resolved as the predicates' argument is (see [Languages](#languages)); a column without one gets 1033.
- Both paren and bare `ON catalog` forms work.
- `WITH` takes `CHANGE_TRACKING [=] {MANUAL | AUTO | OFF [, NO POPULATION]}`, `STOPLIST [=] {OFF | SYSTEM | name}` and `SEARCH PROPERTY LIST [=] name`, parenthesized or bare.
  The tracking mode, `is_enabled` and the stoplist are kept on the `FullTextIndex` and reported by `sys.fulltext_indexes`; the tracking mode carries no search behavior — the simulator searches the live rows rather than a crawled index (see [the query pipeline](#no-index--the-rows-are-read-not-crawled)).
  `STOPLIST OFF` does: a search then treats the system stoplist's words as ordinary terms (probed 2026-09-26 against SQL Server 2025: `CONTAINS(s, 'the')` matched), and `FULLTEXTCATALOGPROPERTY`'s `UniqueKeyCount` counts them.
  The binding reads the stoplist setting when the search runs, so an `ALTER` reaches a cached plan; an accent-sensitivity change bumps the schema version instead, since the fold binds at compile.
- Every full-text DDL statement refuses to run inside a user transaction (Msg 574, naming the statement), and a column list is checked column by column as real does — missing, not a text or document type, a document type without `TYPE COLUMN`, named twice, `STATISTICAL_SEMANTICS` — see `ResolveFullTextColumns` (probed 2026-09-26).
- Only the system stoplist exists here, and no search property list, so naming one is Msg 30023 / 30025.

### `ALTER FULLTEXT INDEX` against a settled index

Real populates asynchronously, so several `ALTER` verbs answer differently while a crawl is running, warning that a population is currently active or will not be stopped.
The simulator has no crawl and answers as real does once population has completed (probed 2026-09-26 against SQL Server 2025), which depends on the tracking mode: under `AUTO` a `START FULL | INCREMENTAL POPULATION` still warns 7636, `STOP` warns 7676 and `RESUME` is silent, while under `MANUAL` / `OFF` `RESUME` warns 9975; `START UPDATE POPULATION` with tracking `OFF` is Msg 7664.
Dropping the last column disables the index and adding one back re-enables it; a disabled index keeps answering searches, as real's does from what it crawled.

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

`FullTextWordBreaker` reproduces SQL Server 2025's English (LCID 1033) breaker, which the neutral (0) and British English (2057) breakers match term for term.
It was fitted and measured against `sys.dm_fts_parser` (probed 2026-09-29), comparing every row a text breaks to — term, occurrence, noise flag, and the sentence and paragraph markers:

| Corpus | Inputs | Identical output |
| --- | --- | --- |
| Generated classes: every punctuation mark between and around letters and digits, apostrophes, hyphens, dotted words, numbers, currencies, units, dates, times, addresses, paths, non-ASCII scripts, emoji — English and neutral | 5,298 | 99.7% (99.8% accent-insensitive) |
| Held out: realistic sentences and paragraphs mixing all of the above | 1,500 | 99.9% |
| Held out: random punctuation-heavy strings and shuffled tokens | 2,989 | 99.5% |

The index side agrees: for all 1,500 held-out texts, a real full-text index's `sys.dm_fts_index_keywords_position_by_document` held exactly the parser's non-noise terms at the parser's occurrences.
`FullTextWordBreakerTests` pins a hundred-odd of the probed inputs.

A text breaks at whitespace into chunks, and each chunk left to right into terms.
The classes the breaker recognizes, most of them emitting a composite and its parts at successive positions:

| Class | Example | Terms |
| --- | --- | --- |
| Interior apostrophe joins (`'`, `‘`, `’`, `` ` ``) | `O'Brien`, `5'10` | `o'brien`; a trailing one drops (`dogs'` → `dogs`) |
| Hyphen or underscore compound (`-`, `_`, `–`, `—`) | `red-hot` | `red-hot`@1, `red`@1, `hot`@2 — but `e-mail` stays whole |
| `&` beside a single letter | `at&t`, `q&a` | one term; `abc&def` breaks |
| Dotted compound, when the last segment reads as a file extension or top-level domain | `file.txt`, `www.example.com` | composite and parts; `ab.cd` breaks, `abc.cd` doesn't (the domain needs three characters before it) |
| Dotted acronym of single letters | `U.S.A.` | `u.s.a.` and `usa`, with `e.g.` / `i.e.` / `ph.d.` kept as `e.g` … |
| Lexicon tokens | `c#`, `c++`, `j#`, `j++`, `.net`, `km/h`, `m/s`, `it!`, `yahoo!` | one term each — `f#` breaks to `f` |
| Numbers | `42`, `1,000.50`, `-5`, `007` | the written form and `nn42`, `nn1000d5`, `nn5-`, `nn7` |
| Currency, attached or spaced, before or after | `$5`, `5€`, `USD 5`, `$405 USD` | `nn5$`, `nn5€`, `nn5usd` — ISO codes in upper case only, plus a few local spellings (`kr`, `Ft`, `R$`) |
| Space-grouped digits | `1 000 000` | the composite with `nn1000000`, then each group |
| Dates, numeric or with a capitalized month name | `2026-08-02`, `08/02/2026`, `Aug 2, 2026` | the written form, `dd20260802` (both `dd19…` and `dd20…` for a two-digit year) and the three fields |
| Times | `10:30`, `5pm`, `10 am`, `3 o'clock` | the written form with `tt24103000`, and an hour from 1 to 12 alone also `tt24223000` |
| E-mail addresses, URLs, drive and UNC paths | `foo@bar.com` | the whole and each letter-and-digit run |
| Emoticons | `:)`, `;-(` | one term |

The rules behind each row are documented on `FullTextWordBreaker` and its lexicon partial, whose tables — the abbreviations that suppress a sentence break, the extensions and domains, the currency codes — were read off the parser by sweeping candidate lists: every one- to three-letter word in three casings, the English stopwords, a few hundred abbreviations and common words, every two-letter domain.
Numeric dates read year-month-day with a four-digit year first (year-day-month with periods) and day-month-year with it last, the two fields swapping when the month would be over 12; years run from 1000 to 2999, and a shape that isn't a valid date falls back to a number compound.

**Positions skip at sentence and paragraph breaks.**
A sentence break — `!`, `?`, `…`, or one or two periods, closed at most by a quote or bracket, unless the word before is an abbreviation (`Mr.`, `etc.`, a single capital) — puts the next word nine positions past the last, and a line break 129 past; `sys.dm_fts_parser` reports the marker one short of the word.
Three periods (`one... two`) end no sentence.
The gap is what makes `"end next"` miss `end. Next`, and it counts toward `NEAR`'s distance.

Three folds apply:

- **Case**, always.
  Matching is case-insensitive whatever the column's collation says — probe-confirmed against a `Latin1_General_CS_AS` column, where `apple` and `APPLE` both matched both `Apple Banana` and `apple cherry`.
- **Compatibility forms**, always: full-width and half-width forms, ligatures and the `dž` digraphs to their plain spelling, and `ß` / `æ` / `œ` / `ĳ` to two letters, so `straße` is `strasse` even to an accent-sensitive catalog.
- **Accents**, only when the backing catalog was created `WITH ACCENT_SENSITIVITY = OFF`.
  The default is ON, so `café` and `cafe` are distinct terms; the fold strips Latin, Greek and Cyrillic marks and leaves a Devanagari virama or a kana voicing mark alone.

An **`xml` column** contributes its content and not its markup, which is what real indexes: probing `<r kind="cv"><skill>Engineer</skill></r>` found `Engineer` and the attribute *value* `cv`, but neither the element name `skill` nor the attribute name `kind`.
A **`varbinary` column paired through `TYPE COLUMN`** contributes nothing — real filters the document into text first, and the simulator has no filter, so the column is searchable but empty rather than word-broken as bytes.

**Stopwords** come from each column's language: `FullTextLanguage` carries every system stoplist `sys.fulltext_system_stopwords` reports — 15,829 words over 46 languages, kept verbatim in the embedded `SystemStopwords.tsv` and served by that view.
English's 154 hold the single letters and digits, which is why `CONTAINS(col, '7')` and `CONTAINS(col, 'o')` match nothing while `CONTAINS(col, '42')` matches; a number's companion is noise when the number is (`nn5`).
A noise word is never indexed — so `the` finds nothing even searched under a language whose stoplist lacks it — but it keeps its position.

An ignored word doesn't merely fail to match — it collapses the clause holding it, matching real: `the AND quick` and `quick AND NOT the` both return nothing, while `the OR quick` returns `quick`'s rows.
Any ignored word in the condition also raises real's severity-10 **Msg 9927** (`Informational: The full-text search condition contained noise word(s).`) through the `InfoMessage` surface, once per statement.

### How a search reads the breaker's output

A condition's term breaks exactly as indexed text does, into a run of positions each holding the terms the breaker put there, any one of which matches.
The rules below were fitted with a differential of 4,334 `CONTAINS` / `FREETEXT` conditions — phrases, prefixes, `NEAR`, `FORMSOF`, boolean combinations, formatted numbers, dates and amounts — over 300 and 600 indexed rows (probed 2026-09-29): every one of the first 2,089 and all but 2 of the next 2,245 returned real's rows, the two misses reading one document where an `o'clock` time splits a date.

- **A position matches through any of its terms**, so `42` finds `42.0` through `nn42`, `"22:30"` finds `10:30`'s afternoon reading, and `"red hot"` finds `red-hot`.
- **A composite stands for its first position only.**
  A phrase still needs the parts after it, so `"2026-08-02"` does not find `August 2, 2026`, though both carry `dd20260802`, and `10` does not find `10:30`.
- **A noise word constrains only the distance around it.**
  `"jumps over lazy"` misses `jumps over the lazy`, but a noise word leading or trailing a phrase drops out: `"with ships"` finds a row starting `ships`.
- **`NEAR` measures between whole occurrences**: a phrase spans its positions, trailing noise included, so nothing lies between `carbon-fiber` and `requires` in `carbon-fiber requires`.
  Each operand needs its own occurrence, so `NEAR((requires, requires), 0)` needs the word twice, adjacent.
- **A star prefixes every part of the word it ends**, and every term at those positions: `"red-hot*"` finds `reds hotter`, `"the*"` finds `theory`, and `"07/26/*"` reaches `nn79` through `07`'s companion `nn7`.
  A starred phrase holding an unstarred noise word matches nothing (`"word of mou*"`).
- **`FORMSOF(INFLECTIONAL, …)` expands a single-letter noise word** (`x` to `x's`), which then has to match, while a longer one stays out as in a phrase — so `FORMSOF(INFLECTIONAL, "vitamin b complex")` finds nothing and `FORMSOF(INFLECTIONAL, "word of mouth")` finds the phrase; a single digit has no forms, so its leaf matches nothing.

### Languages

The column's `LANGUAGE` (1033 when the index names none, real's `default full-text language`) picks the stoplist its content is indexed under, and the condition is read in the first searched column's language unless the call names one.
`LANGUAGE n` takes an LCID, a binary such as `0x407`, or a name or alias `sys.syslanguages` knows (`'German'`), and refuses an LCID without a full-text language (**Msg 7696**) and an unknown name (**Msg 7678**).

Only English morphology is modeled: under neutral, English and British English, inflectional searches expand through the stemmer below; under any other language they match the written form, as real's German `FREETEXT(s, 'run', LANGUAGE 1031)` fails to find `running`.
Every language breaks words by the English rules; how far the real breakers differ is in [Divergences](#divergences).

### `sys.dm_fts_parser`

`sys.dm_fts_parser('condition', lcid, stoplist_id, accent_sensitivity)` (`Parser/Selection.FullTextParser.cs`) parses a condition the way `CONTAINS` does and lists what the engine makes of it: one group per leaf in written order (a word, a phrase, each `FORMSOF` argument), each term at its occurrence with its noise flag, the markers, an inflectional leaf's expansions ahead of the word they grow from, and the leaf's source text.
The stoplist argument is 0 for the language's system stoplist or NULL for none (anything else is **Msg 30092**); an accent sensitivity of 0 folds accents; a NULL condition, LCID or accent sensitivity is **Msg 7645** at severity 16 with states 201, 202 and 203.
A differential over 44 conditions matched real in every column but `keyword` for 43; the other lists verb forms real's lexicon withholds (see [The stemmer](#the-stemmer)).
`keyword` is the term in UTF-16 big-endian, which is real's too except for an accented term, whose keyword real encodes its own way.

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
**`FORMSOF(THESAURUS, …)`** matches only the written word — see [The thesaurus](#the-thesaurus).
**`ISABOUT`** is an OR for row matching; its weights steer `RANK` only.
An unquoted word ends at `!` as at the other operator marks, so `'lightweight!'` is **Msg 7630** near `!`.

`, LANGUAGE n` works on all four members — see [Languages](#languages).

### `FREETEXT`

The whole string word-breaks, stopwords drop out, and what survives is OR-ed together after inflectional expansion.
Punctuation and quotes carry no operator meaning: `FREETEXT(body, '"quick brown"')` is `quick OR brown`.
Probe-confirmed: `FREETEXT(body, 'quick geese')` returns the rows holding either, and `FREETEXT(body, 'mouse')` finds a row holding `mice`.

### The stemmer

`FullTextLexicon.Stem` reduces both the query term and the indexed term to one key, so they match when the keys agree.
Rules: strip a possessive `'s` / `'`, then one of `-ies` / `-ied` → `y`, the `-sses` / `-shes` / `-ches` / `-xes` / `-zes` and `-oes` / `-ies` plurals, plain `-s`, or verbal `-ing` / `-ed` with the doubled-consonant undo and the silent-`e` restore.
An irregular table sits ahead of the rules, carrying the strong verbs, the irregular plurals (`child` / `children`, `mouse` / `mice`, `foot` / `feet`), the Latin and Greek pairs (`analysis` / `analyses`, `index` / `indices`, `datum` / `data`, `matrix` / `matrices`), and the `-f` / `-ves` family.

A 68-word differential — one word per row, `FREETEXT` for each — put the simulator on real's answer for every word but one class, and `Inflectional_Equivalence_Classes_Match_Reference` pins twenty of them.

`sys.dm_fts_parser` lists a word's forms as those the stemmer maps back to it: the irregular row, or the regular plural, past and gerund with the two possessives — `run` gets real's exact five, `ran`, `run's`, `running`, `runs`, `runs'`.
Real consults a part-of-speech lexicon and lists only the paradigms a word has, so `red` gets no verb forms there where the simulator lists `redded` and `redding`.

### The thesaurus

Real's out-of-the-box thesaurus is empty — the shipped files hold commented-out samples — and `FORMSOF(THESAURUS, IE)`, `NT5` and `jog`, the sample entries, each expand to nothing but themselves (probed 2026-09-29).
The simulator models that: `FORMSOF(THESAURUS, …)` and `FREETEXT`'s thesaurus pass match the written word only.
Populating a thesaurus means editing XML files in the server's install tree and loading them with `sys.sp_fulltext_load_thesaurus_file`, which isn't modeled; see [Not modeled yet](#not-modeled-yet).

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
| `LANGUAGE` naming an LCID with no full-text language | **Msg 7696** state 10 |
| `LANGUAGE` naming no language `sys.syslanguages` knows | **Msg 7678** state 12, quoting the name |

Msg 7630's message quotes the condition whole.
State 3 is what an operator keyword standing in *operand* position produces — real reads `AND` there as an ordinary word, which is why `'quick AND AND fox'` reports near `fox` and `'NOT x'` reports near `x`.

**When each error fires** follows real's split:

- The **column and table gates** (7601 / 207) bind at parse time, so a `CREATE PROCEDURE` naming an unindexed table fails to create.
- A **literal condition** parses at statement compile, so `IF 1 = 0 SELECT … CONTAINS(body, '(bad')` still raises 7630 — real rejects it too.
- A **module body** is the one place real defers: `CREATE PROCEDURE … CONTAINS(body, '(bad')` creates happily and raises at `EXEC`.
  The simulator skips the condition parse while `BatchContext.CreateTimeBinding` is set to match.
- A **variable or parameter** condition parses per execution, as on real.

### Divergences

- **The breaker's last residue** — the inputs the corpora above still miss: a URL's odd leftovers (real emits `:/` as a term, and a port as `:8080`), a punctuation run such as `%/` real keeps as a term, a signed chain like `+1-555-123-4567` real reads as four signed numbers, and a spaced currency reached from inside a chunk a time or date split.
- **Other languages' breakers.**
  Every language breaks by the English rules with its own stoplist, which real matches only for neutral and British English.
  The rest carry locale rules — German reads `33,667.95` with a decimal comma, splits `d'angelo`, and knows no English month or meridiem — so on 400 English-text inputs per language the parser's output matched real's for 24–33% of them (German, French, Spanish, Italian, Dutch, Brazilian Portuguese, Russian); searched as a German column, the same differential's conditions returned real's rows for 94.7% of 505.
- **Other languages' morphology** — see [Languages](#languages).
- **The stemmer holds one lemma per surface form.**
  Real's expansion can span two: `leaves` reaches `leaf` *and* `leave`, where the simulator picks `leaf`.
- **`sys.dm_fts_parser` for an accented term** reports the lower-cased term, where real's `display_term` reconstructs it from its keyword (`σοφΊα`) and its `keyword` encodes the accents apart.
- **`RANK` values** — see above.
- **No crawl lag** — see above.
- A phrase or `NEAR` can't span two columns of a multi-column index in either engine; the simulator gets that by leaving a wide position gap between columns rather than by tracking column identity.

## Catalog views in `BuiltInResources.cs`

**`sys.fulltext_catalogs`** (9-col): `fulltext_catalog_id` / `name` / `path` (NULL — no on-disk storage) / `is_default` / `is_accent_sensitivity_on` / `data_space_id` (NULL) / `file_id` (NULL) / `principal_id` / `is_importing` (always false).

**`sys.fulltext_indexes`**: real's 16 columns; `is_enabled`, the change-tracking pair and `stoplist_id` (**0** = system stoplist, NULL for `STOPLIST OFF`) follow the DDL, `index_version` is 2 and `incremental_timestamp` NULL whatever the population history (probed 2026-09-26), `has_crawl_completed` is true, `crawl_type` / `crawl_type_desc` are `F` / `FULL_CRAWL`, the crawl dates NULL, `data_space_id` **1** = PRIMARY and `property_list_id` NULL.
`stoplist_id` and `data_space_id` are **non-NULL by design** (probe-confirmed against the reference's AW database): DacFx's `SqlFullTextIndex` reverse-engineering INNER JOINs `sys.data_spaces` on `data_space_id` (a NULL drops the parent index element, orphaning its column specifiers → client-side NRE in `SqlFullTextIndexColumnSpecifierPopulator`) and reads `stoplist_id` to choose `DoUseSystemStopList` (0 = system) vs `IsStopListOff` (NULL = disabled) — a NULL there scripts the wrong stoplist mode.

**`sys.fulltext_index_columns`** (5-col, full row): `object_id` / `column_id` / `type_column_id` / `language_id` / `statistical_semantics` (always false).

**`sys.fulltext_languages`** (2-col): `lcid` / `name` — the 59 languages a stock SQL Server 2025 instance ships (probed from the reference; static reference data).
DacFx's full-text-index-column populator INNER JOINs it by `language_id`, so an empty view NREs the column-specifier build; AW's indexes use LCID 1033 (English).

**`sys.fulltext_system_stopwords`** (2-col): `stopword` / `language_id` — real's 15,829 rows verbatim (probed 2026-09-29), the stoplists the search drops noise words by.

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

- **The `SEMANTIC*` rowsets** (`SEMANTICKEYPHRASETABLE`, `SEMANTICSIMILARITYTABLE`, `SEMANTICSIMILARITYDETAILSTABLE`) — `NotSupportedException` at parse, naming the function. `STATISTICAL_SEMANTICS` on a column is real's Msg 41209, as no semantic language statistics database is ever registered.
- **Filesystem-placement semantics** (`ON FILEGROUP` / `IN PATH`) — parse-and-discard.
- **Custom stoplists and search property lists** (`CREATE FULLTEXT STOPLIST`, `CREATE SEARCH PROPERTY LIST`) — `sys.fulltext_stoplists` ships empty, so naming a stoplist or property list is refused as a missing one; `sys.fulltext_document_types` ships empty too.
- **The index keyword DMVs** — `sys.dm_fts_index_keywords`, `…_by_document`, `…_position_by_document` and `…_by_property` — which the breaker already has what it takes to answer.
- **`sys.sp_fulltext_load_thesaurus_file`** and a populated thesaurus.
  Probed 2026-09-29: the procedure succeeds silently for a known LCID, refuses to run inside a transaction, and for an unknown or NULL LCID rethrows real's error 30050 (`Both the thesaurus file for lcid '9999' and the global thesaurus could not be loaded.`) as a user error from `sys.sp_fulltext_rethrow_error`.
  A thesaurus of one's own is XML edited into the server's install tree, which no statement reaches.
- **`TYPE COLUMN` document extraction** — a `varbinary` column paired with an extension column is stored and projected through the catalog views, but its bytes are not filtered into text, so a search over one matches nothing. `xml` columns *are* indexed, by content — see [word breaking](#word-breaking).
- **Other languages' breakers and morphologies** — see [Divergences](#divergences).

## BACPAC round-trip

`ModelXmlReader` dispatches `SqlFullTextCatalog` (phase 1) → `CREATE FULLTEXT CATALOG name WITH ACCENT_SENSITIVITY = {ON|OFF} [AS DEFAULT] AUTHORIZATION owner` and `SqlFullTextIndex` (phase 8) → `CREATE FULLTEXT INDEX ON t (col [TYPE COLUMN c] LANGUAGE n, …) KEY INDEX key ON catalog`.
AW's catalog + 3 indexes (incl. `Production.Document`'s multi-column `TYPE COLUMN` pairing) load skip-free and re-export/re-import cleanly against a real full-text-enabled SQL Server.
See [`bacpac-loader.md`](bacpac-loader.md).
