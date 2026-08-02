using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>EVENTDATA()</c> — the <c>&lt;EVENT_INSTANCE&gt;</c> document describing the
/// DDL statement that fired the running database-scope DDL trigger, as an
/// <c>xml</c> value. Outside such a body — at top level, or inside a DML
/// trigger — it evaluates to NULL rather than raising (probe-confirmed against
/// SQL Server 2025).
/// </summary>
/// <remarks>
/// The document is built once per fire by <c>Simulation.BuildDdlEventData</c>
/// and carried on the body's <see cref="TriggerFrame"/>, so every call within
/// one body returns the same instance — including the same <c>PostTime</c>.
/// </remarks>
internal sealed class EventDataFunction : Expression
{
    public EventDataFunction(ParserContext context) => ErrorFunctionCtor.EnsureNoArgs(context, "eventdata");

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.TriggerFrame?.DdlEventData is { } document
            ? SqlValue.FromXml(document)
            : SqlValue.Null(SqlType.Xml);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Xml;

    internal override bool ResultIsNullable(NullabilityContext context) => true;

    internal override string DebugDisplay() => "EVENTDATA()";
}
