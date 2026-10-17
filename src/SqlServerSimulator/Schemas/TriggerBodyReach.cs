using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// What a trigger's body may do beyond its own statements, read from its text
/// once (<see cref="Trigger.BodyReach"/>, <see cref="DdlTrigger.BodyReach"/>):
/// whether it may send rows to the firing statement's client, which is what a
/// firing statement's thread of its own (<see cref="StatementCoroutine"/>)
/// exists for, and whether it may write, which can fire another trigger.
/// </summary>
/// <remarks>
/// The reading is conservative in both directions it can err: a shape it
/// can't place counts as sending, so the only cost of a miss is a thread
/// handoff the statement didn't need.
/// </remarks>
[Flags]
internal enum TriggerBodyReach
{
    None = 0,

    /// <summary>
    /// A statement of the body may send rows: a <c>SELECT</c> that isn't an
    /// assignment, a cursor's query or an <c>INSERT</c>'s source, a
    /// <c>FETCH</c> without <c>INTO</c>, an <c>OUTPUT</c> clause,
    /// <c>READTEXT</c>, <c>DBCC</c>, <c>RESTORE</c>, <c>RECEIVE</c>, any
    /// <c>EXEC</c> (whose procedure or dynamic batch may send what it likes),
    /// a body opening with a bare name (a procedure called without
    /// <c>EXEC</c>), a CLR body, or a text the tokenizer refuses.
    /// </summary>
    SendsRows = 1,

    /// <summary>
    /// A statement of the body writes or changes a definition, which can fire
    /// another table's trigger or a DDL trigger.
    /// </summary>
    Writes = 2,
}

/// <summary>Reads a trigger body's text for its <see cref="TriggerBodyReach"/>.</summary>
internal static class TriggerBodyScan
{
    /// <summary>Everything a body may do, for one this scan can't read.</summary>
    public const TriggerBodyReach Everything = TriggerBodyReach.SendsRows | TriggerBodyReach.Writes;

