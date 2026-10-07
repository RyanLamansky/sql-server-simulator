using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The legacy text-pointer statements — READTEXT, WRITETEXT and UPDATETEXT —
// over a text / ntext / image column, addressed by the pointer TEXTPTR hands
// out (see LegacyTextPointer for the encoding and how a row is found from it).
// Grammar, units and every diagnostic are probe-confirmed against SQL Server
// 2025.
//
// Offsets and sizes count bytes for text and image and characters for ntext.
// The three statements are not DML as far as the rest of the engine is
// concerned: no trigger fires (probe-confirmed against an AFTER UPDATE
// trigger, which stays silent for both writing forms), no rowversion column
// advances, WRITETEXT and UPDATETEXT report @@ROWCOUNT 0, and
// READTEXT returns one row of one column named after the column it read.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
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
        var pointer = ParseLegacyLobPointer(batch, target, write: false);
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
        // A cell a write set NULL still has a pointer, and reads back as NULL.
        var slice = current.IsNull
            ? SqlValue.Null(column.Type)
            : SliceLobValue(column.Type, current, (int)offset, size == 0 ? length - (int)offset : (int)size);
        return new SimulatedSqlResultSet([column.Type], [column.Name], [[slice]]);
    }

    /// <summary>
    /// <c>WRITETEXT [BULK] table.column text_ptr [TIMESTAMP = 0x…] [WITH LOG]
    /// value</c> — a whole-value replacement. A NULL value sets the cell NULL.
    /// The <c>TIMESTAMP</c> clause takes a binary literal of any length and
    /// is otherwise ignored, as real ignores it (probed 2026-10-07 against
    /// SQL Server 2025). The <c>BULK</c> form takes no value: it suspends the
    /// batch for the data the session's next message brings (see
    /// <see cref="SimulatedBulkTextRequest"/>), then runs again to write it.
    /// </summary>
    private static SimulatedStatementOutcome ParseWriteTextStatement(ParserContext context)
    {
        var batch = context.Batch;
        context.MoveNextRequired(); // consume WRITETEXT
        var bulk = ConsumeOptionalBulk(context);
        var target = ParseLegacyLobTarget(batch);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobPointer(batch, target, write: true);
        Expression? value = null;
        if (bulk)
        {
            ParseBulkTextTail(batch, timestampAllowed: true);
        }
        else
        {
            context.MoveNextRequired();
            if (ConsumeOptionalTimestamp(context))
                context.MoveNextRequired();
            ConsumeOptionalWithLog(context);
            value = ParseLegacyLobOperand(context);
        }
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var reply = bulk ? TakeBulkTextReply(batch) : null;
        var runtime = new RuntimeContext(NoColumnResolver, batch);
        try
        {
            var (table, columnIndex) = ResolveLegacyLobColumn(batch, target);
            var columnType = table.Columns[columnIndex].Type;
            var address = ResolveWrittenTextPointerRow(batch, table, columnIndex, pointer.Run(runtime), "WRITE TEXT");
            SqlValue written;
            if (!bulk)
            {
                var given = value!.Run(runtime);
                written = given.IsNull ? SqlValue.Null(columnType) : given.CoerceTo(columnType);
            }
            else if (reply is null)
            {
                return AwaitBulkText(batch, "WRITETEXT");
            }
            else
            {
                written = BulkTextValue(columnType, reply);
            }
            WriteLobCell(batch, table, columnIndex, address, written);
        }
        catch (SimulatedSqlException ex) when (TextWriteFailure(ex) is { } failure)
        {
            throw failure;
        }
        return new SimulatedNonQuery(0);
    }

    /// <summary>
    /// <c>UPDATETEXT [BULK] table.column text_ptr { NULL | insert_offset }
    /// { NULL | delete_length } [WITH LOG] [ value | table.column text_ptr ]</c>
    /// — a splice. A NULL or negative insert offset appends and a NULL or
    /// negative delete length runs to the end, both probe-confirmed; an offset
    /// past the value is Msg 7116 and a deletion running past it is Msg 7135.
    /// The <c>BULK</c> form inserts the data the session's next message
    /// brings, as <c>WRITETEXT BULK</c> takes its own, and judges the offset
    /// and length only once it has them.
    /// </summary>
    private static SimulatedStatementOutcome ParseUpdateTextStatement(ParserContext context)
    {
        var batch = context.Batch;
        context.MoveNextRequired(); // consume UPDATETEXT
        var bulk = ConsumeOptionalBulk(context);
        var target = ParseLegacyLobTarget(batch);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobPointer(batch, target, write: true);
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
        if (bulk)
        {
            ParseBulkTextTail(batch, timestampAllowed: false);
        }
        else
        {
            var afterDeleteLength = context.SaveCheckpoint();
            if (context.MoveNext())
            {
                ConsumeOptionalWithLog(context);
                switch (context.Token)
                {
                    case Name:
                        (sourceTarget, sourcePointer) = ParseLegacyLobCopySource(batch);
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
        }

        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var reply = bulk ? TakeBulkTextReply(batch) : null;
        var runtime = new RuntimeContext(NoColumnResolver, batch);
        try
        {
            var (table, columnIndex) = ResolveLegacyLobColumn(batch, target);
            var column = table.Columns[columnIndex];
            var address = ResolveWrittenTextPointerRow(batch, table, columnIndex, pointer.Run(runtime), "UPDATE TEXT");
            if (bulk && reply is null)
                return AwaitBulkText(batch, "UPDATETEXT");
            var bulkValue = reply is null ? (SqlValue?)null : BulkTextValue(column.Type, reply);
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
            if (bulkValue is { } streamed)
            {
                insertedValue = streamed;
            }
            else if (sourceTarget is { } source)
            {
                // The source's pointer is read ahead of its type (probed
                // 2026-10-07 against SQL Server 2025).
                var (sourceTable, sourceColumnIndex) = ResolveLegacyLobColumn(batch, source);
                var sourceAddress = ResolveTextPointerRow(sourceTable, sourceColumnIndex, sourcePointer!.Run(runtime), "UPDATE TEXT", state: 2);
                var sourceType = sourceTable.Columns[sourceColumnIndex].Type;
                if (sourceType != column.Type)
                    throw SimulatedSqlException.CannotConvertDataType(sourceType.SqlServerName, column.Type.SqlServerName);
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
        }
        catch (SimulatedSqlException ex) when (TextWriteFailure(ex) is { } failure)
        {
            throw failure;
        }
        // Real's UPDATETEXT leaves @@ROWCOUNT at 0, as WRITETEXT does (probed
        // 2026-10-07 against SQL Server 2025).
        return new SimulatedNonQuery(0);
    }

    /// <summary>
    /// What an error a <c>WRITETEXT</c> or <c>UPDATETEXT</c> raised running
    /// becomes, where <c>READTEXT</c>'s end only their statement (probed
    /// 2026-10-07 against SQL Server 2025): the pointer's value, an offset, a
    /// length or the copy form's source type abort as under <c>XACT_ABORT</c> — the batch ends and the
    /// transaction rolls back, or a <c>TRY</c> catching it finds the
    /// transaction doomed — and the column a table the batch's compile
    /// couldn't see turns out to have ends the batch. Null for any other.
    /// </summary>
    private static SimulatedSqlException? TextWriteFailure(SimulatedSqlException ex) => ex switch
    {
        { AbortsAsUnderXactAbort: true } or { TerminatesBatch: true } => null,
        { Number: 518 or 7116 or 7123 or 7133 or 7135 } or { Number: 7125, State: 5 } => ex.AbortingAsUnderXactAbort(),
        { Number: 7125 } => ex.EndingBatch(),
        _ => null,
    };

    /// <summary>
    /// The row a <c>WRITETEXT</c> or <c>UPDATETEXT</c> pointer addresses, the
    /// statement counting as writing from here: every error after this is
    /// followed by Msg 3621 (<see cref="StatementContext.WritesText"/>).
    /// </summary>
    private static (int PageIndex, int SlotIndex) ResolveWrittenTextPointerRow(BatchContext batch, HeapTable table, int columnIndex, SqlValue pointerValue, string utility)
    {
        batch.CurrentStatement.WritesText = true;
        return ResolveTextPointerRow(table, columnIndex, pointerValue, utility, state: 2);
    }

    /// <summary>
    /// A statement's pointer operand: a binary literal, a number or a
    /// variable — a character literal or <c>NULL</c> is a syntax error at it —
    /// whose type, and the column's, are judged as the batch compiles (probed
    /// 2026-10-07 against SQL Server 2025): a column no text pointer can
    /// address is Msg 7125, and a pointer that can't hold one is Msg 7122 —
    /// any type but <c>binary</c>, <c>varbinary</c>, <c>char</c> and
    /// <c>varchar</c> of at least 16, a binary literal shorter than 16 bytes
    /// included, save that a write takes an integer, whose value is Msg 7125
    /// as the statement runs. A table the compile can't see yet defers the
    /// column's check to the run.
    /// </summary>
    private static Expression ParseLegacyLobPointer(BatchContext batch, MultiPartName target, bool write)
    {
        var context = batch.Parser;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Null } || (context.Token is Literal literal && literal.Value.Type.ClrType != typeof(byte[])))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var token = context.Token;
        var pointer = ParseLegacyLobOperand(context);

        if (target.Count >= 2)
        {
            var tableName = new MultiPartName(target[0]);
            for (var i = 1; i < target.Count - 1; i++)
                tableName = tableName.WithAddedPart(target[i]);
            if (batch.TryResolveTable(tableName, out var table))
            {
                var collation = batch.DatabaseFor(table).Collation;
                foreach (var column in table.Columns)
                {
                    if (collation.Equals(column.Name, target.Leaf) && column.Type is not (TextSqlType or NTextSqlType or ImageSqlType))
                        throw SimulatedSqlException.TextPointerConflictsWithColumnName();
                }
            }
        }

        var holdsPointer = token switch
        {
            Literal { Value: var bytes } => bytes.AsBytes.Length >= LegacyTextPointer.Width,
            Numeric => write,
            _ => pointer.GetSqlType(batch, NoColumnTypeResolver) switch
            {
                BinarySqlType binary => binary.length >= LegacyTextPointer.Width,
                VarbinarySqlType varbinary => varbinary.length is not SqlType.MaxLengthSentinel and >= LegacyTextPointer.Width,
                CharSqlType character => character.length >= LegacyTextPointer.Width,
                VarcharSqlType varchar => varchar.length is not SqlType.MaxLengthSentinel and >= LegacyTextPointer.Width,
                var type => write && IsIntegerPointer(type),
            },
        };
        return holdsPointer ? pointer : throw SimulatedSqlException.InvalidTextPointerType();
    }

    private static bool IsIntegerPointer(SqlType type) =>
        type.ClrType == typeof(byte) || type.ClrType == typeof(short) || type.ClrType == typeof(int) || type.ClrType == typeof(long);

    /// <summary>
    /// The request a bulk form's first run ends with, once its column and
    /// pointer have checked out. Only a statement the batch's own dispatch
    /// loop runs can suspend the batch; the statements a block, a
    /// <c>TRY</c>, an <c>IF</c>, a <c>WHILE</c> or a module body runs send
    /// their outcomes with the statement enclosing them.
    /// </summary>
    private static SimulatedBulkTextRequest AwaitBulkText(BatchContext batch, string statement)
    {
        if (batch.FramedStatementDepth != 1 || !batch.ContinueOnError || batch.YieldsBetweenStatements
            || batch.ProcFrame is not null || batch.TriggerFrame is not null || batch.UdfFrame is not null)
        {
            throw new NotSupportedException($"{statement} BULK inside a block, TRY, IF, WHILE, procedure, trigger or dynamic SQL, or on a MARS session, isn't modeled.");
        }
        return new SimulatedBulkTextRequest();
    }

    /// <summary>
    /// Takes the data a resumed batch brought its bulk form, which only the
    /// statement's second run finds.
    /// </summary>
    private static SimulatedBulkTextRequest? TakeBulkTextReply(BatchContext batch)
    {
        var reply = batch.BulkTextReply;
        batch.BulkTextReply = null;
        return reply;
    }

    /// <summary>
    /// The value the bulk form's data spell for the column: the bytes in the
    /// column's own encoding — <c>text</c>'s code page, <c>ntext</c>'s UTF-16
    /// with an odd trailing byte read as a character's low byte, <c>image</c>
    /// verbatim (probed 2026-10-07 against SQL Server 2025). No data is
    /// Msg 4022, data cut short Msg 4002.
    /// </summary>
    private static SqlValue BulkTextValue(SqlType columnType, SimulatedBulkTextRequest reply)
    {
        if (reply.StreamEndedEarly)
            throw SimulatedSqlException.BulkTextStreamEndedEarly();
        if (reply.Data is not { } data)
            throw SimulatedSqlException.BulkTextDataNotSent();
        if (columnType is NTextSqlType && data.Length % 2 != 0)
            Array.Resize(ref data, data.Length + 1);
        return columnType.Decode(data);
    }

    private static bool ConsumeOptionalBulk(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Bulk })
            return false;
        context.MoveNextRequired();
        return true;
    }

    /// <summary>
    /// What may follow a bulk form's pointer or delete length: <c>WRITETEXT</c>'s
    /// <c>TIMESTAMP</c> clause and <c>WITH LOG</c>, and nothing more — data
    /// written in the statement, the copy form's source included, is Msg 185
    /// as the batch compiles (probed 2026-10-07 against SQL Server 2025).
    /// Leaves the cursor on the last token the statement owns.
    /// </summary>
    private static void ParseBulkTextTail(BatchContext batch, bool timestampAllowed)
    {
        var context = batch.Parser;
        var owned = context.SaveCheckpoint();
        if (!context.MoveNext())
        {
            context.RestoreCheckpoint(owned);
            return;
        }
        if (timestampAllowed && ConsumeOptionalTimestamp(context))
        {
            owned = context.SaveCheckpoint();
            if (!context.MoveNext())
            {
                context.RestoreCheckpoint(owned);
                return;
            }
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString log || !log.Span.Equals("LOG", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            owned = context.SaveCheckpoint();
            if (!context.MoveNext())
            {
                context.RestoreCheckpoint(owned);
                return;
            }
        }
        switch (context.Token)
        {
            case Literal or Numeric or AtPrefixedString or ReservedKeyword { Keyword: Keyword.Null }:
                throw SimulatedSqlException.BulkTextDataInStatement();
            case Name when !timestampAllowed:
                _ = ParseLegacyLobCopySource(batch);
                throw SimulatedSqlException.BulkTextDataInStatement();
            default:
                context.RestoreCheckpoint(owned);
                break;
        }
    }

    /// <summary>
    /// <c>WRITETEXT</c>'s <c>TIMESTAMP = 0x…</c> clause, when the cursor is on
    /// it: the literal is required, any length, and nothing else — a variable,
    /// a string, a number or <c>NULL</c> is a syntax error at it. Leaves the
    /// cursor on the literal.
    /// </summary>
    private static bool ConsumeOptionalTimestamp(ParserContext context)
    {
        if (context.Token is not UnquotedString word || !word.Span.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            return false;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Literal { Value.Type.ClrType: var clrType } || clrType != typeof(byte[]))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        return true;
    }

    /// <summary>
    /// <c>UPDATETEXT</c>'s copy form, <c>table.column text_ptr</c>: a name
    /// followed by something other than a pointer operand is a syntax error at
    /// that token, which is what a <c>TIMESTAMP</c> clause meets here.
    /// </summary>
    private static (MultiPartName Target, Expression Pointer) ParseLegacyLobCopySource(BatchContext batch)
    {
        var context = batch.Parser;
        var name = BatchContext.ParseObjectName(context);
        context.MoveNextRequired();
        var pointer = ParseLegacyLobPointer(batch, name, write: true);
        return name.Count < 2 ? throw SimulatedSqlException.TableAndColumnNamesRequiredForTextUtility() : (name, pointer);
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
    /// utility, and an integer one Msg 7125 at state 5. Any other is read as
    /// its bytes — a character value's in its code page — cut to the first 16,
    /// and bytes that are fewer, name another table or column, a deleted row,
    /// or a cell with no LOB root are Msg 7123 rendering them as real renders
    /// them (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    private static (int PageIndex, int SlotIndex) ResolveTextPointerRow(HeapTable table, int columnIndex, SqlValue pointerValue, string utility, byte state)
    {
        if (pointerValue.IsNull)
            throw SimulatedSqlException.NullTextPointer(utility, state);
        if (IsIntegerPointer(pointerValue.Type))
            throw SimulatedSqlException.TextPointerConflictsWithColumnName(state: 5);
        var bytes = pointerValue.Type.ClrType == typeof(string) ? CharSqlType.Cp1252Encoder.GetBytes(pointerValue.AsString) : pointerValue.AsBytes;
        var pointer = bytes.AsSpan(0, Math.Min(bytes.Length, LegacyTextPointer.Width));
        return pointer.Length == LegacyTextPointer.Width && LegacyTextPointer.TryResolve(pointer, table, columnIndex, out var address)
            ? address
            : throw SimulatedSqlException.InvalidTextPointerValue($"0x{Convert.ToHexString(pointer)}");
    }

    /// <summary>
    /// The <c>text</c> / <c>ntext</c> / <c>image</c> columns among an
    /// <c>UPDATE</c>'s assigned ones, or null when there are none — the only
    /// columns whose cells <see cref="NoteRootedLobNulls"/> tracks.
    /// </summary>
    private static int[]? LegacyLobColumnsAmong(HeapTable table, IReadOnlyList<int> assigned)
    {
        List<int>? found = null;
        foreach (var k in assigned)
        {
            if (table.Columns[k].Type is TextSqlType or NTextSqlType or ImageSqlType)
                (found ??= []).Add(k);
        }
        return found?.ToArray();
    }

    /// <summary>
    /// Before an <c>UPDATE</c> rewrites the row at the address, records each
    /// of <paramref name="lobColumns"/> it takes from a value to NULL: real
    /// keeps that cell's LOB root, so its text pointer stays (see
    /// <see cref="Heap.RootedNullLobCells"/>).
    /// </summary>
    private static void NoteRootedLobNulls(HeapTable table, int[] lobColumns, int page, int slot, SqlValue[]? oldFull, SqlValue[] newFull)
    {
        foreach (var k in lobColumns)
        {
            if (!newFull[k].IsNull)
                continue;
            var ordinal = table.StorageOrdinals[k];
            var old = oldFull is not null ? oldFull[k]
                : table.Heap.ReadSlotBytes(page, slot) is { } bytes ? RowDecoder.DecodeColumn(table.StoredColumns, bytes, ordinal, table.Heap)
                : SqlValue.Null(table.Columns[k].Type);
            if (!old.IsNull)
                table.Heap.MarkRootedNullLob(page, slot, ordinal);
        }
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
        var storedOrdinal = table.StorageOrdinals[columnIndex];
        // A cell written NULL keeps its LOB root, and with it its pointer.
        if (newValue.IsNull && !values[storedOrdinal].IsNull)
            table.Heap.MarkRootedNullLob(address.PageIndex, address.SlotIndex, storedOrdinal);
        values[storedOrdinal] = newValue;
        var lockable = IsLockableTable(table);
        if (lockable)
            batch.AcquireRowLockTxScoped(table, address.PageIndex, address.SlotIndex, LockMode.Exclusive, RowLockPurpose.UpdatePreImage);
        if (table.ChangeTracking is { } tracking)
        {
            var keyOrdinals = TableChangeTracking.KeyOrdinals(table);
            tracking.Record(batch, table, TableChangeTracking.KeyOf(DecodeFullRow(table, oldBytes), keyOrdinals), ChangeTrackingOperation.Update,
                tracking.UpdatedColumns(table, keyOrdinals, [columnIndex]));
        }
        var undoLog = table.IsTableVariable ? batch.CurrentTableVarUndoLog : batch.CurrentUndoLog;
        if (lockable && VersionStore.IsVersioningEnabled(batch.DatabaseFor(table)))
            VersionStore.CaptureWrite(batch, table, address, address, oldBytes, VersionWriteKind.Update);
        table.Heap.UpdateAt(address.PageIndex, address.SlotIndex, RowEncoder.EncodeRow(table.StoredColumns, values, table.Heap), undoLog);
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
}
