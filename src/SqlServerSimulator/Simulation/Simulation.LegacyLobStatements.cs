using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// The legacy text-pointer statements — <c>READTEXT</c>, <c>WRITETEXT</c> and
/// <c>UPDATETEXT</c> — over a <c>text</c> / <c>ntext</c> / <c>image</c> column,
/// addressed by the pointer <c>TEXTPTR</c> hands out (see
/// <see cref="LegacyTextPointer"/> for the encoding and how a row is found from
/// it). Grammar, units and every diagnostic are probe-confirmed against SQL
/// Server 2025.
/// </summary>
/// <remarks>
/// Offsets and sizes count <b>bytes</b> for <c>text</c> and <c>image</c> and
/// <b>characters</b> for <c>ntext</c>. The three statements are not DML as far
/// as the rest of the engine is concerned: no trigger fires (probe-confirmed
/// against an AFTER UPDATE trigger, which stays silent for both writing forms),
/// no <c>rowversion</c> column advances, <c>WRITETEXT</c> reports
/// <c>@@ROWCOUNT</c> 0 and <c>UPDATETEXT</c> reports 1, and <c>READTEXT</c>
/// returns one row of one column named after the column it read.
/// </remarks>
partial class Simulation
{
    /// <summary>
    /// <c>READTEXT table.column text_ptr offset size [HOLDLOCK]</c>. A size of
    /// <c>0</c> reads to the end of the value; a window running past it is
    /// Msg 7124 naming the value's own length. The result carries the column's
    /// own type, so the session's <c>SET TEXTSIZE</c> caps it at the client
    /// boundary the way it caps any other LOB read.
    /// </summary>
    private static SimulatedSqlResultSet? ParseReadTextStatement(BatchContext batch)
    {
        var context = batch.Parser;
        context.MoveNextRequired(); // consume READTEXT
        var target = ParseLegacyLobTarget(batch);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobOperand(context);
        context.MoveNextRequired();
        var offsetExpression = ParseLegacyLobCount(context);
        context.MoveNextRequired();
        var sizeExpression = ParseLegacyLobCount(context);
        // HOLDLOCK asks for the SERIALIZABLE read the simulator's table-level
        // lock already gives a read inside a transaction, so it parses and
        // carries no further effect.
        var afterSize = context.SaveCheckpoint();
        if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.HoldLock })
            context.RestoreCheckpoint(afterSize);
        if (batch.IsSkipping)
            return null;

        var runtime = new RuntimeContext(NoColumnResolver, batch);
        var (table, columnIndex) = ResolveLegacyLobColumn(batch, target);
        var address = ResolveTextPointerRow(table, columnIndex, pointer.Run(runtime), "READ TEXT", state: 1);
        var current = ReadLobCell(table, columnIndex, address);
        var offset = ReadTextOffset(offsetExpression, runtime);
        var size = ReadTextSize(sizeExpression, runtime);
        var length = LegacyLobLength(current);
        if (offset > length || (size > 0 && offset + size > length))
            throw SimulatedSqlException.ReadTextWindowPastData(length);

        var column = table.Columns[columnIndex];
        var slice = SliceLobValue(column.Type, current, (int)offset, size == 0 ? length - (int)offset : (int)size);
        return new SimulatedSqlResultSet([column.Type], [column.Name], [[slice]]);
    }

    /// <summary>
    /// <c>WRITETEXT table.column text_ptr [WITH LOG] value</c> — a whole-value
    /// replacement. A NULL value sets the cell NULL. Real's <c>BULK</c> form is
    /// a bulk-copy stream rather than a statement (it answers Msg 185 to a
    /// normal client), so it isn't modeled.
    /// </summary>
    private static SimulatedStatementOutcome ParseWriteTextStatement(ParserContext context)
    {
        var batch = context.Batch;
        context.MoveNextRequired(); // consume WRITETEXT
        RejectLegacyLobBulk(context, "WRITETEXT");
        var target = ParseLegacyLobTarget(batch);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobOperand(context);
        context.MoveNextRequired();
        ConsumeOptionalWithLog(context);
        var value = ParseLegacyLobOperand(context);
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var runtime = new RuntimeContext(NoColumnResolver, batch);
        var (table, columnIndex) = ResolveLegacyLobColumn(batch, target);
        var address = ResolveTextPointerRow(table, columnIndex, pointer.Run(runtime), "WRITE TEXT", state: 2);
        var written = value.Run(runtime);
        WriteLobCell(batch, table, columnIndex, address, written.IsNull
            ? SqlValue.Null(table.Columns[columnIndex].Type)
            : written.CoerceTo(table.Columns[columnIndex].Type));
        return new SimulatedNonQuery(0);
    }

    /// <summary>
    /// <c>UPDATETEXT table.column text_ptr { NULL | insert_offset }
    /// { NULL | delete_length } [WITH LOG] [ value | table.column text_ptr ]</c>
    /// — a splice. A NULL or negative insert offset appends and a NULL or
    /// negative delete length runs to the end, both probe-confirmed; an offset
    /// past the value is Msg 7116 and a deletion running past it is Msg 7135.
    /// </summary>
    private static SimulatedStatementOutcome ParseUpdateTextStatement(ParserContext context)
    {
        var batch = context.Batch;
        context.MoveNextRequired(); // consume UPDATETEXT
        RejectLegacyLobBulk(context, "UPDATETEXT");
        var target = ParseLegacyLobTarget(batch);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobOperand(context);
        context.MoveNextRequired();
        var offsetExpression = ParseLegacyLobNullableCount(context);
        context.MoveNextRequired();
        var deleteExpression = ParseLegacyLobNullableCount(context);

        // The tail is optional in three ways: WITH LOG may precede it, the
        // inserted data may be a literal / variable or absent (a pure
        // deletion), and the copy form names a second LOB column with its own
        // pointer — the only shape that starts with a name.
        MultiPartName? sourceTarget = null;
        Expression? sourcePointer = null;
        Expression? inserted = null;
        var afterDeleteLength = context.SaveCheckpoint();
        if (context.MoveNext())
        {
            ConsumeOptionalWithLog(context);
            switch (context.Token)
            {
                case Name:
                    sourceTarget = ParseLegacyLobTarget(batch);
                    context.MoveNextRequired();
                    sourcePointer = ParseLegacyLobOperand(context);
                    break;
                case Literal or Numeric or AtPrefixedString or ReservedKeyword { Keyword: Keyword.Null }:
                    inserted = ParseLegacyLobOperand(context);
                    break;
                default:
                    context.RestoreCheckpoint(afterDeleteLength);
                    break;
            }
        }
        else
        {
            context.RestoreCheckpoint(afterDeleteLength);
        }

        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var runtime = new RuntimeContext(NoColumnResolver, batch);
        var (table, columnIndex) = ResolveLegacyLobColumn(batch, target);
        var column = table.Columns[columnIndex];
        var address = ResolveTextPointerRow(table, columnIndex, pointer.Run(runtime), "UPDATE TEXT", state: 2);
        var current = ReadLobCell(table, columnIndex, address);
        var length = LegacyLobLength(current);

        var offsetValue = offsetExpression is null ? null : LegacyLobNullableCount(offsetExpression, runtime);
        var deleteValue = deleteExpression is null ? null : LegacyLobNullableCount(deleteExpression, runtime);
        var offset = offsetValue is not { } o || o < 0 ? length : (int)Math.Min(o, int.MaxValue);
        if (offset > length)
            throw SimulatedSqlException.LobOffsetOutOfRange(offsetValue!.Value, state: 4);
        var deleteLength = deleteValue is not { } d || d < 0 ? length - offset : (int)Math.Min(d, int.MaxValue);
        if (offset + (long)deleteLength > length)
            throw SimulatedSqlException.DeletionLengthOutOfRange(deleteValue!.Value);

        SqlValue insertedValue;
        if (sourceTarget is { } source)
        {
            var (sourceTable, sourceColumnIndex) = ResolveLegacyLobColumn(batch, source);
            var sourceType = sourceTable.Columns[sourceColumnIndex].Type;
            if (sourceType != column.Type)
                throw SimulatedSqlException.CannotConvertDataType(sourceType.SqlServerName, column.Type.SqlServerName);
            var sourceAddress = ResolveTextPointerRow(sourceTable, sourceColumnIndex, sourcePointer!.Run(runtime), "UPDATE TEXT", state: 2);
            insertedValue = ReadLobCell(sourceTable, sourceColumnIndex, sourceAddress);
        }
        else
        {
            var written = inserted?.Run(runtime);
            insertedValue = written is null || written.Value.IsNull
                ? SqlValue.Null(column.Type)
                : written.Value.CoerceTo(column.Type);
        }

        WriteLobCell(batch, table, columnIndex, address, SpliceLobValue(column.Type, current, offset, deleteLength, insertedValue));
        return new SimulatedNonQuery(1);
    }

    /// <summary>
    /// Parses the statement's <c>[db.][schema.]table.column</c> operand. A
    /// single-part name is Msg 182 — the utility needs both halves.
    /// </summary>
    private static MultiPartName ParseLegacyLobTarget(BatchContext batch)
    {
        var name = BatchContext.ParseObjectName(batch.Parser);
        return name.Count < 2 ? throw SimulatedSqlException.TableAndColumnNamesRequiredForTextUtility() : name;
    }

    /// <summary>
    /// Resolves a <c>table.column</c> operand to its table and the column's
    /// index in <see cref="HeapTable.Columns"/>. A missing table is Msg 208, a
    /// missing column Msg 207, and a column no text pointer can address —
    /// anything but <c>text</c> / <c>ntext</c> / <c>image</c> — is Msg 7125.
    /// </summary>
    private static (HeapTable Table, int ColumnIndex) ResolveLegacyLobColumn(BatchContext batch, MultiPartName target)
    {
        var tableName = new MultiPartName(target[0]);
        for (var i = 1; i < target.Count - 1; i++)
            tableName = tableName.WithAddedPart(target[i]);
        if (!batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.InvalidObjectName(tableName);

        var collation = batch.DatabaseFor(table).Collation;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            if (!collation.Equals(table.Columns[i].Name, target.Leaf))
                continue;
            return table.Columns[i].Type is TextSqlType or NTextSqlType or ImageSqlType
                ? (table, i)
                : throw SimulatedSqlException.TextPointerConflictsWithColumnName();
        }

        throw SimulatedSqlException.InvalidColumnName(target.Leaf);
    }

    /// <summary>
    /// The row a pointer addresses. A NULL pointer is Msg 7133 naming the
    /// utility, a pointer narrower than <c>binary(16)</c> is Msg 7122, and
    /// bytes that carry no simulator signature, name another column, or name a
    /// value no live row holds are Msg 7123 rendering the pointer as real
    /// renders it.
    /// </summary>
    private static (int PageIndex, int SlotIndex) ResolveTextPointerRow(HeapTable table, int columnIndex, SqlValue pointerValue, string utility, byte state)
    {
        if (pointerValue.IsNull)
            throw SimulatedSqlException.NullTextPointer(utility, state);
        if (pointerValue.Type.ClrType != typeof(byte[]))
            throw SimulatedSqlException.InvalidTextPointerType();
        var pointer = pointerValue.AsBytes;
        if (pointer.Length < LegacyTextPointer.Width)
            throw SimulatedSqlException.InvalidTextPointerType();

        var hex = $"0x{Convert.ToHexString(pointer.AsSpan(0, LegacyTextPointer.Width))}";
        if (!LegacyTextPointer.TryRead(pointer, out var columnHash, out var valueHash)
            || columnHash != LegacyTextPointer.ColumnHash(table.Columns[columnIndex].Name))
        {
            throw SimulatedSqlException.InvalidTextPointerValue(hex);
        }

        var key = (columnHash, valueHash);
        if (table.TextPointerRows is { } rows
            && rows.TryGetValue(key, out var cached)
            && table.Heap.ReadSlotBytes(cached.PageIndex, cached.SlotIndex) is not null)
        {
            return cached;
        }

        var storedOrdinal = table.StorageOrdinals[columnIndex];
        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var cell = RowDecoder.DecodeColumn(table.StoredColumns, rowBytes, storedOrdinal, table.Heap);
            if (cell.IsNull || LegacyTextPointer.ValueHash(cell) != valueHash)
                continue;
            table.RememberTextPointerRow(key, (pageIndex, slotIndex));
            return (pageIndex, slotIndex);
        }

        throw SimulatedSqlException.InvalidTextPointerValue(hex);
    }

    private static SqlValue ReadLobCell(HeapTable table, int columnIndex, (int PageIndex, int SlotIndex) address)
    {
        var bytes = table.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex)
            ?? throw SimulatedSqlException.InvalidTextPointerValue("0x");
        return RowDecoder.DecodeColumn(table.StoredColumns, bytes, table.StorageOrdinals[columnIndex], table.Heap);
    }

    /// <summary>
    /// Rewrites one LOB cell in place, through the heap's ordinary update path
    /// so the write rolls back with its transaction and a snapshot reader still
    /// sees the pre-write version. No trigger runs and no other column moves —
    /// which is what makes these statements invisible to a <c>rowversion</c>
    /// column, as on real.
    /// </summary>
    private static void WriteLobCell(BatchContext batch, HeapTable table, int columnIndex, (int PageIndex, int SlotIndex) address, SqlValue newValue)
    {
        table.OwningDatabase?.RejectWriteWhenReadOnly();
        var oldBytes = table.Heap.ReadSlotBytes(address.PageIndex, address.SlotIndex)
            ?? throw SimulatedSqlException.InvalidTextPointerValue("0x");
        var values = RowDecoder.DecodeRow(table.StoredColumns, oldBytes, table.Heap);
        values[table.StorageOrdinals[columnIndex]] = newValue;
        var lockable = IsLockableTable(table);
        if (lockable)
            batch.AcquireRowLockTxScoped(table, address.PageIndex, address.SlotIndex, LockMode.Exclusive);
        var undoLog = table.IsTableVariable ? batch.CurrentTableVarUndoLog : batch.CurrentUndoLog;
        table.Heap.UpdateAt(address.PageIndex, address.SlotIndex, RowEncoder.EncodeRow(table.StoredColumns, values, table.Heap), undoLog);
        if (lockable && VersionStore.IsVersioningEnabled(batch.DatabaseFor(table)))
            VersionStore.CaptureWrite(batch, table, address, address, oldBytes, VersionWriteKind.Update);
    }

    /// <summary>
    /// Value length in the statement's own unit: characters for the two
    /// character LOBs (<c>text</c>'s single-byte code page makes its byte
    /// offsets and its character positions the same number) and bytes for
    /// <c>image</c>. A NULL cell has length 0.
    /// </summary>
    private static int LegacyLobLength(SqlValue value) =>
        value.IsNull ? 0
        : SqlType.IsStringCategory(value.Type) ? value.AsString.Length
        : value.AsBytes.Length;

    private static SqlValue SliceLobValue(SqlType columnType, SqlValue value, int offset, int length)
    {
        if (columnType is ImageSqlType)
            return SqlValue.FromImage(value.IsNull ? [] : value.AsBytes.AsSpan(offset, length).ToArray());
        var text = value.IsNull ? string.Empty : value.AsString.Substring(offset, length);
        return columnType is NTextSqlType ? SqlValue.FromNText(text) : SqlValue.FromText(text);
    }

    private static SqlValue SpliceLobValue(SqlType columnType, SqlValue current, int offset, int deleteLength, SqlValue inserted)
    {
        if (columnType is ImageSqlType)
        {
            var bytes = current.IsNull ? [] : current.AsBytes;
            var insertedBytes = inserted.IsNull ? [] : inserted.AsBytes;
            var result = new byte[bytes.Length - deleteLength + insertedBytes.Length];
            bytes.AsSpan(0, offset).CopyTo(result);
            insertedBytes.CopyTo(result.AsSpan(offset));
            bytes.AsSpan(offset + deleteLength).CopyTo(result.AsSpan(offset + insertedBytes.Length));
            return SqlValue.FromImage(result);
        }

        var text = current.IsNull ? string.Empty : current.AsString;
        var spliced = string.Concat(text.AsSpan(0, offset), inserted.IsNull ? string.Empty : inserted.AsString, text.AsSpan(offset + deleteLength));
        return columnType is NTextSqlType ? SqlValue.FromNText(spliced) : SqlValue.FromText(spliced);
    }

    /// <summary>
    /// One operand of the statement grammar: a literal, a variable, or the
    /// <c>NULL</c> keyword. Real accepts nothing composite here — <c>'a' + 'b'</c>
    /// is Msg 102 at the operator — which parsing a lone primary reproduces,
    /// since the operator is then the statement's own unexpected token.
    /// </summary>
    private static Expression ParseLegacyLobOperand(ParserContext context)
    {
        if (context.Token is not (Literal or Numeric or AtPrefixedString or ReservedKeyword { Keyword: Keyword.Null }))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // ParsePrimary stops one token past its operand; every operand here is
        // a single token, so restoring puts the cursor back on it and the
        // statement keeps the parser-wide "Token is the last consumed one"
        // contract its own callers and the dispatch loop rely on.
        var checkpoint = context.SaveCheckpoint();
        var expression = Expression.ParsePrimary(context);
        context.RestoreCheckpoint(checkpoint);
        return expression;
    }

    /// <summary>
    /// <c>READTEXT</c>'s offset and size. Real's grammar takes an unsigned
    /// integer or a variable, so a leading sign is Msg 102 at the sign.
    /// </summary>
    private static Expression ParseLegacyLobCount(ParserContext context)
    {
        if (context.Token is Operator { Character: '-' or '+' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return ParseLegacyLobOperand(context);
    }

    /// <summary>
    /// <c>UPDATETEXT</c>'s insert offset and delete length, which real writes as
    /// <c>{ NULL | value }</c> and reads a negative value the same way it reads
    /// NULL (probe-confirmed).
    /// </summary>
    private static Expression? ParseLegacyLobNullableCount(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.Null })
            return null;
        if (context.Token is Operator { Character: '-' })
        {
            // A sign is legal here and reads the same as NULL, so the operand
            // parse never sees it.
            context.MoveNextRequired();
            _ = ParseLegacyLobOperand(context);
            return null;
        }

        return ParseLegacyLobOperand(context);
    }

    /// <summary>
    /// <c>READTEXT</c>'s offset: a NULL reads from the start, and a negative one
    /// — which only a variable can carry, the grammar refusing a written sign —
    /// is Msg 7116 at real's own state 3.
    /// </summary>
    private static long ReadTextOffset(Expression expression, RuntimeContext runtime)
    {
        var value = LegacyLobNullableCount(expression, runtime) ?? 0;
        return value >= 0 ? value : throw SimulatedSqlException.LobOffsetOutOfRange(value, state: 3);
    }

    /// <summary>
    /// <c>READTEXT</c>'s size, where NULL and a negative value both read to the
    /// end of the value exactly as 0 does (probe-confirmed).
    /// </summary>
    private static long ReadTextSize(Expression expression, RuntimeContext runtime) =>
        Math.Max(0, LegacyLobNullableCount(expression, runtime) ?? 0);

    private static long? LegacyLobNullableCount(Expression expression, RuntimeContext runtime)
    {
        var value = expression.Run(runtime);
        return value.IsNull ? null : value.CoerceTo(SqlType.BigInt).AsInt64;
    }

    private static void ConsumeOptionalWithLog(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return;
        // The simulator has no recovery log to opt into, so WITH LOG parses and
        // carries no further effect — the write is logged for rollback either
        // way through the undo log.
        context.MoveNextRequired();
        if (context.Token is not UnquotedString log || !log.Span.Equals("LOG", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
    }

    /// <summary>
    /// The runtime value of a count operand whose grammar forbids a sign, so
    /// only a variable can turn it negative.
    /// </summary>

    private static void RejectLegacyLobBulk(ParserContext context, string statement)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.Bulk })
            throw new NotSupportedException($"{statement} BULK isn't modeled (it is a bulk-copy data stream rather than a statement).");
    }
}
