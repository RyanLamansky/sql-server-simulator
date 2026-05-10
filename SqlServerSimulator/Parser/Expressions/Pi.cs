using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>PI()</c>: returns the float constant <see cref="Math.PI"/>.
/// Zero-argument function with required parens — bare <c>pi</c> resolves as
/// a column reference (Msg 207). Any argument raises Msg 174
/// ("The pi function requires 0 argument(s).") — probe-confirmed against
/// SQL Server 2025 (2026-05-10).
/// </summary>
internal sealed class Pi : Expression
{
    public Pi(ParserContext context)
    {
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.FunctionRequiresNArguments("pi", 0);
    }

    public override SqlValue Run(RuntimeContext runtime) => SqlValue.FromDouble(Math.PI);

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Float;

    internal override string DebugDisplay() => "PI()";
}
