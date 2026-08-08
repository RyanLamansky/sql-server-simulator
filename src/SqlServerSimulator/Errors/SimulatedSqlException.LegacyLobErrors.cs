namespace SqlServerSimulator;

// Diagnostics of the legacy text-pointer statements — READTEXT, WRITETEXT and
// UPDATETEXT — every one probe-confirmed against SQL Server 2025 for message
// text, class and state.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server's Msg 182 — the statement named a single-part
    /// identifier where the utility needs <c>table.column</c>.
    /// </summary>
    internal static SimulatedSqlException TableAndColumnNamesRequiredForTextUtility() =>
        new("Table and column names must be supplied for the READTEXT or WRITETEXT utility.", 182, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7122 — the pointer operand's own type isn't the
    /// <c>binary(16)</c> the statements take.
    /// </summary>
    internal static SimulatedSqlException InvalidTextPointerType() =>
        new("Invalid text, ntext, or image pointer type. Must be binary(16).", 7122, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7123 — the 16 bytes are not a pointer this
    /// column will answer to: arbitrary bytes, a pointer read from a different
    /// column, or one whose row is gone. Real renders the value as
    /// <c>0x</c>-prefixed uppercase hex.
    /// </summary>
    internal static SimulatedSqlException InvalidTextPointerValue(string pointerHex) =>
        new($"Invalid text, ntext, or image pointer value {pointerHex}.", 7123, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7124 — <c>READTEXT</c>'s window runs past the
    /// value, real naming the value's own length. A size of 0 means "to the
    /// end", so only an offset past the length trips it there.
    /// </summary>
    internal static SimulatedSqlException ReadTextWindowPastData(long dataLength) =>
        new($"The offset and length specified in the READTEXT statement is greater than the actual data length of {dataLength}.", 7124, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 7125 — the named column isn't one a text pointer
    /// can address (anything but <c>text</c> / <c>ntext</c> / <c>image</c>).
    /// </summary>
    internal static SimulatedSqlException TextPointerConflictsWithColumnName() =>
        new("The text, ntext, or image pointer value conflicts with the column name specified.", 7125, 16, 4);

    /// <summary>
    /// Mimics SQL Server's Msg 7133 — the pointer operand evaluated to NULL,
    /// which is what a cell that was never written hands back. Real spells the
    /// utility in the message and splits the state: <c>READ TEXT</c> at 1,
    /// <c>WRITE TEXT</c> and <c>UPDATE TEXT</c> at 2.
    /// </summary>
    internal static SimulatedSqlException NullTextPointer(string utility, byte state) =>
        new($"NULL textptr (text, ntext, or image pointer) passed to {utility} function.", 7133, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 7116 — an offset outside the value: past its end
    /// for <c>UPDATETEXT</c>'s insert offset (state 4), or negative for
    /// <c>READTEXT</c>'s (state 3, which only a variable can reach since the
    /// grammar refuses a written sign).
    /// </summary>
    internal static SimulatedSqlException LobOffsetOutOfRange(long offset, byte state) =>
        new($"Offset {offset} is not in the range of available LOB data.", 7116, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 7135 — <c>UPDATETEXT</c>'s deletion length runs
    /// past the end of the value.
    /// </summary>
    internal static SimulatedSqlException DeletionLengthOutOfRange(long length) =>
        new($"Deletion length {length} is not in the range of available text, ntext, or image data.", 7135, 16, 4);

    /// <summary>
    /// Mimics SQL Server's Msg 518 — <c>UPDATETEXT</c>'s copy form named a
    /// source column of a different legacy LOB type than the destination.
    /// </summary>
    internal static SimulatedSqlException CannotConvertDataType(string from, string to) =>
        new($"Cannot convert data type {from} to {to}.", 518, 16, 1);
}
