using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException MissingEndCommentMark() => new("Missing end comment mark '*/'.", 113, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 105: a string literal or quote-delimited
    /// identifier opened with <c>'</c> or <c>"</c> was never closed before
    /// end of input. Real SQL Server echoes the scanned body in the message
    /// (probe-confirmed for the <c>"</c> form against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException UnclosedStringLiteral(string body) =>
        new($"Unclosed quotation mark after the character string '{body}'.", 105, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 8631: expression parsing consumed the thread's
    /// stack down to the runtime's safety threshold. Real SQL Server raises
    /// this from an actual stack probe (probe-confirmed 2026-07-15: a
    /// 6000-term <c>1 + 1 + …</c> chain fails, 3000 succeeds — the threshold
    /// is stack-dependent, not a fixed count); the simulator mirrors that via
    /// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack"/>,
    /// so the depth it tolerates scales with the calling thread's stack size.
    /// Class 17 — batch-aborting, matching real.
    /// </summary>
    internal static SimulatedSqlException ServerStackLimitReached() =>
        new("Internal error: Server stack limit has been reached. Please look for potentially deep nesting in your query, and try to simplify it.", 8631, 17, 1);

    /// <summary>
    /// Mimics SQL Server error 191: parenthesized-expression nesting exceeded
    /// the structural limit. Probe-confirmed 2026-07-15: 1000 nested parens
    /// succeed on the reference, 2000 raise this — the simulator's limit is
    /// deliberately lower (512) so the structural Msg 191 fires before the
    /// stack probe converts the same shape into Msg 8631 on default-size
    /// (1 MB) threads.
    /// </summary>
    internal static SimulatedSqlException StatementNestedTooDeeply() =>
        new("Some part of your SQL statement is nested too deeply. Rewrite the query or break it up into smaller queries.", 191, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 125: <c>CASE</c> / <c>IIF</c> expressions were
    /// lexically nested beyond ten levels. Probe-confirmed 2026-07-18: Class
    /// 15, exact wording verbatim, and the <b>state</b> identifies the
    /// construct being entered when the eleventh level is reached — <c>4</c>
    /// for a searched/simple <c>CASE</c>, <c>2</c> for <c>IIF</c> (which
    /// desugars to a searched CASE). Nesting in a <c>WHEN</c> condition
    /// counts identically to a <c>THEN</c> / <c>ELSE</c> result, and the
    /// count is not reset by an intervening scalar-subquery boundary.
    /// </summary>
    internal static SimulatedSqlException CaseExpressionsNestedTooDeeply(byte state) =>
        new("Case expressions may only be nested to level 10.", 125, 15, state);

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
    /// Mimics SQL Server error 1038: a select-list column alias resolved to
    /// the empty string — <c>AS ''</c>, <c>AS []</c>, <c>AS ""</c>, bare
    /// <c>''</c>, or the alias-on-left <c>'' = expr</c>. Shares SQL Server's
    /// wording with the SELECT INTO missing-column-name diagnostic but lands
    /// at State 4 (probe-confirmed against SQL Server 2025), distinct from
    /// SELECT INTO's State 5.
    /// </summary>
    internal static SimulatedSqlException EmptyColumnAlias() =>
        new("An object or column name is missing or empty. For SELECT INTO statements, verify each column has a name. For other statements, look for empty alias names. Aliases defined as \"\" or [] are not allowed. Change the alias to a valid name.", 1038, 15, 4);

    /// <summary>
    /// Mimics SQL Server error 195: a <c>SET</c> statement names an option
    /// that isn't in the recognized set — and the parser saw enough of the
    /// rest of the shape (ON/OFF or a value token) to recognize it was meant
    /// as a SET option. Wording is probe-confirmed verbatim against SQL Server
    /// 2025 (2026-05-14): the offending name is preserved verbatim (uppercase
    /// in the probe) inside single quotes. The narrower failure mode where the
    /// name isn't followed by anything parseable falls through to the generic
    /// Msg 102 path instead.
    /// </summary>
    internal static SimulatedSqlException UnrecognizedSetOption(string name) =>
        new($"'{name}' is not a recognized SET option.", 195, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 111: the given <paramref name="statementKind"/>
    /// (e.g. <c>"CREATE/ALTER PROCEDURE"</c>, <c>"CREATE VIEW"</c>) must be
    /// the first statement in a query batch. Probe-confirmed wording per
    /// kind against SQL Server 2025 (2026-05-13): PROCEDURE merges CREATE
    /// and ALTER into one label; VIEW / FUNCTION / TRIGGER / SCHEMA each
    /// use their separate <c>CREATE</c> / <c>ALTER</c> labels.
    /// </summary>
    internal static SimulatedSqlException MustBeFirstStatementInBatch(string statementKind) =>
        new($"'{statementKind}' must be the first statement in a query batch.", 111, 15, 1);

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
    /// Mimics SQL Server error 5324: a <c>MERGE</c> statement had a
    /// <c>WHEN MATCHED</c> (or <c>WHEN NOT MATCHED BY SOURCE</c>) clause
    /// carrying a search condition appear <em>after</em> a clause of the
    /// same family with no search condition. Real SQL Server requires the
    /// unconditional fallback to be last in the family. Probe-confirmed
    /// against SQL Server 2025 (2026-05-13): Class 16, State 1, exact
    /// wording verbatim. The <paramref name="clauseFamily"/> is either
    /// <c>"WHEN MATCHED"</c> or <c>"WHEN NOT MATCHED BY SOURCE"</c> —
    /// <c>WHEN NOT MATCHED [BY TARGET]</c> has at most one clause total
    /// (Msg 10714) and so doesn't share this path.
    /// </summary>
    internal static SimulatedSqlException MergeUnconditionalMustBeLast(string clauseFamily) =>
        new($"In a MERGE statement, a '{clauseFamily}' clause with a search condition cannot appear after a '{clauseFamily}' clause with no search condition.", 5324, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8672: a <c>MERGE</c> statement's
    /// <c>WHEN MATCHED ... THEN UPDATE</c> branch attempted to update the
    /// same target row from multiple source rows. SQL Server's <c>DELETE</c>
    /// matched branch is forgiving (multiple matches collapse to one delete),
    /// but <c>UPDATE</c> raises this. Probe-confirmed against SQL Server 2025
    /// (2026-05-13): Class 16, State 1, exact wording verbatim.
    /// </summary>
    internal static SimulatedSqlException MergeMultiMatch() =>
        new("The MERGE statement attempted to UPDATE or DELETE the same row more than once. This happens when a target row matches more than one source row. A MERGE statement cannot UPDATE/DELETE the same row of the target table multiple times. Refine the ON clause to ensure a target row matches at most one source row, or use the GROUP BY clause to group the source rows.", 8672, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10710: a <c>MERGE</c> statement specified an
    /// <c>UPDATE</c> or <c>DELETE</c> action in a <c>WHEN NOT MATCHED</c> /
    /// <c>WHEN NOT MATCHED BY TARGET</c> clause where only <c>INSERT</c> is
    /// legal. Probe-confirmed against SQL Server 2025 (2026-05-13): Class 15,
    /// State 1, exact wording verbatim.
    /// </summary>
    internal static SimulatedSqlException MergeUpdateNotAllowedInNotMatched() =>
        new("An action of type 'UPDATE' is not allowed in the 'WHEN NOT MATCHED' clause of a MERGE statement.", 10710, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 10711: a <c>MERGE</c> statement specified an
    /// <c>INSERT</c> action in a <c>WHEN MATCHED</c> or <c>WHEN NOT MATCHED
    /// BY SOURCE</c> clause where only <c>UPDATE</c> / <c>DELETE</c> is
    /// legal. Probe-confirmed against SQL Server 2025 (2026-05-13): Class
    /// 15, State 1, exact wording verbatim. The <paramref name="clauseType"/>
    /// is either <c>"WHEN MATCHED"</c> or <c>"WHEN NOT MATCHED BY SOURCE"</c>.
    /// </summary>
    internal static SimulatedSqlException MergeInsertNotAllowedInClause(string clauseType) =>
        new($"An action of type 'INSERT' is not allowed in the '{clauseType}' clause of a MERGE statement.", 10711, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 10714: a <c>MERGE</c> statement had more than
    /// one <c>WHEN NOT MATCHED [BY TARGET]</c> clause. Real SQL Server allows
    /// at most one INSERT branch (unlike <c>WHEN MATCHED</c> and <c>WHEN
    /// NOT MATCHED BY SOURCE</c>, both of which can have multiple
    /// AND-conditioned clauses). Probe-confirmed against SQL Server 2025
    /// (2026-05-13): Class 15, State 1, exact wording verbatim — note the
    /// idiosyncratic <c>"a 'INSERT' clause"</c> phrasing.
    /// </summary>
    internal static SimulatedSqlException MergeMultipleNotMatchedClauses() =>
        new("An action of type 'WHEN NOT MATCHED' cannot appear more than once in a 'INSERT' clause of a MERGE statement.", 10714, 15, 1);

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
    /// at compile time, so the simulator does too (distinct from the
    /// un-taken-branch deferred-name-resolution gap, where un-taken branches
    /// escape Msg 208).
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

    /// <summary>
    /// Mimics SQL Server error 10704: a no-arg <c>THROW;</c> was used outside
    /// any enclosing <c>CATCH</c> block. Probe-confirmed against SQL Server
    /// 2025 (2026-05-12): Class 15, State 1, exact wording verbatim. The
    /// re-raise form only makes sense inside a CATCH (where there's an
    /// in-flight error to re-raise); outside CATCH the parser surfaces this
    /// to nudge the user toward either inserting the statement into a CATCH
    /// or supplying the (number, message, state) parameter form.
    /// </summary>
    internal static SimulatedSqlException ThrowMustBeInsideCatch() =>
        new("To rethrow an error, a THROW statement must be used inside a CATCH block. Insert the THROW statement inside a CATCH block, or add error parameters to the THROW statement.", 10704, 15, 1) { TerminatesBatch = true };

    /// <summary>
    /// Constructs a <c>THROW &lt;number&gt;, &lt;message&gt;, &lt;state&gt;</c>-raised
    /// exception with the user-supplied number / message / state. Real SQL
    /// Server fixes the severity at <c>16</c> for the value form regardless
    /// of which <c>number</c> the user supplies (probe-confirmed against
    /// SQL Server 2025: <c>THROW 50001, 'custom', 7</c> reports Class 16
    /// State 7). The factory also serves the no-arg <c>THROW;</c> re-raise
    /// by reconstructing from the in-flight error's captured number /
    /// message / state.
    /// </summary>
    internal static SimulatedSqlException ThrowRaised(int number, string message, byte state) =>
        new(message, number, 16, state) { TerminatesBatch = true };

    /// <summary>
    /// Mimics SQL Server error 2787: a <c>RAISERROR</c> format string contains
    /// a specifier the runtime doesn't accept. Real SQL Server's RAISERROR
    /// printf-style formatter supports a fixed subset of C runtime specifiers
    /// (<c>%s %d %i %u %o %x %X %ld %li %I64d %I64i</c>); anything else (e.g.
    /// <c>%c</c>, <c>%p</c>, a trailing lone <c>%</c>) raises this. The
    /// <paramref name="spec"/> argument is the offending token verbatim, with
    /// the leading <c>%</c> included (probe-confirmed wording: <c>"Invalid
    /// format specification: '%c'."</c>). Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException RaiserrorInvalidFormatSpec(string spec) =>
        new($"Invalid format specification: '{spec}'.", 2787, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2786: a <c>RAISERROR</c> substitution argument's
    /// runtime type doesn't match the corresponding format specifier (e.g.
    /// <c>%d</c> with a string-typed arg, <c>%s</c> with an int, <c>%d</c>
    /// with a bigint — <c>%I64d</c> is the bigint specifier). Real SQL Server
    /// reports the 1-based <paramref name="paramIndex"/> in the message text
    /// (probe-confirmed: <c>"The data type of substitution parameter 1 does not
    /// match the expected type of the format specification."</c>). Class 16
    /// State 1.
    /// </summary>
    internal static SimulatedSqlException RaiserrorTypeMismatch(int paramIndex) =>
        new($"The data type of substitution parameter {paramIndex} does not match the expected type of the format specification.", 2786, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2747: a <c>RAISERROR</c> call supplied more
    /// than 20 substitution arguments. Probe-confirmed wording verbatim
    /// (<c>"Too many substitution parameters for RAISERROR. Cannot exceed 20
    /// substitution parameters."</c>). Class 16 State 1. The cap applies to
    /// arguments passed even when the format string has no specifiers — the
    /// 20-arg limit is structural, not driven by the specifier count.
    /// </summary>
    internal static SimulatedSqlException RaiserrorTooManySubstitutionParameters() =>
        new("Too many substitution parameters for RAISERROR. Cannot exceed 20 substitution parameters.", 2747, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2748: a <c>FORMATMESSAGE</c> (or RAISERROR)
    /// substitution argument has a data type that can never be a substitution
    /// parameter (<c>bit</c> / <c>decimal</c> / <c>money</c> / <c>float</c> /
    /// <c>datetime</c> / <c>uniqueidentifier</c> / etc. — only the integer
    /// family excluding <c>bit</c>, the string family, and the binary family
    /// are permitted). Real SQL Server echoes the type name and the 1-based
    /// parameter position (probe-confirmed verbatim: <c>"Cannot specify float
    /// data type (parameter 1) as a substitution parameter."</c>). Class 16
    /// State 1.
    /// </summary>
    internal static SimulatedSqlException SubstitutionParameterTypeNotAllowed(string typeName, int paramIndex) =>
        new($"Cannot specify {typeName} data type (parameter {paramIndex}) as a substitution parameter.", 2748, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2732: a <c>RAISERROR</c> call passed a numeric
    /// <c>msg_id</c> outside the valid user-defined range (must be 13000
    /// through 2147483647) or used the reserved value <c>50000</c> as a
    /// literal id (real SQL Server reserves 50000 for the synthesized id of
    /// the inline-message-string form, so passing it literally fails). Real
    /// SQL Server's text echoes the rejected id (probe-confirmed: <c>"Error
    /// number 50000 is invalid. The number must be from 13000 through
    /// 2147483647 and it cannot be 50000."</c>). Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException RaiserrorMsgIdInvalid(int msgId) =>
        new($"Error number {msgId} is invalid. The number must be from 13000 through 2147483647 and it cannot be 50000.", 2732, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 18054: a <c>RAISERROR</c> call passed a numeric
    /// <c>msg_id</c> in the valid user-defined range (13000-2147483647 excluding
    /// 50000) but no row matching that id exists in <c>sys.messages</c>. The
    /// simulator hasn't modeled the <c>sys.messages</c> registry or
    /// <c>sp_addmessage</c>, so every non-50000 numeric msg_id surfaces this
    /// (probe-confirmed against SQL Server 2025 with the same wording for
    /// unregistered ids: <c>"Error 60000, severity 16, state 1 was raised, but
    /// no message with that error number was found in sys.messages. If error
    /// is larger than 50000, make sure the user-defined message is added using
    /// sp_addmessage."</c>). Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException RaiserrorMsgIdNotFound(int msgId, byte severity, byte state) =>
        new($"Error {msgId}, severity {severity}, state {state} was raised, but no message with that error number was found in sys.messages. If error is larger than 50000, make sure the user-defined message is added using sp_addmessage.", 18054, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2754: a <c>RAISERROR</c> call specified
    /// severity &gt; 18 from a non-sysadmin connection (real SQL Server gates
    /// the high-severity range behind sysadmin role + <c>WITH LOG</c>). The
    /// simulator has no principal model and matches the probe's non-sysadmin
    /// behavior here — apps targeting real SQL Server from a non-sysadmin
    /// connection see the same wall. Probe-confirmed wording verbatim
    /// (<c>"Error severity levels greater than 18 can only be specified by
    /// members of the sysadmin role, using the WITH LOG option."</c>). Class
    /// 16 State 1.
    /// </summary>
    internal static SimulatedSqlException RaiserrorSeverityRequiresSysadmin() =>
        new("Error severity levels greater than 18 can only be specified by members of the sysadmin role, using the WITH LOG option.", 2754, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2778: a <c>RAISERROR</c> call used the
    /// <c>WITH LOG</c> option from a non-sysadmin connection. Probe-confirmed
    /// wording verbatim (<c>"Only System Administrator can specify WITH LOG
    /// option for RAISERROR command."</c>). Class 16 State 2 — note the
    /// state is 2, not 1 like its severity-gated sibling (Msg 2754).
    /// </summary>
    internal static SimulatedSqlException RaiserrorLogRequiresSysadmin() =>
        new("Only System Administrator can specify WITH LOG option for RAISERROR command.", 2778, 16, 2);

    /// <summary>
    /// Constructs the <see cref="SimulatedSqlException"/> a successful
    /// <c>RAISERROR</c> raises for severities ≥ 11 (catchable by TRY/CATCH).
    /// Always uses error number <c>50000</c> (the inline-message-string form's
    /// synthesized id). Class is the supplied severity; state is the supplied
    /// state. Sev ≤ 10 is the informational path — those don't throw; the
    /// caller writes <c>@@ERROR</c> directly when <c>WITH SETERROR</c> is set
    /// and discards the message otherwise.
    /// </summary>
    internal static SimulatedSqlException RaiserrorRaised(string message, byte severity, byte state) =>
        new(message, 50000, severity, state);
}
