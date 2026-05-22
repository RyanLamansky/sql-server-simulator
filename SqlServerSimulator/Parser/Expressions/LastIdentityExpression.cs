using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>: returns the
/// executing connection's last-inserted identity value as
/// <c>numeric(38, 0)</c>, or NULL when the most recent INSERT didn't touch
/// an identity column. The two T-SQL surfaces differ in scope on real SQL
/// Server (SCOPE_IDENTITY is per scope, @@IDENTITY is per session); the
/// simulator collapses both to <see cref="SimulatedDbConnection.LastIdentity"/>.
/// </summary>
/// <remarks>
/// Reads <see cref="RuntimeContext.Batch"/>'s connection at evaluation
/// time — nothing captured at parse time. Required for parsed-once-run-many
/// expressions (e.g. baked into a column default) that may execute on a
/// different connection from the one that parsed them.
/// </remarks>
internal sealed class LastIdentityExpression : Expression
{
    private static readonly SqlType ResultType = SqlType.GetDecimal(38, 0);

    public LastIdentityExpression()
    {
    }

    public LastIdentityExpression(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        runtime.Batch.Connection.LastIdentity is decimal v
            ? SqlValue.FromDecimal(ResultType, v)
            : SqlValue.Null(ResultType);

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => "SCOPE_IDENTITY()";
}
