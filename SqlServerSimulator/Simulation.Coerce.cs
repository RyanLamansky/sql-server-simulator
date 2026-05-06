using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Coerces an auto-generated identity <see cref="long"/> to the column's
    /// declared integer type, raising the IDENTITY-specific Msg 8115 if the
    /// next value won't fit.
    /// </summary>
    private static SqlValue CoerceForIdentity(long value, HeapColumn identityColumn)
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
    /// <see cref="IsVerboseTruncationActive"/>.
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
    private static void EnforceMaxLength(SqlValue source, HeapColumn column, string tableName, Simulation simulation)
    {
        if (source.IsNull || column.MaxLength is not int max || max == SqlType.MaxLengthSentinel)
            return;

        int actual;
        if (column.Type == SqlType.Varbinary || column.Type is BinarySqlType)
        {
            if (source.Type is not (VarbinarySqlType or BinarySqlType))
                return;
            actual = source.AsBytes.Length;
        }
        else if (column.Type == SqlType.Varchar || column.Type is CharSqlType)
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            actual = SqlType.Varchar.GetVariableByteCount(SqlValue.FromVarchar(source.AsString));
        }
        else
        {
            if (source.Type.Category != SqlTypeCategory.String)
                return;
            actual = source.AsString.Length;
        }

        if (actual <= max)
            return;

        if (!simulation.IsVerboseTruncationActive())
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy();

        throw column.Type == SqlType.Varbinary || column.Type is BinarySqlType
            ? SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, source.AsBytes, max)
            : SimulatedSqlException.StringOrBinaryWouldBeTruncated(tableName, column.Name, source.AsString, max);
    }

    /// <summary>
    /// Coerces an INSERT source value to the destination column's type,
    /// converting any overflow into the SQL Server-shaped Msg 8115. Truncation
    /// of strings/bytes is handled separately by <see cref="EnforceMaxLength"/>
    /// before this method runs.
    /// </summary>
    private static SqlValue CoerceForInsert(SqlValue source, SqlType targetType)
    {
        try
        {
            return source.CoerceTo(targetType);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(targetType.ToString()!);
        }
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
    private static void EvaluateComputedColumns(HeapTable destinationTable, SqlValue[] rowValues)
    {
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var column = destinationTable.Columns[i];
            if (column.Computed is null)
                continue;

            SqlValue ResolveByName(List<string> reference)
            {
                var leaf = reference[^1];
                for (var k = 0; k < destinationTable.Columns.Length; k++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[k].Name, leaf))
                        return rowValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(reference);
            }

            rowValues[i] = CoerceForInsert(column.Computed.Run(ResolveByName), column.Type);
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
    private static SqlValue[] ProjectStoredValues(HeapTable destinationTable, SqlValue[] rowValues)
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
}
