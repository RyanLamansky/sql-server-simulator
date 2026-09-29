# Bulk loads and ad hoc data sources

`BULK INSERT`, `OPENROWSET(BULK …)`, `OPENROWSET` over a provider and `OPENDATASOURCE`.
Everything below was probed 2026-09-29 against SQL Server 2025 on Linux, through the edge-probe harness (`.vs/edge-probe`, `--bulk <dir>` wires `OpenBulkFile` to the same probe directory the server reads, `--adhoc` registers the simulation as `localhost`).
The simulator models that Linux server whatever host it runs on, which three rules make visible: `CODEPAGE` is refused, a login without `CONTROL SERVER` reaches no file, and `'\n'` means a line feed alone.

Code: [`Simulation.BulkInsert.cs`](../../src/SqlServerSimulator/Simulation/Simulation.BulkInsert.cs) (the statement), [`Selection.OpenRowset.cs`](../../src/SqlServerSimulator/Parser/Selection.OpenRowset.cs) (both `OPENROWSET` forms and `OPENDATASOURCE`), [`BulkOptions.cs`](../../src/SqlServerSimulator/Parser/BulkOptions.cs), and the readers under [`Storage/Bulk/`](../../src/SqlServerSimulator/Storage/Bulk/).
Rows land through `BulkInsertRows`, the engine `SqlBulkCopy`'s `INSERT BULK` uses ([`tds-endpoint.md`](tds-endpoint.md#bulk-load-sqlbulkcopy)).

## File access

**Settled — don't re-pitch:** the simulator never touches the host's file system on its own.
A consumer opts in through one public `init` property, `Simulation.OpenBulkFile` (`Func<string, Stream?>`), which receives the path as the statement spells it and returns a readable stream or null for "no such file"; with no delegate every path is a file that doesn't exist.
The reason is containment: a simulation exposed over the TDS endpoint would otherwise hand any client the host's files, and tests should never depend on real disk paths — they serve files from memory.
The delegate's files are only ever read.

- A missing file is real's own **Msg 4860**, at state 1 for `BULK INSERT`'s data file, 3 for a format file, 4 for an `OPENROWSET(BULK …)` data file.
  It ends the batch, a `TRY` in the same scope doesn't catch it, and raised in a procedure or dynamic batch it ends only that batch (`IsBulkRefusal`).
- A login without `CONTROL SERVER` gets **Msg 4860 at state 75** for every file, `bulkadmin` or not: the Linux server lets no other login read one.
- An `ERRORFILE` is a file the server can't create, since the simulator writes nothing: **Msg 4861** for it — code 80 (`The file exists.`) when the delegate answers for the path, code 5 (`Access is denied.`) otherwise — then again for the `.Error.Txt` file beside it, before any row loads.
  Real opens both up front even for a clean load, so the refusal is what a server with an unwritable error-file location reports, and it ends the batch uncaught by a `TRY`.
- `DATA_SOURCE` names no external data source (**Msg 12703**, state 2); `ERRORFILE_DATA_SOURCE` and `FORMATFILE_DATA_SOURCE` are accepted and ignored.

## BULK INSERT

`BULK INSERT target FROM 'file' [WITH ( option [, …] )]`: the target is a table, a synonym or an updatable view (a table variable is Msg 102), the path a string literal (a variable is Msg 102).
The whole statement parses before the target resolves, so a syntax error outranks a missing table, which is **Msg 208 at state 160**.
Options parse as [`BulkOptions`](../../src/SqlServerSimulator/Parser/BulkOptions.cs) documents: each at most once (**Msg 4130**), numbers as unsigned integer literals and text as string literals, else Msg 102; `CODEPAGE` is **Msg 16202** (class 15, at compile).
`TABLOCK`, `ORDER`, `ROWS_PER_BATCH` and `KILOBYTES_PER_BATCH` carry no effect; an `ORDER` column the table lacks is Msg 4817, class 0, and the hint is ignored.

At run time: `ADMINISTER BULK OPERATIONS` (**Msg 4834** at state 4, ending the batch uncaught), `DATA_SOURCE`, the file access above, the format file, the error file, then `FIRSTROW` past `LASTROW` (**Msg 4880**), `FORMAT = 'CSV'` with a native type (**Msg 5339**) and a `FIELDQUOTE` longer than one character (**Msg 4878**).
No `INSERT` permission is asked: real's Linux server never gets that far for a login without `CONTROL SERVER`.

### Reading the file

Character data (`char`, the default) is UTF-8, a byte-order mark skipped; `widechar` is UTF-16 after its byte-order mark.
A `char` load of a file marked UTF-16 reads it as `widechar` and a `widechar` load of an unmarked file as `char`, each saying so twice (Msg 4830 / Msg 4831, class 0).
The field terminator defaults to a tab (a comma under `FORMAT = 'CSV'`), the row terminator to `'\n'`, which is a line feed alone, so a CRLF file keeps its carriage returns in the last column; both take `\t`, `\n`, `\r`, `\0`, `\\` escapes and `0x` hex bytes, UTF-16 code units under `widechar`.

