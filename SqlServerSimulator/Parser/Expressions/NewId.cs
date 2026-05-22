using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NEWID()</c>: returns a fresh random <c>uniqueidentifier</c>.
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/newid-transact-sql
/// </summary>
internal sealed class NewId : Expression
{
    public NewId(ParserContext context)
    {
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromGuid(Guid.NewGuid());

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.UniqueIdentifier;

    internal override string DebugDisplay() => "NEWID()";
}
