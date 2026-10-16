using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// Where a statement's text ends, as real's statement events and
// sys.dm_exec_requests place it — see docs/claude/extended-events.md#events.
partial class Simulation
{
    /// <summary>
    /// Where the statement of <paramref name="parser"/>'s text starting at
    /// <paramref name="start"/> ends, the parser standing past it — on the
    /// next statement's first token, its separator, or the text's end: at its
    /// own last character, and at the character before the next statement,
    /// the separator and white space between them included, or -1 when it
    /// runs to the text's end. Which real reports is the statement's kind's
    /// (<see cref="EndsAtItsLastToken"/>), settled where an end is read.
    /// </summary>
    private static (int Tight, int Loose) StatementEndsOf(ParserContext parser, int start)
    {
        var text = parser.Command.CommandText;
        var next = NextStatementStart(parser);
        var loose = next >= text.Length ? -1 : next - 1;
        if (parser.PreviousTokenEnd <= start)
            return (loose, loose);
        var last = parser.PreviousTokenEnd - 1;
        while (last > start && (char.IsWhiteSpace(text[last]) || text[last] == ';'))
            last--;
        return (last, loose);
    }

    /// <summary>
    /// The end real reports for the statement of <paramref name="text"/> at
    /// <paramref name="start"/> whose two ends are <paramref name="tight"/>
    /// and <paramref name="loose"/> (see <see cref="StatementEndsOf"/>).
    /// </summary>
    internal static int ReportedStatementEnd(string text, int start, int tight, int loose)
    {
        if (!EndsAtItsLastToken(text, start, out var takesTerminator))
            return loose;
        if (takesTerminator)
        {
            var at = tight + 1;
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;
            if (at < text.Length && text[at] == ';')
                return at;
        }
        return tight;
    }

    /// <summary>
    /// Whether a statement <paramref name="error"/> ended still reports its
    /// completion: one <c>RAISERROR</c> raised, an error of severity 11 or
    /// less, and one real follows with Msg 3621 — a write the error
    /// terminated. Anything else — a <c>SELECT</c>'s divide by zero, a
    /// <c>THROW</c>, a refused DDL, a missing procedure — reports its start
    /// alone (probed 2026-10-16 against SQL Server 2025:
    /// <c>update t set a = a / 0</c> and <c>drop table missing</c> complete,
    /// <c>select 1/(a-1) from t</c> and <c>create table</c> of a name taken
    /// don't).
    /// </summary>
    private static bool CompletesDespite(BatchContext batch, SimulatedSqlException error) =>
        error.RaisedByRaiserror || error.Class <= 11 || IsStatementTerminationNoticed(batch, error);

