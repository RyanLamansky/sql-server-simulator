using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>DBCC TRACEON(N)</c> / <c>DBCC TRACEOFF(N)</c>. The optional
    /// <c>, -1</c> suffix that promotes the flag to global scope isn't modeled
    /// — the simulator has a single connection so session vs global doesn't
    /// matter today.
    /// </summary>
    private bool TryParseDbcc(ParserContext context)
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
        _ = turningOn ? this.TraceFlags.Add(flag) : this.TraceFlags.Remove(flag);
        return true;
    }
}
