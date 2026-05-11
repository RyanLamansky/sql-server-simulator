using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException MissingEndCommentMark() => new("Missing end comment mark '*/'.", 113, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 105: a string literal opened with <c>'</c> was
    /// never closed before end of input.
    /// </summary>
    internal static SimulatedSqlException UnclosedStringLiteral() =>
        new("Unclosed quotation mark after the character string.", 105, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNearKeyword(ReservedKeyword token) => new($"Incorrect syntax near the keyword '{token}'.", 156, 15, 1);

    /// <summary>
    /// Msg 156 variant that takes the keyword text directly — for sites
    /// where the parser detected the misplaced keyword via lookahead /
    /// post-parse semantic check rather than the original ReservedKeyword
    /// token. Lowercased to match the existing factory's output. Used by
    /// the SELECT INTO + UNION rejection path (INTO is only valid on the
    /// first branch of a set-op chain).
    /// </summary>
    internal static SimulatedSqlException SyntaxErrorNearKeyword(string keyword) => new($"Incorrect syntax near the keyword '{keyword}'.", 156, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(ParserContext context) => new($"Incorrect syntax near '{context.Token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(Token? token) => new($"Incorrect syntax near '{token}'.", 102, 15, 1);

    internal static SimulatedSqlException SyntaxErrorNear(char c) => new($"Incorrect syntax near '{c}'.", 102, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 189: a built-in function received the wrong
    /// number of arguments. Wording uses the lowercase function name and the
    /// per-function minimum (e.g. <c>"The concat function requires 2 to 254
    /// arguments."</c>). Probe-confirmed against SQL Server 2025 (2026-05-09)
    /// for <c>CONCAT</c> (min 2) and <c>CONCAT_WS</c> (min 3).
    /// </summary>
    internal static SimulatedSqlException FunctionArgumentCount(string lowercaseFunctionName, int min) =>
        new($"The {lowercaseFunctionName} function requires {min} to 254 arguments.", 189, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 3902: a <c>COMMIT</c> was issued with no
    /// active transaction. Probe-confirmed against SQL Server 2025
    /// (2026-05-08): Class 16, State 1, exact wording verbatim.
    /// </summary>
    internal static SimulatedSqlException NoCorrespondingBeginCommit() =>
        new("The COMMIT TRANSACTION request has no corresponding BEGIN TRANSACTION.", 3902, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3903: a <c>ROLLBACK</c> was issued with no
    /// active transaction. Probe-confirmed against SQL Server 2025
    /// (2026-05-08): Class 16, State 1, exact wording verbatim.
    /// </summary>
    internal static SimulatedSqlException NoCorrespondingBeginRollback() =>
        new("The ROLLBACK TRANSACTION request has no corresponding BEGIN TRANSACTION.", 3903, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 319: a CTE-prefixed statement (a <c>WITH</c>
    /// clause introducing a common table expression) followed another
    /// statement with no <c>;</c> separator. Probe-confirmed verbatim text /
    /// Class 15 / State 1. The wording is structural: real SQL Server lists
    /// every grammar slot where <c>WITH</c> appears (CTE, xmlnamespaces,
    /// change-tracking context) since the parser can't distinguish at this
    /// point. A <c>WITH</c> at batch start, or immediately after a <c>;</c>,
    /// is fine — only a back-to-back <c>statement WITH cte</c> sequence
    /// triggers this.
    /// </summary>
    internal static SimulatedSqlException CteRequiresPrecedingSemicolon() =>
        new("Incorrect syntax near the keyword 'with'. If this statement is a common table expression, an xmlnamespaces clause or a change tracking context clause, the previous statement must be terminated with a semicolon.", 319, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 10713: a <c>MERGE</c> statement was not
    /// followed by a <c>;</c>. Probe-confirmed verbatim text (note the
    /// hyphenated <c>"semi-colon"</c>) / Class 15 / State 1. <c>MERGE</c> is
    /// the only statement family the server requires to be terminated with a
    /// semicolon, regardless of whether another statement follows or the
    /// batch ends.
    /// </summary>
    internal static SimulatedSqlException MergeMustBeTerminated() =>
        new("A MERGE statement must be terminated by a semi-colon (;).", 10713, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 4145: an <c>IF</c> condition (or any other
    /// slot SQL Server typed as a Boolean predicate) received a value-typed
    /// expression instead of a Boolean expression — e.g.
    /// <c>IF 1</c>, <c>IF NULL</c>, <c>IF (cast(null as bit))</c>,
    /// <c>IF 'abc'</c>. Probe-confirmed against SQL Server 2025 (2026-05-11):
    /// Class 15, State 1, exact wording verbatim. The "near 'X'" suffix
    /// is whatever token follows the cond — usually a statement-starting
    /// keyword like <c>'select'</c> or a paren.
    /// </summary>
    internal static SimulatedSqlException NonBooleanInConditionContext(Token? nextToken) =>
        new($"An expression of non-boolean type specified in a context where a condition is expected, near '{nextToken}'.", 4145, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 135: a <c>BREAK</c> statement appeared outside
    /// any enclosing <c>WHILE</c>. Probe-confirmed against SQL Server 2025
    /// (2026-05-11): Class 15, State 1, exact wording verbatim. Fires even
    /// from un-taken IF branches — SQL Server applies the loop-scope check
    /// at compile time, so the simulator does too (distinct from the Q15
    /// deferred-name-resolution gap, where un-taken branches escape Msg 208).
    /// </summary>
    internal static SimulatedSqlException BreakOutsideLoop() =>
        new("Cannot use a BREAK statement outside the scope of a WHILE statement.", 135, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 136: a <c>CONTINUE</c> statement appeared
    /// outside any enclosing <c>WHILE</c>. Probe-confirmed against SQL
    /// Server 2025 (2026-05-11): Class 15, State 1, exact wording verbatim.
    /// Same compile-time semantics as Msg 135.
    /// </summary>
    internal static SimulatedSqlException ContinueOutsideLoop() =>
        new("Cannot use a CONTINUE statement outside the scope of a WHILE statement.", 136, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 178: a <c>RETURN</c> statement carries a
    /// value (e.g. <c>RETURN 5</c>) in a context where the value form isn't
    /// allowed — at batch level, only the bare <c>RETURN</c> form is legal.
    /// The value form is reserved for stored procedures and scalar functions.
    /// Probe-confirmed against SQL Server 2025 (2026-05-11): Class 15,
    /// State 1, exact wording verbatim. Fires at compile time — even from
    /// un-taken IF branches, same pattern as Msg 135 (BREAK).
    /// </summary>
    internal static SimulatedSqlException ReturnWithValueNotAllowed() =>
        new("A RETURN statement with a return value cannot be used in this context.", 178, 15, 1);
}