    /// <summary>
    /// Whether real ends the text of the statement at <paramref name="start"/>
    /// at its own last token, leaving out the separator, white space and
    /// comments after it, rather than at the next statement's start: a
    /// query, <c>EXEC</c> but not <c>EXECUTE AS</c>, a cursor's
    /// <c>DECLARE</c>, <c>TRUNCATE</c>, <c>DBCC</c>, <c>RETURN</c>, and among
    /// the DDL what real's grammar ends the same way — <c>CREATE</c>
    /// <c>TABLE</c>, any index, <c>STATISTICS</c>, <c>DATABASE</c> and
    /// <c>XML SCHEMA COLLECTION</c>; <c>ALTER</c> <c>TABLE</c>,
    /// <c>INDEX</c>, <c>SCHEMA</c>, <c>DATABASE</c> (not its scoped
    /// configuration) and <c>PARTITION FUNCTION</c>; <c>DROP</c> of a
    /// table, index, view, procedure, function, trigger, default, rule,
    /// sequence, synonym, user, database, partition function or XML schema
    /// collection. Every other statement — the other DDL, <c>SET</c>,
    /// <c>DECLARE</c> of a variable, <c>PRINT</c>, <c>GRANT</c>, the
    /// transaction and cursor statements, <c>WAITFOR</c>, <c>CHECKPOINT</c>
    /// — takes what follows it up to the next statement (probed 2026-10-16
    /// against SQL Server 2025, over <c>sql_statement_completed</c> and a
    /// blocked request's <c>statement_end_offset</c>: <c>drop synonym s</c>
    /// ends at its <c>s</c>, <c>create synonym s for t;</c> takes its
    /// <c>;</c> and the line break after it). <c>MERGE</c>, whose grammar
    /// requires its terminator, takes the <c>;</c> that ends it
    /// (<paramref name="takesTerminator"/>).
    /// </summary>
    private static bool EndsAtItsLastToken(string text, int start, out bool takesTerminator)
    {
        takesTerminator = false;
        var words = new Range[4];
        var count = 0;
        var index = start;
        try
        {
            while (count < words.Length && Tokenizer.NextToken(text, ref index, Collation.Baseline) is { } token)
            {
                switch (token)
                {
                    case Whitespace or Comment:
                        continue;
                    case Operator { Character: '(' } when count == 0:
                        return true;
                    case ReservedKeyword or UnquotedString or Name:
                        words[count++] = token.StartIndex..token.EndIndex;
                        continue;
                    case AtPrefixedString when count == 1:
                        // DECLARE @v …: a variable, not a cursor.
                        words[count++] = token.StartIndex..token.EndIndex;
                        continue;
                }
                break;
            }
        }
        catch (SimulatedSqlException)
        {
            // Text the tokenizer refuses ends the words read so far.
        }
        if (count == 0)
            return false;

        bool Is(int word, string value) => word < count && text.AsSpan(words[word]).Equals(value, StringComparison.OrdinalIgnoreCase);

        if (Is(0, "MERGE"))
            return takesTerminator = true;
        if (Is(0, "SELECT") || Is(0, "INSERT") || Is(0, "UPDATE") || Is(0, "DELETE") || Is(0, "WITH")
            || Is(0, "TRUNCATE") || Is(0, "DBCC") || Is(0, "RETURN"))
        {
            return true;
        }
        if (Is(0, "EXEC") || Is(0, "EXECUTE"))
            return !Is(1, "AS");
        if (Is(0, "DECLARE"))
            return count > 1 && text[words[1].Start] != '@';
        if (Is(0, "CREATE"))
        {
            return Is(1, "TABLE") || Is(1, "INDEX") || Is(1, "STATISTICS") || Is(1, "DATABASE")
                || Is(1, "UNIQUE") || Is(1, "CLUSTERED") || Is(1, "NONCLUSTERED") || Is(1, "PRIMARY")
                || Is(1, "COLUMNSTORE") || Is(1, "SPATIAL")
                || (Is(1, "XML") && (Is(2, "INDEX") || Is(2, "SCHEMA")));
        }
        if (Is(0, "ALTER"))
        {
            return Is(1, "TABLE") || Is(1, "INDEX") || Is(1, "SCHEMA")
                || (Is(1, "DATABASE") && !Is(2, "SCOPED"))
                || (Is(1, "PARTITION") && Is(2, "FUNCTION"));
        }
        if (Is(0, "DROP"))
        {
            return Is(1, "TABLE") || Is(1, "INDEX") || Is(1, "VIEW") || Is(1, "PROCEDURE") || Is(1, "PROC")
                || Is(1, "FUNCTION") || Is(1, "TRIGGER") || Is(1, "DEFAULT") || Is(1, "RULE")
                || Is(1, "SEQUENCE") || Is(1, "SYNONYM") || Is(1, "USER") || Is(1, "CREDENTIAL")
                || (Is(1, "DATABASE") && !Is(2, "SCOPED") && !Is(2, "AUDIT") && !Is(2, "ENCRYPTION"))
                || (Is(1, "PARTITION") && Is(2, "FUNCTION"))
                || (Is(1, "XML") && Is(2, "SCHEMA"));
        }
        return false;
    }
}
