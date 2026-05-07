using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Backs both <c>SCOPE_IDENTITY()</c> and <c>@@IDENTITY</c>: returns the
/// simulation's last-inserted identity value as <c>numeric(38, 0)</c>, or
/// NULL when the most recent INSERT didn't touch an identity column. The
/// two T-SQL surfaces differ in scope on real SQL Server (SCOPE_IDENTITY
/// is per scope, @@IDENTITY is per session); the simulator collapses both
/// to <see cref="Simulation.LastIdentity"/> until session-scoped state is
/// modeled — see the same simplification on <see cref="Simulation.TraceFlags"/>.
/// </summary>
internal sealed class LastIdentityExpression : Expression
{
    private static readonly SqlType ResultType = SqlType.GetDecimal(38, 0);

    private readonly Simulation simulation;

    public LastIdentityExpression(Simulation simulation) => this.simulation = simulation;

    public LastIdentityExpression(ParserContext context)
    {
        this.simulation = context.Simulation;
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(Func<MultiPartName, SqlValue> getColumnValue) =>
        this.simulation.LastIdentity is decimal v
            ? SqlValue.FromDecimal(ResultType, v)
            : SqlValue.Null(ResultType);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => ResultType;

    internal override string DebugDisplay() => "SCOPE_IDENTITY()";
}
