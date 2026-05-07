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

    public SqlType[] Schema => schema;

    public IEnumerable<byte[]> RowBytes => rowBytes;

    public override RowCursor CreateCursor() => new SqlValueCursor(schema, rowBytes.GetEnumerator());

    private sealed class SqlValueCursor(SqlType[] schema, IEnumerator<byte[]> source) : RowCursor
    {
        public override int FieldCount => schema.Length;

        public override bool MoveNext() => source.MoveNext();

        public override SqlValue this[int ordinal] => RowDecoder.DecodeColumn(schema, source.Current, ordinal);

        protected override void DisposeCore() => source.Dispose();
    }
}
