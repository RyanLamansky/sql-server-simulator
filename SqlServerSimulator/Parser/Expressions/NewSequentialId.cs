using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NEWSEQUENTIALID()</c>: returns a <c>uniqueidentifier</c> whose
/// successive values compare strictly greater than every prior value
/// produced for the same <see cref="Simulation"/>. SQL Server's grammar
/// restricts this function to a column's <c>DEFAULT</c> clause; using it
/// elsewhere — bare <c>SELECT</c>, INSERT VALUES list, an arithmetic
/// expression, even nested inside a parenthesized DEFAULT body — raises
/// Msg 302. The parser threads a flag through
/// <see cref="ParserContext.InDefaultClause"/>; this constructor checks it.
/// </summary>
/// <remarks>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/newsequentialid-transact-sql
/// </remarks>
internal sealed class NewSequentialId : Expression
{
    private readonly Simulation simulation;

    public NewSequentialId(ParserContext context)
    {
        if (!context.InDefaultClause)
            throw SimulatedSqlException.NewSequentialIdNotInDefault();
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.NewSequentialIdNotInDefault();
        this.simulation = context.Simulation;
    }

    public override SqlValue Run(Func<List<string>, SqlValue> getColumnValue) =>
        SqlValue.FromGuid(this.simulation.GenerateNewSequentialId());

    public override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) => SqlType.UniqueIdentifier;

    internal override string DebugDisplay() => "NEWSEQUENTIALID()";
}
