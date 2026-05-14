using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Table hints (<c>WITH (NOLOCK [, …])</c> on FROM sources, JOIN-RHS, and
/// UPDATE / DELETE targets) and statement-level <c>OPTION (…)</c> hints.
/// The simulator doesn't model locking / isolation / planner choice /
/// indexes, so all recognized hint shapes parse-and-discard. The value of
/// shipping this is grammar compatibility — applications and EF Core
/// pipelines (TagWith → hint) can emit hint clauses without tripping
/// <see cref="SimulatedSqlException"/>.
/// </summary>
/// <remarks>
/// Closed accept-lists per probe (SQL Server 2025, 2026-05-14):
/// unknown table-hint name → Msg 321
/// (<c>"&lt;name&gt;" is not a recognized table hints option.</c>);
/// unknown OPTION hint name → Msg 102 generic syntax error
/// (<c>Incorrect syntax near '&lt;name&gt;'</c>). Conflict-detection
/// (<c>Msg 1047</c> for NOLOCK + XLOCK etc.) isn't modeled because the
/// simulator has no lock state to conflict over.
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// Table hints accepted inside <c>WITH (...)</c> on a base-table /
    /// view / table-variable source or after an UPDATE / DELETE target.
    /// Case-insensitive. Membership-only; argument shapes are validated by
    /// <see cref="ConsumeOneTableHint"/> (bare / <c>= literal</c> /
    /// <c>(arg-list)</c>).
    /// </summary>
    /// <remarks>
    /// Sourced from SQL Server's "Table Hints (Transact-SQL)" docs plus
    /// the probe-confirmed entries. Trust-region note: <c>READONLY</c>
    /// is technically only valid on a TVP-typed parameter, not a FROM
    /// source; the simulator accepts it everywhere because the parser
    /// doesn't carry per-site rejection rules and real apps don't put it
    /// on regular tables.
    /// </remarks>
    private static readonly HashSet<string> TableHintNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOLOCK", "READPAST", "READUNCOMMITTED", "READCOMMITTED", "READCOMMITTEDLOCK",
        "REPEATABLEREAD", "SERIALIZABLE", "SNAPSHOT", "HOLDLOCK", "UPDLOCK", "XLOCK",
        "TABLOCK", "TABLOCKX", "ROWLOCK", "PAGLOCK", "NOWAIT",
        "KEEPIDENTITY", "KEEPDEFAULTS", "NOEXPAND",
        "IGNORE_CONSTRAINTS", "IGNORE_TRIGGERS",
        "FORCESEEK", "FORCESCAN", "INDEX", "SPATIAL_WINDOW_MAX_CELLS",
        "READONLY", "REMOTE",
    };

    /// <summary>
    /// First-word vocabulary accepted inside <c>OPTION (...)</c>. Each entry
    /// is matched case-insensitively against the leading token; trailing
    /// words (<c>PLAN</c> / <c>ORDER</c> / <c>UNION</c> / <c>GROUP</c> /
    /// <c>JOIN</c>), arguments (<c>MAXDOP N</c> / <c>FAST N</c>), and nested
    /// parens (<c>OPTIMIZE FOR (...)</c>, <c>USE HINT (...)</c>) are
    /// consumed-and-discarded by <see cref="ConsumeOneOptionHint"/>.
    /// <c>MAXRECURSION</c> alone has runtime effect — it overrides the per-CTE
    /// recursion limit, so its argument is parsed strictly.
    /// </summary>
    private static readonly HashSet<string> OptionHintFirstWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "RECOMPILE", "MAXRECURSION", "MAXDOP", "FAST",
        "LOOP", "HASH", "MERGE",
        "FORCE",
        "KEEPFIXED", "KEEP", "ROBUST",
        "OPTIMIZE", "USE",
        "EXPAND",
        "IGNORE_NONCLUSTERED_COLUMNSTORE_INDEX", "NO_PERFORMANCE_SPOOL",
        "QUERYTRACEON",
        "TABLE", "PARAMETERIZATION",
        "ORDER", "CONCAT",
    };

    /// <summary>
    /// Consumes an optional table-hint clause after a FROM source / JOIN-RHS
    /// table name (or after an INSERT / UPDATE / DELETE / MERGE target /
    /// MERGE source). The standard <c>WITH (hint [, …])</c> form is always
    /// accepted; the legacy <c>(hint [, …])</c> (no <c>WITH</c>) form is
    /// only accepted when <paramref name="allowLegacyParenForm"/> is
    /// <c>true</c>. FROM / JOIN-RHS pass <c>true</c>; INSERT, UPDATE, DELETE,
    /// and MERGE all pass <c>false</c> (probe-confirmed: real SQL Server
    /// rejects the bare-paren form on every DML target, raising either
    /// Msg 102 for UPDATE / DELETE / MERGE or treating the paren as a
    /// column list on INSERT). The legacy form is disambiguated from a
    /// derived-table column-alias list by peeking at the first inner token
    /// and only consuming when it matches <see cref="TableHintNames"/>. On
    /// entry the cursor sits at the token immediately following the alias
    /// (or the bare table name if no alias was present). On exit the cursor
    /// sits at the next un-consumed lookahead token (WHERE / JOIN / comma /
    /// <c>;</c> / null).
    /// </summary>
    internal static void ParseOptionalTableHints(ParserContext context, bool allowLegacyParenForm = true, bool commitOnLegacyParen = false)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            ConsumeTableHintListBody(context);
            return;
        }
        if (allowLegacyParenForm && context.Token is Operator { Character: '(' })
        {
            // commitOnLegacyParen = true: `(` after the table reference is
            // unambiguously a hint clause attempt (probe-confirmed for
            // MERGE bare-table source with alias and for FROM-source-with-
            // alias on real SQL Server — Msg 321 surfaces with the first
            // inner token as the would-be hint name). commit=false keeps
            // the peek-and-restore disambiguation needed by the existing
            // FROM/JOIN-RHS callers, which don't otherwise prove an alias
            // was consumed at the call site.
            if (commitOnLegacyParen)
            {
                context.MoveNextRequired();
                ConsumeTableHintListBody(context);
                return;
            }
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            if (context.Token is not null && TableHintNames.Contains(context.Token.ToString()))
            {
                ConsumeTableHintListBody(context);
                return;
            }
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Walks the comma-separated body of a table-hint list. Cursor on entry:
    /// the first hint-name token (immediately after the opening <c>(</c>).
    /// Cursor on exit: the next token after the closing <c>)</c>.
    /// </summary>
    private static void ConsumeTableHintListBody(ParserContext context)
    {
        while (true)
        {
            ConsumeOneTableHint(context);
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Validates and consumes a single table-hint entry: <c>name</c>,
    /// <c>name = literal</c>, or <c>name (arg-list)</c>. Unknown name →
    /// Msg 321. Cursor advances to the trailing <c>,</c> or <c>)</c>.
    /// </summary>
    private static void ConsumeOneTableHint(ParserContext context)
    {
        if (context.Token is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var sourceSpan = context.Token.Source;
        if (!TableHintNames.Contains(sourceSpan.ToString()))
            throw SimulatedSqlException.UnrecognizedTableHint(sourceSpan);
        context.MoveNextRequired();
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            context.MoveNextRequired();
            return;
        }
        if (context.Token is Operator { Character: '(' })
        {
            SkipBalancedParens(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Parses the trailing <c>OPTION (hint [, …])</c> clause. Recognized
    /// first-words (per <see cref="OptionHintFirstWords"/>) are accepted
    /// and discarded; <c>MAXRECURSION N</c> additionally overrides every
    /// in-scope <see cref="CteBinding"/>'s recursion limit. Unknown
    /// first-word → Msg 102 (matches probe). Cursor on entry: the
    /// <c>OPTION</c> keyword. Cursor on exit: the next un-consumed token.
    /// </summary>
    private static void ParseOptionClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        while (true)
        {
            context.MoveNextRequired();
            ConsumeOneOptionHint(context);
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                return;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Consumes a single OPTION-clause hint. <c>MAXRECURSION N</c> is
    /// strict-parsed (integer literal 0–32767, applied to every in-scope
    /// CTE binding). Other recognized first-words skip tokens — handling
    /// nested parens — until the next <c>,</c> or <c>)</c> at depth 0.
    /// </summary>
    private static void ConsumeOneOptionHint(ParserContext context)
    {
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.MaxRecursion })
        {
            if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } limitValue })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var limit = limitValue.AsInt32;
            if (limit is < 0 or > 32_767)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.CteBindings is { } bindings)
            {
                foreach (var binding in bindings.Values)
                    binding.MaxRecursion = limit;
            }
            context.MoveNextRequired();
            return;
        }
        if (context.Token is null || !OptionHintFirstWords.Contains(context.Token.ToString()))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        while (context.Token is not (Operator { Character: ')' } or Operator { Character: ',' }))
        {
            if (context.Token is Operator { Character: '(' })
            {
                SkipBalancedParens(context);
            }
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Skips a balanced run of parenthesized tokens starting from the
    /// current <c>(</c>. Cursor on entry: <c>(</c>. Cursor on exit: the
    /// matching <c>)</c>. Used by hint argument-list consumption where the
    /// payload's exact shape is irrelevant (e.g. <c>INDEX(IX_foo(c1, c2))</c>,
    /// <c>OPTIMIZE FOR (@p UNKNOWN, @q = 5)</c>).
    /// </summary>
    private static void SkipBalancedParens(ParserContext context)
    {
        var depth = 1;
        while (depth > 0)
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case Operator { Character: '(' }: depth++; break;
                case Operator { Character: ')' }: depth--; break;
            }
        }
    }
}