The reader is a stream, not lines ([`BulkTextReader`](../../src/SqlServerSimulator/Storage/Bulk/BulkTextReader.cs)): each field runs to its own terminator, so a short row borrows the next line's text and a long one leaves the rest in its last field.
At the end of the file a last field with text is the row's, an empty one drops the row, and any other field the file ends inside is **Msg 4832** — save one that read exactly the row terminator, which ends the file quietly.
`FIRSTROW` skips rows by reading their fields, so a header must split like the data; `LASTROW` stops reading.
An empty field is NULL, and a lone NUL character (what bcp writes for an empty string) is the empty string.

`FORMAT = 'CSV'` reads records: a field is quoted (`FIELDQUOTE`, default `"`, a doubled quote inside standing for one, terminators and line breaks inside kept) or has no quote at all, and it must end at the terminator its position expects.
A stray quote, text after a closing quote, or a record with the wrong number of fields is **Msg 4879** naming the row and field; a quote never closed is Msg 4832; a malformed record `FIRSTROW` skips is **Msg 7301** instead.

`native` and `widenative` read bcp's native layout for the target's columns ([`BulkField.ForNativeColumn`](../../src/SqlServerSimulator/Storage/Bulk/BulkField.cs)); a format file — non-XML or XML — lays out fields of its own, maps them to columns by server column order (0 skips a field) or `ROW` order, and decodes each by host type ([`BulkFormatFile`](../../src/SqlServerSimulator/Storage/Bulk/BulkFormatFile.cs)).

### Fields to columns

Every column the target shows takes a field, computed and `rowversion` columns included, whose fields are read and ignored; so is the identity column's, unless `KEEPIDENTITY` keeps it.
`SCOPE_IDENTITY()` reads the last identity the load generated, and stays as it was when the file's values were kept.
An empty field for a column with a default takes the default unless `KEEPNULLS`.
Through a view the fields follow the view's columns.

A field converts by the bulk provider's own rules, not `CAST`'s ([`BulkTextConverter`](../../src/SqlServerSimulator/Storage/Bulk/BulkTextConverter.cs), probed one candidate per row): an integer takes surrounding whitespace and a sign but no decimal point; `bit` only `0` or `1`; `money` thousands separators but no currency symbol; a decimal rounds to its scale and is truncation past its precision; a binary field is hex without `0x`, an odd length truncation; a date or time reads as `CAST` would at full precision, so a `datetime` takes seven fractional digits.
The failures are row errors: **Msg 4864** (type mismatch), **Msg 4863** (truncation, a `varchar` too long), **Msg 4867** (overflow) — an `nvarchar` too long is Msg 4864 — and **Msg 4869** for an empty field bound for a NOT NULL column no default fills.
`xml`, `json` and `vector` columns parse as `CAST` does, and their errors (Msg 9400, 13609) end the load.

### Row errors, batches and transactions

A row error is sent in its place and the row is skipped; the load carries on, `@@ERROR` reads the last one's number afterwards, and a batch that met one closes without a count — the statement's own closing DONE included.
Inside a `TRY` they are swallowed: nothing is sent and `@@ERROR` reads 0.
More than `MAXERRORS` (default 10) fails the load: the tipping row's error, **Msg 4865** (absent under `MAXERRORS = 0`), then the provider's **Msg 7399** and **Msg 7330**, as Msg 4832 and 4879 are followed too.
That chain ends the batch and rolls a transaction back as under `XACT_ABORT`; a `TRY` catches it and reads Msg 7330.

Rows commit in batches of `BATCHSIZE` file rows — skipped and failed ones counted — each an atomic unit, and each full batch that met no row error closes with a DONE of its own ahead of the statement's total (so `ExecuteNonQuery` sums them, as SqlClient does).
A constraint violation (a key, or a CHECK / FOREIGN KEY under `CHECK_CONSTRAINTS`) ends the statement with Msg 3621, keeping the batches before it; inside a user transaction those stay part of it.
Without `CHECK_CONSTRAINTS` the table's CHECK and FOREIGN KEY constraints are left untrusted, even by an empty file.
Triggers fire only under `FIRE_TRIGGERS`, which also hands the rows to an `INSTEAD OF` trigger — a view's or a table's — that the load otherwise bypasses.

## OPENROWSET(BULK …)

`OPENROWSET(BULK 'file', option [, …]) alias [(columns)]` in a FROM or APPLY position; the alias is required (**Msg 491**), a column list renames.
`SINGLE_BLOB` / `SINGLE_CLOB` / `SINGLE_NCLOB` read the whole file as one `BulkColumn` — `varbinary(max)`, `varchar(max)` in the database's collation, `nvarchar(max)` — and more than one is **Msg 471**.
`SINGLE_CLOB` decodes UTF-8 into the column's code page (invalid UTF-8 is Msg 4863 at state 4 with the provider pair), refusing a UTF-16 file (**Msg 4806**); `SINGLE_NCLOB` needs one (**Msg 4809**).
`OPENJSON` over `SINGLE_CLOB` and `CAST(BulkColumn AS xml)` over `SINGLE_BLOB` are the common load shapes.

