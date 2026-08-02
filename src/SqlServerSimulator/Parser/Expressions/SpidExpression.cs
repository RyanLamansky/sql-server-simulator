using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs <c>@@SPID</c>: returns the session's id as <see cref="SqlType.SmallInt"/>
/// (real SQL Server's @@SPID is smallint — probe-confirmed). First user
/// connection on a fresh <see cref="Simulation"/> reads 51.
/// </summary>
internal sealed class SpidExpression(ParserContext context) : Expression
{
    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromInt16((short)context.Connection.Spid);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.SmallInt;

    internal override bool ResultIsNullable(NullabilityContext context) => false;

    internal override string DebugDisplay() => "@@SPID";
}