    /// <summary>The reach of the body whose text is <paramref name="body"/>.</summary>
    public static TriggerBodyReach Of(string body)
    {
        List<Token> tokens;
        try
        {
            tokens = Significant(body);
        }
        catch (SimulatedSqlException)
        {
            return Everything;
        }
        if (tokens.Count == 0)
            return TriggerBodyReach.None;
        // A batch's first statement may call a procedure by its bare name.
        if (tokens[0] is not ReservedKeyword)
            return Everything;

        var reach = TriggerBodyReach.None;
        var depth = 0;
        // An INSERT at the outermost level whose source query hasn't been met.
        var insertAwaitsSource = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Operator { Character: ';' }:
                    insertAwaitsSource = false;
                    break;
                case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute or Keyword.ReadText or Keyword.Dbcc or Keyword.Restore }:
                case UnquotedString { ContextualKeyword: ContextualKeyword.Output }:
                    return Everything;
                case UnquotedString receive when receive.Span.Equals("RECEIVE", StringComparison.OrdinalIgnoreCase):
                    return Everything;
                case ReservedKeyword { Keyword: Keyword.Fetch }:
                    if (!FetchesInto(tokens, i))
                        return Everything;
                    break;
                case ReservedKeyword { Keyword: Keyword.Insert }:
                    reach |= TriggerBodyReach.Writes;
                    insertAwaitsSource = depth == 0;
                    break;
                case ReservedKeyword { Keyword: Keyword.Update or Keyword.Delete or Keyword.Merge or Keyword.Bulk or Keyword.WriteText or Keyword.UpdateText }:
                case ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter or Keyword.Drop or Keyword.Grant or Keyword.Deny or Keyword.Revoke or Keyword.Truncate }:
                case UnquotedString { ContextualKeyword: ContextualKeyword.Enable or ContextualKeyword.Disable }:
                    reach |= TriggerBodyReach.Writes;
                    if (depth == 0)
                        insertAwaitsSource = false;
                    break;
                case ReservedKeyword { Keyword: Keyword.Values or Keyword.Default or Keyword.Set or Keyword.Declare or Keyword.If or Keyword.While }:
                case ReservedKeyword { Keyword: Keyword.Begin or Keyword.End or Keyword.Else or Keyword.Return or Keyword.Print }:
                    if (depth == 0)
                        insertAwaitsSource = false;
                    break;
                case ReservedKeyword { Keyword: Keyword.Select }:
                    if (depth != 0 && !OpensParenthesizedStatement(tokens, i))
                        break;
                    switch (i == 0 ? null : tokens[i - 1])
                    {
                        // A set operator's further operand, of a query already placed.
                        case ReservedKeyword { Keyword: Keyword.Union or Keyword.All or Keyword.Except or Keyword.Intersect }:
                        // A cursor's query.
                        case ReservedKeyword { Keyword: Keyword.For }:
                            break;
                        default:
                            if (depth == 0 && insertAwaitsSource)
                                insertAwaitsSource = false;
                            else if (!AssignsVariables(tokens, i))
                                return Everything;
                            break;
                    }
                    break;
            }
        }
        return reach;
    }

    private static List<Token> Significant(string body)
    {
        List<Token> tokens = [];
        var index = 0;
        while (Tokenizer.NextToken(body, ref index, Collation.Baseline) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }
        return tokens;
    }

    /// <summary>
    /// Whether the <c>SELECT</c> at <paramref name="select"/>, inside a
    /// parenthesis, may be a statement of its own — <c>(SELECT …)</c> where a
    /// statement can start — rather than a subquery: anything but an operator,
    /// a contextual keyword or a reserved one other than <c>BEGIN</c>,
    /// <c>ELSE</c> or <c>END</c> ahead of the parenthesis.
    /// </summary>
    private static bool OpensParenthesizedStatement(List<Token> tokens, int select)
    {
        if (tokens[select - 1] is not Operator { Character: '(' })
            return false;
        return select < 2 || tokens[select - 2] switch
        {
            Operator { Character: ';' or ')' } => true,
            Operator => false,
            ReservedKeyword { Keyword: Keyword.Begin or Keyword.Else or Keyword.End } => true,
            ReservedKeyword => false,
            // MERGE's USING, APPLY and the like.
            UnquotedString { ContextualKeyword: not ContextualKeyword.NotAKeyword } => false,
            _ => true,
        };
    }

    /// <summary>
    /// Whether the <c>SELECT</c> at <paramref name="select"/> assigns
    /// variables (<c>SELECT [TOP …] @v = …</c>, a compound operator included)
    /// rather than sending rows.
    /// </summary>
    private static bool AssignsVariables(List<Token> tokens, int select)
    {
        var i = select + 1;
        if (i < tokens.Count && tokens[i] is ReservedKeyword { Keyword: Keyword.Distinct or Keyword.All })
            i++;
        if (i < tokens.Count && tokens[i] is ReservedKeyword { Keyword: Keyword.Top })
        {
            i++;
            if (i < tokens.Count && tokens[i] is Operator { Character: '(' })
            {
                for (var depth = 0; i < tokens.Count; i++)
                {
                    depth += tokens[i] switch
                    {
                        Operator { Character: '(' } => 1,
                        Operator { Character: ')' } => -1,
                        _ => 0,
                    };
                    if (depth == 0)
                        break;
                }
            }
            i++;
        }
        if (i + 1 >= tokens.Count || tokens[i] is not AtPrefixedString)
            return false;
        return tokens[i + 1] switch
        {
            Operator { Character: '=' } => true,
            Operator { Character: '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' } => i + 2 < tokens.Count && tokens[i + 2] is Operator { Character: '=' },
            _ => false,
        };
    }

    /// <summary>
    /// Whether the <c>FETCH</c> at <paramref name="fetch"/> reads into
    /// variables, its <c>INTO</c> met before anything that ends its statement.
    /// </summary>
    private static bool FetchesInto(List<Token> tokens, int fetch)
    {
        for (var i = fetch + 1; i < tokens.Count && i <= fetch + 8; i++)
        {
            switch (tokens[i])
            {
                case ReservedKeyword { Keyword: Keyword.Into }:
                    return true;
                case ReservedKeyword { Keyword: Keyword.From }:
                    break;
                case ReservedKeyword or Operator { Character: ';' }:
                    return false;
            }
        }
        return false;
    }
}
