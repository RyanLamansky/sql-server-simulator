using System.Collections.Frozen;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Table hints (<c>WITH (NOLOCK [, …])</c> on FROM sources, JOIN-RHS, and
/// INSERT / UPDATE / DELETE / MERGE targets). The lock hints drive the lock
/// manager and the index hints are checked against the table and the
/// predicates; the statement-level <c>OPTION (…)</c> clause lives in
/// <c>Selection.StatementHints.cs</c>.
/// </summary>
/// <remarks>
/// Closed accept-list per probe (SQL Server 2025, 2026-05-14): an unknown
/// table-hint name is Msg 321
/// (<c>"&lt;name&gt;" is not a recognized table hints option.</c>).
/// </remarks>
internal sealed partial class Selection
{
    /// <summary>
    /// What one recognized table-hint name contributes to the
    /// <see cref="TableHintInfo"/> the clause builds. Every name real accepts
    /// carries one of these; the ones the simulator has no execution effect
    /// for carry <see cref="TableHintKind.Discard"/>, which is what keeps
    /// the accept-list and the modifier dispatch a single table.
    /// </summary>
    private enum TableHintKind
    {
        /// <summary>Recognized, no modeled effect — parsed and discarded.</summary>
        Discard,
        NoLock,
        Serializable,
        Repeatable,
        UpdLock,
        XLock,
        ReadPast,
        NoWait,
        TabLock,
        TabLockX,
        NoExpand,

        /// <summary><c>READCOMMITTED</c>.</summary>
        ReadCommitted,

        /// <summary><c>READCOMMITTEDLOCK</c>.</summary>
        ReadCommittedLock,

        /// <summary><c>INDEX</c>, whose argument list is captured for validation against the resolved table.</summary>
        Index,

        /// <summary><c>FORCESEEK</c>, whose nested index name and seek columns are captured.</summary>
        ForceSeek,

        /// <summary><c>FORCESCAN</c>, which takes no arguments.</summary>
        ForceScan,

        /// <summary><c>ROWLOCK</c>.</summary>
        RowLock,

        /// <summary><c>PAGLOCK</c>.</summary>
        PagLock,

        /// <summary>
        /// <c>IGNORE_CONSTRAINTS</c> / <c>IGNORE_TRIGGERS</c>: bulk-load hints
        /// a read refuses (Msg 8171).
        /// </summary>
        BulkLoadOnly,

        /// <summary>
        /// <c>KEEPIDENTITY</c> / <c>KEEPDEFAULTS</c>: bulk-load hints a read
        /// takes and discards, which a write's target refuses (Msg 8171).
        /// </summary>
        BulkLoadKeep,
    }

    /// <summary>
    /// Table hints accepted inside <c>WITH (...)</c> on a base-table /
    /// view / table-variable source or after an UPDATE / DELETE target,
    /// each mapped to the modifier it sets. Case-insensitive. Argument
    /// shapes are validated by <see cref="ConsumeOneTableHint"/> (bare /
    /// <c>= literal</c> / <c>(arg-list)</c>).
    /// </summary>
    /// <remarks>
    /// Sourced from SQL Server's "Table Hints (Transact-SQL)" docs plus
    /// the probe-confirmed entries. <c>READONLY</c>, a table-valued
    /// parameter's declaration keyword, is no table hint (Msg 321, probed
    /// 2026-10-05 against SQL Server 2025).
    /// </remarks>
    private static readonly FrozenDictionary<string, TableHintKind> TableHintNames = new Dictionary<string, TableHintKind>
    {
        ["NOLOCK"] = TableHintKind.NoLock,
        ["READUNCOMMITTED"] = TableHintKind.NoLock,
        ["HOLDLOCK"] = TableHintKind.Serializable,
        ["SERIALIZABLE"] = TableHintKind.Serializable,
        ["REPEATABLEREAD"] = TableHintKind.Repeatable,
        ["UPDLOCK"] = TableHintKind.UpdLock,
        ["XLOCK"] = TableHintKind.XLock,
        ["READPAST"] = TableHintKind.ReadPast,
        ["NOWAIT"] = TableHintKind.NoWait,
        ["TABLOCK"] = TableHintKind.TabLock,
        ["TABLOCKX"] = TableHintKind.TabLockX,
        ["NOEXPAND"] = TableHintKind.NoExpand,
        ["INDEX"] = TableHintKind.Index,
        ["FORCESEEK"] = TableHintKind.ForceSeek,
        ["FORCESCAN"] = TableHintKind.ForceScan,
        ["IGNORE_CONSTRAINTS"] = TableHintKind.BulkLoadOnly,
        ["IGNORE_TRIGGERS"] = TableHintKind.BulkLoadOnly,
        ["KEEPDEFAULTS"] = TableHintKind.BulkLoadKeep,
        ["KEEPIDENTITY"] = TableHintKind.BulkLoadKeep,
        ["PAGLOCK"] = TableHintKind.PagLock,
        ["READCOMMITTED"] = TableHintKind.ReadCommitted,
        ["READCOMMITTEDLOCK"] = TableHintKind.ReadCommittedLock,
        ["REMOTE"] = TableHintKind.Discard,
        ["ROWLOCK"] = TableHintKind.RowLock,
        ["SNAPSHOT"] = TableHintKind.Discard,
        ["SPATIAL_WINDOW_MAX_CELLS"] = TableHintKind.Discard,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Span-keyed view of <see cref="TableHintNames"/>. A hint name arrives as
    /// a slice of the command text, so looking it up through the alternate key
    /// keeps the parse from materializing a string per hint.
    /// </summary>
    private static readonly FrozenDictionary<string, TableHintKind>.AlternateLookup<ReadOnlySpan<char>> TableHintLookup =
        TableHintNames.GetAlternateLookup<ReadOnlySpan<char>>();

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
        /// ("Equivalent to SERIALIZABLE"). The read takes key-range locks on the
        /// index keys its predicate reaches, as a SERIALIZABLE session's read
        /// does. Read in combination with <see cref="UpdLock"/> or
        /// <see cref="XLock"/>, the range mode follows the hint.
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
        /// <summary>
        /// <c>NOWAIT</c> — every lock this statement takes on the hinted table
        /// gets a zero timeout, so a conflict raises Msg 1222 instead of
        /// waiting. Real documents it as "equivalent to specifying SET
        /// LOCK_TIMEOUT 0 for a specific table".
        /// </summary>
        public bool NoWait;
        /// <summary><c>TABLOCK</c> — escalate to table-S (read) or table-X (write) instead of row-level.</summary>
        public bool TabLock;
        /// <summary>
        /// <c>READCOMMITTED</c> — a READ COMMITTED read of this table whatever
        /// the session's level, so under a READ UNCOMMITTED session it waits
        /// out writers (probed 2026-10-03 against SQL Server 2025); under
        /// <c>READ_COMMITTED_SNAPSHOT</c> it still reads row versions.
        /// </summary>
        public bool ReadCommitted;
        /// <summary>
        /// <c>READCOMMITTEDLOCK</c> — a locking READ COMMITTED read, the hint
        /// that takes a read off row versioning under
        /// <c>READ_COMMITTED_SNAPSHOT</c> or <c>SNAPSHOT</c>.
        /// </summary>
        public bool ReadCommittedLock;

        /// <summary>
        /// A hint that makes this table's read a locking read whatever the
        /// session's level: under READ UNCOMMITTED it waits out writers rather
        /// than reading dirty, and under <c>READ_COMMITTED_SNAPSHOT</c> or
        /// <c>SNAPSHOT</c> it reads the latest committed row under its locks
        /// rather than a row version (probed 2026-10-03 against SQL Server
        /// 2025). <c>TABLOCK</c>, <c>ROWLOCK</c>, <c>PAGLOCK</c> and
        /// <c>NOWAIT</c> aren't among them: those read dirty under READ
        /// UNCOMMITTED and versioned under row versioning, the versioned
        /// <c>TABLOCK</c> without its table S.
        /// </summary>
        public readonly bool LocksRead => this.UpdLock || this.XLock || this.TabLockX || this.Serializable || this.Repeatable || this.ReadCommittedLock;
        /// <summary><c>TABLOCKX</c> — escalate to table-X regardless of read / write direction.</summary>
        public bool TabLockX;
        /// <summary>
        /// <c>NOEXPAND</c> — read an indexed view's materialized index instead
        /// of expanding its body. The simulator always expands, so the hint has
        /// no execution effect; it is tracked because real's SET-option gate
        /// covers it (Msg 1934 for a <c>NOEXPAND</c> reference to an indexed
        /// view from a session whose options the gate refuses).
        /// </summary>
        public bool NoExpand;
        /// <summary>
        /// <c>INDEX(…)</c>, <c>FORCESEEK</c>, <c>FORCESCAN</c> — index-selection
        /// hints. Tracked for Msg 1069 rejection on DML targets (real SQL
        /// Server forbids index hints on INSERT / UPDATE / DELETE / MERGE
        /// targets — they're only valid in a FROM clause or OPTION clause).
        /// They choose no access path here; the plans real can't build under
        /// them are refused (Msg 8622).
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
        /// <summary>
        /// The seek-column list of a nested <c>FORCESEEK(index (col [, …]))</c>,
        /// in written order. Null when the hint carried no nested form. Real
        /// requires the list to be a leading prefix of the named index's
        /// <i>key</i> columns and validates it once the table has resolved —
        /// see <see cref="ValidateForceSeekColumns"/>.
        /// </summary>
        public List<string>? ForceSeekColumns;
        /// <summary>
        /// <c>FORCESEEK</c> — the read must seek, which real refuses with
        /// Msg 8622 when no predicate offers one.
        /// </summary>
        public bool ForceSeek;
        /// <summary><c>FORCESCAN</c> — refused beside <c>FORCESEEK</c> (Msg 10746).</summary>
        public bool ForceScan;
        /// <summary>
        /// An <c>INDEX</c> hint was written, as opposed to the index a nested
        /// <c>FORCESEEK</c> names — the two can't meet (Msg 10747).
        /// </summary>
        public bool IndexNamed;
        /// <summary>
        /// <c>SNAPSHOT</c> — a memory-optimized table read at SNAPSHOT
        /// isolation, the hint a disk-based table refuses (Msg 367).
        /// </summary>
        public bool Snapshot;
        /// <summary>
        /// The first hint written that a memory-optimized table refuses (Msg
        /// 10794), lower-cased as real names it; null when none was.
        /// </summary>
        public string? RefusedByMemoryOptimized;
        /// <summary><c>ROWLOCK</c>, which no other granularity hint may join (Msg 1047).</summary>
        public bool RowLock;
        /// <summary><c>PAGLOCK</c>, which no other granularity hint may join (Msg 1047).</summary>
        public bool PagLock;
        /// <summary>
        /// The first <c>IGNORE_CONSTRAINTS</c> / <c>IGNORE_TRIGGERS</c>
        /// written, as written; a read refuses it (Msg 8171).
        /// </summary>
        public string? BulkLoadOnlyHint;
        /// <summary>
        /// The first of the four bulk-load hints written, as written; a
        /// write's target refuses it (Msg 8171).
        /// </summary>
        public string? BulkLoadHint;
        /// <summary>
        /// The first hint written that changes what the read means — a lock,
        /// isolation or granularity hint, or <c>NOEXPAND</c> — as written; an
        /// <c>OPTION (TABLE HINT …)</c> may carry one only where the source's
        /// own <c>WITH</c> clause does (Msg 8722).
        /// </summary>
        public string? FirstSemanticHint;
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
        if (context.Token is ReservedKeyword { Keyword: Keyword.With } && !AtWithCheckOption(context))
        {
            // A name after the hint position's WITH is a CTE the previous
            // statement ran into (Msg 336, probed 2026-09-24).
            if (context.GetNextRequired() is Name cteName)
                throw SimulatedSqlException.CteAfterUnterminatedStatement(cteName.Value);
            if (context.Token is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
            ConsumeTableHintListBody(context, ref info, legacyForm: false);
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
                // A query there is the syntax error at its SELECT (probed
                // 2026-10-01: `FROM t x (SELECT 3)` is Msg 156).
                if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Select })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                ConsumeTableHintListBody(context, ref info, legacyForm: true);
                return info;
            }
            var checkpoint = context.SaveCheckpoint();
            context.MoveNextRequired();
            if (context.Token is not null && TableHintLookup.ContainsKey(context.Token.Source))
            {
                ConsumeTableHintListBody(context, ref info, legacyForm: true);
                return info;
            }
            context.RestoreCheckpoint(checkpoint);
        }
        return info;
    }

    /// <summary>
    /// The FROM-source / JOIN-RHS entry point: parses the optional table-hint
    /// clause, then settles what a parenthesized run that <i>isn't</i> a
    /// recognized hint list means. Real decides that on the alias — with one
    /// written, the parens are unambiguously the legacy hint form and an
    /// unknown name inside is <b>Msg 321</b>; without one, the parens are an
    /// argument list, so each name inside reports its own <b>Msg 207</b> (the
    /// source itself is not in scope for its own arguments) and the run closes
    /// with <b>Msg 215</b>. An <c>INDEX</c> hint written that way is
    /// <b>Msg 1018</b> instead, real's own "a WITH keyword is now required".
    /// All three probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static TableHintInfo ParseOptionalFromSourceHints(ParserContext context, bool aliasConsumed, string writtenObjectName)
    {
        var before = context.Token;
        var info = ParseOptionalTableHints(context, allowLegacyParenForm: true, commitOnLegacyParen: aliasConsumed);
        // Any hint keeps a view from being indexed (Msg 10140).
        if (!ReferenceEquals(before, context.Token) && context.IndexedViewShapeCollector is { } shape)
            shape.HasTableHint = true;
        if (context.Token is Operator { Character: '(' })
            RefuseArgumentList(context, writtenObjectName, reportsNames: true);
        return info;
    }

    /// <summary>
    /// The argument list a FROM source that is no function was written with —
    /// see <see cref="ParseOptionalFromSourceHints"/>. A query opening it is
    /// the syntax error at its <c>SELECT</c>, which also keeps a parenthesized
    /// query statement after the source from reading as one (probed 2026-10-01
    /// against SQL Server 2025: <c>FROM t (SELECT 3)</c> is Msg 156).
    /// </summary>
    /// <param name="context">Parser state, on the list's <c>(</c>.</param>
    /// <param name="writtenObjectName">The source as written, which Msg 215 names.</param>
    /// <param name="reportsNames">
    /// Whether a name inside reports its Msg 207 ahead of the Msg 215: a
    /// table's or view's does, a common table expression's doesn't.
    /// </param>
    internal static void RefuseArgumentList(ParserContext context, string writtenObjectName, bool reportsNames)
    {
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // A bare name is a column reference; one followed by `(` is a function
        // call and one followed by or preceded by `.` is part of a qualified
        // name, neither of which real answers with Msg 207 here. The pending
        // slot defers the verdict until the next token settles it.
        var errors = new List<SimulatedSqlException>();
        var depth = 1;
        Name? pending = null;
        var afterDot = false;
        while (depth > 0)
        {
            var token = context.Token;
            if (pending is not null && token is not Operator { Character: '(' or '.' })
            {
                if (reportsNames)
                    errors.Add(SimulatedSqlException.InvalidColumnName(pending.Value));
                pending = null;
            }

            switch (token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    pending = null;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
                case Name argumentName when !afterDot:
                    pending = argumentName;
                    break;
            }

            afterDot = token is Operator { Character: '.' };
            if (depth > 0)
                context.MoveNextRequired();
        }

        context.MoveNextOptional();
        errors.Add(SimulatedSqlException.ParametersSuppliedForNonFunction(writtenObjectName));
        throw SimulatedSqlException.Aggregate(errors);
    }

    /// <summary>
    /// A table variable takes no table hint (probed 2026-09-26 against SQL
    /// Server 2025): a <c>WITH</c> after it ends the statement, where the
    /// dispatch loop's Msg 319 for a CTE without its semicolon answers it, and
    /// the legacy <c>(hint)</c> form is Msg 1018 for a recognized hint name and
    /// Msg 102 for anything else.
    /// </summary>
    internal static void RejectTableVariableHints(ParserContext context)
    {
        if (context.Token is not Operator { Character: '(' })
            return;
        var inside = context.GetNextRequired();
        throw TableHintLookup.ContainsKey(inside.Source)
            ? SimulatedSqlException.TableHintNeedsWithKeyword(inside.Source)
            : SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Walks the comma-separated body of a table-hint list. Cursor on entry:
    /// the first hint-name token (immediately after the opening <c>(</c>).
    /// Cursor on exit: the next token after the closing <c>)</c>.
    /// </summary>
    private static void ConsumeTableHintListBody(ParserContext context, ref TableHintInfo info, bool legacyForm)
    {
        while (true)
        {
            ConsumeOneTableHint(context, ref info, legacyForm);
            if (context.Token is Operator { Character: ')' })
            {
                context.MoveNextOptional();
                ValidateHintCombinations(info);
                return;
            }
            // Real takes a hint written straight after another without the
            // comma (probed 2026-10-05 against SQL Server 2025).
            if (context.Token is not null && TableHintLookup.ContainsKey(context.Token.Source))
                continue;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();
        }
    }

    /// <summary>
    /// Cross-hint combination validation. Msg 10746 refuses <c>FORCESEEK</c>
    /// beside <c>FORCESCAN</c>, Msg 10747 a nested <c>FORCESEEK(ix(cols))</c>
    /// beside an <c>INDEX</c> hint, Msg 10750 <c>FORCESCAN</c> beside more
    /// than one index (probed 2026-09-28 against SQL Server 2025).
    /// Msg 1047 refuses two isolation levels (<c>NOLOCK</c> /
    /// <c>READUNCOMMITTED</c>, <c>READCOMMITTED</c>, <c>READCOMMITTEDLOCK</c>,
    /// <c>REPEATABLEREAD</c>, <c>SERIALIZABLE</c> / <c>HOLDLOCK</c>,
    /// <c>SNAPSHOT</c>), two granularities (<c>ROWLOCK</c>, <c>PAGLOCK</c>,
    /// <c>TABLOCK</c>, <c>TABLOCKX</c>), <c>UPDLOCK</c> beside <c>XLOCK</c>,
    /// and a dirty read beside any lock it can't take; Msg 650 refuses
    /// <c>READPAST</c> beside a dirty read or <c>SERIALIZABLE</c> (the whole
    /// pair matrix probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    private static void ValidateHintCombinations(TableHintInfo info)
    {
        if (info.ForceSeek && info.ForceScan)
            throw SimulatedSqlException.ForceScanWithForceSeek();
        if (info.ForceSeekColumns is not null && info.IndexNamed)
            throw SimulatedSqlException.ParameterizedForceSeekWithIndexHint();
        if (info.ForceScan && info.IndexArguments is { Count: > 1 })
            throw SimulatedSqlException.ForceScanWithSeveralIndexes();
        var levels = (info.NoLock ? 1 : 0) + (info.ReadCommitted ? 1 : 0) + (info.ReadCommittedLock ? 1 : 0)
            + (info.Repeatable ? 1 : 0) + (info.Serializable ? 1 : 0) + (info.Snapshot ? 1 : 0);
        var granularities = (info.RowLock ? 1 : 0) + (info.PagLock ? 1 : 0) + (info.TabLock ? 1 : 0) + (info.TabLockX ? 1 : 0);
        if (levels > 1
            || granularities > 1
            || (info.UpdLock && info.XLock)
            || (info.NoLock && (info.UpdLock || info.XLock || granularities > 0)))
        {
            throw SimulatedSqlException.ConflictingLockingHints();
        }
        if (info.ReadPast && (info.NoLock || info.Serializable))
            throw SimulatedSqlException.ReadPastOutsideReadCommitted();
    }

    /// <summary>
    /// Validates DML-target-specific hint restrictions. Called by INSERT /
    /// UPDATE / DELETE / MERGE target sites after
    /// <see cref="ParseOptionalTableHints"/> returns. Msg 1065 rejects
    /// <c>NOLOCK</c> / <c>READUNCOMMITTED</c>; on an INSERT, UPDATE or DELETE
    /// target Msg 10724 rejects <c>FORCESEEK</c>, Msg 10745 <c>FORCESCAN</c>
    /// and Msg 1069 <c>INDEX(…)</c>, which a MERGE target takes and checks
    /// against its table; Msg 4102 rejects <c>READPAST</c> on an INSERT
    /// target, and Msg 8171 any bulk-load hint (probed 2026-10-05 against SQL
    /// Server 2025).
    /// </summary>
    /// <param name="info">The hints the target carries.</param>
    /// <param name="writtenName">The target as written, which Msg 8171 names.</param>
    /// <param name="verb">The statement's verb, upper case.</param>
    internal static void ValidateDmlTargetHints(TableHintInfo info, string writtenName, string verb)
    {
        if (info.NoLock)
            throw SimulatedSqlException.NoLockHintNotAllowedOnDmlTarget();
        if (verb != "MERGE")
        {
            if (info.ForceSeek)
                throw SimulatedSqlException.ForceSeekOnDmlTarget();
            if (info.ForceScan)
                throw SimulatedSqlException.ForceScanOnDmlTarget();
            if (info.IndexHint)
                throw SimulatedSqlException.IndexHintsOnlyInFromOrOption();
        }
        if (info.ReadPast && verb == "INSERT")
            throw SimulatedSqlException.ReadPastOnInsertTarget();
        if (info.BulkLoadHint is { } bulkHint)
            throw SimulatedSqlException.BulkTableHintInvalid(bulkHint, writtenName);
    }

    /// <summary>
    /// A view's table hints: <c>NOEXPAND</c> needs an enabled unique clustered
    /// index on it (Msg 8171 at the line after the view's name — real's line,
    /// in a batch and a module alike), and an index
    /// hint is then checked against the view's indexes (Msg 308); without
    /// <c>NOEXPAND</c> an index hint is ignored with the warning Msg 4430
    /// (all probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static void ValidateViewIndexHints(ParserContext context, Schemas.View view, TableHintInfo info, string writtenName, int nameLine)
    {
        if (info.NoExpand)
        {
            if (!view.Indexes.Exists(static index => index is { IsUnique: true, IsClustered: true, IsDisabled: false }))
                throw SimulatedSqlException.NoExpandHintInvalid(writtenName, state: 2).PinLine(nameLine + context.Batch.LineOffset);
            foreach (var argument in info.IndexArguments ?? [])
            {
                if (argument.Name is { } name && !view.Indexes.Exists(index => context.Batch.CurrentDatabase.Collation.Equals(index.Name, name)))
                    throw SimulatedSqlException.IndexHintNameNotFound(name, writtenName);
            }
            return;
        }
        if (info.IndexArguments is not null && !context.Batch.IsSkipping)
            context.Connection.PendingMessages.Enqueue(SimulatedSqlException.ViewIndexHintsIgnoredMessage(context.Batch, writtenName));
    }

    /// <summary>
    /// Validates the captured <c>INDEX</c>-hint arguments against the
    /// resolved target table, in written order, the first failing argument
    /// raising. An id is checked against the table's <c>sys.indexes</c> ids:
    /// <c>0</c> is always valid, the heap row's <c>1</c> is not (Msg 307), and
    /// a disabled index's id is Msg 316. A name is matched against the PRIMARY
    /// KEY / UNIQUE constraints and the indexes, a disabled one being Msg 315
    /// and an XML index's Msg 309; anything else is Msg 308 (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static void ValidateIndexHintArguments(Collation collation, TableHintInfo info, HeapTable table, string qualifiedTableName)
    {
        if (info.IndexArguments is not { } args)
            return;
        List<IndexIdentity>? identities = null;
        foreach (var arg in args)
        {
            if (arg.Id is { } id)
            {
                if (id == 0)
                    continue;
                identities ??= table.IndexIdentities();
                var identity = identities.Find(candidate => candidate.IndexId == id && (candidate.Constraint is not null || candidate.Index is not null));
                if (identity.Constraint is null && identity.Index is null)
                    throw SimulatedSqlException.IndexHintIdNotFound(id, qualifiedTableName);
                if (identity.Constraint?.IsDisabled ?? identity.Index!.IsDisabled)
                    throw SimulatedSqlException.IndexHintIdDisabled(id, qualifiedTableName);
                continue;
            }
            var name = arg.Name!;
            if (table.KeyConstraints.Find(key => collation.Equals(key.Name, name)) is { } constraint)
            {
                if (constraint.IsDisabled)
                    throw SimulatedSqlException.IndexHintNameDisabled(name, qualifiedTableName);
                continue;
            }
            if (table.Indexes.Find(index => collation.Equals(index.Name, name)) is { } named)
            {
                if (named.IsDisabled)
                    throw SimulatedSqlException.IndexHintNameDisabled(name, qualifiedTableName);
                continue;
            }
            if (table.XmlIndexes.Exists(index => collation.Equals(index.Name, name)))
                throw SimulatedSqlException.XmlIndexInHint(name, qualifiedTableName);
            throw SimulatedSqlException.IndexHintNameNotFound(name, qualifiedTableName);
        }
    }

    /// <summary>
    /// The hint checks that read the session or the table rather than the
    /// hint list alone: <c>READPAST</c> in a READ UNCOMMITTED, SERIALIZABLE or
    /// SNAPSHOT session without a hint naming a level it takes (Msg 650,
    /// ahead of the snapshot refusal Msg 3952), and <c>PAGLOCK</c> on a table
    /// whose heap or clustered index disallows page locks (Msg 651). Both
    /// probed 2026-10-05 against SQL Server 2025.
    /// </summary>
    private static void ValidateLockGranularityHints(ParserContext context, TableHintInfo hints, HeapTable table, MultiPartName writtenName)
    {
        if (hints.ReadPast && !context.Batch.IsSkipping && !(hints.ReadCommitted || hints.ReadCommittedLock || hints.Repeatable)
            && context.Connection.SessionIsolationLevel is System.Data.IsolationLevel.ReadUncommitted or System.Data.IsolationLevel.Serializable or System.Data.IsolationLevel.Snapshot)
        {
            throw SimulatedSqlException.ReadPastOutsideReadCommitted();
        }
        if (hints.PagLock)
        {
            var allowed = KeyLockGroup.ClusteredOwner(table) switch
            {
                KeyConstraint key => key.AllowPageLocks,
                Storage.Index index => index.AllowPageLocks,
                _ => table.HeapAllowPageLocks,
            };
            if (!allowed)
                throw SimulatedSqlException.PageLockHintInhibited(IndexHintTableName(writtenName, table));
        }
    }

    /// <summary>
    /// The table name the index-hint errors give: schema-qualified as the
    /// query wrote or defaulted it, a temporary table by its name alone
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static string IndexHintTableName(MultiPartName writtenName, HeapTable table) =>
        table.Name.StartsWith('#') ? table.Name : $"{writtenName.ImmediateQualifier ?? Database.DefaultSchemaName}.{table.Name}";

    /// <summary>
    /// The name, lower-cased, Msg 10794 gives a table hint a memory-optimized
    /// table refuses; null for one it takes — <c>NOLOCK</c>, the isolation
    /// hints <c>SNAPSHOT</c>, <c>REPEATABLEREAD</c> and <c>SERIALIZABLE</c>,
    /// and the index hints (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static string? MemoryOptimizedRefusedHint(ReadOnlySpan<char> name)
    {
        if (name.Length > 24)
            return null;
        Span<char> upper = stackalloc char[name.Length];
        _ = name.ToUpperInvariant(upper);
        return upper switch
        {
            "HOLDLOCK" => "holdlock",
            "NOEXPAND" => "noexpand",
            "NOWAIT" => "nowait",
            "PAGLOCK" => "paglock",
            "READCOMMITTED" => "readcommitted",
            "READCOMMITTEDLOCK" => "readcommittedlock",
            "READPAST" => "readpast",
            "READUNCOMMITTED" => "readuncommitted",
            "ROWLOCK" => "rowlock",
            "TABLOCK" => "tablock",
            "TABLOCKX" => "tablockx",
            "UPDLOCK" => "updlock",
            "XLOCK" => "xlock",
            _ => null,
        };
    }

    /// <summary>
    /// Validates and consumes a single table-hint entry: <c>name</c>,
    /// <c>name = literal</c>, or <c>name (arg-list)</c>. Unknown name →
    /// Msg 321. Cursor advances to the trailing <c>,</c> or <c>)</c>. Updates
    /// <paramref name="info"/> for the phase-1a-recognized modifier set.
    /// </summary>
    private static void ConsumeOneTableHint(ParserContext context, ref TableHintInfo info, bool legacyForm)
    {
        if (context.Token is null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var sourceSpan = context.Token.Source;
        if (!TableHintLookup.TryGetValue(sourceSpan, out var kind))
            throw SimulatedSqlException.UnrecognizedTableHint(sourceSpan);
        // An INDEX hint is the one name real refuses outright in the legacy
        // no-WITH form, wherever in the list it stands and whether or not an
        // alias preceded the parens (probe-confirmed).
        if (legacyForm && kind == TableHintKind.Index)
            throw SimulatedSqlException.TableHintNeedsWithKeyword(sourceSpan);
        // Recognize the phase-1b lock-affecting hints. NOLOCK / READUNCOMMITTED
        // skip S acquisition (dirty-read). HOLDLOCK / REPEATABLEREAD /
        // SERIALIZABLE retain row-S to transaction end (so re-read sees the
        // same value). UPDLOCK takes row-U (read-with-intent-to-update) so
        // a subsequent UPDATE inside the same tx doesn't deadlock with
        // another connection's S-then-U upgrade. XLOCK treats the read like
        // a write (row-X tx-scoped). TABLOCK / TABLOCKX escalates to table
        // granularity. READPAST skips blocked rows instead of waiting, and
        // NOWAIT zeroes the lock timeout for the hinted table so a conflict
        // raises Msg 1222 at once. Everything else parses-and-discards.
        info.RefusedByMemoryOptimized ??= MemoryOptimizedRefusedHint(sourceSpan);
        if (kind is not (TableHintKind.Discard or TableHintKind.Index or TableHintKind.ForceSeek or TableHintKind.ForceScan or TableHintKind.BulkLoadOnly or TableHintKind.BulkLoadKeep)
            || sourceSpan.Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase))
        {
            info.FirstSemanticHint ??= sourceSpan.ToString();
        }
        if (sourceSpan.Equals("SNAPSHOT", StringComparison.OrdinalIgnoreCase))
            info.Snapshot = true;
        switch (kind)
        {
            case TableHintKind.NoLock: info.NoLock = true; break;
            case TableHintKind.Serializable: info.Serializable = true; break;
            case TableHintKind.Repeatable: info.Repeatable = true; break;
            case TableHintKind.UpdLock: info.UpdLock = true; break;
            case TableHintKind.XLock: info.XLock = true; break;
            case TableHintKind.ReadPast: info.ReadPast = true; break;
            case TableHintKind.NoWait: info.NoWait = true; break;
            case TableHintKind.TabLock: info.TabLock = true; break;
            case TableHintKind.TabLockX: info.TabLockX = true; break;
            case TableHintKind.NoExpand: info.NoExpand = true; break;
            case TableHintKind.ReadCommitted: info.ReadCommitted = true; break;
            case TableHintKind.ReadCommittedLock: info.ReadCommittedLock = true; break;
            case TableHintKind.RowLock: info.RowLock = true; break;
            case TableHintKind.PagLock: info.PagLock = true; break;
            case TableHintKind.BulkLoadOnly:
                info.BulkLoadOnlyHint ??= sourceSpan.ToString();
                info.BulkLoadHint ??= info.BulkLoadOnlyHint;
                break;
            case TableHintKind.BulkLoadKeep: info.BulkLoadHint ??= sourceSpan.ToString(); break;

            case TableHintKind.Index:
                context.SimpleParameterizationBlocked = true;
                info.IndexHint = true;
                info.IndexNamed = true;
                context.MoveNextRequired();
                ConsumeIndexHintArguments(context, ref info);
                return;

            case TableHintKind.ForceScan:
                context.SimpleParameterizationBlocked = true;
                info.IndexHint = true;
                info.ForceScan = true;
                context.MoveNextRequired();
                return;

            case TableHintKind.ForceSeek:
                context.SimpleParameterizationBlocked = true;
                info.IndexHint = true;
                info.ForceSeek = true;
                context.MoveNextRequired();
                if (context.Token is Operator { Character: '(' })
                {
                    // FORCESEEK's nested form names an index —
                    // FORCESEEK(index_name(col [, …])). Peek that name so the
                    // FROM-source call site validates its existence exactly as it
                    // does for INDEX(name); real raises the same Msg 308 for both.
                    // Then rewind so the payload skip below stays the single
                    // consumer of the parenthesized run.
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextRequired();
                    if (context.Token is Name or Numeric)
                    {
                        CaptureOneIndexArgument(context, ref info);
                        // The index name is followed by its own parenthesized
                        // seek-column list, which real's grammar requires (a bare
                        // FORCESEEK(ix) is Msg 102 on the closing parenthesis).
                        // Capture the names so the FROM-source call site can
                        // measure them against the index's key columns
                        // (Msg 362 / 365).
                        if (context.MoveNext() && context.Token is Operator { Character: '(' })
                            CaptureForceSeekColumns(context, ref info);
                        else
                            throw SimulatedSqlException.SyntaxErrorNear(context);
                    }
                    context.RestoreCheckpoint(checkpoint);

                    SkipBalancedParens(context);
                    context.MoveNextRequired();
                }

                return;
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
    /// Reads the <c>(col [, …])</c> seek-column list of a nested
    /// <c>FORCESEEK</c>. Cursor on entry: the opening <c>(</c>. Leaves the
    /// cursor wherever it lands — the caller restores its checkpoint and lets
    /// the payload skip walk the run again. An empty list is Msg 102, matching
    /// real.
    /// </summary>
    private static void CaptureForceSeekColumns(ParserContext context, ref TableHintInfo info)
    {
        var columns = new List<string>();
        while (true)
        {
            if (context.GetNextRequired() is not Name columnName)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columns.Add(columnName.Value);
            if (context.GetNextRequired() is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        info.ForceSeekColumns = columns;
    }

    /// <summary>
    /// Validates a nested <c>FORCESEEK(index (col [, …]))</c>'s seek columns
    /// against the resolved index. Real requires the list to be a leading
    /// prefix of the index's <i>key</i> columns, in order: an <c>INCLUDE</c>d
    /// column, a key column out of position and an unknown name all report
    /// <b>Msg 362</b> naming the first offender, and more names than the index
    /// has key columns reports <b>Msg 365</b> ahead of any name check. Both
    /// messages name the base table rather than the alias the query wrote
    /// (probe-confirmed), and both are raised after the index-name check that
    /// <see cref="ValidateIndexHintArguments"/> answers with Msg 308.
    /// </summary>
    internal static void ValidateForceSeekColumns(Collation collation, TableHintInfo info, HeapTable table)
    {
        if (info.ForceSeekColumns is not { } seekColumns || info.IndexArguments is not { Count: > 0 } args)
            return;
        // The id form names the index by its sys.indexes id, which the
        // messages then give as the name (probed 2026-10-05 against SQL Server
        // 2025); index 0, the heap or clustered scan, seeks nothing.
        if (args[0].Id == 0)
            throw SimulatedSqlException.ForceSeekOnIndexZero();
        var indexName = args[0].Name ?? args[0].Id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (args[0].Id is not null)
            table.SettleIndexIds();

        string[]? keyColumnNames = null;
        foreach (var constraint in table.KeyConstraints)
        {
            if (args[0].Id is { } constraintId ? constraint.IndexId != constraintId : !collation.Equals(constraint.Name, indexName))
                continue;
            keyColumnNames = new string[constraint.StorageOrdinals.Length];
            for (var i = 0; i < keyColumnNames.Length; i++)
                keyColumnNames[i] = table.Columns[ColumnOrdinalForStorageOrdinal(table, constraint.StorageOrdinals[i])].Name;
            break;
        }
        if (keyColumnNames is null)
        {
            foreach (var index in table.Indexes)
            {
                if (args[0].Id is { } namedId ? index.IndexId != namedId : !collation.Equals(index.Name, indexName))
                    continue;
                keyColumnNames = new string[index.KeyColumns.Length];
                for (var i = 0; i < keyColumnNames.Length; i++)
                    keyColumnNames[i] = table.Columns[index.KeyColumns[i].ColumnOrdinal].Name;
                break;
            }
        }
        // An unresolvable index name is Msg 308's business, raised by the
        // sibling validator.
        if (keyColumnNames is null)
            return;

        if (seekColumns.Count > keyColumnNames.Length)
            throw SimulatedSqlException.ForceSeekTooManySeekColumns(table.Name, indexName);
        for (var i = 0; i < seekColumns.Count; i++)
        {
            if (!collation.Equals(seekColumns[i], keyColumnNames[i]))
                throw SimulatedSqlException.ForceSeekColumnNotAKeyColumn(seekColumns[i], table.Name, indexName);
        }
    }

    /// <summary>
    /// Inverts <see cref="HeapTable.StorageOrdinals"/> for one entry. Key
    /// constraints record the columns they cover by storage ordinal, and the
    /// names a diagnostic reports come off the declared column list.
    /// </summary>
    private static int ColumnOrdinalForStorageOrdinal(HeapTable table, int storageOrdinal)
    {
        for (var i = 0; i < table.StorageOrdinals.Length; i++)
        {
            if (table.StorageOrdinals[i] == storageOrdinal)
                return i;
        }
        return storageOrdinal;
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
                info.IndexArguments.Add(IndexHintArgument.ForName(nameToken.Value));
                return;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
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
    /// raises Msg 10715. The hint itself is otherwise parse-and-discard, but
    /// for <c>DISABLE_TSQL_SCALAR_UDF_INLINING</c>, which keeps the statement's
    /// scalar function calls from inlining.
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
            if (value.AsString.Equals("DISABLE_TSQL_SCALAR_UDF_INLINING", StringComparison.OrdinalIgnoreCase))
                context.Batch.CurrentStatement.DisablesScalarUdfInlining = true;
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
    internal static void SkipBalancedParens(ParserContext context)
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
        // A view's or inline function's definition samples nothing (probed
        // 2026-10-04 against SQL Server 2025).
        if (context.DefiningModuleQuery != DefiningModuleQuery.None)
            throw SimulatedSqlException.TableSampleInModuleDefinition();
        context.SimpleParameterizationBlocked = true;
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
