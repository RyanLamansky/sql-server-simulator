using System.Collections.Frozen;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

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
    private static readonly FrozenSet<string> TableHintNames = new HashSet<string>
    {
        "NOLOCK", "READPAST", "READUNCOMMITTED", "READCOMMITTED", "READCOMMITTEDLOCK",
        "REPEATABLEREAD", "SERIALIZABLE", "SNAPSHOT", "HOLDLOCK", "UPDLOCK", "XLOCK",
        "TABLOCK", "TABLOCKX", "ROWLOCK", "PAGLOCK", "NOWAIT",
        "KEEPIDENTITY", "KEEPDEFAULTS", "NOEXPAND",
        "IGNORE_CONSTRAINTS", "IGNORE_TRIGGERS",
        "FORCESEEK", "FORCESCAN", "INDEX", "SPATIAL_WINDOW_MAX_CELLS",
        "READONLY", "REMOTE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// First-word vocabulary accepted inside <c>OPTION (...)</c>. Each entry
    /// is matched case-insensitively against the leading token; trailing
    /// words (<c>PLAN</c> / <c>ORDER</c> / <c>UNION</c> / <c>GROUP</c> /
    /// <c>JOIN</c>), arguments (<c>MAXDOP N</c> / <c>FAST N</c>), and nested
    /// parens (<c>OPTIMIZE FOR (...)</c>, <c>USE PLAN N'...'</c>) are
    /// consumed-and-discarded by <see cref="ConsumeOneOptionHint"/>. Two
    /// entries carry more than a skip: <c>MAXRECURSION</c> overrides the
    /// per-CTE recursion limit (argument parsed strictly), and
    /// <c>USE HINT('name')</c> validates its string argument by name
    /// (<see cref="ConsumeUseHint"/> — Msg 10715 on an unknown hint).
    /// </summary>
    private static readonly FrozenSet<string> OptionHintFirstWords = new HashSet<string>
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
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Valid <c>OPTION (USE HINT('name'))</c> hint names — the contents of
    /// <c>sys.dm_exec_valid_use_hints</c> on SQL Server 2025 (probed
    /// 2026-07-16). Case-insensitive (real accepts a lowercase argument). An
    /// argument outside this set raises Msg 10715 (<see cref="ConsumeUseHint"/>);
    /// every other OPTION hint discards without a name check, but real SQL
    /// Server validates USE HINT against this catalog, so the simulator does
    /// too. The list is version-specific and grows across releases — an app
    /// targeting a hint added after 2025 would need a refresh here, the same
    /// trust-region trade-off the table-hint accept-list carries.
    /// </summary>
    private static readonly FrozenSet<string> ValidUseHintNames = new HashSet<string>
    {
        "ABORT_QUERY_EXECUTION",
        "ASSUME_FIXED_MAX_SELECTIVITY_FOR_REGEXP",
        "ASSUME_FIXED_MIN_SELECTIVITY_FOR_REGEXP",
        "ASSUME_FULL_INDEPENDENCE_FOR_FILTER_ESTIMATES",
        "ASSUME_JOIN_PREDICATE_DEPENDS_ON_FILTERS",
        "ASSUME_MIN_SELECTIVITY_FOR_FILTER_ESTIMATES",
        "ASSUME_PARTIAL_CORRELATION_FOR_FILTER_ESTIMATES",
        "DISABLE_BATCH_MODE_ADAPTIVE_JOINS",
        "DISABLE_BATCH_MODE_MEMORY_GRANT_FEEDBACK",
        "DISABLE_CE_FEEDBACK",
        "DISABLE_DEFERRED_COMPILATION_TV",
        "DISABLE_DOP_FEEDBACK",
        "DISABLE_INTERLEAVED_EXECUTION_TVF",
        "DISABLE_MEMORY_GRANT_FEEDBACK_PERSISTENCE",
        "DISABLE_OPTIMIZED_NESTED_LOOP",
        "DISABLE_OPTIMIZED_PLAN_FORCING",
        "DISABLE_OPTIMIZER_ROWGOAL",
        "DISABLE_PARAMETER_SNIFFING",
        "DISABLE_RESULT_SET_CACHE",
        "DISABLE_ROW_MODE_MEMORY_GRANT_FEEDBACK",
        "DISABLE_TSQL_SCALAR_UDF_INLINING",
        "DISALLOW_BATCH_MODE",
        "ENABLE_HIST_AMENDMENT_FOR_ASC_KEYS",
        "ENABLE_QUERY_OPTIMIZER_HOTFIXES",
        "FORCE_DEFAULT_CARDINALITY_ESTIMATION",
        "FORCE_LEGACY_CARDINALITY_ESTIMATION",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_100",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_110",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_120",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_130",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_140",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_150",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_160",
        "QUERY_OPTIMIZER_COMPATIBILITY_LEVEL_170",
        "QUERY_PLAN_PROFILE",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recognized hint modifiers that affect phase-1b data-lock acquisition.
    /// Every other hint is parse-and-discard.
    /// </summary>
    internal struct TableHintInfo
    {
        /// <summary><c>NOLOCK</c> or <c>READUNCOMMITTED</c> — read source skips S acquisition (dirty-read).</summary>
        public bool NoLock;
        /// <summary>
        /// <c>HOLDLOCK</c> / <c>SERIALIZABLE</c> — equivalent per SQL Server docs
        /// ("Equivalent to SERIALIZABLE"). Acquires table-S tx-scoped, providing
        /// phantom prevention at table granularity (the simulator approximates
        /// SQL Server's key-range locks with a full table-S since no indexes
        /// model range structure). Read in combination with <see cref="UpdLock"/>
        /// or <see cref="XLock"/>, the row-mode wins but the lock is still
        /// tx-scoped (which it would have been anyway).
        /// </summary>
        public bool Serializable;
        /// <summary>
        /// <c>REPEATABLEREAD</c> — promotes row-S to tx-scoped retention so a
        /// re-read of the same row returns the same value. Does NOT prevent
        /// phantoms — concurrent inserts of new rows still succeed. Distinct
        /// from <see cref="Serializable"/>.
        /// </summary>
        public bool Repeatable;
        /// <summary><c>UPDLOCK</c> — read source takes row-U (read-with-intent-to-update) instead of row-S. Tx-scoped.</summary>
        public bool UpdLock;
        /// <summary><c>XLOCK</c> — read source takes row-X (treat read as a write). Tx-scoped.</summary>
        public bool XLock;
        /// <summary><c>READPAST</c> — skip blocked rows during scan instead of waiting. Default RC behavior is wait.</summary>
        public bool ReadPast;
        /// <summary><c>TABLOCK</c> — escalate to table-S (read) or table-X (write) instead of row-level.</summary>
        public bool TabLock;
        /// <summary><c>TABLOCKX</c> — escalate to table-X regardless of read / write direction.</summary>
        public bool TabLockX;
        /// <summary>
        /// <c>INDEX(…)</c>, <c>FORCESEEK</c>, <c>FORCESCAN</c> — index-selection
        /// hints. Tracked for Msg 1069 rejection on DML targets (real SQL
        /// Server forbids index hints on INSERT / UPDATE / DELETE / MERGE
        /// targets — they're only valid in a FROM clause or OPTION clause).
        /// The simulator has no index dispatch so the hint is otherwise
        /// parse-and-discard.
        /// </summary>
        public bool IndexHint;
        /// <summary>
        /// Captured <c>INDEX(arg [, …])</c> / <c>INDEX = arg</c> argument list,
        /// each entry either an integer index_id or a string index name. Null
        /// when no <c>INDEX</c> hint was seen, or for <c>FORCESEEK</c> /
        /// <c>FORCESCAN</c> (those have their own nested syntax that the
        /// simulator parse-and-discards). The caller validates existence
        /// against the resolved <c>HeapTable</c> via
        /// <see cref="ValidateIndexHintArguments(Collation, TableHintInfo, HeapTable, string)"/>.
        /// </summary>
        public List<IndexHintArgument>? IndexArguments;
    }

    /// <summary>
    /// One argument to an <c>INDEX(...)</c> / <c>INDEX = ...</c> hint:
    /// either an index_id (integer literal) or an index name (identifier or
    /// quoted string). Captured during table-hint parsing; validated at the
    /// FROM-source / JOIN-RHS call site once the target table is resolved
    /// (Msg 307 / Msg 308 verbatim).
    /// </summary>
    internal readonly struct IndexHintArgument
    {
        public readonly int? Id;
        public readonly string? Name;
        private IndexHintArgument(int? id, string? name) { Id = id; Name = name; }
        public static IndexHintArgument ForId(int id) => new(id, null);
        public static IndexHintArgument ForName(string name) => new(null, name);
    }

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
    /// <c>;</c> / null). Returns a <see cref="TableHintInfo"/> capturing the
    /// hint modifiers phase 1a's data-lock acquisition acts on; every other
    /// recognized hint discards.
    /// </summary>
    internal static TableHintInfo ParseOptionalTableHints(ParserContext context, bool allowLegacyParenForm = true, bool commitOnLegacyParen = false)
    {
        var info = default(TableHintInfo);
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            ConsumeTableHintListBody(context, ref info);
            return info;
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
                ConsumeTableHintListBody(context, ref info);
                return info;
            }
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            if (context.Token is not null && TableHintNames.Contains(context.Token.ToString()))
            {
                ConsumeTableHintListBody(context, ref info);
                return info;
            }
            context.RestoreCheckpoint(checkpoint);
        }
        return info;
    }

    /// <summary>
    /// Walks the comma-separated body of a table-hint list. Cursor on entry:
    /// the first hint-name token (immediately after the opening <c>(</c>).
    /// Cursor on exit: the next token after the closing <c>)</c>.
    /// </summary>
    private static void ConsumeTableHintListBody(ParserContext context, ref TableHintInfo info)
    {
        while (true)
        {
            ConsumeOneTableHint(context, ref info);
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                ValidateHintCombinations(info);
                return;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Cross-hint combination validation. Msg 1047 fires when
    /// <c>NOLOCK</c> / <c>READUNCOMMITTED</c> appears alongside any locking
    /// hint that would require a real lock (UPDLOCK, XLOCK, HOLDLOCK,
    /// SERIALIZABLE, REPEATABLEREAD, TABLOCKX). Probe-confirmed against SQL
    /// Server 2025 (2026-05-14): the message wording is fixed
    /// ("Conflicting locking hints specified.") regardless of which pair
    /// actually conflicted.
    /// </summary>
    private static void ValidateHintCombinations(TableHintInfo info)
    {
        if (!info.NoLock)
            return;
        if (info.UpdLock || info.XLock || info.Serializable || info.Repeatable || info.TabLockX)
            throw SimulatedSqlException.ConflictingLockingHints();
    }

    /// <summary>
    /// Validates DML-target-specific hint restrictions. Called by INSERT /
    /// UPDATE / DELETE / MERGE target sites after
    /// <see cref="ParseOptionalTableHints"/> returns. Msg 1065 rejects
    /// <c>NOLOCK</c> / <c>READUNCOMMITTED</c>; Msg 1069 rejects
    /// <c>INDEX(…)</c> / <c>FORCESEEK</c> / <c>FORCESCAN</c>. Both
    /// probe-confirmed verbatim.
    /// </summary>
    internal static void ValidateDmlTargetHints(TableHintInfo info)
    {
        if (info.NoLock)
            throw SimulatedSqlException.NoLockHintNotAllowedOnDmlTarget();
        if (info.IndexHint)
            throw SimulatedSqlException.IndexHintsOnlyInFromOrOption();
    }

    /// <summary>
    /// Validates the captured <c>INDEX</c>-hint arguments against the
    /// resolved target table. Called from FROM-source / JOIN-RHS heap-table
    /// paths after the table has been resolved (DML targets short-circuit
    /// earlier via <see cref="ValidateDmlTargetHints"/> / Msg 1069 — index
    /// existence is never reached on those sites). Integer-form id rules
    /// (probe-confirmed against SQL Server 2025): <c>0</c> is always valid
    /// (the "heap scan" reference, accepted even on clustered tables);
    /// <c>N &gt;= 1</c> is valid iff <c>N &lt;= sys.indexes</c> row-count for the
    /// table excluding the heap row, equivalently
    /// <c>KeyConstraints.Count + Indexes.Count</c>. Name form matches
    /// case-insensitively against PRIMARY KEY / UNIQUE constraint names
    /// (<see cref="HeapTable.KeyConstraints"/>) plus <c>CREATE INDEX</c>
    /// entries (<see cref="HeapTable.Indexes"/>). The first failing
    /// argument raises Msg 307 (id form) or Msg 308 (name form) verbatim;
    /// remaining arguments don't run.
    /// </summary>
    internal static void ValidateIndexHintArguments(Collation collation, TableHintInfo info, HeapTable table, string qualifiedTableName)
    {
        if (info.IndexArguments is not { } args)
            return;
        var maxValidId = table.KeyConstraints.Count + table.Indexes.Count;
        foreach (var arg in args)
        {
            if (arg.Id is { } id)
            {
                if (id != 0 && (id < 1 || id > maxValidId))
                    throw SimulatedSqlException.IndexHintIdNotFound(id, qualifiedTableName);
                continue;
            }
            var name = arg.Name!;
            var found = false;
            foreach (var kc in table.KeyConstraints)
            {
                if (collation.Equals(kc.Name, name))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                foreach (var idx in table.Indexes)
                {
                    if (collation.Equals(idx.Name, name))
                    {
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
                throw SimulatedSqlException.IndexHintNameNotFound(name, qualifiedTableName);
        }
    }

    /// <summary>
    /// Validates and consumes a single table-hint entry: <c>name</c>,
    /// <c>name = literal</c>, or <c>name (arg-list)</c>. Unknown name →
    /// Msg 321. Cursor advances to the trailing <c>,</c> or <c>)</c>. Updates
    /// <paramref name="info"/> for the phase-1a-recognized modifier set.
    /// </summary>
    private static void ConsumeOneTableHint(ParserContext context, ref TableHintInfo info)
    {
        if (context.Token is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var sourceSpan = context.Token.Source;
        if (!TableHintNames.Contains(sourceSpan.ToString()))
            throw SimulatedSqlException.UnrecognizedTableHint(sourceSpan);
        // Recognize the phase-1b lock-affecting hints. NOLOCK / READUNCOMMITTED
        // skip S acquisition (dirty-read). HOLDLOCK / REPEATABLEREAD /
        // SERIALIZABLE retain row-S to transaction end (so re-read sees the
        // same value). UPDLOCK takes row-U (read-with-intent-to-update) so
        // a subsequent UPDATE inside the same tx doesn't deadlock with
        // another connection's S-then-U upgrade. XLOCK treats the read like
        // a write (row-X tx-scoped). TABLOCK / TABLOCKX escalates to table
        // granularity. READPAST skips blocked rows instead of waiting.
        // Everything else parses-and-discards.
        if (sourceSpan.Equals("NOLOCK", StringComparison.OrdinalIgnoreCase) || sourceSpan.Equals("READUNCOMMITTED", StringComparison.OrdinalIgnoreCase))
        {
            info.NoLock = true;
        }
        else if (sourceSpan.Equals("HOLDLOCK", StringComparison.OrdinalIgnoreCase)
            || sourceSpan.Equals("SERIALIZABLE", StringComparison.OrdinalIgnoreCase))
        {
            info.Serializable = true;
        }
        else if (sourceSpan.Equals("REPEATABLEREAD", StringComparison.OrdinalIgnoreCase))
        {
            info.Repeatable = true;
        }
        else if (sourceSpan.Equals("UPDLOCK", StringComparison.OrdinalIgnoreCase))
        {
            info.UpdLock = true;
        }
        else if (sourceSpan.Equals("XLOCK", StringComparison.OrdinalIgnoreCase))
        {
            info.XLock = true;
        }
        else if (sourceSpan.Equals("READPAST", StringComparison.OrdinalIgnoreCase))
        {
            info.ReadPast = true;
        }
        else if (sourceSpan.Equals("TABLOCK", StringComparison.OrdinalIgnoreCase))
        {
            info.TabLock = true;
        }
        else if (sourceSpan.Equals("TABLOCKX", StringComparison.OrdinalIgnoreCase))
        {
            info.TabLockX = true;
        }
        else if (sourceSpan.Equals("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            info.IndexHint = true;
            context.MoveNextRequired();
            ConsumeIndexHintArguments(context, ref info);
            return;
        }
        else if (sourceSpan.Equals("FORCESEEK", StringComparison.OrdinalIgnoreCase)
            || sourceSpan.Equals("FORCESCAN", StringComparison.OrdinalIgnoreCase))
        {
            info.IndexHint = true;
        }
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
    /// Captures the argument list for an <c>INDEX</c> hint. Cursor on entry:
    /// the token immediately after <c>INDEX</c> (the opening <c>(</c> or
    /// <c>=</c>). Cursor on exit: the next un-consumed token after the
    /// closing <c>)</c> (paren form) or after the single literal (=-form).
    /// Each argument is a non-negative integer literal (captured as
    /// <see cref="IndexHintArgument.ForId"/>) or an identifier (captured as
    /// <see cref="IndexHintArgument.ForName"/>). Real SQL Server rejects
    /// negative-int and other shapes with Msg 102; the simulator surfaces
    /// the same via <c>SimulatedSqlException.SyntaxErrorNear</c>.
    /// </summary>
    private static void ConsumeIndexHintArguments(ParserContext context, ref TableHintInfo info)
    {
        if (context.Token is Operator { Character: '=' })
        {
            context.MoveNextRequired();
            CaptureOneIndexArgument(context, ref info);
            context.MoveNextRequired();
            return;
        }
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        while (true)
        {
            CaptureOneIndexArgument(context, ref info);
            context.MoveNextRequired();
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextRequired();
                return;
            }
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Reads exactly one <c>INDEX</c>-hint argument at the current cursor
    /// position: a non-negative integer literal becomes
    /// <see cref="IndexHintArgument.ForId"/>; an identifier (or quoted
    /// string) becomes <see cref="IndexHintArgument.ForName"/>. Anything
    /// else raises Msg 102. Does not advance the cursor — the caller does
    /// that after capture so the comma / paren walk happens in one place.
    /// </summary>
    private static void CaptureOneIndexArgument(ParserContext context, ref TableHintInfo info)
    {
        info.IndexArguments ??= [];
        switch (context.Token)
        {
            case Numeric { Value: { IsNull: false } value }:
                info.IndexArguments.Add(IndexHintArgument.ForId(value.AsInt32));
                return;
            case Name nameToken:
                info.IndexArguments.Add(IndexHintArgument.ForName(nameToken.Source.ToString()));
                return;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
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
        // USE HINT is the one OPTION hint whose argument SQL Server validates
        // by name (Msg 10715 on an unknown hint). Detect `USE HINT` specifically
        // — `USE PLAN N'…'` and any other USE-prefixed hint fall through to the
        // generic skip below — and hand off before the generic path consumes it.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Use })
        {
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            var isUseHint = context.Token is Name useHintKeyword
                && useHintKeyword.Source.Equals("HINT", StringComparison.OrdinalIgnoreCase);
            context.RestoreCheckpoint(checkpoint);
            if (isUseHint)
            {
                ConsumeUseHint(context);
                return;
            }
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
    /// Consumes and validates a <c>USE HINT ( 'name' [, 'name'] … )</c> clause.
    /// Cursor on entry: the <c>USE</c> keyword. Cursor on exit: the token after
    /// the closing <c>)</c>. Each argument must be a non-null string literal
    /// (probe-confirmed: an empty argument list or a non-string argument raises
    /// the generic Msg 102, e.g. <c>USE HINT()</c> → <c>near ')'</c>,
    /// <c>USE HINT(123)</c> → <c>near '123'</c>) whose value is in
    /// <see cref="ValidUseHintNames"/> (case-insensitive) — an unknown name
    /// raises Msg 10715. The hint itself is otherwise parse-and-discard.
    /// </summary>
    private static void ConsumeUseHint(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        while (true)
        {
            if (context.GetNextRequired() is not Literal { Value: { IsNull: false } value } || !SqlType.IsStringCategory(value.Type))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (!ValidUseHintNames.Contains(value.AsString))
                throw SimulatedSqlException.InvalidUseHint(value.AsString);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ')' }:
                    context.MoveNextRequired();
                    return;
                case Operator { Character: ',' }:
                    continue;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
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

    /// <summary>
    /// Consumes an optional <c>TABLESAMPLE [SYSTEM] (n [PERCENT | ROWS])
    /// [REPEATABLE (seed)]</c> clause on a FROM source when present, leaving the
    /// cursor at the next un-consumed lookahead token (a no-op otherwise).
    /// The sample is <b>discarded</b>: the simulator returns every row, a
    /// deterministic approximation of SQL Server's nondeterministic random
    /// sample (which the wire contract permits — a sample is any subset).
    /// </summary>
    private static void ParseOptionalTableSample(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.TableSample })
            return;
        var collation = context.Batch.CurrentDatabase.Collation;
        context.MoveNextRequired();
        // Optional SYSTEM sampling-method identifier (contextual).
        if (context.Token is Name method && collation.Equals(method.Value, "SYSTEM"))
            context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        SkipBalancedParens(context);
        context.MoveNextOptional();
        // Optional REPEATABLE (seed).
        if (context.Token is Name repeatable && collation.Equals(repeatable.Value, "REPEATABLE"))
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            SkipBalancedParens(context);
            context.MoveNextOptional();
        }
    }
}
