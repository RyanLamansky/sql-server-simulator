using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Tabular query result over one of two row representations: encoded
/// <c>byte[]</c> rows in the page-row format (the niche producers — set ops,
/// TVFs, OPENJSON, views, …), or already-projected <see cref="SqlValue"/>[]
/// rows (the FROM-bearing SELECT projection path). The <c>SqlValue[]</c> form
/// travels to the client as it is — <see cref="MaterializeRows"/> keeps the
/// producer's form at the statement boundary and <see cref="CreateCursor"/>
/// serves its cells directly — so a projecting SELECT stops encoding each row
/// into a page image the reader would decode straight back. The one thing the
/// page image did that the values don't is the encoder's lossy narrowing of
/// character data an ANSI code page can't carry, which
/// <c>RowEncoder.StorageForm</c> applies per cell for the columns that can
/// suffer it. <see cref="RowBytes"/> stays available for the byte-consuming
/// paths (INSERT…SELECT, set-op operands, subqueries, FOR XML / FOR JSON,
/// cursors); it encodes the <see cref="SqlValue"/>[] form lazily, so those
/// callers pay exactly what they did before.
/// </summary>
internal sealed class SimulatedSqlResultSet : SimulatedQueryResult
{
    private readonly SqlType[] schema;
    private readonly string[] columnNames;
    private IEnumerable<byte[]>? rowBytes;
    private IEnumerable<SqlValue[]>? rowValues;

    /// <summary>
    /// <paramref name="recordsAffected"/> is set only by a DML statement whose
    /// <c>OUTPUT</c> clause returns rows to the client: the statement reports
    /// what it changed as well as what it returned. Left at <c>-1</c>
    /// everywhere else, where the row count is a returned-row count.
    /// </summary>
    public SimulatedSqlResultSet(SqlType[] schema, string[] columnNames, IEnumerable<byte[]> rowBytes, int recordsAffected = -1)
        : base(recordsAffected)
    {
        this.schema = schema;
        this.columnNames = columnNames;
        this.rowBytes = rowBytes;
    }

    public SimulatedSqlResultSet(SqlType[] schema, string[] columnNames, IEnumerable<SqlValue[]> rowValues)
    {
        this.schema = schema;
        this.columnNames = columnNames;
        this.rowValues = rowValues;
    }

    public override string[] ColumnNames => this.columnNames;

    public override SqlType[] Schema => this.schema;

    public IEnumerable<byte[]> RowBytes => this.rowBytes
        ?? this.rowValues!.Select(values => RowEncoder.EncodeRow(this.schema, values));

    /// <summary>
    /// The mirror of <see cref="RowBytes"/> for the callers that want cells
    /// rather than a page image — <c>SELECT … INTO</c>, which re-encodes each
    /// row against the destination's own columns. Where the producer already
    /// projected <see cref="SqlValue"/>[] this hands those rows over untouched,
    /// so the statement stops paying an encode <em>and</em> a decode per row to
    /// arrive back at the values the projection computed; a byte-producing
    /// source decodes exactly as the caller used to.
    /// </summary>
    public IEnumerable<SqlValue[]> RowValues => this.rowValues
        ?? this.rowBytes!.Select(bytes => RowDecoder.DecodeRow(this.schema, bytes));

    /// <summary>
    /// Drains the row sequence into a list, keeping whichever form the producer
    /// yielded, and reports the row count. The dispatch loop calls this at the
    /// statement boundary — statement atomicity requires the rows be produced
    /// before the next statement runs, and <c>@@ROWCOUNT</c> requires the count
    /// — so a projecting SELECT holds <see cref="SqlValue"/> rows from here to
    /// the client instead of a page image it would decode straight back.
    /// </summary>
    /// <summary>
    /// Caps the row sequence at the session's <c>SET ROWCOUNT</c> value, doing
    /// nothing when the option is off (<c>0</c>). Applied to a statement's own
    /// result — the top-level SELECT, the source a <c>SELECT … INTO</c> or an
    /// <c>INSERT … SELECT</c> consumes, a <c>MERGE</c>'s USING source — rather
    /// than to the inner plans a join or subquery drives, matching real, where
    /// the cap is on what the statement returns or changes. The wrap is lazy,
    /// so a capped statement stops producing rows at the cap instead of
    /// producing them all and discarding the tail.
    /// </summary>
    public SimulatedSqlResultSet WithRowCountLimit(long limit)
    {
        if (limit <= 0)
            return this;
        var take = (int)Math.Min(limit, int.MaxValue);
        if (this.rowValues is { } values)
            this.rowValues = values.Take(take);
        else
            this.rowBytes = this.rowBytes!.Take(take);
        return this;
    }

