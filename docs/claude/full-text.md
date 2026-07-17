# Full-text catalog + index

Skip-with-diagnostic. DDL + catalog views + per-table index slot ship; query-time predicates (`CONTAINS` / `FREETEXT` / `CONTAINSTABLE` / `FREETEXTTABLE` / `SEMANTIC*`) raise explicit `NotSupportedException`.

The bacpac-loaded AW procedure `uspSearchCandidateResumes` (which exercises `CONTAINSTABLE`) parses through CREATE PROCEDURE — proc bodies are stored verbatim and only re-tokenized on EXEC — and fails loudly with the documented `NotSupportedException` when called.

## Storage

**`FullTextCatalog`** (`SqlServerSimulator/FullTextCatalog.cs`) carries id + name + is_default + is_accent_sensitivity_on + principal_id + create_date.

**`Database.FullTextCatalogs`** — per-database `ConcurrentDictionary<string, FullTextCatalog>` (case-insensitive). The catalog-id counter starts at 5 — matches Microsoft Learn's documented numbering convention (ids 0..4 are reserved internal slots).

**`FullTextIndex`** (`SqlServerSimulator/FullTextIndex.cs`) carries catalog_id + key_index_name + unique_index_id (resolved at CREATE) + `List<FullTextIndexColumn>`.

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
    [WITH (option [, …])]

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
- Trailing `WITH (…)` options block parse-and-discards via `SkipBalancedParens`.

Statement dispatch: `Fulltext` is added to the `ContextualKeyword` enum; CREATE / DROP routes match `UnquotedString { ContextualKeyword: ContextualKeyword.Fulltext }`. DROP is routed through `TryParseDropFullText` ahead of the generic DROP-target switch.

## Predicate / rowset rejection

- `WHERE CONTAINS(col, '…')` / `WHERE FREETEXT(col, '…')` — `BooleanExpression.ParseAtom` intercepts the `ReservedKeyword`s `Contains` and `FreeText` ahead of the comparison parse and raises `NotSupportedException` with `"Full-text search predicates (CONTAINS|FREETEXT) are not modeled."`.
- `FROM CONTAINSTABLE(...) AS t` / `FROM FREETEXTTABLE(...)` / the two `SEMANTIC*` variants — `Selection.ParseSingleFromSource` intercepts the rowset-function keywords ahead of the syntax-error default and raises `NotSupportedException` with `"Full-text rowset functions (CONTAINSTABLE|...) are not modeled."`.

## Catalog views in `BuiltInResources.cs`

**`sys.fulltext_catalogs`** (9-col): `fulltext_catalog_id` / `name` / `path` (NULL — no on-disk storage) / `is_default` / `is_accent_sensitivity_on` / `data_space_id` (NULL) / `file_id` (NULL) / `principal_id` / `is_importing` (always false).

**`sys.fulltext_indexes`** (14-col): `object_id` / `unique_index_id` / `fulltext_catalog_id` / `is_enabled` (true) / `change_tracking_state` (`A`) / `change_tracking_state_desc` (`AUTO`) / `has_crawl_completed` (true) / `crawl_type` (`F`) / `crawl_type_desc` (`FULL`) / `crawl_start_date` (NULL) / `crawl_end_date` (NULL) / `stoplist_id` (**0** = system stoplist) / `data_space_id` (**1** = PRIMARY) / `property_list_id` (NULL). `stoplist_id` and `data_space_id` are **non-NULL by design** (probe-confirmed against the reference's AW database): DacFx's `SqlFullTextIndex` reverse-engineering INNER JOINs `sys.data_spaces` on `data_space_id` (a NULL drops the parent index element, orphaning its column specifiers → client-side NRE in `SqlFullTextIndexColumnSpecifierPopulator`) and reads `stoplist_id` to choose `DoUseSystemStopList` (0 = system) vs `IsStopListOff` (NULL = disabled) — a NULL there scripts the wrong stoplist mode.

**`sys.fulltext_index_columns`** (5-col, full row): `object_id` / `column_id` / `type_column_id` / `language_id` / `statistical_semantics` (always false).

**`sys.fulltext_languages`** (2-col): `lcid` / `name` — the 59 languages a stock SQL Server 2025 instance ships (probed from the reference; static reference data). DacFx's full-text-index-column populator INNER JOINs it by `language_id`, so an empty view NREs the column-specifier build; AW's indexes use LCID 1033 (English).

Column shapes are probe-confirmed against the local SQL Server 2025 (CU7) reference, which has Full-Text installed.

## `FULLTEXTSERVICEPROPERTY('property_name')`

`Parser/Expressions/FullTextServiceProperty.cs`. Returns a plain `int` — probe-confirmed against SQL Server 2025 (unlike `SERVERPROPERTY`, which is `sql_variant`), so the result type is always `int` regardless of whether the argument is a compile-time constant (no constant-detection branch, unlike `ServerProperty`).

The simulator reports Full-Text as installed (`SERVERPROPERTY('IsFullTextInstalled') = 1`; CREATE FULLTEXT CATALOG / INDEX are modeled), so for self-consistency `FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')` returns `1`. The reference box returns `0` only because Full-Text isn't installed there — and while uninstalled it returns NULL for the resource-tuning properties too, so their installed values can't be probed. The simulator reports the installed value of `0` for each: `ConnectTimeout`, `LoadOSResources`, `ResourceUsage`, `VerifyResourceUsage`. An unrecognized property name returns NULL `int` (probe-confirmed convention); names are case-insensitive. `FULLTEXTCATALOGPROPERTY` is not modeled.

## Known gaps

- **Query-time text search** — tokenizer / stemmer / inverted-index pipeline. Out of scope.
- **`ALTER FULLTEXT CATALOG` / `INDEX`** (REORGANIZE / REBUILD / START/STOP POPULATION / ADD/DROP column) — `NotSupportedException` at parse.
- **Filesystem-placement semantics** (`ON FILEGROUP` / `IN PATH`) — parse-and-discard.
- **`sys.fulltext_document_types` / `sys.fulltext_stoplists`** — shipped empty (the stoplist registry is inert since only the system stoplist is modeled). (`sys.fulltext_languages` now ships populated — see above.)

## BACPAC round-trip

`ModelXmlReader` dispatches `SqlFullTextCatalog` (phase 1) → `CREATE FULLTEXT CATALOG name WITH ACCENT_SENSITIVITY = {ON|OFF} [AS DEFAULT] AUTHORIZATION owner` and `SqlFullTextIndex` (phase 8) → `CREATE FULLTEXT INDEX ON t (col [TYPE COLUMN c] LANGUAGE n, …) KEY INDEX key ON catalog`. AW's catalog + 3 indexes (incl. `Production.Document`'s multi-column `TYPE COLUMN` pairing) load skip-free and re-export/re-import cleanly against a real full-text-enabled SQL Server. See [`bacpac-loader.md`](bacpac-loader.md).
