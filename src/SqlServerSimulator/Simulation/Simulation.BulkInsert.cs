using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Bulk;

namespace SqlServerSimulator;

// BULK INSERT: a data file read through OpenBulkFile into a table or an
// updatable view with bulk-load semantics — the engine SqlBulkCopy's INSERT
// BULK uses (Simulation.BulkLoad.cs), fed by the character, native and
// format-file readers under Storage/Bulk.
partial class Simulation
{
    /// <summary>The <c>Ad Hoc Distributed Queries</c> server option's <c>sp_configure</c> id.</summary>
    private const int AdHocDistributedQueriesConfigurationId = 16391;

    /// <summary>
    /// The installed value of <c>Ad Hoc Distributed Queries</c>, which gates
    /// <c>OPENROWSET</c> and <c>OPENDATASOURCE</c> over a provider.
    /// </summary>
    internal bool AdHocDistributedQueriesEnabled => this.ConfigurationInUse(AdHocDistributedQueriesConfigurationId) != 0;

    /// <summary>
    /// The file <paramref name="path"/> names, read whole through
    /// <see cref="OpenBulkFile"/>; null when there is no such file — always,
    /// when the host supplied no delegate — or the delegate couldn't open it.
    /// </summary>
    internal byte[]? ReadBulkFile(string path)
    {
        if (this.OpenBulkFile is not { } open)
            return null;
        try
        {
            using var stream = open(path);
            if (stream is null)
                return null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file a bulk load names, refusing first the session whose login
    /// holds no <c>CONTROL SERVER</c> — the Linux server lets no other login
    /// reach a file, <c>bulkadmin</c> or not (Msg 4860 at state 75) — then a
    /// file that doesn't exist (Msg 4860 at <paramref name="missingState"/>).
    /// </summary>
    internal static byte[] ReadBulkFileFor(BatchContext batch, string path, byte missingState)
    {
        var simulation = batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(batch.Connection, Permission.ControlServer))
            throw SimulatedSqlException.BulkFileNotFound(path, 75);
        return simulation.ReadBulkFile(path) ?? throw SimulatedSqlException.BulkFileNotFound(path, missingState);
    }

    /// <summary>
    /// <c>BULK INSERT target FROM 'file' [WITH ( option [, …] )]</c>, entered
    /// on <c>BULK</c>. The whole statement parses before its target resolves,
    /// so a syntax error outranks a missing table; at run time the checks go
    /// <c>ADMINISTER BULK OPERATIONS</c> (Msg 4834), <c>DATA_SOURCE</c>, the
    /// file, the format file, the error file, then the row range (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Rows commit in batches of <c>BATCHSIZE</c> file rows — skipped and
    /// failed ones counted — each its own atomic unit: a constraint violation
    /// ends the statement with the batches before it kept, and each full batch
    /// that loaded without a row error closes with a DONE of its own ahead of
    /// the statement's total. A row that doesn't convert is reported and
    /// skipped (swallowed inside a <c>TRY</c>) until more than
    /// <c>MAXERRORS</c> have, when the load fails as a whole.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> RunBulkInsertStatement(BatchContext batch)
    {
        var context = batch.Parser;
        batch.CurrentStatement.WritesRows = true;
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Insert })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var name = BatchContext.ParseObjectName(context);
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.From })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Literal { Value.Type.Category: SqlTypeCategory.String } pathLiteral)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var path = pathLiteral.Value.AsString;
        var options = new BulkOptions();
        context.MoveNextOptional();
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            while (true)
            {
                options.ParseOption(context, rowset: false);
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
            }
            context.MoveNextOptional();
        }
        context.RejectTrailingToken();
        if (options.CodePageWritten is { } codePage)
            throw SimulatedSqlException.OptionNotSupportedOnPlatform(codePage);

        name = batch.ExpandSynonym(name);
        HeapTable table;
        HeapColumn[] fileColumns;
        int[] baseOrdinals;
        View? insteadOfView = null;
        if (batch.TryResolveView(name, out var view))
        {
            // A partitioned view, or a view over one, takes no bulk load once
            // its members qualify (Msg 4437), which real settles as it compiles
            // the statement, a file it can't read deferring that (probed
            // 2026-10-01 against SQL Server 2025).
            if (view.PartitionedBase is { } partitioned)
            {
                if (batch.Connection.Simulation.ReadBulkFile(path) is null)
                {
                    if (batch.IsSkipping)
                        yield break;
                    if (!batch.Connection.Simulation.SessionHoldsServerPermission(batch.Connection, Permission.AdministerBulkOperations))
                        throw SimulatedSqlException.BulkLoadPermissionDenied();
                    _ = ReadBulkFileFor(batch, path, missingState: 1);
                }
                _ = AnalyzePartitionedView(batch, partitioned, PartitionedMembers(batch, partitioned, name.ToString(), PartitionedWrite.Insert));
                throw SimulatedSqlException.PartitionedViewBulkTarget(PartitionedViewLabel(partitioned));
            }

            // FIRE_TRIGGERS hands a view's rows to its INSTEAD OF trigger;
            // without it they reach the base table (probed 2026-09-29).
            if (options.FireTriggers && HasInsteadOfTrigger(batch, view, TriggerActions.Insert))
                insteadOfView = view;
            // A load fills every column, so a derived one refuses it as an
            // INSERT without a column list is refused (Msg 4406).
            table = view.BaseTable ?? throw (view.DerivedOutputColumns is { } derived && Array.IndexOf(derived, true) >= 0
                ? SimulatedSqlException.ViewDmlTouchesDerivedField(view.UnionOwnerName ?? name.ToString())
                : NonUpdatableViewError(view, name.ToString()));
            fileColumns = view.OutputColumns;
            baseOrdinals = view.BaseColumnOrdinals;
        }
        else if (batch.TryResolveTable(name, out var resolved))
        {
            table = resolved;
            var visible = new List<HeapColumn>();
            var ordinals = new List<int>();
            for (var i = 0; i < resolved.Columns.Length; i++)
            {
                if (resolved.Columns[i].IsHidden)
                    continue;
                visible.Add(resolved.Columns[i]);
                ordinals.Add(i);
            }
            fileColumns = [.. visible];
            baseOrdinals = [.. ordinals];
        }
        else
        {
            // Real names a missing bulk target at state 160 (probed 2026-09-29).
            throw batch.TryResolveSynonym(name, out _)
                ? batch.UnresolvableObjectName(name)
                : SimulatedSqlException.InvalidObjectName(name.WithoutOmittedLeading(), 160);
        }
        batch.HasSessionScopedReference = true;
        if (batch.IsSkipping)
            yield break;

        var simulation = batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(batch.Connection, Permission.AdministerBulkOperations))
            throw SimulatedSqlException.BulkLoadPermissionDenied();
        if (options.DataSource is { } dataSource)
            throw SimulatedSqlException.ExternalDataSourceNotFound(dataSource);
        var data = ReadBulkFileFor(batch, path, missingState: 1);
        BulkFormatFile? format = null;
        if (options.FormatFile is { } formatPath)
        {
            var formatBytes = ReadBulkFileFor(batch, formatPath, missingState: 3);
            format = BulkFormatFile.Parse(formatBytes, batch.CurrentDatabase.Collation, out _)
                ?? throw new NotSupportedException($"The format file '{formatPath}' is in a form the simulator doesn't read.");
        }
        if (options.ErrorFile is { } errorFile)
        {
            // The simulator writes no file, so an error file is one the server
            // can't create — refused, with the .Error.Txt file beside it, before
            // a row loads (probed 2026-09-29: real opens both up front).
            var first = SimulatedSqlException.BulkErrorFileNotOpened(errorFile, simulation.ReadBulkFile(errorFile) is not null);
            first.ResolveDiagnostics(batch.CurrentStatement.StartLine, batch.LineOffset, batch.ErrorProcedureName);
            yield return new SimulatedErrorOutcome(first) { DoneKind = batch.CurrentStatement.DoneKind };
            var besideIt = errorFile + ".Error.Txt";
            throw SimulatedSqlException.BulkErrorFileNotOpened(besideIt, simulation.ReadBulkFile(besideIt) is not null);
        }
        var firstRow = Math.Max(options.FirstRow ?? 1, 1);
        var lastRow = options.LastRow is > 0 and var last ? last : long.MaxValue;
        if (firstRow > lastRow)
            throw SimulatedSqlException.BulkFirstRowAfterLastRow();
        var dataFileType = options.DataFileType ?? "char";
        if (options.Csv && dataFileType is "native" or "widenative")
            throw SimulatedSqlException.BulkCsvNeedsCharacterData();
        if (options.FieldQuote is { Length: not 1 })
            throw SimulatedSqlException.BulkInvalidQuoteCharacter();
        foreach (var sorted in options.Order ?? [])
        {
            if (Array.Find(table.Columns, c => batch.CurrentDatabase.Collation.Equals(c.Name, sorted)) is null)
                batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.BulkSortedColumnInvalidMessage(batch, sorted));
        }

        // Which base column each file column fills: -1 for one whose value the
        // load ignores — a computed or rowversion column, the identity column
        // without KEEPIDENTITY, a view's derived column.
        var targetColumns = new List<HeapColumn>();
        var targetSlot = new int[fileColumns.Length];
        for (var i = 0; i < fileColumns.Length; i++)
        {
            targetSlot[i] = -1;
            if (baseOrdinals.Length <= i || baseOrdinals[i] < 0)
                continue;
            var column = table.Columns[baseOrdinals[i]];
            if (column.Computed is not null || column.Type is RowVersionSqlType || (column.Identity is not null && !options.KeepIdentity))
                continue;
            if (format is not null && !Array.Exists(format.Fields, f => f.Target == i))
                continue;
            targetSlot[i] = targetColumns.Count;
            targetColumns.Add(column);
        }
        var plan = new BulkInsertPlan(table, [.. targetColumns], options.KeepNulls, options.CheckConstraints, options.FireTriggers);

        var source = OpenBulkSource(batch, path, data, format, dataFileType, options, fileColumns);
        var reportRowErrors = batch.TryFrameDepth == 0;
        var maxErrors = options.MaxErrors ?? 10;
        var batchSize = options.BatchSize is > 0 and var size ? size : long.MaxValue;
        var pending = new List<SqlValue[]>();
        long rowsInBatch = 0;
        var batchHadError = false;
        var batchSentRow = false;
        long total = 0;
        var errorCount = 0;
        var lastErrorNumber = 0;
        var cells = new object?[source.FieldCount];

        while (source.NextRowNumber <= lastRow && source.Read(cells, skipping: source.NextRowNumber < firstRow))
        {
            var rowNumber = source.NextRowNumber - 1;
            rowsInBatch++;
            if (rowNumber >= firstRow)
            {
                batchSentRow = true;
                var row = new SqlValue[targetColumns.Count];
                for (var t = 0; t < row.Length; t++)
                    row[t] = SqlValue.Null(targetColumns[t].Type);
                SimulatedSqlException? rowError = null;
                for (var f = 0; f < cells.Length && rowError is null; f++)
                {
                    var destination = source.DestinationOf(f);
                    if (destination < 0 || targetSlot[destination] < 0)
                        continue;
                    var column = targetColumns[targetSlot[destination]];
                    var failure = ConvertBulkCell(cells[f], column.Type, out var value);
                    // Errors name the field by its place in the file.
                    rowError = failure switch
                    {
                        BulkConversionFailure.TypeMismatch => SimulatedSqlException.BulkConversionTypeMismatch(rowNumber, f + 1, fileColumns[destination].Name),
                        BulkConversionFailure.Truncation => SimulatedSqlException.BulkConversionTruncation(rowNumber, f + 1, fileColumns[destination].Name),
                        BulkConversionFailure.Overflow => SimulatedSqlException.BulkConversionOverflow(rowNumber, f + 1, fileColumns[destination].Name),
                        // A NULL field bound for a NOT NULL column no default
                        // fills is a row error too, not the INSERT's Msg 515.
                        _ when value.IsNull && !column.Nullable && (column.Default is null || options.KeepNulls)
                            => SimulatedSqlException.BulkUnexpectedNull(rowNumber, f + 1, fileColumns[destination].Name),
                        _ => null,
                    };
                    row[targetSlot[destination]] = value;
                }
                if (rowError is null)
                {
                    pending.Add(row);
                }
                else
                {
                    errorCount++;
                    batchHadError = true;
                    lastErrorNumber = rowError.Number;
                    if (reportRowErrors)
                    {
                        rowError.ResolveDiagnostics(batch.CurrentStatement.StartLine, batch.LineOffset, batch.ErrorProcedureName);
                        yield return new SimulatedErrorOutcome(rowError) { DoneKind = batch.CurrentStatement.DoneKind };
                    }
                    if (errorCount > maxErrors)
                    {
                        throw maxErrors > 0
                            ? SimulatedSqlException.BulkRowStreamFailed(SimulatedSqlException.BulkMaxErrorsExceeded(maxErrors))
                            : SimulatedSqlException.BulkRowStreamFailed();
                    }
                }
            }

            if (rowsInBatch == batchSize)
            {
                var inserted = this.InsertBulkBatch(context, plan, pending, insteadOfView, targetSlot);
                total += inserted;
                if (!batchHadError && batchSentRow)
                    yield return new SimulatedNonQuery(inserted) { DoneKind = batch.CurrentStatement.DoneKind };
                pending.Clear();
                rowsInBatch = 0;
                batchHadError = false;
                batchSentRow = false;
            }
        }
        total += this.InsertBulkBatch(context, plan, pending, insteadOfView, targetSlot);

        batch.Connection.LastStatementRowCount = (int)Math.Min(total, int.MaxValue);
        if (errorCount > 0 && reportRowErrors)
        {
            batch.Connection.LastErrorNumber = lastErrorNumber;
            batch.CurrentStatement.SuppressErrorReset = true;
        }
        // A batch that reported a row error closes without a count, the
        // statement's own closing DONE included (probed 2026-09-29).
        yield return new SimulatedNonQuery((int)Math.Min(total, int.MaxValue)) { CountSuppressed = batchHadError && reportRowErrors ? true : null };
    }

    /// <summary>
    /// One batch's rows written as its own atomic unit — or handed, shaped as
    /// the view's columns, to <paramref name="insteadOfView"/>'s INSTEAD OF
    /// trigger; the rows written.
    /// </summary>
    private int InsertBulkBatch(ParserContext context, BulkInsertPlan plan, List<SqlValue[]> rows, View? insteadOfView, int[] viewSlots) =>
        RunMutation(context, parser =>
        {
            if (insteadOfView is null)
                return this.BulkInsertRows(plan, rows, parser);
            if (rows.Count == 0)
                return new SimulatedNonQuery(0);
            var viewColumns = insteadOfView.OutputColumns;
            var viewRows = new List<SqlValue[]>(rows.Count);
            foreach (var row in rows)
            {
                var viewRow = new SqlValue[viewColumns.Length];
                for (var i = 0; i < viewRow.Length; i++)
                    viewRow[i] = i < viewSlots.Length && viewSlots[i] >= 0 ? row[viewSlots[i]] : SqlValue.Null(viewColumns[i].Type);
                viewRows.Add(viewRow);
            }
            parser.Connection.LastStatementRowCount = viewRows.Count;
            _ = this.TryFireInsteadOfTrigger(parser.Batch, insteadOfView, TriggerActions.Insert, viewColumns, insertedRows: viewRows, deletedRows: null, affectedRowCount: viewRows.Count);
            return new SimulatedNonQuery(viewRows.Count);
        }).RecordsAffected;

    /// <summary>
    /// One cell into its column's type: NULL as NULL, text by the bulk
    /// provider's conversion rules, a native value as <c>CAST</c> converts it.
    /// </summary>
    internal static BulkConversionFailure ConvertBulkCell(object? cell, SqlType type, out SqlValue value)
    {
        switch (cell)
        {
            case null:
                value = SqlValue.Null(type);
                return BulkConversionFailure.None;
            case string text:
                return BulkTextConverter.TryConvert(text, type, out value);
            default:
                try
                {
                    value = ((SqlValue)cell).CoerceTo(type);
                    return BulkConversionFailure.None;
                }
                catch (OverflowException)
                {
                    value = default;
                    return BulkConversionFailure.Overflow;
                }
                catch (Exception ex) when (ex is SimulatedSqlException or FormatException or NotSupportedException or ArgumentException)
                {
                    value = default;
                    return BulkConversionFailure.TypeMismatch;
                }
        }
    }

    /// <summary>
    /// The reader a load's options call for over <paramref name="data"/>: the
    /// format file's fields, the table's native layout, or character data —
    /// UTF-8 for <c>char</c> (a byte-order mark skipped), UTF-16 for
    /// <c>widechar</c>, a file whose byte-order mark says otherwise read as
    /// what it is, with the two messages real sends about it.
    /// </summary>
    internal static BulkRowSource OpenBulkSource(BatchContext batch, string path, byte[] data, BulkFormatFile? format, string dataFileType, BulkOptions options, HeapColumn[] fileColumns)
    {
        if (format is not null && options.Csv)
        {
            // CSV under a format file reads its records as text, the fields
            // bounded by the format file's own terminators.
            var csvText = DecodeCharacterData(data);
            var fieldTerms = Array.ConvertAll(format.Fields, f => f.Host == BulkHostType.NChar ? Encoding.Unicode.GetString(f.Terminator) : Encoding.UTF8.GetString(f.Terminator));
            var csvQuote = options.FieldQuote is { Length: 1 } fq ? fq[0] : '"';
            return new BulkRowSource(new BulkTextReader(csvText, fieldTerms, fieldTerms[0], fieldTerms[^1], csvQuote), Array.ConvertAll(format.Fields, f => f.Target), path);
        }
        if (format is not null)
        {
            var targets = Array.ConvertAll(format.Fields, f => f.Target);
            return new BulkRowSource(new BulkBinaryReader(data, 0, format.Fields, Encoding.UTF8), targets, path);
        }
        if (dataFileType is "native" or "widenative")
        {
            var fields = new BulkField[fileColumns.Length];
            for (var i = 0; i < fields.Length; i++)
                fields[i] = BulkField.ForNativeColumn(fileColumns[i], i, dataFileType == "widenative");
            var encoding = fileColumns.Length > 0 ? CodePageEncodingOf(fileColumns) : Encoding.Latin1;
            return new BulkRowSource(new BulkBinaryReader(data, 0, fields, encoding), [.. Enumerable.Range(0, fields.Length)], path);
        }

        var wide = dataFileType == "widechar";
        var hasUnicodeMark = data is [0xFF, 0xFE, ..];
        if (wide != hasUnicodeMark)
        {
            for (var i = 0; i < 2; i++)
                batch.Connection.PendingMessages.Enqueue(wide ? SimulatedSqlException.BulkAssumedCharMessage(batch) : SimulatedSqlException.BulkAssumedWidecharMessage(batch));
            wide = hasUnicodeMark;
        }
        var text = wide ? Encoding.Unicode.GetString(data, 2, data.Length - 2) : DecodeCharacterData(data);
        var fieldTerminator = BulkFormatFile.UnescapeTerminator(options.FieldTerminator ?? (options.Csv ? "," : "\\t"), wide);
        var rowTerminator = BulkFormatFile.UnescapeTerminator(options.RowTerminator ?? "\\n", wide);
        var terminators = new string[Math.Max(fileColumns.Length, 1)];
        for (var i = 0; i < terminators.Length; i++)
            terminators[i] = i == terminators.Length - 1 ? rowTerminator : fieldTerminator;
        char? quote = options.Csv ? (options.FieldQuote is { Length: 1 } q ? q[0] : '"') : null;
        return new BulkRowSource(new BulkTextReader(text, terminators, fieldTerminator, rowTerminator, quote), [.. Enumerable.Range(0, terminators.Length)], path);
    }

    /// <summary>A character-mode file's text: UTF-8, a byte-order mark skipped.</summary>
    private static string DecodeCharacterData(byte[] data) =>
        data is [0xEF, 0xBB, 0xBF, ..] ? Encoding.UTF8.GetString(data, 3, data.Length - 3) : Encoding.UTF8.GetString(data);

    /// <summary>The code page a native file's character data is in: the first character column's collation's.</summary>
    private static Encoding CodePageEncodingOf(HeapColumn[] columns)
    {
        foreach (var column in columns)
        {
            if (column.Type is CharSqlType or VarcharSqlType or TextSqlType)
                return column.Type.Collation!.StorageEncoding;
        }
        return CharSqlType.Cp1252Encoder;
    }
}

