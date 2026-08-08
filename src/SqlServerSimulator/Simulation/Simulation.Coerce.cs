using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using StoredIndex = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Coerces an auto-generated identity <see cref="long"/> to the column's
    /// declared integer type, raising the IDENTITY-specific Msg 8115 if the
    /// next value won't fit.
    /// </summary>
    internal static SqlValue CoerceForIdentity(long value, HeapColumn identityColumn)
    {
        try
        {
            return SqlValue.FromInt64(value).CoerceTo(identityColumn.Type);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
        }
    }

    /// <summary>
    /// Raises a truncation error when the SOURCE value's natural length would
    /// exceed <paramref name="column"/>'s declared maximum. The check fires
    /// pre-coerce so that <c>char(N)</c> / <c>nchar(N)</c> / <c>binary(N)</c>
    /// columns — whose CoerceTo silently truncates to match SQL Server's CAST
    /// semantics — still raise the bind-time truncation error. NULL values
    /// and columns without a declared max are no-ops. Selects between the
    /// verbose Msg 2628 (with table/column/value) and the legacy Msg 8152 via
    /// <see cref="SimulatedDbConnection.IsVerboseTruncationActive"/>.
    /// </summary>
    /// <remarks>
    /// Length unit follows the column's storage encoding: CP1252 byte count
    /// for <c>varchar</c> / <c>char(N)</c>, raw byte count for <c>varbinary</c>
    /// / <c>binary(N)</c>, UCS-2 code units (<see cref="string.Length"/>) for
    /// <c>nvarchar</c> / <c>nchar(N)</c> / <c>sysname</c>. Non-string sources
    /// fall through (e.g. <c>INSERT INTO varchar(5) VALUES (12345)</c>): the
    /// integer-to-string format path inside <c>CoerceTo</c> produces a value
    /// the column can hold for the common cases, and any genuine overflow
    /// surfaces as a coercion error instead.
    /// </remarks>
    private static void EnforceMaxLength(SqlValue source, HeapColumn column, string tableName, SimulatedDbConnection connection)
    {
        if (source.IsNull || column.MaxLength is not int max || max == SqlType.MaxLengthSentinel)
            return;

        int actual;
        if (column.Type is VarbinarySqlType or BinarySqlType)
        {
            if (source.Type is not (VarbinarySqlType or BinarySqlType))
                return;
            actual = source.AsBytes.Length;
        }
        else if (column.Type is VarcharSqlType or CharSqlType)
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            // Route through the column collation's storage encoding so the
            // byte budget reflects what the column will actually store:
            // CP1252 for default / Latin1 / BIN / BIN2, UTF-8 for the three
            // *_UTF8 collations. Reading the encoding off the collation
            // (rather than calling GetVariableByteCount on column.Type)
            // works uniformly for both VarcharSqlType (variable-length) and
            // CharSqlType (fixed-length, no GetVariableByteCount override).
            actual = column.Type.Collation!.StorageEncoding.GetByteCount(source.AsString);
        }
        else
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            actual = source.AsString.Length;
        }

        if (actual <= max)
            return;

        if (!connection.IsVerboseTruncationActive())
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy();

        throw column.Type is VarbinarySqlType or BinarySqlType
            ? SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, source.AsBytes, max)
            : SimulatedSqlException.StringOrBinaryWouldBeTruncated(
                tableName,
                column.Name,
                source.AsString,
                max,
                column.Type is VarcharSqlType or CharSqlType ? column.Type.Collation!.StorageEncoding : null);
    }

    /// <summary>
    /// Coerces an INSERT source value to the destination column's type,
    /// converting any overflow into the SQL Server-shaped error — the
    /// source-type-keyed Msg 220 / 232 / 237 family via
    /// <see cref="SimulatedSqlException.TryConversionOverflow"/>, or the
    /// generic Msg 8115 where the chooser declines. Truncation of
    /// strings/bytes is handled separately by <see cref="EnforceMaxLength"/>
    /// before this method runs.
    /// </summary>
    /// <summary>
    /// <see cref="CoerceForInsert(SqlValue, SqlType)"/> plus the typed-xml
    /// contract: a value landing in an <c>xml(&lt;collection&gt;)</c> column is
    /// validated against that collection and stored in <b>canonical form</b>,
    /// so <c>&lt;c&gt;1.500&lt;/c&gt;</c> stores — and a trigger's
    /// <c>INSERTED</c> reads — as <c>&lt;c&gt;1.5&lt;/c&gt;</c>
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    /// <remarks>
    /// Hung on the coercion each write performs <em>per assigned column</em>
    /// rather than on a whole-row pass, because that is real's own rule and
    /// the cheap one: an <c>UPDATE</c> that never names the xml column must
    /// neither re-read its schema nor re-validate a value it isn't touching.
    /// </remarks>
    private static SqlValue CoerceForInsert(SqlValue source, HeapColumn column)
    {
        var coerced = CoerceForInsert(source, column.Type);
        if (column.XmlSchemaCollection is not { } collection || coerced.IsNull)
            return coerced;
        var canonical = XmlSchemaValidation.ValidateAndNormalize(collection, coerced.AsString);
        return ReferenceEquals(canonical, coerced.AsString) ? coerced : SqlValue.FromXml(canonical);
    }

    private static SqlValue CoerceForInsert(SqlValue source, SqlType targetType)
    {
        try
        {
            return source.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.TryConversionOverflow(source, targetType)
                ?? SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }
    }

    /// <summary>
    /// Materializes one row into a table-valued-parameter / structured-parameter
    /// clone: evaluates computed columns, enforces NOT NULL (Msg 515), CHECK
    /// (Msg 547), PRIMARY KEY / UNIQUE constraints (Msg 2627) and UNIQUE indexes
    /// (Msg 2601) against the rows inserted so far, then writes the row through
    /// the heap encoder. Shared by the in-process ADO.NET Structured parameter
    /// path and the TDS-wire TVP decode so both give the constraint-violation
    /// fidelity real SQL Server does ("The data for table-valued parameter … SQL
    /// Server error is: N"). The clone is a table variable, so the insert is not
    /// transaction-logged; it exists only to seed the parameter binding.
    /// </summary>
    internal static void InsertTableValuedParameterRow(HeapTable destination, SqlValue[] fullRowValues, BatchContext batch)
    {
        EvaluateComputedColumns(destination, fullRowValues, batch);
        EnforceNotNull(destination, fullRowValues);
        EnforceCheckConstraints(destination, fullRowValues, batch);
        var storedValues = ProjectStoredValues(destination, fullRowValues);
        // The table-variable grammar exposes no constraint WITH clause, so no
        // key here can carry IGNORE_DUP_KEY and neither call can ask to skip.
        _ = EnforceKeyConstraints(destination, fullRowValues, storedValues, batch);
        _ = EnforceUniqueIndexes(destination, fullRowValues, storedValues, batch);
        _ = destination.Heap.Insert(RowEncoder.EncodeRow(destination.StoredColumns, storedValues, destination.Heap));
    }

    /// <summary>
    /// Walks the table's computed columns and fills their slots in
    /// <paramref name="rowValues"/> by evaluating each expression against the
    /// current row's stored-column values. Computed-of-computed references
    /// are rejected at CREATE TABLE (Msg 1759), so every reference inside a
    /// computed expression resolves to a regular column already present in
    /// <paramref name="rowValues"/>. Both persisted and non-persisted slots
    /// are filled — persisted slots feed <see cref="ProjectStoredValues"/>
    /// for encoding, non-persisted slots feed any <c>OUTPUT INSERTED.&lt;col&gt;</c>
    /// projection.
    /// </summary>
    internal static void EvaluateComputedColumns(HeapTable destinationTable, SqlValue[] rowValues, BatchContext batch)
    {
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var column = destinationTable.Columns[i];
            if (column.Computed is null)
                continue;

            SqlValue ResolveByName(MultiPartName reference)
            {
                for (var k = 0; k < destinationTable.Columns.Length; k++)
                {
                    if (batch.CurrentDatabase.Collation.Equals(destinationTable.Columns[k].Name, reference.Leaf))
                        return rowValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(reference);
            }

            rowValues[i] = CoerceForInsert(column.Computed.Run(new RuntimeContext(ResolveByName, batch)), column.Type);
        }
    }

    /// <summary>
    /// Subsets the row's full-column SqlValue array down to just the values
    /// that participate in row storage, in storage-ordinal order — the shape
    /// <see cref="RowEncoder.EncodeRow(ReadOnlySpan{HeapColumn}, ReadOnlySpan{SqlValue}, Heap?)"/>
    /// expects when handed
    /// <see cref="HeapTable.StoredColumns"/>. Non-persisted computed columns
    /// have no storage slot and are dropped here.
    /// </summary>
    internal static SqlValue[] ProjectStoredValues(HeapTable destinationTable, SqlValue[] rowValues)
    {
        if (destinationTable.StoredColumns.Length == destinationTable.Columns.Length)
            return rowValues;

        var stored = new SqlValue[destinationTable.StoredColumns.Length];
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var ordinal = destinationTable.StorageOrdinals[i];
            if (ordinal >= 0)
                stored[ordinal] = rowValues[i];
        }
        return stored;
    }

    /// <summary>
    /// Per-column NULL check on the final row, run after defaults have filled
    /// and computed columns have evaluated. A NULL in a NOT-NULL column raises
    /// Msg 515 naming that column. Mirrors SQL Server's order: any inserted
    /// value (or computed result) of NULL in a non-nullable column fails the
    /// statement; identity columns can't be nullable so they're a no-op here.
    /// Non-persisted computed columns participate even though they're not
    /// stored — SQL Server considers their evaluated value when checking
    /// nullability.
    /// </summary>
    private static void EnforceNotNull(HeapTable destinationTable, SqlValue[] rowValues, string verb = "INSERT")
    {
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var column = destinationTable.Columns[i];
            if (!column.Nullable && rowValues[i].IsNull)
                throw SimulatedSqlException.CannotInsertNull(column.Name, QualifyForNullMessage(destinationTable), verb);
        }
    }

    /// <summary>
    /// The table name as Msg 515 spells it, which differs by table kind
    /// (probe-confirmed against SQL Server 2025): a permanent table is named
    /// <c>database.schema.table</c>, a temp table is qualified into
    /// <c>tempdb.dbo</c>, and a table variable is named bare.
    /// </summary>
    /// <remarks>
    /// Real writes a temp table's <em>internal</em> name — the declared name
    /// padded with underscores to a fixed width plus a per-session numeric
    /// suffix. The simulator names it <c>tempdb.dbo.#t</c>: the database and
    /// schema are what identify the table for a reader, and the padding
    /// encodes a session-local identity nothing consumes.
    /// </remarks>
    private static string QualifyForNullMessage(HeapTable table)
    {
        if (table.IsTableVariable)
            return table.Name;
        if (table.OwningDatabase is { } owner)
            return QualifyTableName(table, owner);
        return $"{TempdbDatabaseName}.{Database.DefaultSchemaName}.{table.Name}";
    }

    /// <summary>
    /// The database name the constraint-violation messages (the Msg 547
    /// family) put in their <c>in database "…"</c> slot: the table's own
    /// owning database, or <c>tempdb</c> for a temp table or table variable,
    /// which is where real serves those from.
    /// </summary>
    internal static string DatabaseNameFor(HeapTable table) =>
        table.OwningDatabase?.Name ?? TempdbDatabaseName;

    /// <summary>
    /// The <c>schema.table</c> half of the same messages.
    /// </summary>
    internal static string SchemaQualifiedName(HeapTable table, Database? database) =>
        database is null
            ? $"{Database.DefaultSchemaName}.{table.Name}"
            : QualifyTableName(table, database)[(database.Name.Length + 1)..];

    /// <summary>
    /// Evaluates each declared CHECK constraint against the new row. A
    /// predicate that evaluates to <c>false</c> (definitely-false in SQL
    /// Server's three-valued logic) raises Msg 547 naming the constraint;
    /// <c>true</c> and <c>null</c> (UNKNOWN) both pass — the latter matches
    /// SQL Server's documented "NULL → row passes CHECK" rule. Resolver
    /// matches the row's column ordinals via case-insensitive name compare,
    /// the same shape <see cref="EvaluateComputedColumns"/> uses.
    /// </summary>
    private static void EnforceCheckConstraints(HeapTable destinationTable, SqlValue[] rowValues, BatchContext batch, string verb = "INSERT")
    {
        if (destinationTable.CheckConstraints.Count == 0)
            return;

        SqlValue ResolveByName(MultiPartName reference)
        {
            for (var k = 0; k < destinationTable.Columns.Length; k++)
            {
                if (batch.CurrentDatabase.Collation.Equals(destinationTable.Columns[k].Name, reference.Leaf))
                    return rowValues[k];
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        var runtime = new RuntimeContext(ResolveByName, batch);
        foreach (var check in destinationTable.CheckConstraints)
        {
            if (check.IsDisabled)
                continue;
            if (check.Predicate.Run(runtime) == false)
            {
                throw SimulatedSqlException.CheckConstraintViolation(
                    check.Name,
                    DatabaseNameFor(destinationTable),
                    SchemaQualifiedName(destinationTable, destinationTable.OwningDatabase),
                    check.InlineColumn,
                    verb);
            }
        }
    }

    /// <summary>
    /// Raises Msg 8655 when <paramref name="table"/> carries a <i>disabled
    /// clustered</i> index. On real the clustered index <b>is</b> the table's
    /// storage, so disabling it makes the data unreachable: every query and every
    /// DML against the table fails, naming the offending index
    /// (probe-confirmed for SELECT and INSERT alike). A disabled
    /// <i>nonclustered</i> index only stops being enforced and searched — the
    /// table stays fully usable — so it isn't grounds for this.
    /// <para>
    /// DDL is deliberately not gated: <c>ALTER INDEX … REBUILD</c> and
    /// <c>DROP INDEX</c> keep working on a locked table, which is how real
    /// recovers it, so this is called from the query row-source and the DML target
    /// paths rather than from table resolution.
    /// </para>
    /// </summary>
    internal static void RejectDisabledClusteredIndex(HeapTable table)
    {
        foreach (var constraint in table.KeyConstraints)
        {
            if (constraint.IsDisabled && constraint.IsClustered)
                throw SimulatedSqlException.QueryProcessorDisabledIndex(constraint.Name, table.Name);
        }

        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled && index.IsClustered)
                throw SimulatedSqlException.QueryProcessorDisabledIndex(index.Name, table.Name);
        }
    }

    /// <summary>
    /// The <c>QUOTED_IDENTIFIER</c> option name as Msg 1934 / 1935 spell it.
    /// </summary>
    internal const string QuotedIdentifierOptionName = "QUOTED_IDENTIFIER";

    /// <summary>
    /// The option list Msg 1934 names for the session at
    /// <paramref name="context"/>'s parse position, or <see langword="null"/>
    /// when every option real's gate requires is set the way it wants.
    /// <para>
    /// Real requires <c>QUOTED_IDENTIFIER</c> / <c>ANSI_NULLS</c> /
    /// <c>CONCAT_NULL_YIELDS_NULL</c> / <c>ANSI_WARNINGS</c> /
    /// <c>ANSI_PADDING</c> ON and <c>NUMERIC_ROUNDABORT</c> OFF, and names
    /// every offending one comma-separated in that fixed order — not the
    /// order the session set them (probe-confirmed against SQL Server 2025
    /// with three and five options wrong at once). <c>QUOTED_IDENTIFIER</c>
    /// is reported <i>alone</i> when it is off, whatever the other five say.
    /// </para>
    /// <para>
    /// <c>ARITHABORT</c> is documented as part of the required set but never
    /// appears: real's gate accepts a session whose <c>ARITHABORT</c> bit is
    /// 0 as long as <c>ANSI_WARNINGS</c> is on (probe-confirmed by reading
    /// <c>@@OPTIONS &amp; 64</c> in the failing batch), which is the
    /// ANSI_WARNINGS-implies-ARITHABORT rule standing in for it.
    /// </para>
    /// </summary>
    internal static string? IncorrectSetOptionNames(ParserContext context)
    {
        // The parse-position setting, so a module body answers from its own
        // creation-time capture rather than the caller's session.
        if (!context.QuotedIdentifiers)
            return QuotedIdentifierOptionName;
        var connection = context.Connection;
        if (connection is { AnsiNulls: true, ConcatNullYieldsNull: true, AnsiWarnings: true, AnsiPadding: true, NumericRoundabort: false })
            return null;
        var offenders = new List<string>(5);
        if (!connection.AnsiNulls)
            offenders.Add("ANSI_NULLS");
        if (!connection.ConcatNullYieldsNull)
            offenders.Add("CONCAT_NULL_YIELDS_NULL");
        if (!connection.AnsiWarnings)
            offenders.Add("ANSI_WARNINGS");
        if (!connection.AnsiPadding)
            offenders.Add("ANSI_PADDING");
        if (connection.NumericRoundabort)
            offenders.Add("NUMERIC_ROUNDABORT");
        return string.Join(", ", offenders);
    }

    /// <summary>
    /// Raises Msg 1934 when a write to <paramref name="table"/> runs under a
    /// SET-option setting real's gate refuses and the table carries one of the
    /// features whose stored expressions real re-evaluates at write time — a
    /// <c>PERSISTED</c> computed column, an enabled index over a computed
    /// column, an enabled filtered index, an XML or spatial index, or an
    /// indexed view built on it. Those expressions were parsed under the
    /// creating session's setting, so real refuses to maintain them from a
    /// session that would read them differently.
    /// <para>
    /// Probe-confirmed boundaries (SQL Server 2025): reads are never gated —
    /// <c>SELECT</c> from such a table succeeds; a non-persisted computed
    /// column with no index over it doesn't gate; a <i>disabled</i> filtered
    /// index doesn't gate; and dropping the computed column's index lifts the
    /// gate again. The check reads the parse-position setting, so a module
    /// body runs it against the module's captured
    /// <see cref="Schemas.SchemaObject.UsesQuotedIdentifier"/>, not the
    /// caller's.
    /// </para>
    /// <para>
    /// Create-time body binding is exempt — real accepts
    /// <c>CREATE PROCEDURE … AS INSERT …</c> under OFF and raises only when
    /// the body runs. Ordinary skip mode is <b>not</b> exempt: a never-taken
    /// <c>IF 1 = 0 INSERT …</c> still raises, real gating the batch rather
    /// than the executed path (both probe-confirmed).
    /// </para>
    /// </summary>
    /// <param name="table">The DML target.</param>
    /// <param name="batch">Supplies the effective SET-option settings.</param>
    /// <param name="verb">The statement name real echoes (<c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c>).</param>
    internal static void RejectIncorrectSetOptionsForWrite(HeapTable table, BatchContext batch, string verb)
    {
        // Option reads first: the table walk below is per-write work, and a
        // session with the options real wants — the overwhelmingly common
        // case — never reaches it.
        if (batch.CreateTimeBinding || IncorrectSetOptionNames(batch.Parser) is not { } options || !RequiresCorrectSetOptions(table))
            return;
        throw SimulatedSqlException.IncorrectSetOptions(verb, options);
    }

    /// <summary>
    /// Whether writing to <paramref name="table"/> re-evaluates an expression
    /// captured under a creating session's SET options — the condition behind
    /// <see cref="RejectIncorrectSetOptionsForWrite"/>.
    /// </summary>
    private static bool RequiresCorrectSetOptions(HeapTable table)
    {
        if (table.XmlIndexes.Count > 0 || table.SpatialIndexes.Count > 0 || table.DependentIndexedViews.Count > 0)
            return true;

        foreach (var column in table.Columns)
        {
            if (column is { Computed: not null, IsPersisted: true })
                return true;
        }

        foreach (var index in table.Indexes)
        {
            if (index.IsDisabled)
                continue;
            if (index.Filter is not null || IndexCoversComputedColumn(table, index))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="index"/> keys or includes a computed column of
    /// <paramref name="table"/>. An index over a computed column gates writes
    /// even when the column itself isn't persisted (probe-confirmed): the
    /// index stores the computed value, so maintaining it re-evaluates the
    /// expression.
    /// </summary>
    private static bool IndexCoversComputedColumn(HeapTable table, StoredIndex index)
    {
        foreach (var key in index.KeyColumns)
        {
            if ((uint)key.ColumnOrdinal < (uint)table.Columns.Length && table.Columns[key.ColumnOrdinal].Computed is not null)
                return true;
        }

        foreach (var ordinal in index.IncludedColumnOrdinals)
        {
            if ((uint)ordinal < (uint)table.Columns.Length && table.Columns[ordinal].Computed is not null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// What key-uniqueness enforcement decided about a candidate row.
    /// </summary>
    private enum RowKeyVerdict
    {
        /// <summary>No duplicate — the caller writes the row.</summary>
        Unique,

        /// <summary>
        /// The row duplicates a key whose index or constraint declares
        /// <c>IGNORE_DUP_KEY</c>, so the caller drops it and carries on with the
        /// rest of the statement. Only the plain INSERT paths act on this; the
        /// UPDATE / MERGE enforcers never return it, because real keeps raising
        /// Msg 2601 / 2627 there (probe-confirmed, including MERGE's
        /// <c>WHEN NOT MATCHED THEN INSERT</c>).
        /// </summary>
        SkipDuplicate,
    }

    /// <summary>
    /// Records that a duplicate was dropped, queueing the severity-0 Msg 3604
    /// the first time it happens in this statement. Real emits it once however
    /// many rows were skipped, and not at all when none were (probe-confirmed).
    /// </summary>
    private static RowKeyVerdict ReportIgnoredDuplicate(BatchContext batch)
    {
        if (!batch.CurrentStatement.ReportedIgnoredDuplicate)
        {
            batch.CurrentStatement.ReportedIgnoredDuplicate = true;
            batch.AppendInfoError(@class: 0, state: 0, number: 3604, message: "Duplicate key was ignored.");
        }

        return RowKeyVerdict.SkipDuplicate;
    }

    /// <summary>
    /// Prepares a key-uniqueness seek of <paramref name="table"/>'s heap for the
    /// key tuple <paramref name="storageOrdinals"/> names in
    /// <paramref name="storedRowValues"/>: resolves the per-component promoted
    /// types the seek entry keys on (each key column's own stored type, matching
    /// the foreign-key path's convention) and builds the probe.
    /// <see langword="false"/> means the caller has to fall back to the full
    /// scan — a key column has no storage slot, or a key component is NULL,
    /// which the seek's NULL-free buckets can't answer for a rule under which
    /// two NULLs collide.
    /// <para>
    /// Every other heap seeks, small ones included: a minimum-size gate was
    /// measured and dropped. It won nowhere — 500 keyed tables seeded 1 / 3 / 10
    /// / 50 rows each came out within noise either way — because building a
    /// bucket entry over a heap that small is nearly free, and it cost 26% at
    /// 200 rows per table and up to 1.9× per insert on a few-hundred-row narrow
    /// table, whose rows all still fit the page it exempted. It saved no memory
    /// either: a table big enough for its index to matter is past any such
    /// threshold by definition, so the gate only ever exempted indexes that were
    /// trivially small, while making enforcement allocate <i>more</i> (every
    /// scan comparison decodes a value).
    /// </para>
    /// </summary>
    private static bool TryPrepareKeySeek(
        HeapTable table, int[] storageOrdinals, SqlValue[] storedRowValues, out SqlType[] commons, out SqlValueKey probe)
    {
        (commons, probe) = ([], default);
        var types = new SqlType[storageOrdinals.Length];
        for (var i = 0; i < storageOrdinals.Length; i++)
        {
            if (storageOrdinals[i] < 0)
                return false;
            types[i] = table.StoredColumns[storageOrdinals[i]].Type;
        }

        if (!TryBuildSeekProbe(storedRowValues, storageOrdinals, types, out probe))
            return false;

        commons = types;
        return true;
    }

    /// <summary>
    /// Decodes a whole existing row image into full-ordinal order — the shape a
    /// filtered index's WHERE predicate evaluates against. Reuses
    /// <paramref name="buffer"/> across rows (allocated on first call), since
    /// the predicate reads each row's values before the next decode overwrites
    /// them. Columns with no storage slot (non-persisted computed) surface as
    /// typed NULLs.
    /// </summary>
    private static SqlValue[] DecodeFullRow(HeapTable table, ReadOnlySpan<byte> rowBytes, ref SqlValue[]? buffer)
    {
        buffer ??= new SqlValue[table.Columns.Length];
        for (var c = 0; c < table.Columns.Length; c++)
        {
            var storageOrdinal = table.StorageOrdinals[c];
            buffer[c] = storageOrdinal >= 0
                ? RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, storageOrdinal, table.Heap)
                : SqlValue.Null(table.Columns[c].Type);
        }
        return buffer;
    }

    /// <summary>
    /// The full row with its <b>computed columns evaluated</b> — what a key or
    /// filter naming a non-persisted computed column has to be read from, since
    /// such a column occupies no storage slot and the plain decode leaves it
    /// NULL.
    /// </summary>
    private static SqlValue[] DecodeFullRowWithComputed(HeapTable table, ReadOnlySpan<byte> rowBytes, BatchContext batch, ref SqlValue[]? buffer)
    {
        var full = DecodeFullRow(table, rowBytes, ref buffer);
        EvaluateComputedColumns(table, full, batch);
        return full;
    }

    /// <summary>One key tuple, read by full ordinal off an evaluated full row.</summary>
    private static SqlValue[] ReadKeyByFullOrdinals(int[] fullOrdinals, SqlValue[] fullRow)
    {
        var key = new SqlValue[fullOrdinals.Length];
        for (var i = 0; i < fullOrdinals.Length; i++)
            key[i] = fullRow[fullOrdinals[i]];
        return key;
    }

    /// <summary>
    /// Every key tuple the table's live rows carry over <paramref name="fullOrdinals"/>,
    /// with the computed components evaluated — the existing-row side of a
    /// uniqueness check whose key no seek can reach.
    /// </summary>
    /// <remarks>
    /// Built once and probed per row rather than re-scanned per row: a key with
    /// a non-persisted computed component can't use the per-<c>Heap</c> seek
    /// cache (which indexes stored bytes), so without this a K-row statement
    /// would scan the whole table K times. <paramref name="excludedAddresses"/>
    /// drops the rows the statement is itself rewriting, whose new keys the
    /// caller compares among themselves.
    /// </remarks>
    private static HashSet<SqlValueKey> BuildComputedKeySet(
        HeapTable table,
        int[] fullOrdinals,
        BooleanExpression? filter,
        BatchContext batch,
        HashSet<(int Page, int Slot)>? excludedAddresses)
    {
        var keys = new HashSet<SqlValueKey>();
        SqlValue[]? buffer = null;
        foreach (var (page, slot, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            if (excludedAddresses is not null && excludedAddresses.Contains((page, slot)))
                continue;
            var full = DecodeFullRowWithComputed(table, rowBytes, batch, ref buffer);
            if (filter is not null && EvaluateIndexFilter(filter, table, full, batch) != true)
                continue;
            _ = keys.Add(new SqlValueKey(ReadKeyByFullOrdinals(fullOrdinals, full)));
        }

        return keys;
    }

    /// <summary>
    /// The statement-scoped existing-key set for one unique key whose columns
    /// include a non-persisted computed column, built on first use and then
    /// extended with each row the statement admits — so the rows a multi-row
    /// INSERT adds collide with each other exactly as they would with rows
    /// already on disk.
    /// </summary>
    private static HashSet<SqlValueKey> ComputedKeySetFor(
        HeapTable table, object key, int[] fullOrdinals, BooleanExpression? filter, BatchContext batch)
    {
        var cache = batch.CurrentStatement.ComputedUniqueKeys ??= new Dictionary<object, HashSet<SqlValueKey>>(ReferenceEqualityComparer.Instance);
        if (!cache.TryGetValue(key, out var keys))
            cache[key] = keys = BuildComputedKeySet(table, fullOrdinals, filter, batch, excludedAddresses: null);
        return keys;
    }

    /// <summary>
    /// Finds a row whose key tuple equals the new row's, raising Msg 2627 with
    /// the offending constraint's name. Skips when the table has no PK/UNIQUE
    /// constraints. Each constraint either seeks the shared per-<c>Heap</c>
    /// cache — whose candidates come back verified against live bytes, so a hit
    /// <i>is</i> the duplicate — or, when <see cref="TryPrepareKeySeek"/>
    /// declines, joins the scan pass below.
    /// SqlValue equality handles SQL Server's NULLs-equal-for-UNIQUE
    /// rule (two NULLs collide; one NULL and one non-NULL don't), and the seek's
    /// key equality is the same comparison per component — including the
    /// collation-aware, ANSI-padded string path of
    /// <see cref="SqlValue.Equals(SqlValue)"/>, which
    /// <see cref="SqlValue.GetHashCode"/> is built to agree with so a
    /// case-insensitive duplicate lands in the bucket its collision needs.
    /// <paramref name="storedRowValues"/> is the row's values in
    /// storage-ordinal order (the output of <see cref="ProjectStoredValues"/>);
    /// the scan decodes existing rows one key column at a time so we don't pay
    /// the cost of materializing whole rows for tables that have just a small
    /// composite key.
    /// </summary>
    private static RowKeyVerdict EnforceKeyConstraints(HeapTable destinationTable, SqlValue[] rowValues, SqlValue[] storedRowValues, BatchContext batch)
    {
        if (destinationTable.KeyConstraints.Count == 0)
            return RowKeyVerdict.Unique;

        List<KeyConstraint>? scanned = null;
        foreach (var constraint in destinationTable.KeyConstraints)
        {
            // ALTER INDEX … DISABLE takes the backing index out of service, and
            // while it's out the constraint isn't enforced (probe-confirmed).
            if (constraint.IsDisabled)
                continue;
            // A UNIQUE constraint over a non-persisted computed column probes
            // the statement's key set — see the unique-index path for why.
            if (!constraint.KeysAreStored)
            {
                var keys = ComputedKeySetFor(destinationTable, constraint, constraint.FullOrdinals, filter: null, batch);
                if (!keys.Add(new SqlValueKey(ReadKeyByFullOrdinals(constraint.FullOrdinals, rowValues))))
                {
                    return constraint.IgnoreDupKey
                        ? ReportIgnoredDuplicate(batch)
                        : throw KeyConstraintViolationOnComputedKey(destinationTable, constraint, rowValues);
                }
                continue;
            }
            if (!TryPrepareKeySeek(destinationTable, constraint.StorageOrdinals, storedRowValues, out var commons, out var probe))
            {
                (scanned ??= []).Add(constraint);
                continue;
            }

            if (HeapSeekCache.For(destinationTable.Heap).AnyRowMatches(
                    destinationTable.Heap, destinationTable.StoredColumns, constraint.StorageOrdinals, commons, probe))
            {
                return constraint.IgnoreDupKey
                    ? ReportIgnoredDuplicate(batch)
                    : throw KeyConstraintViolation(destinationTable, constraint, storedRowValues);
            }
        }

        if (scanned is null)
            return RowKeyVerdict.Unique;

        var storedColumns = destinationTable.StoredColumns;
        var lobStore = destinationTable.Heap;

        foreach (var rowBytes in destinationTable.Heap.EnumerateRows())
        {
            foreach (var constraint in scanned)
            {
                var allEqual = true;
                for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
                {
                    var ord = constraint.StorageOrdinals[i];
                    var existing = RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, lobStore);
                    if (!existing.Equals(storedRowValues[ord]))
                    {
                        allEqual = false;
                        break;
                    }
                }
                if (allEqual)
                {
                    return constraint.IgnoreDupKey
                        ? ReportIgnoredDuplicate(batch)
                        : throw KeyConstraintViolation(destinationTable, constraint, storedRowValues);
                }
            }
        }

        return RowKeyVerdict.Unique;
    }

    /// <summary>Msg 2627 for <paramref name="constraint"/>, rendering the
    /// offending key tuple the way SQL Server does. Shared by the INSERT and
    /// UPDATE enforcement paths.</summary>
    private static SimulatedSqlException KeyConstraintViolation(HeapTable table, KeyConstraint constraint, SqlValue[] storedValues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(storedValues[constraint.StorageOrdinals[i]]));
        }
        return SimulatedSqlException.ViolationOfKeyConstraint(constraint.ViolationKindWord, constraint.Name, table.Name, sb.ToString());
    }

    /// <summary>
    /// Msg 2627 / Msg 2601 rendered from a full row rather than a stored one —
    /// the shape a key naming a non-persisted computed column needs, since that
    /// column's value exists only on the evaluated full row.
    /// </summary>
    private static SimulatedSqlException KeyConstraintViolationOnComputedKey(HeapTable table, KeyConstraint constraint, SqlValue[] fullRow)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < constraint.FullOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(fullRow[constraint.FullOrdinals[i]]));
        }
        return SimulatedSqlException.ViolationOfKeyConstraint(constraint.ViolationKindWord, constraint.Name, table.Name, sb.ToString());
    }

    /// <inheritdoc cref="KeyConstraintViolationOnComputedKey"/>
    private static SimulatedSqlException UniqueIndexViolationOnComputedKey(Storage.Index index, string qualifiedTableName, SqlValue[] fullRow) =>
        SimulatedSqlException.ViolationOfUniqueIndex(index.Name, qualifiedTableName, FormatIndexKeyValues(ReadKeyByFullOrdinals(index.KeyFullOrdinals, fullRow)));

    /// <summary>Msg 2601 for <paramref name="index"/>. Shared by the INSERT and
    /// UPDATE enforcement paths.</summary>
    private static SimulatedSqlException UniqueIndexViolation(Storage.Index index, string qualifiedTableName, SqlValue[] storedValues)
    {
        var key = new SqlValue[index.KeyStorageOrdinals.Length];
        for (var i = 0; i < key.Length; i++)
            key[i] = storedValues[index.KeyStorageOrdinals[i]];
        return SimulatedSqlException.ViolationOfUniqueIndex(index.Name, qualifiedTableName, FormatIndexKeyValues(key));
    }

    /// <summary>
    /// Walks <see cref="HeapTable.Indexes"/> for every UNIQUE entry and
    /// raises Msg 2601 on the first key-tuple collision against existing
    /// rows. When an index has a <c>Index.Filter</c>, only rows for
    /// which the filter evaluates true on both sides participate in the
    /// uniqueness check (filtered-unique-index semantic) — the seek narrows the
    /// candidates by key and the filter is then evaluated on each candidate's
    /// own decoded row, so a filtered index seeks like any other. Mirrors
    /// <see cref="EnforceKeyConstraints"/>'s seek-or-scan shape; called
    /// alongside it after a successful row build.
    /// </summary>
    private static RowKeyVerdict EnforceUniqueIndexes(HeapTable destinationTable, SqlValue[] rowValues, SqlValue[] storedRowValues, BatchContext batch)
    {
        if (destinationTable.Indexes.Count == 0)
            return RowKeyVerdict.Unique;

        var hasUnique = false;
        foreach (var ix in destinationTable.Indexes)
        {
            if (ix.IsUnique && !ix.IsDisabled)
            {
                hasUnique = true;
                break;
            }
        }
        if (!hasUnique)
            return RowKeyVerdict.Unique;

        var storedColumns = destinationTable.StoredColumns;
        var lobStore = destinationTable.Heap;
        SqlValue[]? existingRowValues = null;
        var qualifiedTableName = $"{Database.DefaultSchemaName}.{destinationTable.Name}";

        foreach (var index in destinationTable.Indexes)
        {
            if (!index.IsUnique || index.IsDisabled)
                continue;
            if (index.Filter is not null && Simulation.EvaluateIndexFilter(index.Filter, destinationTable, rowValues, batch) != true)
                continue;

            // A key naming a non-persisted computed column can't be seeked, so
            // it probes the statement's own key set instead — built from one
            // scan and extended with each admitted row.
            if (!index.KeysAreStored)
            {
                var fullOrdinals = index.KeyFullOrdinals;
                var keys = ComputedKeySetFor(destinationTable, index, fullOrdinals, index.Filter, batch);
                var candidate = new SqlValueKey(ReadKeyByFullOrdinals(fullOrdinals, rowValues));
                if (!keys.Add(candidate))
                {
                    return index.IgnoreDupKey
                        ? ReportIgnoredDuplicate(batch)
                        : throw UniqueIndexViolationOnComputedKey(index, qualifiedTableName, rowValues);
                }
                continue;
            }

            if (TryPrepareKeySeek(destinationTable, index.KeyStorageOrdinals, storedRowValues, out var commons, out var probe))
            {
                foreach (var (_, _, bytes) in HeapSeekCache.For(lobStore)
                    .MatchingRows(lobStore, storedColumns, index.KeyStorageOrdinals, commons, probe))
                {
                    if (index.Filter is { } seekFilter
                        && Simulation.EvaluateIndexFilter(seekFilter, destinationTable, DecodeFullRow(destinationTable, bytes, ref existingRowValues), batch) != true)
                    {
                        continue;
                    }

                    return index.IgnoreDupKey
                        ? ReportIgnoredDuplicate(batch)
                        : throw UniqueIndexViolation(index, qualifiedTableName, storedRowValues);
                }
                continue;
            }

            foreach (var rowBytes in destinationTable.Heap.EnumerateRows())
            {
                if (index.Filter is { } filter
                    && Simulation.EvaluateIndexFilter(filter, destinationTable, DecodeFullRow(destinationTable, rowBytes, ref existingRowValues), batch) != true)
                {
                    continue;
                }

                var allEqual = true;
                for (var i = 0; i < index.KeyStorageOrdinals.Length; i++)
                {
                    var ord = index.KeyStorageOrdinals[i];
                    var existing = RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, lobStore);
                    if (!existing.Equals(storedRowValues[ord]))
                    {
                        allEqual = false;
                        break;
                    }
                }
                if (allEqual)
                {
                    return index.IgnoreDupKey
                        ? ReportIgnoredDuplicate(batch)
                        : throw UniqueIndexViolation(index, qualifiedTableName, storedRowValues);
                }
            }
        }

        return RowKeyVerdict.Unique;
    }

    /// <summary>
    /// Renders a key-tuple slot the way SQL Server's Msg 2627 does: NULL as
    /// <c>&lt;NULL&gt;</c>, strings raw (no enclosing quotes), numerics in
    /// invariant culture, byte arrays as <c>0x</c>-prefixed hex, date/time
    /// values in their canonical ISO forms. Every key-eligible type is
    /// covered explicitly — un-modeled types throw rather than reaching for
    /// the debugger-only <see cref="object.ToString"/>, since the convention
    /// here is that production paths never depend on debug-only formatting.
    /// </summary>
    private static string FormatKeyValue(SqlValue value) =>
        value.IsNull ? "<NULL>"
        : value.Type.Category == SqlTypeCategory.String ? value.AsString
        : value.Type switch
        {
            _ when value.Type == SqlType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.BigInt => value.AsInt64.ToString(CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.SmallInt => value.AsInt16.ToString(CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.TinyInt => value.AsByte.ToString(CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.Bit => value.AsBoolean ? "1" : "0",
            _ when value.Type == SqlType.UniqueIdentifier => value.AsGuid.ToString("D", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.Date => value.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.DateTime => value.AsDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.SmallDateTime => value.AsSmallDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateTime2SqlType => value.AsDateTime2.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            TimeSqlType => value.AsTime.ToString("c", CultureInfo.InvariantCulture),
            DateTimeOffsetSqlType => value.AsDateTimeOffset.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.Float || value.Type == SqlType.Real => value.FormatApproximateWithStyle(0),
            _ when value.Type == SqlType.Money || value.Type == SqlType.SmallMoney => value.AsMoneyDecimal38.ToString(),
            VarbinarySqlType or BinarySqlType => $"0x{Convert.ToHexString(value.AsBytes)}",
            DecimalSqlType => value.AsDecimal38.ToString(),
            _ => throw new NotSupportedException($"No key-violation rendering for {value.Type}."),
        };
}
