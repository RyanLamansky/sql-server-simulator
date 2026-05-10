using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>. The optional
    /// <c>, -1</c> suffix that promotes the flag to global scope isn't modeled
    /// — flags scope to <see cref="SimulatedDbConnection.TraceFlags"/> on the
    /// executing connection, so concurrent connections don't share state.
    /// </summary>
    private static bool TryParseDbcc(ParserContext context)
    {
        context.MoveNextRequired();
        bool turningOn;
        switch (context.AsContextual())
        {
            case ContextualKeyword.TraceOn: turningOn = true; break;
            case ContextualKeyword.TraceOff: turningOn = false; break;
            default: return false;
        }

        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        if (context.GetNextRequired() is not Operator { Character: ')' })
            return false;

        var flag = numericValue.AsInt32;
        var flags = context.Connection.TraceFlags;
        _ = turningOn ? flags.Add(flag) : flags.Remove(flag);
        return true;
    }
}