A `FORMATFILE` lays the file out as rows, with the bulk options above: a non-XML file's `SQLCHAR` field of length `n` is `varchar(n)`, `SQLNCHAR` `nvarchar(n / 2)`, a native host type itself, and **Msg 4838** for `SQLDECIMAL`; an XML file's `COLUMN` types it, a string one without `LENGTH` taking its field's maximum.
No format at all is **Msg 15808**, `FORMAT = 'CSV'` without one **Msg 472**, and a `WITH` column list over CSV **Msg 5374**; as a DML target the `BULK` form is Msg 156.
The file is opened when the statement runs, and read again each time its rows are; `ADMINISTER BULK OPERATIONS` is not asked, only file access.

## Ad hoc provider rowsets

`OPENROWSET('provider', 'connection string' | 'server'; 'user'; 'password', 'query' | db.schema.object)` follows real's `Ad Hoc Distributed Queries` option, off by default, refused with **Msg 15281** as the batch compiles — after a provider other than SQL Server's, which the Linux server has none of, is **Msg 7222**.
`RECONFIGURE` changing the option retires compiled plans, so a view over an ad hoc rowset raises it (with Msg 4413 naming the view) once the option is off.

Once enabled, the rowset reads through the linked-server machinery ([`linked-servers.md`](linked-servers.md)) with a transient server named `(null)`, which is how real's messages name it:
- The connection string's `Server` / `Data Source` / `Address` names a simulation registered through `AddRemoteSimulation` (a `tcp:` prefix and a port stripped); none, or a local alias such as `localhost` registered as nothing else, is the session's own instance, a **loopback**; any other name is Msg 2 (`Named Pipes Provider: …`) at line 0, ending the batch as it compiles.
  `Database` / `Initial Catalog` is where its sessions start, else `master`.
- A query is a pass-through as `OPENQUERY`'s is; one with no result set is Msg 7357, and one with an `xml` column Msg 9514 naming the rowset's alias, else `OPENROWSET`.
  An object needs three parts — fewer is **Msg 7313**, four is Msg 117 — and a missing one is **Msg 7314**.
- `INSERT` / `UPDATE` / `DELETE` through either form write as a linked server's target does, the joined forms through its alias; a `MERGE` target is **Msg 5315**, and a write inside a local transaction is the loopback's **Msg 3910** (a registered remote's **Msg 7391**).
- A column-alias list is Msg 102.

`OPENDATASOURCE('provider', 'init string').db.schema.object` — in FROM, as a DML target or after `EXEC` — is **Msg 7302** (`Cannot create an instance of OLE DB provider "MSDASC"`): the Linux server has no data-link component to open it with.
The `Msg 7222` / `Msg 15281` refusals come first.

## Divergences

- A `BULK INSERT` closes with the `INSERT` statement kind in its DONE token; real's own code wasn't captured.
- UTF-8 with an invalid sequence right before a terminator: real's decoder can swallow the terminator into the field (`e4 80 0a` loaded as `U+FFFD` plus a line feed), where the simulator decodes with a replacement character and still finds it.
- Inside a `TRY`, real loads the rows of an `xml` column that don't parse; here Msg 9400 ends the load wherever it runs.
- An XML format file's `SQLDECIMAL` column types as `decimal` where real's `type_name` reads `numeric`.
- A format-file rowset's field that doesn't convert ends the statement with its row error; `MAXERRORS` isn't applied to a rowset.
- Ad hoc: the provider string's keywords aren't checked and nothing authenticates, so an OLE DB spelling real's driver refuses (`User ID=` / `Password=` → Msg 7399, 7303) connects here, as does the three-part form real's loopback refused on encryption.
  An unreachable server's Msg 2 arrives without the two Msg 7412 provider messages real sends ahead of it.
- A table created in the same batch as an ad hoc read of it: real checks the remote metadata when the batch compiles and fails (Msg 7314 / 208), where the simulator's compile walk stops at the table's first write — the general limitation in [`control-flow.md`](control-flow.md#not-modeled-yet).

## Not modeled yet

- External data sources (`CREATE EXTERNAL DATA SOURCE`), so `DATA_SOURCE` is always Msg 12703, and the SQL Server 2022+ `OPENROWSET(BULK …)` forms over one (`FORMAT = 'PARQUET'` / `'DELTA'`, a `WITH` schema).
- What an `ERRORFILE` would hold — the failed rows' bytes and a `Row N File Offset …` line per row in `.Error.Txt` on real.
- `sql_variant` and CLR-typed fields in a native file or format file (`NotSupportedException`).
- `EXEC … AT DATA_SOURCE`.
- An ad hoc rowset's object form over a `sys` or `INFORMATION_SCHEMA` view, which a four-part name reads ([`linked-servers.md`](linked-servers.md#reads)); here it is Msg 7314.
