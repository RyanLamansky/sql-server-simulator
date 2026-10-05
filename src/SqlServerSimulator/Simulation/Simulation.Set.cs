using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Dispatches the <c>SET</c> shapes: <c>SET @v = expr</c> (variable
    /// assignment, <see cref="TryParseSetVariable"/>), <c>SET IDENTITY_INSERT
    /// t ON|OFF</c> (<see cref="TryParseSetIdentityInsert"/>), and the
    /// closed-list session / connection / planner option family
    /// (<see cref="TryParseSetSessionOption"/>), of which the options with an
    /// effect are handled by name there and the rest accepted and discarded.
    /// <paramref name="assignsVariable"/> tells the variable form apart, which
    /// leaves <c>@@ROWCOUNT</c> at 1 where the others reset it. Returning
    /// <c>false</c> falls through to the caller's <see cref="SimulatedSqlException.SyntaxErrorNear(ParserContext)"/>
    /// (Msg 102); explicit Msg 195 fires when an unrecognized option name
    /// appears followed by a recognizable value (ON/OFF/literal).
    /// </summary>
    private static bool TryParseSet(ParserContext context, out bool assignsVariable)
    {
        var afterSet = context.GetNextRequired();
        assignsVariable = afterSet is AtPrefixedString;
        // What the statement names in its DONE (probed 2026-09-28 against SQL
        // Server 2025): a variable assignment is a SELECT-kind statement
        // counting one row; the option forms refine it below.
        if (assignsVariable)
        {
            context.Batch.CurrentStatement.DoneKind = StatementDoneKind.Select;
            context.Batch.CurrentStatement.DoneCount = 1;
        }
        return afterSet switch
        {
            ReservedKeyword { Keyword: Keyword.Identity_Insert } => TryParseSetIdentityInsert(context),
            AtPrefixedString variableToken => TryParseSetVariable(context, variableToken),
            // `SET @@x = …` names a variable no DECLARE can make, and a built-in
            // @@ function no SET can assign (probed 2026-10-02 and 2026-10-04
            // against SQL Server 2025: SET @@ROWCOUNT is Msg 102 at it).
            DoubleAtPrefixedString { IsBuiltIn: true } builtIn => throw SimulatedSqlException.SyntaxErrorNear(builtIn),
            DoubleAtPrefixedString global => throw SimulatedSqlException.MustDeclareSetTarget(global.ErrorText[1..]),
            _ => TryParseSetSessionOption(context, afterSet),
        };
    }

    /// <summary>
    /// Parses <c>SET &lt;option&gt; ...</c> for every option in the closed
    /// accept-list. Includes the multi-option comma form
    /// (<c>SET ANSI_NULLS, QUOTED_IDENTIFIER, … ON</c>) restricted to bool-
    /// shaped options, plus the multi-word
    /// <c>SET TRANSACTION ISOLATION LEVEL …</c> and
    /// <c>SET STATISTICS {IO|TIME|XML|PROFILE} ON|OFF</c> sub-forms.
    /// Unrecognized option name followed by a recognizable value → Msg 195
    /// (probe-confirmed verbatim).
    /// </summary>
    private static bool TryParseSetSessionOption(ParserContext context, Token afterSet)
    {
        // Multi-word sub-forms whose leading token is a ReservedKeyword.
        switch (afterSet)
        {
            case ReservedKeyword { Keyword: Keyword.Transaction }:
                context.Batch.CurrentStatement.DoneKind = StatementDoneKind.SetValue;
                return TryParseSetTransactionIsolationLevel(context);
            case ReservedKeyword { Keyword: Keyword.Statistics }:
                return TryParseSetStatistics(context);
            case ReservedKeyword { Keyword: Keyword.Offsets }:
                return TryParseSetOffsets(context);
            // ReservedKeyword options that take an integer (ROWCOUNT / TEXTSIZE).
            // They tokenize as ReservedKeyword because the words appear in the
            // T-SQL reserved set; the SET parser accepts them by Keyword check.
            case ReservedKeyword { Keyword: var intOption and (Keyword.RowCount or Keyword.TextSize) }:
                context.Batch.CurrentStatement.DoneKind = intOption == Keyword.TextSize ? StatementDoneKind.SetTextSize : StatementDoneKind.SetRowCount;
                return ConsumeIntegerValue(context, applyTextSize: intOption == Keyword.TextSize);
        }

        if (afterSet is not UnquotedString unquoted)
            return false;

        // FIPS_FLAGGER parses and is discarded (probed 2026-10-04 against SQL
        // Server 2025), as is OFFSETS, a reserved keyword dispatched above.
        if (unquoted.Value.Equals("FIPS_FLAGGER", StringComparison.OrdinalIgnoreCase))
            return TryParseSetFipsFlagger(context);

        if (!RecognizedOptions.TryGetValue(unquoted.Value, out var firstKind))
            return TryRaiseUnrecognizedSetOption(context, unquoted);

        var firstName = unquoted.Value;
        context.MoveNextRequired();
        // An on/off option given any other value is Msg 102 state 4 naming
        // the option (probed 2026-10-02 against SQL Server 2025).
        if (firstKind == SetOptionKind.OnOff && context.Token is not (ReservedKeyword { Keyword: Keyword.On or Keyword.Off } or Operator { Character: ',' }))
            throw SimulatedSqlException.SyntaxErrorNear(unquoted, state: 4);

        // Multi-option comma form is OnOff-only: SET opt1, opt2, ... ON|OFF.
        if (firstKind == SetOptionKind.OnOff && context.Token is Operator { Character: ',' })
        {
            var affectsQuotedIdentifier = IsQuotedIdentifierOption(firstName);
            var sessionOptionNames = new List<string> { firstName };
            while (context.Token is Operator { Character: ',' })
            {
                if (context.GetNextRequired() is not UnquotedString next)
                    return false;
                if (!RecognizedOptions.TryGetValue(next.Value, out var nextKind) || nextKind != SetOptionKind.OnOff)
                    throw SimulatedSqlException.UnrecognizedSetOption(next.Value, onOff: true);
                affectsQuotedIdentifier |= IsQuotedIdentifierOption(next.Value);
                sessionOptionNames.Add(next.Value);
                context.MoveNextRequired();
            }
            if (context.Token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } commaOnOff)
                return false;
            // A SHOWPLAN switch must stand alone (probed 2026-10-04 against
            // SQL Server 2025).
            if (sessionOptionNames.Exists(IsShowplanOption))
                throw SimulatedSqlException.ShowplanNotAlone(state: 1).PinLine(0);
            var commaOn = commaOnOff.Keyword == Keyword.On;
            context.Batch.CurrentStatement.DoneKind = commaOn ? StatementDoneKind.SetOptionOn : StatementDoneKind.SetOptionOff;
            if (affectsQuotedIdentifier)
                ApplyQuotedIdentifierOption(context, commaOn);
            // Every listed option shares the trailing ON|OFF value and takes
            // effect as it would alone (probed 2026-10-04 against SQL Server
            // 2025: SET NOCOUNT, XACT_ABORT ON sets both).
            // NOEXEC applies last, so the options listed with it still take
            // effect (probed 2026-10-04: SET NOEXEC, NOCOUNT ON sets both).
            foreach (var listed in sessionOptionNames.OrderBy(listed => listed.Equals("NOEXEC", StringComparison.OrdinalIgnoreCase)))
                ApplyOnOffOption(context, listed, commaOn);
            if (!sessionOptionNames.TrueForAll(IsModuleIgnoredOption))
                FunctionBodyShape.NoteSideEffect(context.Batch, commaOn ? "SET OPTION ON" : "SET OPTION OFF", FunctionBodyShape.StatementOperatorState);
            return true;
        }

        // Integer-shaped options accept a signed value: SMO's scripting
        // preamble sends `SET LOCK_TIMEOUT -1`, where `-1` tokenizes as an
        // Operator('-') followed by the Numeric.
        var negativeInteger = false;
        if (firstKind is SetOptionKind.Integer or SetOptionKind.IntegerOrIdent or SetOptionKind.Binary && context.Token is Operator { Character: '-' })
        {
            negativeInteger = true;
            context.MoveNextRequired();
        }

        // The value-taking options' refusals of a value their grammar has no
        // slot for (probed 2026-10-04 against SQL Server 2025): LOCK_TIMEOUT
        // takes an int literal alone and QUERY_GOVERNOR_COST_LIMIT a number
        // alone, each anything else Msg 102 state 3 at the option's name;
        // DATEFIRST given a string or NULL is Msg 2743 state 3, and DATEFORMAT
        // or LANGUAGE given a number or NULL Msg 2743 state 2, each ending only
        // the statement.
        if (firstName.Equals("LOCK_TIMEOUT", StringComparison.OrdinalIgnoreCase)
            && !(context.Token is Numeric { Value: { IsNull: false, Type: var lockType } } && lockType == SqlType.Int32))
        {
            throw SimulatedSqlException.SyntaxErrorNear("LOCK_TIMEOUT", state: 3);
        }
        if (firstName.Equals("QUERY_GOVERNOR_COST_LIMIT", StringComparison.OrdinalIgnoreCase) && context.Token is not Numeric)
            throw SimulatedSqlException.SyntaxErrorNear("QUERY_GOVERNOR_COST_LIMIT", state: 3);
        if (firstName.Equals("DATEFIRST", StringComparison.OrdinalIgnoreCase) && context.Token is Literal or ReservedKeyword { Keyword: Keyword.Null })
        {
            if (context.Batch.IsSkipping)
                return true;
            throw SimulatedSqlException.DateFirstRequiresInteger();
        }
        if ((firstName.Equals("DATEFORMAT", StringComparison.OrdinalIgnoreCase) || firstName.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase))
            && context.Token is Numeric or ReservedKeyword { Keyword: Keyword.Null })
        {
            if (context.Batch.IsSkipping)
                return true;
            throw SimulatedSqlException.OptionRequiresString(firstName.ToUpperInvariant());
        }
        if (firstName.Equals("DEADLOCK_PRIORITY", StringComparison.OrdinalIgnoreCase) && context.Token is ReservedKeyword { Keyword: Keyword.Null })
        {
            if (context.Batch.IsSkipping)
                return true;
            throw SimulatedSqlException.DeadlockPriorityInvalid();
        }
        if (firstName.Equals("CONTEXT_INFO", StringComparison.OrdinalIgnoreCase))
            return SetContextInfo(context, negativeInteger);

        if (!ConsumeValueForKind(context, firstKind))
            return false;

        if (IsShowplanOption(firstName))
            RequireShowplanAlone(context, context.Token is ReservedKeyword { Keyword: Keyword.On });

        // Every SET form but `SET @v = …` is a side-effecting operator inside a
        // function body; real names the boolean toggles 'SET OPTION ON' / 'OFF'
        // and lumps the value-taking ones under 'SET COMMAND'. A function body
        // may SET the two options every module captures at CREATE, which it
        // ignores as a procedure does (probed 2026-10-04 against SQL Server
        // 2025).
        if (!IsModuleIgnoredOption(firstName))
        {
            FunctionBodyShape.NoteSideEffect(
                context.Batch,
                firstKind != SetOptionKind.OnOff ? "SET COMMAND"
                    : context.Token is ReservedKeyword { Keyword: Keyword.On } ? "SET OPTION ON" : "SET OPTION OFF",
                FunctionBodyShape.StatementOperatorState);
        }

        // QUOTED_IDENTIFIER and PARSEONLY, which apply while the batch
        // compiles, send no DONE of their own.
        context.Batch.CurrentStatement.DoneKind = firstKind != SetOptionKind.OnOff ? StatementDoneKind.SetValue
            : firstName.Equals("QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase) || firstName.Equals("PARSEONLY", StringComparison.OrdinalIgnoreCase) ? StatementDoneKind.NoDone
            : context.Token is ReservedKeyword { Keyword: Keyword.On } ? StatementDoneKind.SetOptionOn
            : StatementDoneKind.SetOptionOff;

        if (IsQuotedIdentifierOption(firstName) && context.Token is ReservedKeyword { Keyword: var qiOnOff })
            ApplyQuotedIdentifierOption(context, qiOnOff == Keyword.On);

        if (firstKind == SetOptionKind.OnOff && context.Token is ReservedKeyword { Keyword: var onOff })
            ApplyOnOffOption(context, firstName, onOff == Keyword.On);

        // DATEFIRST names the weekday the week starts on, in 1..7 — read by
        // @@DATEFIRST, by DATEPART / DATENAME's weekday and week units and by
        // DATETRUNC(week, …). Real refuses a non-int parameter with Msg 2743
        // (a literal past the int range and a bigint variable alike) and
        // anything outside 1..7 with Msg 2742 echoing the value, a NULL
        // variable rendering as 0.
        if (firstName.Equals("DATEFIRST", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping)
        {
            var requested = ReadIntegerOptionValue(context) switch
            {
                null => throw SimulatedSqlException.DateFirstRequiresInteger(),
                var v => negativeInteger ? -v.Value : v.Value,
            };
            context.Connection.DateFirst = requested is >= 1 and <= 7
                ? (byte)requested
                : throw SimulatedSqlException.DateFirstOutOfRange(requested);
            context.Batch.DateFirstSetExplicitly = true;
        }

        // DATEFORMAT names the order a numeric date string's parts read in,
        // from a bare name, a string literal or a variable, case aside.
        if (firstName.Equals("DATEFORMAT", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping
            && ReadIdentifierOptionValue(context) is { } dateFormatName)
        {
            context.Connection.DateFormat = DateOrder.Find(dateFormatName) ?? throw SimulatedSqlException.DateFormatInvalid(dateFormatName);
            context.Batch.DateFormatSetExplicitly = true;
        }

        // LANGUAGE carries the session's language for @@LANGUAGE / @@LANGID and
        // — the load-bearing half — implicitly moves DATEFIRST to the one the
        // language declares, unless this batch has already set DATEFIRST
        // itself. The precedence is per batch, not per session: `SET DATEFIRST
        // 3` then `SET LANGUAGE German` in one batch stays 3, while the same
        // pair split across two batches ends at German's own 1 (both
        // probe-confirmed). DATEFORMAT moves to the language's own order under
        // the same per-batch precedence, independently of DATEFIRST's; the
        // message language doesn't follow, so diagnostics stay English.
        if (firstName.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping
            && ReadIdentifierOptionValue(context) is { } languageName)
        {
            if (Language.Find(languageName) is { } language)
            {
                context.Connection.Language = language;
                // A procedure, trigger or dynamic batch changes it silently
                // (probed 2026-10-04 against SQL Server 2025).
                if (context.Batch.ProcFrame is null && context.Batch.TriggerFrame is null)
                    context.Connection.PendingMessages.Enqueue(SimulatedSqlException.LanguageChangedMessage(context.Batch, language));
                if (!context.Batch.DateFirstSetExplicitly)
                    context.Connection.DateFirst = language.DateFirst;
                if (!context.Batch.DateFormatSetExplicitly)
                    context.Connection.DateFormat = DateOrder.Find(language.DateFormat)!;
            }
            else if (context.Batch.TryFrameDepth == 0)
            {
                // Real swallows the failed SET LANGUAGE inside a TRY block
                // outright — no error raised and no CATCH entered, the
                // statement simply no-ops and the body carries on
                // (probe-confirmed, dynamic SQL included). Outside one it is
                // an ordinary statement-terminating Msg 2740.
                throw SimulatedSqlException.LanguageNotFound(languageName);
            }
        }

        // LOCK_TIMEOUT is the one Integer-shape option that has semantic
        // effect — it drives lock-acquisition wait via
        // SimulatedDbConnection.LockTimeoutMillis. Every other Integer /
        // Identifier / Binary option parses-and-discards (the simulator
        // doesn't model the underlying behavior). Probe-confirmed default
        // is -1 (wait forever); positive N = wait up to N ms; 0 = fail-fast.
        if (firstName.Equals("LOCK_TIMEOUT", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping)
        {
            if (context.Token is Numeric { Value: { IsNull: false, Type: var t } literal } && t == SqlType.Int32)
                context.Connection.LockTimeoutMillis = negativeInteger ? -literal.AsInt32 : literal.AsInt32;
        }

        if (firstName.Equals("DEADLOCK_PRIORITY", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping)
            context.Connection.DeadlockPriority = ReadDeadlockPriority(context, negativeInteger);

        return true;
    }

    private static bool IsShowplanOption(string name) =>
        name.Equals("SHOWPLAN_ALL", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SHOWPLAN_TEXT", StringComparison.OrdinalIgnoreCase)
        || name.Equals("SHOWPLAN_XML", StringComparison.OrdinalIgnoreCase);

    private static bool IsModuleIgnoredOption(string name) =>
        name.Equals("ANSI_NULLS", StringComparison.OrdinalIgnoreCase)
        || name.Equals("QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A <c>SET SHOWPLAN_*</c> switch inside a module refuses the
    /// <c>CREATE</c> with Msg 1067 state 1 at line 0, and turning one on must
    /// be the batch's only statement: after another it is Msg 1067 state 1 at
    /// line 0, ahead of one state 2 at its own line, either as the batch
    /// compiles, so none of it runs; turning one off may sit beside anything
    /// (probed 2026-10-04 against SQL Server 2025). Cursor on the <c>ON</c> /
    /// <c>OFF</c>.
    /// </summary>
    private static void RequireShowplanAlone(ParserContext context, bool on)
    {
        var batch = context.Batch;
        if (batch.UdfFrame is not null || batch.TriggerFrame is not null || batch.ProcFrame is { IsDynamicSql: false })
            throw SimulatedSqlException.ShowplanNotAlone(state: 1).PinLine(0);
        if (!on)
            return;
        if (batch.BlockDepth > 0 || batch.HasDispatchedStatement)
            throw SimulatedSqlException.ShowplanNotAlone(state: 1).PinLine(0);
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        while (next is Operator { Character: ';' })
            next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        if (next is not null)
            throw SimulatedSqlException.ShowplanNotAlone(state: 2);
    }

    /// <summary>
    /// <c>SET OFFSETS keyword [, …] ON | OFF</c>, each keyword one of
    /// <c>SELECT</c> / <c>FROM</c> / <c>ORDER</c> / <c>TABLE</c> /
    /// <c>PROCEDURE</c> / <c>STATEMENT</c> / <c>PARAM</c> / <c>EXECUTE</c>.
    /// </summary>
    private static bool TryParseSetOffsets(ParserContext context)
    {
        while (true)
        {
            var keyword = context.GetNextRequired();
            var word = keyword switch
            {
                ReservedKeyword reserved => reserved.Keyword.ToString(),
                UnquotedString text => text.Value,
                _ => null,
            };
            if (word is null || !BuiltInToken.EqualsAny(word, "EXECUTE", "FROM", "ORDER", "PARAM", "PROCEDURE", "SELECT", "STATEMENT", "TABLE"))
                throw SimulatedSqlException.SyntaxErrorNear(keyword);
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        context.Batch.CurrentStatement.DoneKind = StatementDoneKind.SetValue;
        return context.Token is ReservedKeyword { Keyword: Keyword.On or Keyword.Off };
    }

    /// <summary><c>SET FIPS_FLAGGER 'ENTRY' | 'FULL' | 'INTERMEDIATE' | OFF</c>; its warnings aren't sent.</summary>
    private static bool TryParseSetFipsFlagger(ParserContext context)
    {
        var value = context.GetNextRequired();
        if (value is ReservedKeyword { Keyword: Keyword.Off }
            || (value is Literal { Value: { IsNull: false } level } && BuiltInToken.EqualsAny(level.AsString, "ENTRY", "FULL", "INTERMEDIATE")))
        {
            context.Batch.CurrentStatement.DoneKind = StatementDoneKind.SetValue;
            return true;
        }
        throw SimulatedSqlException.SyntaxErrorNear(value, state: 3);
    }

    /// <summary>
    /// Applies one on/off option as it runs, alone or in a comma list: the
    /// session state <see cref="RecordSessionStateOption"/> keeps, and the
    /// options with an effect of their own — <c>XACT_ABORT</c> (a body's SET
    /// binds for the body, <see cref="SimulatedDbConnection.SessionOptionScope"/>
    /// restoring the caller's), <c>FMTONLY</c>, <c>NOCOUNT</c> (a statement's
    /// DONE then omits its row count, the identity-retrieval pattern ODBC
    /// drivers depend on), <c>NOEXEC</c> (which applies as it runs, and whose
    /// OFF is the one statement still running under it, probed 2026-09-28) and
    /// <c>PARSEONLY</c>, which applies while the batch parses
    /// (<see cref="ScanParseTimeOptions"/>) and a module body refuses.
    /// </summary>
    private static void ApplyOnOffOption(ParserContext context, string name, bool on)
    {
        RecordSessionStateOption(context, name, on);
        var batch = context.Batch;
        if (name.Equals("NOEXEC", StringComparison.OrdinalIgnoreCase))
        {
            if (!batch.SkipsForControlFlow)
                context.Connection.NoExec = batch.NoExecActive = on;
            return;
        }
        if (name.Equals("PARSEONLY", StringComparison.OrdinalIgnoreCase))
        {
            if (batch.UdfFrame is not null || batch.TriggerFrame is not null || batch.ProcFrame is { IsDynamicSql: false })
                throw SimulatedSqlException.ParseOnlyInModule();
            return;
        }
        if (batch.IsSkipping)
            return;
        if (name.Equals("XACT_ABORT", StringComparison.OrdinalIgnoreCase))
            context.Connection.XactAbort = on;
        else if (name.Equals("FMTONLY", StringComparison.OrdinalIgnoreCase))
            context.Connection.FmtOnly = on;
        else if (name.Equals("NOCOUNT", StringComparison.OrdinalIgnoreCase))
            context.Connection.NoCount = on || context.Connection.RunningLogonTriggers;
    }

    /// <summary>
    /// <c>SET CONTEXT_INFO</c>: stores up to 128 bytes for <c>CONTEXT_INFO()</c>
    /// and the session DMVs, from a binary literal, a number (its binary
    /// form, a sign allowed) or a variable of any type but a string; a NULL,
    /// a string or a value past 128 bytes is Msg 2743, ending only the
    /// statement (probed 2026-09-28 and 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static bool SetContextInfo(ParserContext context, bool negative)
    {
        var batch = context.Batch;
        SqlValue value;
        switch (context.Token)
        {
            case AtPrefixedString variable:
                value = batch.GetVariableSlot(variable.Value).Value;
                break;
            case Literal { Value: var literal }:
                value = literal;
                break;
            case Numeric { Value: var number }:
                value = negative ? NegateLiteral(number) : number;
                break;
            case ReservedKeyword { Keyword: Keyword.Null }:
                value = SqlValue.Null(SqlType.Varbinary);
                break;
            default:
                return false;
        }
        FunctionBodyShape.NoteSideEffect(batch, "SET COMMAND", FunctionBodyShape.StatementOperatorState);
        context.Batch.CurrentStatement.DoneKind = StatementDoneKind.SetValue;
        if (batch.IsSkipping)
            return true;
        if (value.IsNull || SqlType.IsStringCategory(value.Type))
            throw SimulatedSqlException.ContextInfoRequiresBinary();
        var bytes = value.CoerceTo(SqlType.Varbinary).AsBytes;
        if (bytes.Length > 128)
            throw SimulatedSqlException.ContextInfoRequiresBinary();
        context.Connection.ContextInfo = bytes;
        return true;
    }

    /// <summary>
    /// True when <paramref name="optionName"/> is a SET option that carries
    /// the <c>QUOTED_IDENTIFIER</c> semantic — the option itself, or
    /// <c>ANSI_DEFAULTS</c>, whose bundle includes it (probe-confirmed:
    /// <c>SET ANSI_DEFAULTS OFF</c> flips <c>"…"</c> to string-literal
    /// tokenization).
    /// </summary>
    private static bool IsQuotedIdentifierOption(string optionName) =>
        optionName.Equals("QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase)
        || optionName.Equals("ANSI_DEFAULTS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Applies a parsed <c>SET QUOTED_IDENTIFIER</c> (or <c>ANSI_DEFAULTS</c>)
    /// with SQL Server's parse-time scoping, probe-confirmed against SQL
    /// Server 2025:
    /// <list type="bullet">
    /// <item>Runs at parse regardless of control flow — deliberately NOT
    /// gated on <c>IsSkipping</c>, because a SET inside a never-taken IF
    /// branch still applies to everything after it in the batch AND persists
    /// to the session.</item>
    /// <item>Top-level batches flip both the in-flight tokenizer flag and the
    /// session setting.</item>
    /// <item>Dynamic SQL (<c>EXEC('…')</c> / <c>sp_executesql</c>) flips only
    /// its own batch's flag — the change reverts when the dynamic batch
    /// ends.</item>
    /// <item>Procedure / function / trigger bodies ignore the statement
    /// entirely (the documented "ignored in a stored procedure" rule).</item>
    /// </list>
    /// </summary>
    private static void ApplyQuotedIdentifierOption(ParserContext context, bool on)
    {
        var batch = context.Batch;
        if (batch.UdfFrame is not null || batch.TriggerFrame is not null || batch.ProcFrame is { IsDynamicSql: false })
            return;
        context.QuotedIdentifiers = on;
        if (batch.ProcFrame is null)
            context.Connection.QuotedIdentifiers = on;
    }

    /// <summary>
    /// Records an on/off session option onto the connection as the <c>SET</c>
    /// runs, not while it parses: the batch's compile walk and a never-taken IF
    /// branch leave it alone, so a statement ahead of the SET in its batch
    /// still sees the old setting — its <c>NULL = NULL</c>, its string
    /// <c>+</c>, <c>SESSIONPROPERTY</c> and <c>@@OPTIONS</c> alike (probed
    /// 2026-09-26 against SQL Server 2025). Inside a procedure, trigger or
    /// dynamic-SQL body the option applies for the body and reverts when it
    /// returns (<see cref="SimulatedDbConnection.SessionOptionScope"/>), save
    /// <c>ANSI_NULLS</c>, whose SET a procedure or trigger body ignores as it
    /// does <c>QUOTED_IDENTIFIER</c>'s, the module running under the setting
    /// captured when it was created (probed 2026-09-28). <c>ANSI_DEFAULTS</c>
    /// sets its seven options together. Options with no session state here
    /// fall through the default arm — including XACT_ABORT, whose write
    /// happens in the caller.
    /// </summary>
    private static void RecordSessionStateOption(ParserContext context, string optionName, bool on)
    {
        var batch = context.Batch;
        if (batch.IsSkipping || batch.UdfFrame is not null)
            return;
        var connection = context.Connection;
        var moduleBody = batch.TriggerFrame is not null || batch.ProcFrame is { IsDynamicSql: false };
        Span<char> upper = stackalloc char[optionName.Length];
        _ = optionName.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "ANSI_DEFAULTS":
                if (!moduleBody)
                    connection.AnsiNulls = on;
                connection.AnsiPadding = on;
                connection.AnsiWarnings = on;
                connection.CursorCloseOnCommit = on;
                connection.ImplicitTransactions = on;
                connection.AnsiNullDefaultOn = on;
                if (on)
                    connection.AnsiNullDefaultOff = false;
                break;
            case "ANSI_NULLS":
                if (!moduleBody)
                    connection.AnsiNulls = on;
                break;
            case "ANSI_NULL_DFLT_OFF":
                connection.AnsiNullDefaultOff = on;
                if (on)
                    connection.AnsiNullDefaultOn = false;
                break;
            case "ANSI_NULL_DFLT_ON":
                connection.AnsiNullDefaultOn = on;
                if (on)
                    connection.AnsiNullDefaultOff = false;
                break;
            case "ANSI_PADDING":
                connection.AnsiPadding = on;
                break;
            case "ANSI_WARNINGS":
                connection.AnsiWarnings = on;
                break;
            case "ARITHABORT":
                connection.Arithabort = on;
                break;
            case "ARITHIGNORE":
                connection.ArithIgnore = on;
                break;
            case "CONCAT_NULL_YIELDS_NULL":
                connection.ConcatNullYieldsNull = on;
                break;
            case "CURSOR_CLOSE_ON_COMMIT":
                connection.CursorCloseOnCommit = on;
                break;
            case "FORCEPLAN":
                RecordListedOnlyOption(context, ListedOnlyOptions.ForcePlan, on);
                break;
            case "IMPLICIT_TRANSACTIONS":
                connection.ImplicitTransactions = on;
                break;
            case "NO_BROWSETABLE":
                if (batch.ProcFrame is null && batch.TriggerFrame is null)
                    connection.NoBrowseTable = on;
                break;
            case "NUMERIC_ROUNDABORT":
                connection.NumericRoundabort = on;
                break;
            case "REMOTE_PROC_TRANSACTIONS":
                RecordListedOnlyOption(context, ListedOnlyOptions.RemoteProcTransactions, on);
                break;
            default:
                break;
        }
    }

    /// <summary>Records a <see cref="ListedOnlyOptions"/> switch as it runs.</summary>
    private static void RecordListedOnlyOption(ParserContext context, ListedOnlyOptions option, bool on)
    {
        if (context.Batch.IsSkipping || context.Batch.UdfFrame is not null)
            return;
        var connection = context.Connection;
        connection.ListedOnlyOptions = on ? connection.ListedOnlyOptions | option : connection.ListedOnlyOptions & ~option;
    }

    /// <summary>
    /// Distinguishes Msg 195 (clearly meant as a SET option — unknown name
    /// followed by a recognizable ON/OFF/value) from Msg 102 (unknown name
    /// followed by nothing recognizable — propagates through the caller's
    /// fallthrough). Probe-confirmed split: <c>SET BANANA ON</c> raises 195,
    /// <c>SET BANANA</c> (no trailing tokens) raises 102.
    /// </summary>
    private static bool TryRaiseUnrecognizedSetOption(ParserContext context, UnquotedString unrecognized)
    {
        // Peek one token past the unknown name with a checkpoint/restore so
        // a Msg 102 fallthrough reports the offending name verbatim
        // (probe-confirmed: `SET BANANA` → `Incorrect syntax near 'BANANA'`).
        // Recognizable value-like next-tokens raise the dedicated Msg 195.
        var nameValue = unrecognized.Value;
        var checkpoint = context.SaveCheckpoint();
        var peeked = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        if (peeked is ReservedKeyword { Keyword: Keyword.On or Keyword.Off }
            or Numeric or Literal or UnquotedString or DelimitedIdentifier or Operator { Character: '-' or '+' })
        {
            throw SimulatedSqlException.UnrecognizedSetOption(nameValue, onOff: peeked is ReservedKeyword);
        }
        throw SimulatedSqlException.SyntaxErrorNear(unrecognized);
    }

    /// <summary>
    /// <c>SET TRANSACTION ISOLATION LEVEL {READ UNCOMMITTED | READ COMMITTED |
    /// REPEATABLE READ | SNAPSHOT | SERIALIZABLE}</c>. Token shapes are mixed
    /// (READ is reserved; SNAPSHOT/SERIALIZABLE/REPEATABLE/UNCOMMITTED/COMMITTED
    /// are not), so the parser accepts 1–2 trailing tokens after LEVEL by
    /// token-class rather than enumerated keyword.
    /// </summary>
    private static bool TryParseSetTransactionIsolationLevel(ParserContext context)
    {
        if (context.GetNextRequired() is not UnquotedString isolation
            || !isolation.Value.Equals("ISOLATION", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (context.GetNextRequired() is not UnquotedString level
            || !level.Value.Equals("LEVEL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // The level itself: 1 token (SNAPSHOT, SERIALIZABLE) or 2 tokens
        // (READ UNCOMMITTED, READ COMMITTED, REPEATABLE READ).
        context.MoveNextRequired();
        var (newLevel, consumeAnother) = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Read } =>
                ResolveReadIsolationLevel(context),
            UnquotedString { Value: var name } when name.Equals("REPEATABLE", StringComparison.OrdinalIgnoreCase) =>
                (System.Data.IsolationLevel.RepeatableRead, true),
            UnquotedString { Value: var name } when name.Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase) =>
                (System.Data.IsolationLevel.Snapshot, false),
            UnquotedString { Value: var name } when name.Equals("SERIALIZABLE", StringComparison.OrdinalIgnoreCase) =>
                (System.Data.IsolationLevel.Serializable, false),
            // Any other word is a syntax error at it (probed 2026-10-02
            // against SQL Server 2025).
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (consumeAnother)
            context.MoveNextRequired();
        // An un-taken branch's SET leaves the level alone (probed 2026-10-03
        // against SQL Server 2025).
        if (!context.Batch.IsSkipping)
            context.Batch.Connection.SessionIsolationLevel = newLevel;
        FunctionBodyShape.NoteSideEffect(context.Batch, "SET TRANSACTION ISOLATION LEVEL", FunctionBodyShape.StatementOperatorState);
        return true;
    }

    /// <summary>
    /// Peeks the token after <c>READ</c> to decide between
    /// <c>READ UNCOMMITTED</c> and <c>READ COMMITTED</c>; defaults to
    /// READ COMMITTED if the peek isn't a recognizable trailer (parser
    /// continues to consume the trailer either way for the canonical form).
    /// </summary>
    private static (System.Data.IsolationLevel Level, bool ConsumeAnother) ResolveReadIsolationLevel(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        if (context.Token is UnquotedString { Value: var name })
        {
            if (name.Equals("UNCOMMITTED", StringComparison.OrdinalIgnoreCase))
            {
                context.RestoreCheckpoint(checkpoint);
                return (System.Data.IsolationLevel.ReadUncommitted, true);
            }
            if (name.Equals("COMMITTED", StringComparison.OrdinalIgnoreCase))
            {
                context.RestoreCheckpoint(checkpoint);
                return (System.Data.IsolationLevel.ReadCommitted, true);
            }
        }
        context.RestoreCheckpoint(checkpoint);
        return (System.Data.IsolationLevel.ReadCommitted, true);
    }

    /// <summary>
    /// <c>SET STATISTICS {IO | TIME | XML | PROFILE} ON|OFF</c>. The
    /// sub-option (IO/TIME/XML/PROFILE) tokenizes as <c>UnquotedString</c>;
    /// neither IO nor PROFILE is in the reserved list, and TIME is a
    /// <see cref="ContextualKeyword"/> but the value position accepts it as
    /// a bare identifier without semantic dispatch.
    /// </summary>
    private static bool TryParseSetStatistics(ParserContext context)
    {
        // A comma list shares the trailing ON | OFF (probed 2026-10-04 against
        // SQL Server 2025: SET STATISTICS IO, TIME ON).
        var names = new List<string>(2);
        while (true)
        {
            if (context.GetNextRequired() is not StringToken subOption)
                return false;
            names.Add(subOption.ToString());
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }
        if (context.Token is not ReservedKeyword { Keyword: var statisticsOnOff and (Keyword.On or Keyword.Off) })
            return false;
        context.Batch.CurrentStatement.DoneKind = statisticsOnOff == Keyword.On ? StatementDoneKind.SetStatisticsOn : StatementDoneKind.SetStatisticsOff;
        var on = statisticsOnOff == Keyword.On;
        foreach (var name in names)
        {
            if (BuiltInToken.Equals(name, "IO") || BuiltInToken.Equals(name, "TIME"))
            {
                var batch = context.Batch;
                if (!batch.IsSkipping && batch.UdfFrame is null)
                {
                    if (BuiltInToken.Equals(name, "IO"))
                        context.Connection.StatisticsIo = on;
                    else
                        context.Connection.StatisticsTime = on;
                }
            }
            else
            {
                var listed = BuiltInToken.Equals(name, "PROFILE") ? ListedOnlyOptions.StatisticsProfile
                    : BuiltInToken.Equals(name, "XML") ? ListedOnlyOptions.StatisticsXml
                    : ListedOnlyOptions.None;
                RecordListedOnlyOption(context, listed, on);
            }
        }
        FunctionBodyShape.NoteSideEffect(
            context.Batch,
            statisticsOnOff == Keyword.On ? "SET STATISTICS ON" : "SET STATISTICS OFF",
            FunctionBodyShape.StatementOperatorState);
        return true;
    }

    /// <summary>
    /// Reads the value token following a ReservedKeyword SET option that
    /// takes an integer (ROWCOUNT / TEXTSIZE). Cursor on entry is positioned
    /// at the option keyword; advances once (twice for a signed value) and
    /// validates the value token is a non-NULL <see cref="Numeric"/>. An
    /// integral literal past the int range raises Msg 1080 regardless of
    /// skip state (Level 15, a compile-time check). TEXTSIZE carries semantic
    /// effect (probe-confirmed against SQL Server 2025, 2026-07-19): the
    /// value lands in <c>SimulatedDbConnection.TextSize</c> with <c>-1</c>
    /// preserved verbatim (unlimited, SqlClient's login value) while <c>0</c>
    /// and every other negative collapse to the 4096 default; ROWCOUNT stays
    /// parse-and-discard.
    /// </summary>
    private static bool ConsumeIntegerValue(ParserContext context, bool applyTextSize)
    {
        var value = context.GetNextRequired();
        var negative = false;
        if (value is Operator { Character: '-' } minus)
        {
            // ROWCOUNT's grammar has no sign slot: real reports Msg 102 near
            // the '-' rather than reaching its own Msg 507 (probe-confirmed;
            // the negative value only reaches 507 through a variable).
            if (!applyTextSize)
                throw SimulatedSqlException.SyntaxErrorNear(minus);
            negative = true;
            value = context.GetNextRequired();
        }

        // The variable form (`SET ROWCOUNT @n`) reads the slot's runtime
        // value; real accepts a bigint there even though a literal past the int
        // range is Msg 1080, and refuses any other type with Msg 507 (a string
        // holding a number and a decimal included). TEXTSIZE's grammar has no
        // variable slot: Msg 102 at it (probed 2026-10-04 against SQL Server
        // 2025).
        if (value is AtPrefixedString variable)
        {
            if (applyTextSize)
                throw SimulatedSqlException.SyntaxErrorNear(value);
            var slot = context.Batch.GetVariableSlot(variable.Value);
            if (context.Batch.IsSkipping)
                return true;
            var slotValue = slot.Value;
            if (slotValue.IsNull || !SqlType.IsIntegerCategory(slotValue.Type))
                throw SimulatedSqlException.InvalidRowCountArgument();
            var rows = slotValue.CoerceTo(SqlType.BigInt).AsInt64;
            if (rows < 0)
                throw SimulatedSqlException.InvalidRowCountArgument();
            context.Connection.RowCountLimit = rows;
            return true;
        }

        if (value is not Numeric { Value.IsNull: false } literal)
            return false;

        if (literal.Value.Type == SqlType.BigInt)
            throw SimulatedSqlException.IntegerValueOutOfRange((negative ? -literal.Value.AsInt64 : literal.Value.AsInt64).ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A literal these options can't take as an int — one past the int range
        // (numeric with scale 0) or one carrying a fraction — is Msg 1080 with
        // the value echoed as written (probe-confirmed for `SET ROWCOUNT 2.5`).
        if (literal.Value.Type is DecimalSqlType)
            throw SimulatedSqlException.IntegerValueOutOfRange(negative ? literal.Value.AsDecimal38.Negate().ToString() : literal.Value.AsDecimal38.ToString());

        if (!context.Batch.IsSkipping && literal.Value.Type == SqlType.Int32)
        {
            var requested = negative ? -literal.Value.AsInt32 : literal.Value.AsInt32;
            if (applyTextSize)
                context.Connection.TextSize = requested == -1 ? -1 : requested <= 0 ? 4096 : requested;
            else
                context.Connection.RowCountLimit = requested;
        }

        // Real names these two apart from the generic value-taking options
        // (probe-confirmed: 'SET ROW COUNT' with the words split, 'SET TEXTSIZE'
        // without).
        FunctionBodyShape.NoteSideEffect(
            context.Batch,
            applyTextSize ? "SET TEXTSIZE" : "SET ROW COUNT",
            FunctionBodyShape.StatementOperatorState);
        return true;
    }

    /// <summary>
    /// Reads the value token for an option's value-shape. Cursor on entry
    /// is positioned at the value token (already advanced past the name).
    /// </summary>
    private static bool ConsumeValueForKind(ParserContext context, SetOptionKind kind) => kind switch
    {
        SetOptionKind.OnOff => context.Token is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
        // The variable form (`SET DATEFIRST @d`, `SET LOCK_TIMEOUT @n`) is
        // legal wherever an integer literal is.
        SetOptionKind.Integer => context.Token is Numeric { Value.IsNull: false } or AtPrefixedString,
        // The variable form (`SET LANGUAGE @l`, `SET DATEFORMAT @f`) is legal
        // wherever a bare identifier is.
        SetOptionKind.Identifier => context.Token is Name or Literal or AtPrefixedString,
        SetOptionKind.IntegerOrIdent => context.Token is Numeric or Name or Literal or AtPrefixedString,
        SetOptionKind.Binary => context.Token is Literal or AtPrefixedString,
        _ => false,
    };

    /// <summary>
    /// Reads the <c>int</c> an Integer-shape SET option was given, from either
    /// a literal or a variable, with the cursor already on the value token.
    /// Returns <see langword="null"/> when the parameter isn't <c>int</c>-typed
    /// — a literal past the int range (which tokenizes as bigint or numeric) or
    /// a variable of a wider type — which is the distinction real reports as
    /// Msg 2743 for <c>DATEFIRST</c>. A NULL variable reads as <c>0</c>, which
    /// is the value real's out-of-range message echoes for it.
    /// </summary>
    private static long? ReadIntegerOptionValue(ParserContext context) => context.Token switch
    {
        Numeric { Value: { IsNull: false, Type: var type } literal } when type == SqlType.Int32 => literal.AsInt32,
        AtPrefixedString variable => context.Batch.GetVariableSlot(variable.Value).Value switch
        {
            { IsNull: true } => 0L,
            { Type: var type } slotValue when type == SqlType.Int32 || type == SqlType.SmallInt || type == SqlType.TinyInt =>
                slotValue.CoerceTo(SqlType.Int32).AsInt32,
            _ => null,
        },
        _ => null,
    };

    /// <summary>
    /// Reads <c>SET DEADLOCK_PRIORITY</c>'s value — <c>LOW</c> / <c>NORMAL</c>
    /// / <c>HIGH</c>, an integer in -10..10, or a variable holding either,
    /// a NULL variable reading as <c>NORMAL</c> — raising Msg 2755 for
    /// anything else, a string holding a number included (probed 2026-09-28
    /// and 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static int ReadDeadlockPriority(ParserContext context, bool negative)
    {
        var value = context.Token switch
        {
            AtPrefixedString variable => context.Batch.GetVariableSlot(variable.Value).Value,
            Numeric { Value: var literal } => literal,
            Literal { Value: var literal } => literal,
            Name name => SqlValue.FromNVarchar(name.Value),
            _ => throw SimulatedSqlException.DeadlockPriorityInvalid(),
        };
        if (value.IsNull)
            return 0;
        if (SqlType.IsCollatedString(value.Type))
        {
            var text = value.AsString.Trim();
            return text.Equals("LOW", StringComparison.OrdinalIgnoreCase) ? -5
                : text.Equals("NORMAL", StringComparison.OrdinalIgnoreCase) ? 0
                : text.Equals("HIGH", StringComparison.OrdinalIgnoreCase) ? 5
                : throw SimulatedSqlException.DeadlockPriorityInvalid();
        }
        if (!SqlType.IsIntegerCategory(value.Type))
            throw SimulatedSqlException.DeadlockPriorityInvalid();
        var priority = value.CoerceTo(SqlType.BigInt).AsInt64;
        if (negative)
            priority = -priority;
        return priority is >= -10 and <= 10 ? (int)priority : throw SimulatedSqlException.DeadlockPriorityInvalid();
    }

    /// <summary>
    /// Reads the identifier an Identifier-shape SET option was given, from a
    /// bare name, a quoted literal or a variable. Returns <see langword="null"/>
    /// for a NULL variable, which real treats as naming no language at all.
    /// </summary>
    private static string? ReadIdentifierOptionValue(ParserContext context) => context.Token switch
    {
        Name name => name.Value,
        Literal literal => literal.Value.IsNull ? null : literal.Value.AsString,
        AtPrefixedString variable => context.Batch.GetVariableSlot(variable.Value).Value switch
        {
            { IsNull: true } => null,
            var slotValue => slotValue.CoerceTo(SqlType.NVarchar).AsString,
        },
        _ => null,
    };

    /// <summary>
    /// Value-shape of each recognized SET option. Determines how many tokens
    /// to consume after the option name and what the legal shapes look like.
    /// </summary>
    private enum SetOptionKind
    {
        OnOff,
        Integer,
        Identifier,
        IntegerOrIdent,
        Binary,
    }

    /// <summary>
    /// Closed accept-list of SET-option names whose name token is an
    /// <see cref="UnquotedString"/> (i.e. not a reserved keyword). Each
    /// maps to its value-shape. Reserved-keyword-named options (ROWCOUNT,
    /// TEXTSIZE, TRANSACTION, STATISTICS) dispatch separately in
    /// <see cref="TryParseSetSessionOption"/>. Sourced from the SQL Server
    /// "SET Statements" docs and probe-confirmed against SQL Server 2025
    /// (2026-05-14) for the canonical-shape entries.
    /// </summary>
    private static readonly FrozenDictionary<string, SetOptionKind> RecognizedOptions = new Dictionary<string, SetOptionKind>
    {
        ["ANSI_NULLS"] = SetOptionKind.OnOff,
        ["ANSI_NULL_DFLT_ON"] = SetOptionKind.OnOff,
        ["ANSI_NULL_DFLT_OFF"] = SetOptionKind.OnOff,
        ["QUOTED_IDENTIFIER"] = SetOptionKind.OnOff,
        ["ANSI_WARNINGS"] = SetOptionKind.OnOff,
        ["ANSI_PADDING"] = SetOptionKind.OnOff,
        ["CONCAT_NULL_YIELDS_NULL"] = SetOptionKind.OnOff,
        ["ARITHABORT"] = SetOptionKind.OnOff,
        ["ARITHIGNORE"] = SetOptionKind.OnOff,
        ["NUMERIC_ROUNDABORT"] = SetOptionKind.OnOff,
        ["XACT_ABORT"] = SetOptionKind.OnOff,
        ["FMTONLY"] = SetOptionKind.OnOff,
        ["NOEXEC"] = SetOptionKind.OnOff,
        ["FORCEPLAN"] = SetOptionKind.OnOff,
        ["PARSEONLY"] = SetOptionKind.OnOff,
        ["CURSOR_CLOSE_ON_COMMIT"] = SetOptionKind.OnOff,
        ["ANSI_DEFAULTS"] = SetOptionKind.OnOff,
        ["REMOTE_PROC_TRANSACTIONS"] = SetOptionKind.OnOff,
        ["NO_BROWSETABLE"] = SetOptionKind.OnOff,
        ["NOCOUNT"] = SetOptionKind.OnOff,
        ["IMPLICIT_TRANSACTIONS"] = SetOptionKind.OnOff,
        ["SHOWPLAN_ALL"] = SetOptionKind.OnOff,
        ["SHOWPLAN_TEXT"] = SetOptionKind.OnOff,
        ["SHOWPLAN_XML"] = SetOptionKind.OnOff,
        ["DISABLE_DEF_CNST_CHK"] = SetOptionKind.OnOff,
        ["LOCK_TIMEOUT"] = SetOptionKind.Integer,
        ["DATEFIRST"] = SetOptionKind.Integer,
        ["QUERY_GOVERNOR_COST_LIMIT"] = SetOptionKind.Integer,
        ["DATEFORMAT"] = SetOptionKind.Identifier,
        ["LANGUAGE"] = SetOptionKind.Identifier,
        ["DEADLOCK_PRIORITY"] = SetOptionKind.IntegerOrIdent,
        ["CONTEXT_INFO"] = SetOptionKind.Binary,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>SET @v = expr</c> and the compound forms <c>SET @v += expr</c>
    /// / <c>-=</c> / <c>*=</c> / <c>/=</c> / <c>%=</c> / <c>&amp;=</c> / <c>|=</c>
    /// / <c>^=</c>. Resolves the slot via <see cref="BatchContext.GetVariableSlot"/>
    /// (Msg 137 if undeclared); compound forms desugar to the equivalent
    /// <c>FromCompoundOp(op, VariableReference(@v), rhs)</c> so the existing
    /// arithmetic / string-concat dispatch runs unchanged (NULL propagates,
    /// string <c>+=</c> concatenates, decimal/money widening matches plain
    /// <c>+</c>). The compound op's two characters must be adjacent in the
    /// source (probe-confirmed: <c>SET @v + = 5</c> with a space raises
    /// Msg 102 near <c>'+'</c>). After the arithmetic step the result is
    /// coerced through the slot's declared type via
    /// <see cref="Cast.ApplyCoercion"/>, preserving
    /// silent-truncation / Msg-245 semantics from the regular CAST path.
    /// </summary>
    private static bool TryParseSetVariable(ParserContext context, AtPrefixedString variableToken)
    {
        // Cursor variable: SET @c = CURSOR … / SET @c = @otherVar / SET @c =
        // named_cursor. Routes away from the scalar slot machinery.
        if (context.Batch.CursorVariables.ContainsKey(variableToken.Value))
            return TryParseSetCursorVariable(context, variableToken.Value);

        if (!context.Batch.Variables.ContainsKey(variableToken.Value) && !context.Batch.TableVariables.ContainsKey(variableToken.Value))
        {
            // An undeclared target is refused once the statement has parsed.
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '.' } && TryConsumeAssignmentOperator(context) is not null)
            {
                context.MoveNextRequired();
                _ = Expression.Parse(context);
            }
            throw SimulatedSqlException.MustDeclareSetTarget(variableToken.Value);
        }
        var slot = context.Batch.GetVariableSlot(variableToken.Value);

        context.MoveNextRequired();
        if (context.Token is Operator { Character: '.' })
            return TryParseSetInstanceMember(context, variableToken, slot);
        if (TryConsumeAssignmentOperator(context) is not char assignOp)
            return false;

        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        // A scalar UDF body's analysis walk carries what the value read to the
        // variable, a compound assignment keeping what it held before.
        if (context.Batch.UdfFrame is { AnalyzesReturnMask: true })
        {
            var read = DataMask.Of(rhs, static _ => null, typeOf: null);
            slot.Mask = assignOp == '=' ? read : DataMask.Merge(slot.Mask, read);
        }
        // One vector variable assigned to another of a different base type or
        // dimension count is refused compiling the batch (probed 2026-09-29
        // against SQL Server 2025).
        if (context.Batch.IsSkipping && assignOp == '=' && slot.DeclaredType is VectorSqlType && rhs is VariableReference)
            AssignmentRules.RequireAssignable(rhs, rhs.GetSqlType(context.Batch, NoColumnTypeResolver), slot.DeclaredType);

        // Any other value whose type the batch can settle while compiling is
        // judged then too, so a refused one stops the whole batch before it
        // runs: `SET @nv = (SELECT … FOR XML …, TYPE)` is Msg 257 with nothing
        // run (probed 2026-10-02 against SQL Server 2025). A value naming what
        // the batch has yet to create defers to its run, as real's statement
        // does.
        else if (context.Batch.IsSkipping && assignOp == '=' && StaticTypeOrNull(rhs, context.Batch) is { } staticType)
            AssignmentRules.RequireAssignable(rhs, staticType, slot.DeclaredType);
        if (context.Batch.IsSkipping)
            return true;
        var assignedExpr = assignOp == '='
            ? rhs
            : TwoSidedExpression.FromCompoundOp(assignOp, new VariableReference(variableToken, context), rhs, context);
        // A subquery hands an unresolved collation on to the variable, which
        // settles it as any assignment target does (Msg 456 for varchar).
        var assignedType = assignedExpr.GetSqlType(context.Batch, NoColumnTypeResolver);
        UnresolvedCollation.RequireAssignable(assignedType);
        AssignmentRules.RequireAssignable(assignedExpr, assignedType, slot.DeclaredType);
        var rhsValue = assignedExpr.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        Cast.RejectRoundingUnderRoundAbort(rhsValue, slot.DeclaredType, context.Batch);
        slot.Assign(DataMasking.ForAssignment(context.Batch, assignedExpr, rhsValue, SqlValue.NameVariantBase(rhsValue, Cast.ApplyCoercion(rhsValue, slot.DeclaredType, slot.DeclaredMaxLength), assignedExpr.ResultReportsNumeric), slot.DeclaredType));
        return true;
    }

    private static SqlType? StaticTypeOrNull(Expression expression, BatchContext batch)
    {
        try
        {
            return expression.GetSqlType(batch, NoColumnTypeResolver);
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Routes <c>SET @v.&lt;member&gt; …</c> to the one instance-member form
    /// that has a mutator: an XML method call (only <c>.modify()</c> is one —
    /// anything else is Msg 8113) or the spatial <c>STSrid</c> property
    /// assignment. A non-xml variable carrying a method call is Msg 258,
    /// naming its type the way real does.
    /// </summary>
    private static bool TryParseSetInstanceMember(ParserContext context, AtPrefixedString variableToken, VariableSlot slot)
    {
        // A CLR user-defined type's property, field or mutator method.
        if (slot.DeclaredType is ClrUdtSqlType clrType)
        {
            if (context.GetNextRequired() is not Name clrMember)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            var mutation = ClrTypeMutation.Parse(new VariableReference(variableToken, context), $"@{variableToken.Value}", clrType, clrMember.Value, context);
            if (context.Batch.IsSkipping)
                return true;
            slot.Assign(mutation.Run(new RuntimeContext(NoColumnResolver, context.Batch)));
            return true;
        }

        var checkpoint = context.SaveCheckpoint();
        if (slot.DeclaredType is JsonSqlType
            && context.GetNextRequired() is Name jsonMethod
            && Collation.Baseline.Equals(jsonMethod.Value, "modify")
            && context.GetNextOptional() is Operator { Character: '(' })
        {
            var jsonMutator = JsonModify.ParseMethod(context, new VariableReference(variableToken, context), $"@{variableToken.Value}");
            _ = jsonMutator.GetSqlType(context.Batch, NoColumnTypeResolver);
            if (context.Batch.IsSkipping)
                return true;
            slot.Assign(jsonMutator.Run(new RuntimeContext(NoColumnResolver, context.Batch)));
            context.Connection.LastStatementRowCount = 1;
            return true;
        }
        context.RestoreCheckpoint(checkpoint);
        if (context.GetNextRequired() is Name method
            && XmlMethodCall.IsKnownMethodName(method.Value)
            && context.GetNextOptional() is Operator { Character: '(' })
        {
            if (slot.DeclaredType is not XmlSqlType)
                throw SimulatedSqlException.CannotCallMethodsOn(SimulatedSqlException.FamilyRootName(slot.DeclaredType));
            var mutator = XmlModify.Parse(new VariableReference(variableToken, context), $"@{variableToken.Value}", method.Value, context, resolveColumnType: null);
            if (context.Batch.IsSkipping)
                return true;
            slot.Assign(mutator.Run(new RuntimeContext(NoColumnResolver, context.Batch)));
            context.Connection.LastStatementRowCount = 1;
            return true;
        }
        context.RestoreCheckpoint(checkpoint);
        return TryParseSetSpatialProperty(context, slot, $"@{variableToken.Value}");
    }

    /// <summary>
    /// Parses <c>SET @g.STSrid = expr</c> — the one assignable member of a
    /// spatial value. Cursor enters on the <c>.</c>.
    /// </summary>
    /// <remarks>
    /// Every other spatial property is read-only, which real reports as
    /// Msg 6595; a name that isn't a member at all reports Msg 6592. A NULL
    /// right-hand side surfaces as the bare .NET argument failure real emits
    /// with no 24xxx code, and an SRID outside 0..999999 as Msg 24100.
    /// </remarks>
    private static bool TryParseSetSpatialProperty(ParserContext context, VariableSlot slot, string variableName)
    {
        if (context.GetNextRequired() is not Name member)
            return false;
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '=' })
            return false;
        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        if (context.Batch.IsSkipping)
            return true;
        if (slot.DeclaredType is not SpatialSqlType spatial)
            return false;
        if (!member.Value.Equals("STSrid", StringComparison.Ordinal))
        {
            throw SpatialMethodCall.IsKnownMemberName(member.Value)
                ? SimulatedSqlException.ClrPropertyReadOnly(member.Value, spatial.ClrTypeName)
                : SimulatedSqlException.ClrPropertyNotFound(member.Value, spatial.ClrTypeName);
        }

        // A NULL receiver refuses the mutator outright, ending the batch
        // (probed 2026-10-05 against SQL Server 2025).
        if (slot.Value.IsNull)
            throw SimulatedSqlException.ClrMutatorOnNull(member.Value, variableName);
        var assigned = rhs.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        if (assigned.IsNull)
            throw SimulatedSqlException.SpatialSridCannotBeNull(spatial.IsGeography);
        var srid = SpatialGeometry.ValidateSrid(ScalarArguments.CoerceToInt(assigned), spatial.IsGeography);
        slot.Value = SqlValue.FromSpatial(slot.Value.AsSpatial.WithSrid(srid), spatial.IsGeography);
        return true;
    }

    /// <summary>
    /// Parses <c>SET @c = &lt;cursor-source&gt;</c> where <c>@c</c> is a cursor
    /// variable: a fresh <c>CURSOR … FOR …</c> definition (an unnamed,
    /// refcounted cursor), another cursor variable, or a named cursor. The
    /// variable is rebound — dropping the reference it previously held and
    /// taking one on the new cursor. On entry the cursor is on the variable
    /// token.
    /// </summary>
    private static bool TryParseSetCursorVariable(ParserContext context, string variableName)
    {
        context.MoveNextRequired(); // step onto '='
        if (context.Token is not Operator { Character: '=' })
            return false;
        context.MoveNextRequired(); // step onto the RHS first token

        Cursor? newCursor;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Cursor }:
                // A cursor assignment reports no count (probed 2026-09-28).
                context.Batch.CurrentStatement.DoneCount = -1;
                if (BuildCursorDefinition(context.Batch, "", reqStatic: false, scroll: false) is not { } built)
                    return true; // skipping — tokens consumed
                built.Cursor.IsUnnamed = true;
                built.Cursor.OriginVariable = "@" + variableName;
                newCursor = built.Cursor;
                break;
            case AtPrefixedString sourceVar:
                context.MoveNextOptional();
                if (context.Batch.IsSkipping)
                    return true;
                newCursor = context.Batch.CursorVariables.TryGetValue(sourceVar.Value, out var src) && src is not null
                    ? src
                    : throw SimulatedSqlException.CursorVariableNotAllocated(sourceVar.Value);
                break;
            case Name namedCursor:
                context.MoveNextOptional();
                if (context.Batch.IsSkipping)
                    return true;
                newCursor = ResolveNamedCursor(context.Batch, namedCursor.Value);
                break;
            default:
                return false;
        }

        RebindCursorVariable(context.Batch, variableName, newCursor);
        return true;
    }

    /// <summary>
    /// At the current token position, detects whether the parser is sitting
    /// on the assignment-operator slot of a SET / UPDATE-SET statement.
    /// Returns <c>'='</c> for a plain assignment (one token consumed), the
    /// arithmetic char for compound (<c>+ - * / % &amp; | ^</c>, two tokens
    /// consumed), or <c>null</c> when the position isn't a recognized
    /// assignment operator (caller raises Msg 102). Compound forms require
    /// the arith char and the trailing <c>=</c> to be adjacent in source
    /// (no intervening whitespace) — probe-confirmed against SQL Server 2025.
    /// On a successful match, <see cref="ParserContext.Token"/> is left at
    /// the last consumed operator token; callers advance once more to step
    /// onto the RHS first token.
    /// </summary>
    private static char? TryConsumeAssignmentOperator(ParserContext context) =>
        context.Token is not Operator first
            ? null
            : first.Character == '='
                ? '='
                : first.Character is not ('+' or '-' or '*' or '/' or '%' or '&' or '|' or '^')
                    ? null
                    : context.GetNextRequired() is not Operator { Character: '=' } second || second.StartIndex != first.EndIndex
                        ? null
                        : first.Character;

    /// <summary>
    /// Parses <c>SET IDENTITY_INSERT &lt;table&gt; ON|OFF</c>. ON sets the
    /// session's active <c>IDENTITY_INSERT</c> target after verifying no
    /// other table holds it (Msg 8107); OFF clears the target if it matches.
    /// </summary>
    private static bool TryParseSetIdentityInsert(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var onOff } || onOff is not (Keyword.On or Keyword.Off))
            return false;

        context.Batch.CurrentStatement.DoneKind = StatementDoneKind.SetIdentityInsert;
        FunctionBodyShape.NoteSideEffect(
            context.Batch,
            onOff == Keyword.On ? "SET IDENTITY_INSERT ON" : "SET IDENTITY_INSERT OFF",
            FunctionBodyShape.StatementOperatorState);

        if (context.Batch.IsSkipping)
            return true;

        // A view is Msg 8105 and a missing object Msg 1088, each naming the
        // object as written (probed 2026-10-01 against SQL Server 2025); every
        // refusal of the statement ends the batch (probed 2026-10-04).
        if (!context.Batch.TryResolveTable(tableName, out var heapTable))
        {
            throw (context.Batch.TryResolveView(tableName, out _)
                ? SimulatedSqlException.IdentityInsertNotUserTable(tableName.ToString())
                : SimulatedSqlException.IdentityInsertObjectNotFound(tableName.ToString())).EndingBatch();
        }

        // Setting it takes ALTER on the table — inside a procedure too, which
        // ownership chaining doesn't reach — and a refusal reads as the
        // missing table (probed 2026-10-04 against SQL Server 2025).
        if (heapTable.Name is not ['#', ..]
            && !PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(heapTable), heapTable.ObjectId, heapTable.SchemaId))
        {
            throw SimulatedSqlException.IdentityInsertDenied(tableName.ToString()).EndingBatch();
        }

        if (onOff == Keyword.On)
        {
            // A table with no identity column can't be an IDENTITY_INSERT
            // target — Msg 8106 (probe-confirmed against SQL Server 2025).
            if (heapTable.IdentityOrdinal < 0)
                throw SimulatedSqlException.TableHasNoIdentityForSet(tableName.ToString()).EndingBatch();
            // Msg 8107 names the held table three-part and the requested one
            // as written.
            if (context.Connection.IdentityInsertTable is string held && !context.Batch.CurrentDatabase.Collation.Equals(held, heapTable.Name))
                throw SimulatedSqlException.IdentityInsertAlreadyOn(context.Connection.IdentityInsertQualifiedName ?? held, tableName.ToString()).EndingBatch();
            context.Connection.IdentityInsertTable = heapTable.Name;
            context.Connection.IdentityInsertQualifiedName = QualifyForTruncationMessage(heapTable);
        }
        else if (context.Batch.CurrentDatabase.Collation.Equals(context.Connection.IdentityInsertTable, heapTable.Name))
        {
            context.Connection.IdentityInsertTable = null;
            context.Connection.IdentityInsertQualifiedName = null;
        }
        return true;
    }
}

/// <summary>
/// The <c>SET</c> switches the simulator keeps only so <c>DBCC USEROPTIONS</c>
/// can list them: <c>STATISTICS PROFILE</c> and <c>XML</c> (whose plans aren't
/// built), <c>FORCEPLAN</c> and <c>REMOTE_PROC_TRANSACTIONS</c>.
/// </summary>
[Flags]
internal enum ListedOnlyOptions
{
    None = 0,
    StatisticsProfile = 1 << 2,
    StatisticsXml = 1 << 3,
    ForcePlan = 1 << 4,
    RemoteProcTransactions = 1 << 5,
}
