using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Tabular query result over one of two row representations: encoded
/// <c>byte[]</c> rows in the page-row format (the niche producers — set ops,
/// TVFs, OPENJSON, views, …), or already-projected <see cref="SqlValue"/>[]
/// rows (the FROM-bearing SELECT projection path). The <c>SqlValue[]</c> form
/// lets the reader's cursor serve cells directly, skipping the
/// encode-then-re-decode round-trip a projected SELECT would otherwise pay
/// (the projection computes the values, and the cursor would decode them right
/// back). <see cref="RowBytes"/> stays available for the byte-consuming paths
/// (INSERT…SELECT, SELECT INTO, set-op operands, subqueries); it encodes the
/// <see cref="SqlValue"/>[] form lazily, so those callers pay exactly what they
/// did before and the reader path pays neither encode nor re-decode.
/// </summary>
internal sealed class SimulatedSqlResultSet : SimulatedQueryResult
{
    private readonly SqlType[] schema;
    private readonly string[] columnNames;
    private readonly IEnumerable<byte[]>? rowBytes;
    private readonly IEnumerable<SqlValue[]>? rowValues;

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

    public override RowCursor CreateCursor() => this.rowValues is { } values
        ? new ValueArrayCursor(this.schema.Length, values.GetEnumerator())
        : new SqlValueCursor(this.schema, this.rowBytes!.GetEnumerator());

    /// <summary>
    /// Cursor over already-projected <see cref="SqlValue"/>[] rows — the
    /// indexer returns the stored value directly, no per-cell decode. Shares
    /// the peek-and-buffer <see cref="HasRows"/> shape with
    /// <see cref="SqlValueCursor"/>.
    /// </summary>
    private sealed class ValueArrayCursor(int fieldCount, IEnumerator<SqlValue[]> source) : RowCursor
    {
        private SqlValue[]? buffered;
        private SqlValue[]? current;
        private bool peeked;
        private bool sourceDone;
        private bool everHadRows;

        public override int FieldCount => fieldCount;

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
            : current[ordinal];

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
