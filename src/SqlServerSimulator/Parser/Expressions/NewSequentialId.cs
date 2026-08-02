using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NEWSEQUENTIALID()</c>: returns a <c>uniqueidentifier</c> whose
/// successive values compare strictly greater than every prior value
/// produced for the same <see cref="Simulation"/>. SQL Server's grammar
/// restricts this function to a column's <c>DEFAULT</c> clause; using it
/// elsewhere — bare <c>SELECT</c>, INSERT VALUES list, or any composing
/// arithmetic expression — raises Msg 302. The parser threads a flag
/// through <see cref="ParserContext.InDefaultClause"/>; this constructor
/// checks it. Both DEFAULT positions are covered: inline column
/// (<c>CREATE TABLE … col uniqueidentifier DEFAULT newsequentialid()</c>)
/// and named constraint (<c>ALTER TABLE t ADD CONSTRAINT df DEFAULT
/// (newsequentialid()) FOR col</c>). Argumented calls
/// (<c>newsequentialid(1)</c>) also raise Msg 302.
/// A function body is the one context where real answers differently — Msg 443
/// for the side-effecting operator — so the gate stands down there and lets the
/// body-shape walk speak.
/// </summary>
/// <remarks>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/newsequentialid-transact-sql
/// </remarks>
internal sealed class NewSequentialId : Expression
{
    private readonly Simulation simulation;

    public NewSequentialId(ParserContext context)
    {
        // Inside a scalar-UDF / multi-statement-TVF body being bound, real's
        // answer is the side-effecting-operator rule instead — Msg 443 naming
        // 'newsequentialid' (probe-confirmed for both kinds, and for a call in
        // a body table variable's DEFAULT clause). The shape walk has already
        // recorded it, so the CREATE reports it in the order real does;
        // everywhere else, including a procedure body, Msg 302 stands.
        if (!context.InDefaultClause && context.Batch.FunctionBodyShape is null)
            throw SimulatedSqlException.NewSequentialIdNotInDefault();
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.NewSequentialIdNotInDefault();
        this.simulation = context.Simulation;
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        SqlValue.FromGuid(this.simulation.GenerateNewSequentialId());

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.UniqueIdentifier;

    internal override string DebugDisplay() => "NEWSEQUENTIALID()";
}