    public int MaterializeRows()
    {
        if (this.rowValues is { } values)
        {
            var list = values as List<SqlValue[]> ?? [.. values];
            this.rowValues = list;
            return list.Count;
        }

        var bytes = this.rowBytes as List<byte[]> ?? [.. this.rowBytes!];
        this.rowBytes = bytes;
        return bytes.Count;
    }

    public override RowCursor CreateCursor() => this.rowValues is { } values
        ? new ValueArrayCursor(this.schema, values.GetEnumerator())
        : new SqlValueCursor(this.schema, this.rowBytes!.GetEnumerator());

    /// <summary>
    /// Cursor over already-projected <see cref="SqlValue"/>[] rows — the
    /// indexer serves the stored value, no per-cell decode, past the one
    /// narrowing the storage form would have applied (see
    /// <c>RowEncoder.NarrowingColumns</c>: only a column whose ANSI code page
    /// can fold a character carries any per-cell work, and most schemas carry
    /// none at all). Shares the peek-and-buffer <see cref="HasRows"/> shape with
    /// <see cref="SqlValueCursor"/>.
    /// </summary>
    private sealed class ValueArrayCursor(SqlType[] schema, IEnumerator<SqlValue[]> source) : RowCursor
    {
        private readonly bool[]? narrowing = RowEncoder.NarrowingColumns(schema);
        private SqlValue[]? buffered;
        private SqlValue[]? current;
        private bool peeked;
        private bool sourceDone;
        private bool everHadRows;

        public override int FieldCount => schema.Length;

        public override bool HasRows
        {
            get
            {
                if (everHadRows)
                    return true;
                if (peeked || sourceDone)
                    return false;
                peeked = true;
                if (source.MoveNext())
                {
                    buffered = source.Current;
                    everHadRows = true;
                    return true;
                }
                sourceDone = true;
                return false;
            }
        }

        public override bool MoveNext()
        {
            if (buffered is not null)
            {
                current = buffered;
                buffered = null;
                return true;
            }
            if (sourceDone)
            {
                current = null;
                return false;
            }
            peeked = true;
            if (source.MoveNext())
            {
                current = source.Current;
                everHadRows = true;
                return true;
            }
            sourceDone = true;
            current = null;
            return false;
        }

        public override SqlValue this[int ordinal] => current is null
            ? throw new InvalidOperationException("No current row.")
            : narrowing is null || !narrowing[ordinal]
                ? current[ordinal]
                : RowEncoder.StorageForm(current[ordinal], schema[ordinal]);

        protected override void DisposeCore() => source.Dispose();
    }

    /// <summary>
    /// Cursor over a result-set's row bytes. Owns a single-row lookahead so
    /// <see cref="HasRows"/> can peek the source without disturbing the
    /// row stream the consumer reads via <see cref="MoveNext"/>.
    /// </summary>
    /// <remarks>
    /// State (after each public call): <c>peeked</c> records whether the
    /// source has been advanced once for HasRows; <c>buffered</c> holds
    /// that peeked row until MoveNext serves it; <c>current</c> holds the
    /// row last served to the consumer (decoded by the indexer);
    /// <c>everHadRows</c> is the sticky bit HasRows reads after consumption.
    /// SqlClient's HasRows uses a TDS-token peek; this cursor's source has
    /// no token discriminator, so peek-and-buffer is the natural analog.
    /// </remarks>
    private sealed class SqlValueCursor(SqlType[] schema, IEnumerator<byte[]> source) : RowCursor
    {
        private readonly HeapColumn[] columns = RowDecoder.ColumnsFor(schema);
        private byte[]? buffered;
        private byte[]? current;
        private bool peeked;
        private bool sourceDone;
        private bool everHadRows;

        public override int FieldCount => schema.Length;

        public override bool HasRows
        {
            get
            {
                if (everHadRows)
                    return true;
                if (peeked || sourceDone)
                    return false;
                peeked = true;
                if (source.MoveNext())
                {
                    buffered = source.Current;
                    everHadRows = true;
                    return true;
                }
                sourceDone = true;
                return false;
            }
        }

        public override bool MoveNext()
        {
            if (buffered is not null)
            {
                current = buffered;
                buffered = null;
                return true;
            }
            if (sourceDone)
            {
                current = null;
                return false;
            }
            peeked = true;
            if (source.MoveNext())
            {
                current = source.Current;
                everHadRows = true;
                return true;
            }
            sourceDone = true;
            current = null;
            return false;
        }

        public override SqlValue this[int ordinal] => current is null
            ? throw new InvalidOperationException("No current row.")
            : RowDecoder.DecodeColumn(columns, current, ordinal);

        protected override void DisposeCore() => source.Dispose();
    }
}
