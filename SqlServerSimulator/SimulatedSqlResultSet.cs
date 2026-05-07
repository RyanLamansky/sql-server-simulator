using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// Tabular query result whose rows are encoded byte arrays in the page-row
/// format. The reader navigates the row's bytes directly via
/// <see cref="RowDecoder"/> on each accessor call, never
/// rehydrating the row into <see cref="SqlValue"/>[].
/// </summary>
internal sealed class SimulatedSqlResultSet(SqlType[] schema, string[] columnNames, IEnumerable<byte[]> rowBytes) : SimulatedQueryResult
{
    public override string[] ColumnNames => columnNames;

    public override SqlType[] Schema => schema;

    public IEnumerable<byte[]> RowBytes => rowBytes;

    public override RowCursor CreateCursor() => new SqlValueCursor(schema, rowBytes.GetEnumerator());

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
            : RowDecoder.DecodeColumn(schema, current, ordinal);

        protected override void DisposeCore() => source.Dispose();
    }
}