/// <summary>
/// A bulk load's rows over either reader, numbered from 1, each field bound
/// for a destination column. A read error ending the row stream — the file
/// ending mid-row, a malformed CSV field — surfaces as real reports it,
/// followed by the provider's Msg 7399 and Msg 7330; a malformed CSV record
/// that <c>FIRSTROW</c> skips is Msg 7301 instead.
/// </summary>
internal sealed class BulkRowSource
{
    private readonly BulkTextReader? text;
    private readonly BulkBinaryReader? binary;
    private readonly int[] destinations;
    private readonly string path;
    private readonly string?[]? textFields;

    public BulkRowSource(BulkTextReader reader, int[] destinations, string path)
    {
        this.text = reader;
        this.destinations = destinations;
        this.path = path;
        this.textFields = new string?[destinations.Length];
    }

    public BulkRowSource(BulkBinaryReader reader, int[] destinations, string path)
    {
        this.binary = reader;
        this.destinations = destinations;
        this.path = path;
    }

    /// <summary>How many fields each row carries.</summary>
    public int FieldCount => this.destinations.Length;

    /// <summary>The number of the row the next <see cref="Read"/> reads.</summary>
    public long NextRowNumber => this.text?.Row ?? this.binary!.Row;

    /// <summary>The 0-based destination column of field <paramref name="field"/>, or -1.</summary>
    public int DestinationOf(int field) => this.destinations[field];

    /// <summary>
    /// Reads the next row into <paramref name="cells"/> — text, a native
    /// value, or null for NULL; false at the end of the data.
    /// <paramref name="skipping"/> marks a row <c>FIRSTROW</c> passes over.
    /// </summary>
    public bool Read(object?[] cells, bool skipping)
    {
        try
        {
            if (this.text is { } reader)
            {
                if (!reader.Read(this.textFields!, this.path))
                    return false;
                Array.Copy(this.textFields!, cells, cells.Length);
                return true;
            }
            return this.binary!.Read(cells);
        }
        catch (SimulatedSqlException ex) when (ex.Number == 4879 && skipping)
        {
            throw SimulatedSqlException.BulkColumnsInfoUnavailable();
        }
        catch (SimulatedSqlException ex) when (ex.Number is 4832 or 4879)
        {
            throw SimulatedSqlException.BulkRowStreamFailed(ex);
        }
    }
}
