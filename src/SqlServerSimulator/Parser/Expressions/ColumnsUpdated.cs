using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>COLUMNS_UPDATED()</c> — the firing statement's updated-column bitmask,
/// as a <c>varbinary</c> whose bit <c>(id - 1) % 8</c> of byte
/// <c>(id - 1) / 8</c> marks <c>column_id</c> <c>id</c>, least-significant
/// bit first. The mask spans the parent table's column-id watermark, so a
/// dropped column keeps its bit position and the length doesn't shrink.
/// </summary>
/// <remarks>
/// Unlike <see cref="UpdatePredicate"/>, this is a value expression and is
/// legal outside a trigger, where it evaluates to NULL rather than raising —
/// an asymmetry probe-confirmed against SQL Server 2025 (<c>UPDATE(col)</c>
/// raises Msg 140 in the same position).
/// A DELETE trigger sees a zero-length value, not a run of zero bytes:
/// <c>DATALENGTH(COLUMNS_UPDATED())</c> is 0 there.
/// A database-scope DDL trigger body has no firing columns, so it reads NULL
/// as well.
/// </remarks>
internal sealed class ColumnsUpdatedFunction : Expression
{
    private static readonly VarbinarySqlType ResultType = VarbinarySqlType.Get(8000);

    public ColumnsUpdatedFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "columns_updated");

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.TriggerFrame is { Trigger: not null } frame
            ? SqlValue.FromVarbinary(ResultType, frame.ColumnsUpdatedMask)
            : SqlValue.Null(ResultType);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => "COLUMNS_UPDATED()";
}
