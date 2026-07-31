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
    /// Dispatches the four <c>SET</c> shapes: <c>SET @v = expr</c>
    /// (variable assignment, has runtime effect via
    /// <see cref="TryParseSetVariable"/>), <c>SET IDENTITY_INSERT t ON|OFF</c>
    /// (session-state mutation, see <see cref="TryParseSetIdentityInsert"/>),
    /// and the closed-list session / connection / planner option family
    /// (<see cref="TryParseSetSessionOption"/>). The last family is
    /// parse-and-discard: the simulator doesn't model locking / isolation /
    /// language / dateformat / planner choice / warnings-on-rounding, so the
    /// honest stance is to accept the canonical shapes and ignore. Returning
    /// <c>false</c> falls through to the caller's <see cref="SimulatedSqlException.SyntaxErrorNear(ParserContext)"/>
    /// (Msg 102); explicit Msg 195 fires when an unrecognized option name
    /// appears followed by a recognizable value (ON/OFF/literal).
    /// </summary>
    private static bool TryParseSet(ParserContext context) =>
        context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Identity_Insert } => TryParseSetIdentityInsert(context),
            AtPrefixedString variableToken => TryParseSetVariable(context, variableToken),
            var afterSet => TryParseSetSessionOption(context, afterSet),
        };

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
                return TryParseSetTransactionIsolationLevel(context);
            case ReservedKeyword { Keyword: Keyword.Statistics }:
                return TryParseSetStatistics(context);
            // ReservedKeyword options that take an integer (ROWCOUNT / TEXTSIZE).
            // They tokenize as ReservedKeyword because the words appear in the
            // T-SQL reserved set; the SET parser accepts them by Keyword check.
            case ReservedKeyword { Keyword: var intOption and (Keyword.RowCount or Keyword.TextSize) }:
                return ConsumeIntegerValue(context, applyTextSize: intOption == Keyword.TextSize);
        }

        if (afterSet is not UnquotedString unquoted)
            return false;

        if (!RecognizedOptions.TryGetValue(unquoted.Value, out var firstKind))
            return TryRaiseUnrecognizedSetOption(context, unquoted);

        var firstName = unquoted.Value;
        context.MoveNextRequired();

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
                    throw SimulatedSqlException.UnrecognizedSetOption(next.Value);
                affectsQuotedIdentifier |= IsQuotedIdentifierOption(next.Value);
                sessionOptionNames.Add(next.Value);
                context.MoveNextRequired();
            }
            if (context.Token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } commaOnOff)
                return false;
            var commaOn = commaOnOff.Keyword == Keyword.On;
            if (affectsQuotedIdentifier)
                ApplyQuotedIdentifierOption(context, commaOn);
            // Every listed option shares the trailing ON|OFF value.
            foreach (var listed in sessionOptionNames)
                RecordSessionStateOption(context, listed, commaOn);
            return true;
        }

        // Integer-shaped options accept a signed value: SMO's scripting
        // preamble sends `SET LOCK_TIMEOUT -1`, where `-1` tokenizes as an
        // Operator('-') followed by the Numeric.
        var negativeInteger = false;
        if (firstKind is SetOptionKind.Integer or SetOptionKind.IntegerOrIdent && context.Token is Operator { Character: '-' })
        {
            negativeInteger = true;
            context.MoveNextRequired();
        }

        if (!ConsumeValueForKind(context, firstKind))
            return false;

        if (IsQuotedIdentifierOption(firstName) && context.Token is ReservedKeyword { Keyword: var qiOnOff })
            ApplyQuotedIdentifierOption(context, qiOnOff == Keyword.On);

        // Record the six ANSI/arithmetic session toggles SESSIONPROPERTY reads
        // (ANSI_NULLS / ANSI_PADDING / ANSI_WARNINGS / ARITHABORT /
        // CONCAT_NULL_YIELDS_NULL / NUMERIC_ROUNDABORT). Other OnOff options
        // no-op inside RecordSessionStateOption.
        if (firstKind == SetOptionKind.OnOff && context.Token is ReservedKeyword { Keyword: var onOff })
            RecordSessionStateOption(context, firstName, onOff == Keyword.On);

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

        // FMTONLY carries semantic effect: while ON, SELECT returns
        // metadata-only zero-row results and data-modifying statements are
        // suppressed. Session-scoped like LOCK_TIMEOUT; gated on !IsSkipping so
        // a never-taken IF branch's SET FMTONLY doesn't perturb the session.
        if (firstName.Equals("FMTONLY", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping
            && context.Token is ReservedKeyword { Keyword: var fmtOnOff })
        {
            context.Connection.FmtOnly = fmtOnOff == Keyword.On;
        }

        // NOCOUNT carries semantic effect: while ON, a statement's DONE token
        // omits the rows-affected count (DONE_COUNT), so an ODBC / pyodbc driver
        // advances past an INSERT's rowcount to a trailing SELECT SCOPE_IDENTITY()
        // — the identity-retrieval pattern mssql-django and most SQL-Server data
        // layers emit. Session-scoped like FMTONLY, gated on !IsSkipping.
        if (firstName.Equals("NOCOUNT", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping
            && context.Token is ReservedKeyword { Keyword: var nocountOnOff })
        {
            context.Connection.NoCount = nocountOnOff == Keyword.On;
        }

        // CONTEXT_INFO carries semantic effect: store the binary value,
        // right-padded / truncated to exactly 128 bytes (SQL Server's
        // fixed buffer), surfaced by CONTEXT_INFO(). The literal-binary form
        // is handled here; a `@var` value side isn't accepted by the SET
        // value parser (parse-and-discard heritage) — that shape stays unmodeled.
        if (firstName.Equals("CONTEXT_INFO", StringComparison.OrdinalIgnoreCase) && !context.Batch.IsSkipping)
        {
            if (context.Token is Literal { Value: { IsNull: false } binary })
            {
                var source = binary.AsBytes;
                var buffer = new byte[128];
                Array.Copy(source, buffer, Math.Min(source.Length, 128));
                context.Connection.ContextInfo = buffer;
            }
        }
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
    /// Records one of the six ANSI/arithmetic session toggles
    /// <c>SESSIONPROPERTY</c> reads onto the connection. Scoping mirrors
    /// <see cref="ApplyQuotedIdentifierOption"/>: the write is suppressed inside
    /// a procedure / function / trigger body and inside dynamic SQL (a non-null
    /// <see cref="BatchContext.ProcFrame"/> covers the dynamic-SQL sentinel too),
    /// so only a top-level <c>SET</c> persists to the session — matching how
    /// real SQL Server scopes these options. Runs regardless of
    /// <see cref="BatchContext.IsSkipping"/> (a SET in a never-taken IF branch
    /// still applies, as with QUOTED_IDENTIFIER). XACT_ABORT records onto the
    /// connection too (its cancel-time transaction-abort behavior reads the
    /// flag); other recognized OnOff options fall through the default arm and
    /// no-op.
    /// </summary>
    private static void RecordSessionStateOption(ParserContext context, string optionName, bool on)
    {
        var batch = context.Batch;
        if (batch.UdfFrame is not null || batch.TriggerFrame is not null || batch.ProcFrame is not null)
            return;
        var connection = context.Connection;
        Span<char> upper = stackalloc char[optionName.Length];
        _ = optionName.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "ANSI_NULLS":
                connection.AnsiNulls = on;
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
            case "CONCAT_NULL_YIELDS_NULL":
                connection.ConcatNullYieldsNull = on;
                break;
            case "NUMERIC_ROUNDABORT":
                connection.NumericRoundabort = on;
                break;
            case "XACT_ABORT":
                connection.XactAbort = on;
                break;
            default:
                break;
        }
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
            or Numeric or Literal or UnquotedString or DelimitedIdentifier)
        {
            throw SimulatedSqlException.UnrecognizedSetOption(nameValue);
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
            _ => (System.Data.IsolationLevel.Unspecified, false),
        };
        if (consumeAnother)
            context.MoveNextRequired();
        if (newLevel != System.Data.IsolationLevel.Unspecified)
            context.Batch.Connection.SessionIsolationLevel = newLevel;
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
        var subOption = context.GetNextRequired();
        var onOff = context.GetNextRequired();
        return subOption is StringToken && onOff is ReservedKeyword { Keyword: Keyword.On or Keyword.Off };
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
        if (value is Operator { Character: '-' })
        {
            negative = true;
            value = context.GetNextRequired();
        }

        if (value is not Numeric { Value.IsNull: false } literal)
            return false;

        if (literal.Value.Type == SqlType.BigInt)
            throw SimulatedSqlException.IntegerValueOutOfRange((negative ? -literal.Value.AsInt64 : literal.Value.AsInt64).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (literal.Value.Type is DecimalSqlType { scale: 0 })
            throw SimulatedSqlException.IntegerValueOutOfRange((negative ? -literal.Value.AsDecimal : literal.Value.AsDecimal).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (applyTextSize && !context.Batch.IsSkipping && literal.Value.Type == SqlType.Int32)
        {
            var requested = negative ? -literal.Value.AsInt32 : literal.Value.AsInt32;
            context.Connection.TextSize = requested == -1 ? -1 : requested <= 0 ? 4096 : requested;
        }

        return true;
    }

    /// <summary>
    /// Reads the value token for an option's value-shape. Cursor on entry
    /// is positioned at the value token (already advanced past the name).
    /// </summary>
    private static bool ConsumeValueForKind(ParserContext context, SetOptionKind kind) => kind switch
    {
        SetOptionKind.OnOff => context.Token is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
        SetOptionKind.Integer => context.Token is Numeric { Value.IsNull: false },
        SetOptionKind.Identifier => context.Token is Name or Literal,
        SetOptionKind.IntegerOrIdent => context.Token is Numeric or Name or Literal,
        SetOptionKind.Binary => context.Token is Literal,
        _ => false,
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

        var slot = context.Batch.GetVariableSlot(variableToken.Value);

        context.MoveNextRequired();
        if (context.Token is Operator { Character: '.' })
            return TryParseSetSpatialProperty(context, slot);
        if (TryConsumeAssignmentOperator(context) is not char assignOp)
            return false;

        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        if (context.Batch.IsSkipping)
            return true;
        var assignedExpr = assignOp == '='
            ? rhs
            : TwoSidedExpression.FromCompoundOp(assignOp, new VariableReference(variableToken, context), rhs);
        var rhsValue = assignedExpr.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        slot.Value = Cast.ApplyCoercion(rhsValue, slot.DeclaredType, slot.DeclaredMaxLength);
        return true;
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
    private static bool TryParseSetSpatialProperty(ParserContext context, VariableSlot slot)
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

        var assigned = rhs.Run(new RuntimeContext(NoColumnResolver, context.Batch));
        if (assigned.IsNull)
            throw SimulatedSqlException.SpatialSridCannotBeNull(spatial.IsGeography);
        var srid = SpatialGeometry.ValidateSrid(ScalarArguments.CoerceToInt(assigned), spatial.IsGeography);
        if (!slot.Value.IsNull)
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
                if (BuildCursorDefinition(context.Batch, "", reqStatic: false, scroll: false) is not { } built)
                    return true; // skipping — tokens consumed
                built.Cursor.IsUnnamed = true;
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

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var heapTable))
            throw SimulatedSqlException.InvalidObjectName(tableName);

        if (onOff == Keyword.On)
        {
            // A table with no identity column can't be an IDENTITY_INSERT
            // target — Msg 8106 (probe-confirmed against SQL Server 2025).
            if (heapTable.IdentityOrdinal < 0)
                throw SimulatedSqlException.TableHasNoIdentityForSet(heapTable.Name);
            if (context.Connection.IdentityInsertTable is string held && !context.Batch.CurrentDatabase.Collation.Equals(held, heapTable.Name))
                throw SimulatedSqlException.IdentityInsertAlreadyOn(held, heapTable.Name);
            context.Connection.IdentityInsertTable = heapTable.Name;
        }
        else if (context.Batch.CurrentDatabase.Collation.Equals(context.Connection.IdentityInsertTable, heapTable.Name))
        {
            context.Connection.IdentityInsertTable = null;
        }
        return true;
    }
}
