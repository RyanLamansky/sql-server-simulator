using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

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

        if (!connection.IsVerboseTruncationActive())
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy();

        throw column.Type is VarbinarySqlType or BinarySqlType
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
    private static void EvaluateComputedColumns(HeapTable destinationTable, SqlValue[] rowValues, BatchContext batch)
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
                    if (Collation.Default.Equals(destinationTable.Columns[k].Name, reference.Leaf))
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
                throw SimulatedSqlException.CannotInsertNull(column.Name, destinationTable.Name, verb);
        }
    }

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
        if (destinationTable.CheckConstraints.Length == 0)
            return;

        SqlValue ResolveByName(MultiPartName reference)
        {
            for (var k = 0; k < destinationTable.Columns.Length; k++)
            {
                if (Collation.Default.Equals(destinationTable.Columns[k].Name, reference.Leaf))
                    return rowValues[k];
            }
            throw SimulatedSqlException.InvalidColumnName(reference);
        }

        var runtime = new RuntimeContext(ResolveByName, batch);
        foreach (var check in destinationTable.CheckConstraints)
        {
            if (check.Predicate.Run(runtime) == false)
                throw SimulatedSqlException.CheckConstraintViolation(check.Name, destinationTable.Name, check.InlineColumn, verb);
        }
    }

    /// <summary>
    /// Linear-scans the table's heap for a row whose key tuple equals the new
    /// row's, raising Msg 2627 with the offending constraint's name on the
    /// first match. Skips when the table has no PK/UNIQUE constraints.
    /// SqlValue equality already handles SQL Server's NULLs-equal-for-UNIQUE
    /// rule (two NULLs collide; one NULL and one non-NULL don't), so the loop
    /// just delegates per column. Comparison is collation-aware for string
    /// columns thanks to <see cref="SqlValue.Equals(SqlValue)"/>'s ANSI-padded
    /// path. <paramref name="storedRowValues"/> is the row's values in
    /// storage-ordinal order (the output of <see cref="ProjectStoredValues"/>);
    /// existing rows are decoded one key column at a time so we don't pay the
    /// cost of materializing whole rows for tables that have just a small
    /// composite key.
    /// </summary>
    private static void EnforceKeyConstraints(HeapTable destinationTable, SqlValue[] storedRowValues)
    {
        if (destinationTable.KeyConstraints.Length == 0)
            return;

        var storedColumns = destinationTable.StoredColumns;
        var lobStore = destinationTable.Heap;

        foreach (var rowBytes in destinationTable.Heap.EnumerateRows())
        {
            foreach (var constraint in destinationTable.KeyConstraints)
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
                    var sb = new StringBuilder();
                    for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
                    {
                        if (i > 0)
                            _ = sb.Append(", ");
                        _ = sb.Append(FormatKeyValue(storedRowValues[constraint.StorageOrdinals[i]]));
                    }
                    throw SimulatedSqlException.ViolationOfKeyConstraint(constraint.ViolationKindWord, constraint.Name, destinationTable.Name, sb.ToString());
                }
            }
        }
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
            _ when value.Type == SqlType.Float => value.AsDouble.ToString("G15", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.Real => value.AsSingle.ToString("G7", CultureInfo.InvariantCulture),
            _ when value.Type == SqlType.Money || value.Type == SqlType.SmallMoney => value.AsMoney.ToString("F4", CultureInfo.InvariantCulture),
            VarbinarySqlType or BinarySqlType => $"0x{Convert.ToHexString(value.AsBytes)}",
            DecimalSqlType d => value.AsDecimal.ToString($"F{d.scale}", CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"No key-violation rendering for {value.Type}."),
        };
}
