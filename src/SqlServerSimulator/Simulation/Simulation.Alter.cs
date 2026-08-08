using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the two ALTER DATABASE forms the simulator currently models:
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c> (per-database
    /// compat) and
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// The simulator has a single database, so any database name (including
    /// <c>CURRENT</c>) is accepted and ignored.
    /// </summary>
    private static bool TryParseAlter(ParserContext context)
    {
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Procedure or Keyword.Proc }:
                // ALTER PROCEDURE is identical in shape to CREATE PROCEDURE —
                // same parameter grammar, same options, same body capture —
                // differing only in the existence-check direction (must exist
                // vs must not). Reuse the CREATE PROCEDURE parser with the
                // isAlter flag set.
                return TryParseCreateProcedure(context, isAlter: true, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Trigger }:
                // Same shape-sharing pattern as ALTER PROCEDURE — body /
                // actions replace in place, ObjectId is preserved.
                return TryParseCreateTrigger(context, isAlter: true, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.View }:
                return TryParseCreateView(context, isAlter: true, createOrAlter: false);
            case ReservedKeyword { Keyword: Keyword.Function }:
                return TryParseCreateFunction(context, isAlter: true, createOrAlter: false);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Sequence }:
                return TryParseAlterSequence(context);
            case ReservedKeyword { Keyword: Keyword.Schema }:
                return TryParseAlterSchemaTransfer(context);
            case ReservedKeyword { Keyword: Keyword.Table }:
                return TryParseAlterTable(context);
            case ReservedKeyword { Keyword: Keyword.Index }:
                return TryParseAlterIndex(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Role }:
                return TryParseAlterRole(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Login }:
                return TryParseAlterLogin(context);
            case Name serverWord when serverWord.Value.Equals("SERVER", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterServerRole(context);
            case Name appWord when appWord.Value.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterApplicationRole(context);
            case ReservedKeyword { Keyword: Keyword.Database }:
                break;
            default:
                return false;
        }

        // Cursor is on DATABASE; advance to the token after it (a db name, the
        // CURRENT keyword, or the SCOPED contextual keyword routing to the
        // database-scoped-configuration path).
        var afterDatabase = context.GetNextRequired();
        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Scoped })
            return TryParseAlterDatabaseScopedConfiguration(context);

        // Otherwise a database name (or CURRENT), which names the database the
        // option lands on — not necessarily the session's. After the name the
        // only legal continuations are SET <option> and COLLATE <name>.
        if (afterDatabase is not (Name or ReservedKeyword { Keyword: Keyword.Current }))
            return false;
        var target = ResolveAlterDatabaseTarget(context, afterDatabase);
        return context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Set } => TryParseAlterDatabaseSet(context, target),
            ReservedKeyword { Keyword: Keyword.Collate } => TryParseAlterDatabaseCollate(context, target),
            _ => false,
        };
    }

    /// <summary>
    /// The database an <c>ALTER DATABASE</c> statement targets: the session's
    /// for <c>CURRENT</c>, else the named one. A name this
    /// <see cref="Simulation"/> doesn't host raises Msg 5011 (state 5), and a
    /// principal without ALTER on the database it did find raises the same
    /// number at state 9 — probe-confirmed, so a restricted caller can't tell
    /// the two apart. Real follows the refusal with a terminating Msg 5069
    /// (<c>ALTER DATABASE statement failed.</c>); the simulator surfaces the
    /// single 5011. Resolution is suppressed in skip mode, where the statement
    /// parses but doesn't run.
    /// </summary>
    private static Database ResolveAlterDatabaseTarget(ParserContext context, Token afterDatabase)
    {
        var target = afterDatabase is not Name named ? context.CurrentDatabase
            : context.Connection.Simulation.Databases.TryGetValue(named.Value, out var named_) ? named_
            : context.Batch.IsSkipping ? context.CurrentDatabase
            : throw SimulatedSqlException.CannotAlterDatabase(named.Value);
        return context.Batch.IsSkipping || PermissionEnforcement.HasDatabasePermission(context.Batch, target, Permission.Alter)
            ? target
            : throw SimulatedSqlException.AlterDatabasePermissionDenied(target.Name);
    }

    /// <summary>
    /// Dispatches <c>ALTER DATABASE name SET &lt;option&gt; …</c>. The four
    /// load-bearing options (COMPATIBILITY_LEVEL, ALLOW_SNAPSHOT_ISOLATION,
    /// READ_COMMITTED_SNAPSHOT, RECURSIVE_TRIGGERS) carry semantic effect and
    /// route to dedicated
    /// helpers; the remaining accept-list (RECOVERY, ANSI_NULLS, QUERY_STORE,
    /// TARGET_RECOVERY_TIME, ACCELERATED_DATABASE_RECOVERY, …) is parse-and-
    /// discard — see <see cref="RecognizedDatabaseOptions"/> for the closed
    /// list, sourced from a probe matrix against SQL Server 2025 (2026-05-14).
    /// </summary>
    private static bool TryParseAlterDatabaseSet(ParserContext context, Database target)
    {
        context.MoveNextRequired();
        // Load-bearing options keep their dedicated handlers. Routing on
        // ContextualKeyword first means the existing 3 paths are unchanged
        // and the new parse-and-discard surface lives on a parallel dict.
        return context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Compatibility_Level } => TryParseAlterDatabaseSetCompatibilityLevel(context, target),
            UnquotedString { ContextualKeyword: ContextualKeyword.Allow_Snapshot_Isolation } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.AllowSnapshotIsolation),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Committed_Snapshot } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.ReadCommittedSnapshot),
            UnquotedString { ContextualKeyword: ContextualKeyword.Recursive_Triggers } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.RecursiveTriggers),
            UnquotedString { ContextualKeyword: ContextualKeyword.Trustworthy } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.Trustworthy),
            UnquotedString { ContextualKeyword: ContextualKeyword.Db_Chaining } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.CrossDatabaseChaining),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Only } => TryParseAlterDatabaseSetAccessMode(context, target, readOnly: true),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Write } => TryParseAlterDatabaseSetAccessMode(context, target, readOnly: false),
            UnquotedString recovery when recovery.Value.Equals("RECOVERY", StringComparison.OrdinalIgnoreCase) => TryParseAlterDatabaseSetRecovery(context, target),
            UnquotedString unquoted when RecognizedDatabaseOptions.TryGetValue(unquoted.Value, out var kind) => ConsumeDatabaseOptionTail(context, target, kind),
            _ => false,
        };
    }

    private static bool TryParseAlterDatabaseSetCompatibilityLevel(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numericValue })
            return false;

        var requested = numericValue.AsInt32;
        if (context.Batch.IsSkipping)
            return true;

        // The one SET option a read-only database refuses (probe-confirmed
        // 2026-08-04): the level lives in the database's own metadata, so real
        // raises Msg 3906 here while ALLOW_SNAPSHOT_ISOLATION /
        // READ_COMMITTED_SNAPSHOT / RECURSIVE_TRIGGERS / ANSI_NULLS / RECOVERY —
        // and READ_WRITE itself — all move freely.
        target.RejectWriteWhenReadOnly();
        if (!Enum.IsDefined((CompatibilityLevel)requested))
            throw SimulatedSqlException.InvalidCompatibilityLevel();

        target.CompatibilityLevel = (CompatibilityLevel)requested;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name SET { READ_ONLY | READ_WRITE } [WITH &lt;termination&gt;]</c>
    /// — the access-mode shape (a bare state, no <c>=</c>), sharing
    /// <see cref="ConsumeAccessModeTail"/> with SINGLE_USER / MULTI_USER /
    /// RESTRICTED_USER. Unlike those, this one is load-bearing:
    /// <see cref="Database.IsReadOnly"/> gates every write to the database.
    /// <para><c>master</c> and <c>tempdb</c> pin the option and raise
    /// <strong>Msg 5058</strong> for either value asked for, at their own states
    /// (5 and 4); <c>model</c> and <c>msdb</c> accept it. All probe-confirmed
    /// against SQL Server 2025 (2026-08-04).</para>
    /// </summary>
    private static bool TryParseAlterDatabaseSetAccessMode(ParserContext context, Database target, bool readOnly)
    {
        if (!ConsumeAccessModeTail(context))
            return false;
        if (context.Batch.IsSkipping)
            return true;

        if (BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, TempdbDatabaseName))
            throw SimulatedSqlException.OptionCannotBeSetInDatabase(readOnly ? "READ_ONLY" : "READ_WRITE", target.Name);

        target.IsReadOnly = readOnly;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name SET RECOVERY { FULL | BULK_LOGGED | SIMPLE }</c>.
    /// </summary>
    /// <remarks>
    /// The simulator has no transaction log, so the setting drives nothing —
    /// but it is what <c>sys.databases.recovery_model</c> /
    /// <c>recovery_model_desc</c> report, and a bacpac carries the source
    /// database's value, so tracking it is what lets an imported database
    /// describe itself the way the original did. An unrecognized value falls
    /// through to the caller's Msg 102 path.
    /// </remarks>
    private static bool TryParseAlterDatabaseSetRecovery(ParserContext context, Database target)
    {
        // FULL tokenizes as a reserved keyword; BULK_LOGGED and SIMPLE as
        // bare identifiers.
        var model = context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Full } => RecoveryModel.Full,
            Name or UnquotedString => context.Token switch
            {
                UnquotedString { Value: var v } when v.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase) => RecoveryModel.Simple,
                UnquotedString { Value: var v } when v.Equals("BULK_LOGGED", StringComparison.OrdinalIgnoreCase) => RecoveryModel.BulkLogged,
                Name { Value: var v } when v.Equals("SIMPLE", StringComparison.OrdinalIgnoreCase) => RecoveryModel.Simple,
                Name { Value: var v } when v.Equals("BULK_LOGGED", StringComparison.OrdinalIgnoreCase) => RecoveryModel.BulkLogged,
                _ => (RecoveryModel?)null,
            },
            _ => null,
        };
        if (model is null)
            return false;
        if (context.Batch.IsSkipping)
            return true;

        target.RecoveryModel = model.Value;
        return true;
    }

    /// <summary>The per-database flags whose SET form is a bare <c>ON</c> / <c>OFF</c>.</summary>
    private enum DatabaseBooleanOption
    {
        AllowSnapshotIsolation,
        ReadCommittedSnapshot,
        RecursiveTriggers,
        Trustworthy,
        CrossDatabaseChaining,
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name SET (ALLOW_SNAPSHOT_ISOLATION | READ_COMMITTED_SNAPSHOT | RECURSIVE_TRIGGERS | TRUSTWORTHY | DB_CHAINING) { ON | OFF }</c>.
    /// The probed real-server gates ALLOW_SNAPSHOT_ISOLATION ON behind a
    /// brief stabilization wait and READ_COMMITTED_SNAPSHOT ON behind a
    /// single-connection requirement; the simulator skips both — the flip
    /// takes effect immediately. <c>WITH (NO_WAIT | ROLLBACK IMMEDIATE | ROLLBACK AFTER n)</c>
    /// termination options are rejected by real SQL Server on versioning
    /// state changes (Msg 5083); the simulator falls through and raises
    /// <see cref="NotSupportedException"/> on the unexpected trailer.
    /// TRUSTWORTHY and DB_CHAINING each refuse a set of system databases —
    /// see <see cref="RejectSystemDatabaseFlag"/>.
    /// </summary>
    private static bool TryParseAlterDatabaseSetBooleanOption(ParserContext context, Database target, DatabaseBooleanOption option)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;
        if (context.Batch.IsSkipping)
            return true;
        RejectSystemDatabaseFlag(target, option);
        var value = on == Keyword.On;
        var database = target;
        switch (option)
        {
            case DatabaseBooleanOption.AllowSnapshotIsolation: database.AllowSnapshotIsolation = value; break;
            case DatabaseBooleanOption.ReadCommittedSnapshot: database.ReadCommittedSnapshot = value; break;
            case DatabaseBooleanOption.Trustworthy: database.Trustworthy = value; break;
            case DatabaseBooleanOption.CrossDatabaseChaining: database.CrossDatabaseChaining = value; break;
            default: database.RecursiveTriggers = value; break;
        }
        return true;
    }

    /// <summary>
    /// The two cross-database-widening flags real refuses to move on some system
    /// databases, whatever the value asked for (probe-confirmed against SQL
    /// Server 2025): <c>TRUSTWORTHY</c> on <c>model</c> / <c>tempdb</c> raises
    /// Msg 15309, and <c>DB_CHAINING</c> on <c>master</c> / <c>model</c> /
    /// <c>tempdb</c> raises Msg 5600. <c>msdb</c> accepts both — it ships
    /// trustworthy and chained.
    /// </summary>
    private static void RejectSystemDatabaseFlag(Database target, DatabaseBooleanOption option)
    {
        switch (option)
        {
            case DatabaseBooleanOption.Trustworthy
                when BuiltInToken.EqualsAny(target.Name, ModelDatabaseName, TempdbDatabaseName):
                throw SimulatedSqlException.CannotAlterTrustworthyState();
            case DatabaseBooleanOption.CrossDatabaseChaining
                when BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, ModelDatabaseName, TempdbDatabaseName):
                throw SimulatedSqlException.CannotSetCrossDatabaseChaining();
            default:
                break;
        }
    }

    /// <summary>
    /// Value-shape of each recognized parse-and-discard ALTER DATABASE option.
    /// Sourced from a probe matrix against SQL Server 2025 (2026-05-14);
    /// shapes that differ from the canonical T-SQL syntax raise Msg 156/102
    /// at the offending token in <see cref="ConsumeDatabaseOptionTail"/>.
    /// </summary>
    private enum AlterDatabaseOptionKind
    {
        /// <summary>Bare ON/OFF after the option name (no <c>=</c>).</summary>
        OnOff,
        /// <summary><c>= ON|OFF</c> — the <c>=</c> is required.</summary>
        EqualsOnOff,
        /// <summary>Bare identifier value (RECOVERY FULL, CURSOR_DEFAULT GLOBAL, …).</summary>
        EnumIdent,
        /// <summary><c>= N {SECONDS|MINUTES}</c> — TARGET_RECOVERY_TIME.</summary>
        IntegerWithUnit,
        /// <summary>
        /// <c>= ON [( opt = val [, …] )] | = OFF | CLEAR [ALL]</c> — QUERY_STORE.
        /// The only option here that retains what it parses, in
        /// <see cref="ParseQueryStoreTail"/>.
        /// </summary>
        QueryStore,
        /// <summary>
        /// A bare access-mode state (SINGLE_USER / MULTI_USER / RESTRICTED_USER)
        /// with no <c>=</c> value, optionally followed by a termination clause
        /// <c>WITH ROLLBACK IMMEDIATE | WITH ROLLBACK AFTER n [SECONDS] | WITH NO_WAIT</c>
        /// — parse-and-discarded (the simulator has no connection-count access
        /// model, so it never actually restricts). Emitted by mssql-django's
        /// test-database teardown (<c>SET SINGLE_USER WITH ROLLBACK IMMEDIATE</c>
        /// before DROP DATABASE).
        /// </summary>
        AccessMode,
    }

    /// <summary>
    /// Closed accept-list of ALTER DATABASE option names whose value shape
    /// fits one of the <see cref="AlterDatabaseOptionKind"/> classes. The
    /// three load-bearing options (COMPATIBILITY_LEVEL, ALLOW_SNAPSHOT_ISOLATION,
    /// READ_COMMITTED_SNAPSHOT) are dispatched via their dedicated helpers
    /// upstream and are intentionally absent here. Each entry mirrors the
    /// option's syntax shape as probed against SQL Server 2025 — see
    /// <c>/tmp/dbopts-probe</c> for the verification matrix.
    /// </summary>
    private static readonly FrozenDictionary<string, AlterDatabaseOptionKind> RecognizedDatabaseOptions = new Dictionary<string, AlterDatabaseOptionKind>
    {
        ["ANSI_NULLS"] = AlterDatabaseOptionKind.OnOff,
        ["ANSI_PADDING"] = AlterDatabaseOptionKind.OnOff,
        ["ANSI_WARNINGS"] = AlterDatabaseOptionKind.OnOff,
        ["ARITHABORT"] = AlterDatabaseOptionKind.OnOff,
        ["CONCAT_NULL_YIELDS_NULL"] = AlterDatabaseOptionKind.OnOff,
        ["NUMERIC_ROUNDABORT"] = AlterDatabaseOptionKind.OnOff,
        ["QUOTED_IDENTIFIER"] = AlterDatabaseOptionKind.OnOff,
        ["TORN_PAGE_DETECTION"] = AlterDatabaseOptionKind.OnOff,
        ["TEMPORAL_HISTORY_RETENTION"] = AlterDatabaseOptionKind.OnOff,
        ["RECOVERY"] = AlterDatabaseOptionKind.EnumIdent,
        ["PAGE_VERIFY"] = AlterDatabaseOptionKind.EnumIdent,
        ["CURSOR_DEFAULT"] = AlterDatabaseOptionKind.EnumIdent,
        ["ACCELERATED_DATABASE_RECOVERY"] = AlterDatabaseOptionKind.EqualsOnOff,
        ["OPTIMIZED_LOCKING"] = AlterDatabaseOptionKind.EqualsOnOff,
        ["TARGET_RECOVERY_TIME"] = AlterDatabaseOptionKind.IntegerWithUnit,
        ["QUERY_STORE"] = AlterDatabaseOptionKind.QueryStore,
        ["SINGLE_USER"] = AlterDatabaseOptionKind.AccessMode,
        ["MULTI_USER"] = AlterDatabaseOptionKind.AccessMode,
        ["RESTRICTED_USER"] = AlterDatabaseOptionKind.AccessMode,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Cursor enters on the option name. Advances past the value tail per
    /// <paramref name="kind"/>; returns true on shape match, false to fall
    /// through to the caller's Msg 102 path on bad trailers.
    /// </summary>
    /// <remarks>
    /// The enum value may tokenize as either a bare identifier (e.g.
    /// BULK_LOGGED) or a reserved keyword (e.g. FULL, GLOBAL, NONE) —
    /// both shapes occur across the RECOVERY / PAGE_VERIFY /
    /// CURSOR_DEFAULT enums. No per-option closed value set is checked for
    /// the discarding kinds; real SQL Server validates at execution time and
    /// the simulator doesn't model the underlying behavior. QUERY_STORE is
    /// the exception — it retains what it parses, so its values are checked.
    /// </remarks>
    private static bool ConsumeDatabaseOptionTail(ParserContext context, Database target, AlterDatabaseOptionKind kind) => kind switch
    {
        AlterDatabaseOptionKind.OnOff =>
            context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
        AlterDatabaseOptionKind.EqualsOnOff => ConsumeEqualsOnOff(context),
        AlterDatabaseOptionKind.EnumIdent =>
            context.GetNextRequired() is Name or ReservedKeyword,
        AlterDatabaseOptionKind.IntegerWithUnit => ConsumeIntegerWithUnit(context),
        AlterDatabaseOptionKind.QueryStore => ParseQueryStoreTail(context, target),
        AlterDatabaseOptionKind.AccessMode => ConsumeAccessModeTail(context),
        _ => false,
    };

    /// <summary>
    /// Cursor on the access-mode name (SINGLE_USER / MULTI_USER /
    /// RESTRICTED_USER), which is the whole option value. Consumes an optional
    /// trailing <c>WITH &lt;termination&gt;</c> clause (ROLLBACK IMMEDIATE /
    /// ROLLBACK AFTER n [SECONDS] / NO_WAIT), discarding every token up to the
    /// statement boundary and leaving the cursor on the clause's last token
    /// (or on the access-mode name when no WITH follows), per the
    /// leave-on-last-token convention the other tail consumers use.
    /// </summary>
    private static bool ConsumeAccessModeTail(ParserContext context)
    {
        var beforeWith = context.SaveCheckpoint();
        if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.With })
        {
            context.RestoreCheckpoint(beforeWith);
            return true;
        }
        // WITH <termination>. Only ROLLBACK and WITH tokenize as keywords;
        // IMMEDIATE / AFTER / SECONDS / NO_WAIT are plain identifiers matched by
        // text. Parse the exact forms rather than scanning to a boundary — a
        // scan can't stop reliably because ROLLBACK is itself a statement-
        // starting keyword. Cursor is left on the clause's last token.
        var termination = context.GetNextRequired();
        if (IsBareWord(termination, "NO_WAIT"))
            return true;
        if (termination is not ReservedKeyword { Keyword: Keyword.Rollback })
            return false;
        var rollbackKind = context.GetNextRequired();
        if (IsBareWord(rollbackKind, "IMMEDIATE"))
            return true;
        if (!IsBareWord(rollbackKind, "AFTER"))
            return false;
        if (context.GetNextRequired() is not Numeric { Value.IsNull: false })
            return false;
        // Optional trailing SECONDS.
        var beforeSeconds = context.SaveCheckpoint();
        if (!IsBareWord(context.GetNextOptional(), "SECONDS"))
            context.RestoreCheckpoint(beforeSeconds);
        return true;
    }

    /// <summary>
    /// Case-insensitive text match for a bare-identifier token. <see cref="UnquotedString"/>
    /// derives from <see cref="Name"/>, so matching <see cref="Name"/> covers both the
    /// contextual-keyword and plain-identifier tokenizations the termination words take.
    /// </summary>
    private static bool IsBareWord(Token? token, string word) =>
        token is Name name && name.Value.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static bool ConsumeEqualsOnOff(ParserContext context) =>
        context.GetNextRequired() switch
        {
            Operator { Character: '=' } => context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.On or Keyword.Off },
            _ => false,
        };

    private static bool ConsumeIntegerWithUnit(ParserContext context) =>
        context.GetNextRequired() switch
        {
            Operator { Character: '=' } => context.GetNextRequired() switch
            {
                Numeric { Value.IsNull: false } => context.GetNextRequired() is UnquotedString,
                _ => false,
            },
            _ => false,
        };

    /// <summary>
    /// Cursor on the QUERY_STORE name token. Accepts three shapes per probe:
    /// <c>= OFF</c>, <c>= ON</c> [optional <c>( … )</c> options block], and
    /// <c>CLEAR [ALL]</c>. Every form lands on <see cref="Database.QueryStore"/>
    /// — see <see cref="QueryStoreOptions"/> for why the values are retained
    /// even though nothing reads them at execution time.
    /// </summary>
    /// <remarks>
    /// The whole tail parses into a copy and swaps in only at the end, so an
    /// options block that raises partway through leaves the configuration as it
    /// was. <c>CLEAR</c> / <c>CLEAR ALL</c> purge captured runtime statistics —
    /// there are none, and neither form touches the configuration. Real's
    /// runtime validation of a sub-option's <em>value</em> isn't modeled: it is
    /// patchy on real (<c>DATA_FLUSH_INTERVAL_SECONDS = 0</c> raises Msg 153
    /// but <c>MAX_STORAGE_SIZE_MB = 0</c> is accepted, both probed 2026-08-08),
    /// so any value the grammar admits is stored.
    /// </remarks>
    private static bool ParseQueryStoreTail(ParserContext context, Database target)
    {
        var pending = target.QueryStore.Copy();
        // CLEAR / CLEAR ALL — no `=`.
        var next = context.GetNextRequired();
        if (IsBareWord(next, "CLEAR"))
        {
            // Optional trailing ALL.
            var checkpoint = context.SaveCheckpoint();
            if (context.GetNextOptional() is not ReservedKeyword { Keyword: Keyword.All })
                context.RestoreCheckpoint(checkpoint);
            RejectQueryStoreOnSystemDatabase(context, target);
            return true;
        }
        if (next is not Operator { Character: '=' })
            return false;
        switch (context.GetNextRequired())
        {
            case ReservedKeyword { Keyword: Keyword.Off }:
                pending.DesiredState = QueryStoreState.Off;
                break;
            case ReservedKeyword { Keyword: Keyword.On }:
                // A bare ON, and an ON carrying only unrelated sub-options,
                // both land READ_WRITE; an OPERATION_MODE entry overrides it.
                pending.DesiredState = QueryStoreState.ReadWrite;
                var afterOn = context.SaveCheckpoint();
                if (context.GetNextOptional() is Operator { Character: '(' })
                {
                    if (!ParseQueryStoreOptionsBlock(context, pending))
                        return false;
                }
                else
                {
                    context.RestoreCheckpoint(afterOn);
                }
                break;
            default:
                return false;
        }
        RejectQueryStoreOnSystemDatabase(context, target);
        if (!context.Batch.IsSkipping)
            target.QueryStore = pending;
        return true;
    }

    /// <summary>
    /// Raises <strong>Msg 12438</strong> when the statement named <c>master</c>
    /// or <c>tempdb</c>. Real refuses every QUERY_STORE form on those two —
    /// <c>= OFF</c> and <c>CLEAR</c> included — and accepts them on
    /// <c>model</c> and <c>msdb</c> (probe-confirmed 2026-08-08). Called after
    /// the tail has parsed, so a malformed statement still reports its syntax
    /// error first, the way real does.
    /// </summary>
    private static void RejectQueryStoreOnSystemDatabase(ParserContext context, Database target)
    {
        if (!context.Batch.IsSkipping && BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, TempdbDatabaseName))
            throw SimulatedSqlException.QueryStoreCannotBeEnabledOnSystemDatabase(target.Name);
    }

    /// <summary>
    /// Cursor on the opening <c>(</c> of a QUERY_STORE options block. Walks
    /// comma-separated <c>SUB_OPTION = value</c> entries onto
    /// <paramref name="pending"/>. An unrecognized sub-option name raises
    /// Msg 102 at the name — real's own behavior, at both nesting levels.
    /// </summary>
    private static bool ParseQueryStoreOptionsBlock(ParserContext context, QueryStoreOptions pending)
    {
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString sub)
                return false;
            if (context.GetNextRequired() is not Operator { Character: '=' })
                return false;
            if (!ParseQueryStoreSubOption(context, pending, sub))
                return false;
            // Comma → another entry; ) → done.
            var sep = context.GetNextRequired();
            if (sep is Operator { Character: ',' })
                continue;
            return sep is Operator { Character: ')' };
        }
    }

    /// <summary>
    /// Cursor on the <c>=</c> of one <c>SUB_OPTION = value</c> entry; consumes
    /// the value and leaves the cursor on its last token. The two policy
    /// sub-options take a parenthesized block of their own, which
    /// <see cref="ParseQueryStoreCleanupPolicy"/> /
    /// <see cref="ParseQueryStoreCapturePolicy"/> handle.
    /// </summary>
    private static bool ParseQueryStoreSubOption(ParserContext context, QueryStoreOptions pending, UnquotedString name)
    {
        Span<char> upper = stackalloc char[name.Value.Length];
        _ = name.Value.AsSpan().ToUpperInvariant(upper);
        switch (upper)
        {
            case "CLEANUP_POLICY":
                return ConsumeSubBlockOpen(context) && ParseQueryStoreCleanupPolicy(context, pending);
            case "DATA_FLUSH_INTERVAL_SECONDS":
                return TryReadInteger(context, out pending.FlushIntervalSeconds);
            case "INTERVAL_LENGTH_MINUTES":
                return TryReadInteger(context, out pending.IntervalLengthMinutes);
            case "MAX_PLANS_PER_QUERY":
                return TryReadInteger(context, out pending.MaxPlansPerQuery);
            case "MAX_STORAGE_SIZE_MB":
                return TryReadInteger(context, out pending.MaxStorageSizeMb);
            case "OPERATION_MODE":
                // READ_ONLY / READ_WRITE only — real answers `OPERATION_MODE = OFF`
                // with Msg 156, which RejectQueryStoreValue raises.
                var operationMode = context.GetNextRequired();
                if (IsBareWord(operationMode, "READ_ONLY"))
                    pending.DesiredState = QueryStoreState.ReadOnly;
                else if (IsBareWord(operationMode, "READ_WRITE"))
                    pending.DesiredState = QueryStoreState.ReadWrite;
                else
                    return RejectQueryStoreValue(operationMode);
                return true;
            case "QUERY_CAPTURE_MODE":
                // ALL tokenizes as a reserved keyword; AUTO / CUSTOM / NONE as
                // bare identifiers.
                var captureMode = context.GetNextRequired();
                var mode = captureMode switch
                {
                    ReservedKeyword { Keyword: Keyword.All } => QueryStoreCaptureMode.All,
                    Name word when word.Value.Equals("AUTO", StringComparison.OrdinalIgnoreCase) => QueryStoreCaptureMode.Auto,
                    Name word when word.Value.Equals("CUSTOM", StringComparison.OrdinalIgnoreCase) => QueryStoreCaptureMode.Custom,
                    Name word when word.Value.Equals("NONE", StringComparison.OrdinalIgnoreCase) => QueryStoreCaptureMode.None,
                    _ => (QueryStoreCaptureMode?)null,
                };
                if (mode is null)
                    return RejectQueryStoreValue(captureMode);
                pending.CaptureMode = mode.Value;
                return true;
            case "QUERY_CAPTURE_POLICY":
                return ConsumeSubBlockOpen(context) && ParseQueryStoreCapturePolicy(context, pending);
            case "SIZE_BASED_CLEANUP_MODE":
                // { AUTO | OFF }: AUTO is a bare identifier, OFF a reserved
                // keyword — and ON is Msg 156 on real, not a synonym for AUTO.
                var cleanup = context.GetNextRequired();
                pending.SizeBasedCleanupAuto = !IsOffKeyword(cleanup);
                return IsOffKeyword(cleanup) || IsBareWord(cleanup, "AUTO") || RejectQueryStoreValue(cleanup);
            case "WAIT_STATS_CAPTURE_MODE":
                // { ON | OFF }, both reserved keywords; AUTO is Msg 102.
                var waitStats = context.GetNextRequired();
                pending.WaitStatsCaptureOn = !IsOffKeyword(waitStats);
                return waitStats is ReservedKeyword { Keyword: Keyword.On or Keyword.Off } || RejectQueryStoreValue(waitStats);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(name);
        }
    }

    /// <summary>
    /// Cursor on the <c>=</c> before a sub-option's own parenthesized block;
    /// advances onto the opening <c>(</c>.
    /// </summary>
    private static bool ConsumeSubBlockOpen(ParserContext context) =>
        context.GetNextRequired() is Operator { Character: '(' };

    /// <summary>
    /// Cursor on the opening <c>(</c> of <c>CLEANUP_POLICY = ( … )</c>, whose
    /// single entry is <c>STALE_QUERY_THRESHOLD_DAYS = N</c>.
    /// </summary>
    private static bool ParseQueryStoreCleanupPolicy(ParserContext context, QueryStoreOptions pending)
    {
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString entry)
                return false;
            if (!entry.Value.Equals("STALE_QUERY_THRESHOLD_DAYS", StringComparison.OrdinalIgnoreCase))
                throw SimulatedSqlException.SyntaxErrorNear(entry);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                return false;
            if (!TryReadInteger(context, out pending.StaleQueryThresholdDays))
                return false;
            var sep = context.GetNextRequired();
            if (sep is Operator { Character: ',' })
                continue;
            return sep is Operator { Character: ')' };
        }
    }

    /// <summary>
    /// Cursor on the opening <c>(</c> of <c>QUERY_CAPTURE_POLICY = ( … )</c>.
    /// Its threshold entry carries a mandatory <c>DAYS</c> / <c>HOURS</c> unit
    /// (singular accepted) that the catalog column normalizes to hours; real
    /// reports Msg 102 at the entry name both for a missing unit and for an
    /// unrecognized one (probed 2026-08-08), which is where the throw lands.
    /// </summary>
    private static bool ParseQueryStoreCapturePolicy(ParserContext context, QueryStoreOptions pending)
    {
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString entry)
                return false;
            if (context.GetNextRequired() is not Operator { Character: '=' })
                return false;
            Span<char> upper = stackalloc char[entry.Value.Length];
            _ = entry.Value.AsSpan().ToUpperInvariant(upper);
            switch (upper)
            {
                case "EXECUTION_COUNT":
                    if (!TryReadInteger(context, out var executions))
                        return false;
                    pending.CapturePolicyExecutionCount = (int)executions;
                    break;
                case "STALE_CAPTURE_POLICY_THRESHOLD":
                    if (!TryReadInteger(context, out var threshold))
                        return false;
                    var unit = context.GetNextRequired();
                    var hoursPerUnit = IsBareWord(unit, "DAYS") || IsBareWord(unit, "DAY") ? 24
                        : IsBareWord(unit, "HOURS") || IsBareWord(unit, "HOUR") ? 1
                        : throw SimulatedSqlException.SyntaxErrorNear(entry);
                    pending.CapturePolicyStaleThresholdHours = (int)threshold * hoursPerUnit;
                    break;
                case "TOTAL_COMPILE_CPU_TIME_MS":
                    if (!TryReadInteger(context, out pending.CapturePolicyTotalCompileCpuTimeMs))
                        return false;
                    break;
                case "TOTAL_EXECUTION_CPU_TIME_MS":
                    if (!TryReadInteger(context, out pending.CapturePolicyTotalExecutionCpuTimeMs))
                        return false;
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(entry);
            }
            var sep = context.GetNextRequired();
            if (sep is Operator { Character: ',' })
                continue;
            return sep is Operator { Character: ')' };
        }
    }

    /// <summary>
    /// Reads the integer value after a sub-option's <c>=</c>. A literal past
    /// <c>int</c> range types as <c>numeric</c> rather than <c>int</c>, so the
    /// coercion — not <c>AsInt32</c> — is what accepts the whole domain.
    /// </summary>
    private static bool TryReadInteger(ParserContext context, out long value)
    {
        value = 0;
        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numeric })
            return false;
        value = numeric.CoerceTo(SqlType.BigInt).AsInt64;
        return true;
    }

    private static bool IsOffKeyword(Token token) => token is ReservedKeyword { Keyword: Keyword.Off };

    /// <summary>
    /// A QUERY_STORE sub-option value the grammar doesn't accept. Real splits
    /// these by what the offending token <em>is</em>: a reserved keyword gets
    /// Msg 156 (<c>OPERATION_MODE = OFF</c>, <c>SIZE_BASED_CLEANUP_MODE = ON</c>)
    /// and anything else Msg 102, which is what the <see langword="false"/>
    /// return reaches through the caller's fall-through.
    /// </summary>
    private static bool RejectQueryStoreValue(Token token) => token is ReservedKeyword keyword
        ? throw SimulatedSqlException.SyntaxErrorNearKeyword(keyword)
        : false;

    /// <summary>
    /// Cursor on the opening <c>(</c>. Walks tokens incrementing/decrementing
    /// a paren counter, returning when the matching <c>)</c> closes. Used
    /// wherever a parenthesized clause's inner grammar isn't enforced.
    /// </summary>
    private static void SkipBalancedParens(ParserContext context)
    {
        var depth = 1;
        while (depth > 0)
        {
            context.MoveNextRequired();
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
            }
        }
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name COLLATE &lt;collation_name&gt;</c>.
    /// Validates the name against <see cref="Collation.IsRecognized"/> and
    /// updates the target database's <see cref="Database.Collation"/> +
    /// <see cref="Database.CollationName"/>. Subsequent identifier compares
    /// route through the new collation; pre-existing catalog dict comparers
    /// don't rebuild (existing objects keep their original identifier
    /// registration — matches real SQL Server). An unrecognized name raises
    /// <see cref="NotSupportedException"/> in direct SQL; the BACPAC loader
    /// catches and records on Warnings.
    /// </summary>
    private static bool TryParseAlterDatabaseCollate(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not UnquotedString token)
            return false;
        if (context.Batch.IsSkipping)
            return true;
        if (Collation.TryGet(token.Value) is not { } resolved)
            throw new NotSupportedException($"ALTER DATABASE COLLATE: collation '{token.Value}' isn't on the simulator's recognized list.");
        var database = target;
        database.Collation = resolved;
        database.CollationName = resolved.Name;
        return true;
    }

    private static bool TryParseAlterDatabaseScopedConfiguration(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Configuration })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            return false;

        context.MoveNextRequired();
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Verbose_Truncation_Warnings })
            return false;

        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: var on } || on is not (Keyword.On or Keyword.Off))
            return false;

        if (!context.Batch.IsSkipping)
            context.CurrentDatabase.VerboseTruncationWarnings = on == Keyword.On;
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SEQUENCE [schema.]name [RESTART [WITH n]] [INCREMENT BY n]
    /// [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE]
    /// [CACHE n | NO CACHE]</c>. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>SEQUENCE</c> contextual keyword. <c>RESTART</c> resets
    /// <see cref="Sequence.CurrentValue"/> to the explicit value or to
    /// <see cref="Sequence.StartValue"/>, and clears
    /// <see cref="Sequence.IsExhausted"/>. Other options replace the
    /// matching field. Probe-confirmed: ALTER SEQUENCE accepts the same
    /// option subset as CREATE SEQUENCE.
    /// </summary>
    private static bool TryParseAlterSequence(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var sequenceName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
        {
            // Walk past any option tokens so the dispatch loop's lookahead
            // doesn't trip on them.
            while (context.MoveNext() && context.Token is not (Operator { Character: ';' } or ReservedKeyword))
            {
                // no-op
            }
            return true;
        }

        if (!context.Batch.TryResolveSequence(sequenceName, out var sequence))
            throw SimulatedSqlException.InvalidObjectName(sequenceName);
        // ALTER SEQUENCE needs ALTER on the sequence (schema ALTER / object
        // CONTROL cover it) — Msg 15151, the same record a missing sequence
        // earns, naming the leaf (probe-confirmed).
        sequence.Schema.Database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(
                context.Batch, context.Batch.DatabaseFor(sequence), sequence.ObjectId, sequence.SchemaId))
        {
            throw SimulatedSqlException.CannotAlterSequence(sequenceName.Leaf);
        }
        // TryResolveSequence took Sch-S; upgrade to Sch-M before mutating
        // the sequence's option fields. Other connections reading the
        // sequence (NEXT VALUE FOR) will wait on the Sch-M acquire.
        context.Batch.AcquireStatementLock(sequence.SchemaLock, LockMode.SchemaModification);

        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Restart }:
                    {
                        // RESTART [WITH n]: peek WITH; if present, read the
                        // value; otherwise reset to the original start value.
                        var afterRestart = context.SaveCheckpoint();
                        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.With })
                        {
                            // RESTART WITH n moves the sequence's *start* as
                            // well as its position — probe-confirmed against
                            // SQL Server 2025: sys.sequences.start_value
                            // reports n afterwards, and a later bare RESTART
                            // returns to n rather than to the value the
                            // sequence was declared with.
                            sequence.StartValue = ReadSignedIntegerLiteral(context);
                            sequence.CurrentValue = sequence.StartValue;
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterRestart);
                            sequence.CurrentValue = sequence.StartValue;
                        }
                        sequence.IsExhausted = false;
                        // RESTART clears the runtime last-used marker (real
                        // reports last_used_value NULL after a restart, until
                        // the next NEXT VALUE FOR).
                        sequence.LastUsedValue = null;
                        continue;
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    sequence.Increment = ReadSignedIntegerLiteral(context);
                    if (sequence.Increment == 0)
                        throw SimulatedSqlException.SequenceIncrementCannotBeZero(sequence.FullName);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    sequence.MinValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    sequence.MaxValue = ReadSignedIntegerLiteral(context);
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    sequence.Cycle = true;
                    continue;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    {
                        var afterNo = context.GetNextRequired();
                        switch (afterNo)
                        {
                            case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                                sequence.Cycle = false;
                                continue;
                            case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue or ContextualKeyword.MaxValue or ContextualKeyword.Cache }:
                                continue;
                            default:
                                return false;
                        }
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    {
                        var afterCache = context.SaveCheckpoint();
                        if (!context.MoveNext() || context.Token is not (Numeric or Operator { Character: '-' or '+' }))
                        {
                            context.RestoreCheckpoint(afterCache);
                        }
                        else
                        {
                            context.RestoreCheckpoint(afterCache);
                            _ = ReadSignedIntegerLiteral(context);
                        }
                        continue;
                    }
                default:
                    RecordDdlEvent(context, "ALTER_SEQUENCE", sequence.Schema.Name, sequence.Name, "SEQUENCE");
                    return true;
            }
        }
        RecordDdlEvent(context, "ALTER_SEQUENCE", sequence.Schema.Name, sequence.Name, "SEQUENCE");
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SCHEMA dest TRANSFER [ (OBJECT|TYPE)::] source.obj</c>.
    /// Entered with <see cref="ParserContext.Token"/> on the <c>SCHEMA</c>
    /// keyword. Routes the named object between schemas:
    /// <list type="bullet">
    /// <item><c>OBJECT</c> class (default if no prefix given): targets the
    /// shared-namespace dicts on <see cref="Schema"/> —
    /// <see cref="Schema.HeapTables"/>, <see cref="Schema.Views"/>,
    /// <see cref="Schema.Functions"/>, <see cref="Schema.Procedures"/>,
    /// <see cref="Schema.Sequences"/>. Triggers are not directly
    /// transferable — they move along with their parent table or view
    /// automatically (Msg 15347 if named directly).</item>
    /// <item><c>TYPE</c> class: targets <see cref="Schema.TableTypes"/>.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed error paths (SQL Server 2025, 2026-05-13):
    /// </para>
    /// <list type="bullet">
    /// <item>Destination schema doesn't exist → <strong>Msg 15151</strong>
    /// alter-schema variant.</item>
    /// <item>Source object/type doesn't exist → <strong>Msg 15151</strong>
    /// find-object / find-type variant (leaf name only — qualifier not
    /// echoed).</item>
    /// <item>Source = destination schema and the object exists in source →
    /// silent no-op (probe-confirmed).</item>
    /// <item>Object with same leaf already exists in destination →
    /// <strong>Msg 15530</strong>.</item>
    /// <item>Source is a trigger → <strong>Msg 15347</strong> (triggers
    /// follow their parent's schema; can't be transferred directly).</item>
    /// </list>
    /// <para>
    /// When the transferred object is a heap table or view, any attached
    /// triggers automatically reseat into the destination schema's
    /// <see cref="Schema.Triggers"/> dict and their <see cref="Trigger.Schema"/>
    /// reference + <see cref="SchemaObject.SchemaId"/> update — mirrors
    /// real SQL Server's "triggers belong to their parent's schema" rule.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterSchemaTransfer(ParserContext context)
    {
        if (context.GetNextRequired() is not Name destSchemaToken)
            return false;
        var destSchemaName = destSchemaToken.Value;

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Transfer })
            return false;

        // Optional class prefix: OBJECT:: or TYPE::. Both Object and Type are
        // contextual keywords, and the :: separator tokenizes as two adjacent
        // single-character ':' operators.
        var classIsType = false;
        var afterTransfer = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is UnquotedString { ContextualKeyword: var ck }
            && ck is ContextualKeyword.Object or ContextualKeyword.Type)
        {
            var first = context.GetNextRequired();
            var second = context.GetNextRequired();
            if (first is not Operator { Character: ':' } || second is not Operator { Character: ':' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            classIsType = ck == ContextualKeyword.Type;
            context.MoveNextRequired();
        }
        else
        {
            context.RestoreCheckpoint(afterTransfer);
            context.MoveNextRequired();
        }

        var sourceName = BatchContext.ParseObjectName(context);

        if (context.Batch.IsSkipping)
            return true;

        // Real refuses the read-only database ahead of every resolution here —
        // a TRANSFER naming a missing object still reports Msg 3906
        // (probe-confirmed).
        context.CurrentDatabase.RejectWriteWhenReadOnly();

        if (!context.CurrentDatabase.Schemas.TryGetValue(destSchemaName, out var destSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(destSchemaName);
        // ALTER on the destination schema is the first half of real's gate, and
        // reports the same Msg 15151 a missing destination earns.
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, destSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(destSchemaName);

        if (!context.Batch.TryResolveSchema(sourceName, out var sourceSchema))
        {
            throw classIsType
                ? SimulatedSqlException.CannotFindType(sourceName.Leaf)
                : SimulatedSqlException.CannotFindObject(sourceName.Leaf);
        }
        // The second half is CONTROL on the object being moved — probe-confirmed
        // that ALTER on the *source* schema is not enough, and that the refusal
        // is its own Msg 15151 wording.
        RejectUnauthorizedSchemaTransfer(context, sourceSchema, sourceName, classIsType);

        if (classIsType)
            TransferTableType(sourceSchema, destSchema, sourceName.Leaf, context.Batch);
        else
            TransferObject(sourceSchema, destSchema, sourceName.Leaf, context.Batch);
        // Real reports the transferred object, not the schema — SchemaName is
        // the destination and ObjectName / ObjectType describe what moved.
        RecordDdlEvent(context, "ALTER_SCHEMA", destSchemaName, sourceName.Leaf, classIsType ? "TYPE" : "OBJECT");
        return true;
    }

    /// <summary>
    /// Moves a user-defined table type between schemas. Lookup miss →
    /// Msg 15151 find-type; collision in destination → Msg 15530. Same-schema
    /// transfer is a no-op (matches probe). Tests for fidelity: real SQL
    /// Server also moves the type's underlying type-table id via
    /// <see cref="SchemaObject.SchemaId"/>; <see cref="TableType.Schema"/>
    /// reference updates in lockstep.
    /// </summary>
    /// <summary>
    /// The moved-object half of the <c>ALTER SCHEMA … TRANSFER</c> gate: CONTROL
    /// on the object (or the type's owning schema, since the simulator's GRANT
    /// surface carries no <c>TYPE::</c> securable class). Denial is Msg 15151
    /// <c>Cannot transfer the object '…'</c>. No-op when the name resolves to
    /// nothing — the caller's own not-found record still runs.
    /// </summary>
    private static void RejectUnauthorizedSchemaTransfer(ParserContext context, Schema sourceSchema, MultiPartName sourceName, bool classIsType)
    {
        if (classIsType)
        {
            if (sourceSchema.TableTypes.ContainsKey(sourceName.Leaf)
                && !PermissionEnforcement.HasSchemaControl(context.Batch, sourceSchema))
            {
                throw SimulatedSqlException.CannotTransferObject(sourceName.Leaf);
            }
            return;
        }
        foreach (var candidate in sourceSchema.SchemaObjects())
        {
            if (!sourceSchema.Database.Collation.Equals(candidate.Name, sourceName.Leaf))
                continue;
            if (!PermissionEnforcement.HasObjectControl(context.Batch, sourceSchema.Database, candidate.ObjectId, candidate.SchemaId))
                throw SimulatedSqlException.CannotTransferObject(sourceName.Leaf);
            return;
        }
    }

    private static void TransferTableType(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        if (!sourceSchema.TableTypes.TryGetValue(leafName, out var tableType))
            throw SimulatedSqlException.CannotFindType(leafName);
        if (ReferenceEquals(sourceSchema, destSchema))
            return;
        if (destSchema.TableTypes.ContainsKey(leafName))
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
        batch.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaModification);
        _ = sourceSchema.TableTypes.TryRemove(leafName, out _);
        destSchema.TableTypes[leafName] = tableType;
        tableType.Schema = destSchema;
        tableType.SchemaId = destSchema.SchemaId;
    }

    /// <summary>
    /// Moves an object between schemas. Walks the source schema's shared-
    /// namespace dicts (heap tables / views / functions / procedures /
    /// sequences / synonyms) — first hit by leaf name wins. Triggers explicitly raise
    /// Msg 15347 since they're owned by their parent (the trigger's schema
    /// follows its parent's schema automatically). After the move,
    /// HeapTable / View transfers reseat any attached triggers — they belong
    /// to the destination schema after the transfer.
    /// </summary>
    private static void TransferObject(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        // Triggers can't be transferred directly — Msg 15347 owns this case.
        if (sourceSchema.Triggers.TryGetValue(leafName, out _))
            throw SimulatedSqlException.CannotTransferObjectOwnedByParent();

        var sameSchema = ReferenceEquals(sourceSchema, destSchema);

        if (sourceSchema.HeapTables.TryGetValue(leafName, out var heap))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, heap);
            batch.AcquireStatementLock(heap.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.HeapTables.TryRemove(leafName, out _);
            destSchema.HeapTables[leafName] = heap;
            heap.SchemaId = destSchema.SchemaId;
            heap.OwningDatabase = destSchema.Database;
            ReseatAttachedTriggers(sourceSchema, destSchema, heap);
            return;
        }
        if (sourceSchema.Views.TryGetValue(leafName, out var view))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, view);
            batch.AcquireStatementLock(view.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Views.TryRemove(leafName, out _);
            destSchema.Views[leafName] = view;
            view.Schema = destSchema;
            view.SchemaId = destSchema.SchemaId;
            ReseatAttachedTriggers(sourceSchema, destSchema, view);
            return;
        }
        if (sourceSchema.Functions.TryGetValue(leafName, out var fn))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, fn);
            batch.AcquireStatementLock(fn.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Functions.TryRemove(leafName, out _);
            destSchema.Functions[leafName] = fn;
            fn.Schema = destSchema;
            fn.SchemaId = destSchema.SchemaId;
            return;
        }
        if (sourceSchema.Procedures.TryGetValue(leafName, out var proc))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(proc.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Procedures.TryRemove(leafName, out _);
            destSchema.Procedures[leafName] = proc;
            proc.Schema = destSchema;
            proc.SchemaId = destSchema.SchemaId;
            return;
        }
        if (sourceSchema.Sequences.TryGetValue(leafName, out var seq))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(seq.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Sequences.TryRemove(leafName, out _);
            destSchema.Sequences[leafName] = seq;
            seq.Schema = destSchema;
            seq.SchemaId = destSchema.SchemaId;
            return;
        }
        // A synonym moves as a plain name indirection: its stored base name is
        // untouched by the transfer (probe-confirmed — base_object_name still
        // reads [dbo].[t] after the synonym lands in another schema).
        if (sourceSchema.Synonyms.TryGetValue(leafName, out var synonym))
        {
            if (sameSchema) return;
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            batch.AcquireStatementLock(synonym.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Synonyms.TryRemove(leafName, out _);
            destSchema.Synonyms[leafName] = synonym;
            synonym.Schema = destSchema;
            synonym.SchemaId = destSchema.SchemaId;
            return;
        }

        throw SimulatedSqlException.CannotFindObject(leafName);
    }

    /// <summary>
    /// Raises <strong>Msg 15348</strong> when a <c>WITH SCHEMABINDING</c>
    /// module references the object being transferred — a schema-bound
    /// reference is two-part, so moving the referent would break it. Real
    /// gates only the referenced side: transferring the schema-bound module
    /// itself succeeds (probe-confirmed).
    /// </summary>
    private static void RejectTransferOfSchemaBoundReferent(BatchContext batch, SchemaObject target)
    {
        if (SchemaBinding.FindReferencingModule(batch.CurrentDatabase, target) is not null)
            throw SimulatedSqlException.CannotTransferSchemaBoundObject();
    }

    /// <summary>
    /// Parses the modeled <c>ALTER TABLE</c> shapes: <c>SET (SYSTEM_VERSIONING
    /// = OFF)</c>, <c>[WITH CHECK | WITH NOCHECK] ADD [CONSTRAINT name]
    /// (PRIMARY KEY | UNIQUE | FOREIGN KEY | CHECK | DEFAULT) …</c>, and
    /// <c>DROP CONSTRAINT [IF EXISTS] name [, …]</c>. Every other shape (ADD /
    /// DROP COLUMN, ALTER COLUMN, REBUILD, SET other options, ENABLE /
    /// DISABLE, etc.) raises <see cref="NotSupportedException"/> at the
    /// post-name dispatch point. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>TABLE</c> keyword.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed error paths (SQL Server 2025, 2026-05-13):
    /// </para>
    /// <list type="bullet">
    /// <item>Target name doesn't resolve → <strong>Msg 4902</strong>
    /// (alter-table-specific table-not-found variant; distinct from Msg 208's
    /// generic name-resolution wording).</item>
    /// <item>SET (SYSTEM_VERSIONING = OFF) on a plain regular table or
    /// history sibling → <strong>Msg 13591</strong>.</item>
    /// <item>Unmodeled ALTER TABLE shapes → <see cref="NotSupportedException"/>.</item>
    /// </list>
    /// <para>
    /// ADD / DROP CONSTRAINT paths are documented on
    /// <see cref="TryParseAlterTableAddConstraint"/> and
    /// <see cref="TryParseAlterTableDropConstraint"/>.
    /// </para>
    /// </remarks>
    private static bool TryParseAlterTable(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var tableName = BatchContext.ParseObjectName(context);

        // Sch-M for the ALTER's lifetime — acquired here at the dispatcher
        // entry so every sub-parser (ADD / DROP / ALTER COLUMN / ADD CONSTRAINT
        // / DROP CONSTRAINT / CHECK / NOCHECK / SET SYSTEM_VERSIONING) runs
        // under exclusive schema modification. Sub-parsers still call
        // TryResolveTable themselves to surface their own context-specific
        // missing-table error (Msg 4902 / 4904 / etc.); the additional Sch-S
        // those acquires take is harmless under same-owner Sch-M reentrance.
        // Skip the early acquire when the table doesn't exist — the sub-
        // parser's TryResolveTable then raises the right error code without
        // having acquired anything.
        if (!context.Batch.IsSkipping && context.Batch.TryResolveTable(tableName, out var alterTarget))
        {
            // ALTER TABLE needs ALTER on the object (object-scope suffices —
            // probe M5b); a non-privileged principal gets Msg 1088 state 13.
            // Temp tables / table variables are session-owned and exempt.
            alterTarget.OwningDatabase?.RejectWriteWhenReadOnly(state: 12);
            if (!alterTarget.IsTableVariable
                && !BatchContext.IsLocalTempName(alterTarget.Name)
                && !BatchContext.IsGlobalTempName(alterTarget.Name)
                && !PermissionEnforcement.HasObjectAlter(context.Batch, context.Batch.DatabaseFor(alterTarget), alterTarget.ObjectId, alterTarget.SchemaId))
            {
                throw SimulatedSqlException.AlterTablePermissionDenied(tableName.Leaf);
            }
            context.Batch.AcquireStatementLock(alterTarget.SchemaLock, LockMode.SchemaModification);
        }

        // Cursor is on the last name segment; advance to the post-name token.
        context.MoveNextRequired();

        // Optional WITH CHECK | WITH NOCHECK preceding ADD or CHECK / NOCHECK
        // CONSTRAINT. Default differs by action: ADD defaults to validate
        // (= WITH CHECK), CHECK CONSTRAINT defaults to skip-validate (= WITH
        // NOCHECK). Track tri-state so each branch can apply its own default.
        bool? withCheckExplicit = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            withCheckExplicit = context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.Check } => true,
                ReservedKeyword { Keyword: Keyword.NoCheck } => false,
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            context.MoveNextRequired();
        }

        var handled = TryParseAlterTableAction(context, tableName, withCheckExplicit);
        // Every accepted shape raises one ALTER_TABLE event (probe-confirmed:
        // ADD COLUMN and ADD CONSTRAINT both report ALTER_TABLE, differing only
        // in the AlterTableActionList detail the simulator doesn't emit).
        if (handled)
            RecordDdlEvent(context, "ALTER_TABLE", EventSchemaName(tableName), tableName.Leaf, "TABLE");
        return handled;
    }

    /// <summary>
    /// Routes the post-name body of <c>ALTER TABLE</c> to the sub-parser its
    /// leading keyword names. Split from <see cref="TryParseAlterTable"/> so the
    /// caller has one success point to raise the DDL event from.
    /// </summary>
    private static bool TryParseAlterTableAction(ParserContext context, MultiPartName tableName, bool? withCheckExplicit)
    {
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Set }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableSetSystemVersioning(context, tableName);
            case ReservedKeyword { Keyword: Keyword.Add }:
                // ADD defaults to validate; only explicit WITH NOCHECK skips.
                return TryParseAlterTableAddConstraint(context, tableName, withNoCheck: withCheckExplicit == false);
            case ReservedKeyword { Keyword: Keyword.Drop }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableDropConstraint(context, tableName);
            case ReservedKeyword { Keyword: Keyword.Check }:
                // CHECK CONSTRAINT — re-enable enforcement on existing
                // constraint(s). Default skip-validate; explicit WITH CHECK
                // revalidates and clears IsNotTrusted on success.
                return TryParseAlterTableTrustToggle(context, tableName, disable: false, revalidate: withCheckExplicit == true);
            case ReservedKeyword { Keyword: Keyword.NoCheck }:
                // NOCHECK CONSTRAINT — disable enforcement. WITH-prefix is
                // semantically irrelevant (NOCHECK always implies "don't
                // validate"); probe shows real SQL Server accepts but ignores
                // the prefix here.
                return TryParseAlterTableTrustToggle(context, tableName, disable: true, revalidate: false);
            case ReservedKeyword { Keyword: Keyword.Alter }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableAlterColumn(context, tableName);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableRebuild(context, tableName);
            default:
                throw new NotSupportedException("ALTER TABLE supports only SET (SYSTEM_VERSIONING = OFF), ADD / DROP / ALTER COLUMN, ADD / DROP CONSTRAINT, CHECK / NOCHECK CONSTRAINT and REBUILD shapes.");
        }
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … SET (SYSTEM_VERSIONING = OFF | ON [(&lt;options&gt;)])</c>,
    /// where the options are the <c>HISTORY_TABLE</c> /
    /// <c>HISTORY_RETENTION_PERIOD</c> / <c>DATA_CONSISTENCY_CHECK</c> list
    /// shared with CREATE TABLE. Cursor is on the <c>SET</c> keyword on entry.
    /// Probe-confirmed flow for OFF: target table must resolve (Msg 4902
    /// otherwise), must be system-versioned (Msg 13591 otherwise); the
    /// parent's link to its history sibling clears and the sibling's
    /// history-role flag flips. Period / GENERATED-ALWAYS column metadata is
    /// preserved. For ON: the base must have a PERIOD FOR SYSTEM_TIME
    /// declaration (Msg 13510 otherwise); a named history table that exists is
    /// shape-validated against the base and linked, one that doesn't is
    /// created from the base's shape, and an omitted name auto-generates one.
    /// </summary>
    private static bool TryParseAlterTableSetSystemVersioning(ParserContext context, MultiPartName tableName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning })
            throw new NotSupportedException("Only ALTER TABLE … SET (SYSTEM_VERSIONING = OFF | ON (HISTORY_TABLE = name)) is supported.");

        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var onOff = context.GetNextRequired();
        if (onOff is ReservedKeyword { Keyword: Keyword.Off })
        {
            if (context.GetNextRequired() is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            if (context.Batch.IsSkipping)
                return true;

            if (!context.Batch.TryResolveTable(tableName, out var table))
                throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

            if (table.SystemVersioning is null)
                throw SimulatedSqlException.SystemVersioningNotOn(QualifyTableName(table, context.CurrentDatabase));

            var historyTable = table.SystemVersioning;
            table.SystemVersioning = null;
            historyTable.IsHistoryTable = false;
            return true;
        }

        if (onOff is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var options = ParseSystemVersioningOnOptions(context);
        if (context.GetNextRequired() is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var baseTable))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        if (baseTable.PeriodColumns is null)
            throw SimulatedSqlException.SystemVersioningRequiresPeriod(state: 1);

        // Re-issuing SET ON against the sibling the base already has is how a
        // retention period is changed in place; every other re-issue is a
        // rejection, and real reports the existing link before it resolves the
        // name it was handed (probe-confirmed: an unresolvable name reports
        // Msg 13757 rather than Msg 4902).
        if (baseTable.SystemVersioning is { } currentHistory)
        {
            if (options.HistoryTable is not { } requested)
                throw SimulatedSqlException.SystemVersioningAlreadyOn(QualifyTableName(baseTable, context.CurrentDatabase));
            if (!context.Batch.TryResolveTable(requested, out var requestedHistory))
                throw SimulatedSqlException.TemporalTableAlreadyHasHistoryTable(QualifyTableName(baseTable, context.CurrentDatabase));
            if (!ReferenceEquals(requestedHistory, currentHistory))
            {
                throw SimulatedSqlException.TemporalHistoryTableNameNotCorrect(
                    QualifyTableName(requestedHistory, context.CurrentDatabase),
                    QualifyTableName(baseTable, context.CurrentDatabase));
            }
            RequireHistoryCleanupIndex(context, baseTable, currentHistory, options);
            baseTable.HistoryRetentionPeriod = options.RetentionPeriod;
            baseTable.HistoryRetentionUnit = options.RetentionUnit;
            return true;
        }

        HeapTable resolvedHistory;
        if (options.HistoryTable is { } historyName && context.Batch.TryResolveTable(historyName, out var existingHistory))
        {
            RejectUnusableHistoryTable(context, existingHistory);
            ValidateHistoryTableShape(context, baseTable, existingHistory);
            RequireHistoryCleanupIndex(context, baseTable, existingHistory, options);
            resolvedHistory = existingHistory;
        }
        else
        {
            // A history table that doesn't exist yet is created from the
            // base's shape, named as written or auto-named from the base's
            // object id — probe-confirmed: real creates it rather than
            // rejecting the ALTER.
            var historySchema = HistoryDestinationSchema(context, baseTable, options.HistoryTable);
            resolvedHistory = BuildHistoryTable(baseTable, options.HistoryTable?.Leaf ?? AutoHistoryTableName(historySchema, baseTable.ObjectId), historySchema.SchemaId, context);
            resolvedHistory.OwningDatabase = historySchema.Database;
            if (!historySchema.HeapTables.TryAdd(resolvedHistory.Name, resolvedHistory))
                throw SimulatedSqlException.ThereIsAlreadyAnObject(resolvedHistory.Name);
        }

        baseTable.SystemVersioning = resolvedHistory;
        baseTable.HistoryRetentionPeriod = options.RetentionPeriod;
        baseTable.HistoryRetentionUnit = options.RetentionUnit;
        resolvedHistory.IsHistoryTable = true;
        return true;
    }

    /// <summary>
    /// Resolves the schema a to-be-created history table lands in: the one the
    /// name qualifies, or the base table's own for an unqualified or
    /// auto-generated name.
    /// </summary>
    private static Schema HistoryDestinationSchema(ParserContext context, HeapTable baseTable, MultiPartName? historyName)
    {
        if (historyName is { } name && name.Count >= 2)
        {
            return context.Batch.TryResolveSchema(name, out var named)
                ? named
                : throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(name.ImmediateQualifier!);
        }
        foreach (var schema in context.CurrentDatabase.Schemas.Values)
        {
            if (schema.SchemaId == baseTable.SchemaId)
                return schema;
        }
        throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(Database.DefaultSchemaName);
    }

    /// <summary>
    /// Moves every trigger whose <see cref="Trigger.Parent"/> matches
    /// <paramref name="movedParent"/> from <paramref name="sourceSchema"/>'s
    /// <see cref="Schema.Triggers"/> dict into <paramref name="destSchema"/>'s
    /// — mirrors SQL Server's "trigger schema follows parent" rule.
    /// Pre-existing destination-schema triggers with the same leaf are
    /// impossible in practice (a trigger's name shares the shared namespace
    /// via <see cref="Schema.HasNameInSharedNamespace"/>, which the upstream
    /// collision check has already rejected via Msg 15530 before this point).
    /// </summary>
    private static void ReseatAttachedTriggers(Schema sourceSchema, Schema destSchema, SchemaObject movedParent)
    {
        if (ReferenceEquals(sourceSchema, destSchema))
            return;
        string[]? names = null;
        foreach (var kv in sourceSchema.Triggers)
        {
            if (ReferenceEquals(kv.Value.Parent, movedParent))
            {
                names ??= [];
                Array.Resize(ref names, names.Length + 1);
                names[^1] = kv.Key;
            }
        }
        if (names is null) return;
        foreach (var n in names)
        {
            if (!sourceSchema.Triggers.TryRemove(n, out var trigger))
                continue;
            destSchema.Triggers[n] = trigger;
            trigger.Schema = destSchema;
            trigger.SchemaId = destSchema.SchemaId;
        }
    }

    /// <summary>
    /// <c>ALTER TABLE … REBUILD WITH (…)</c> options, mapped to the value
    /// grammar each takes. Every one of them describes physical storage a flat
    /// page list doesn't have, so all are validated by name and discarded.
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenDictionary<string, bool> RebuildOptions =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            // true = the value is a bare word (ON / OFF / a compression level);
            // false = the value is numeric.
            ["DATA_COMPRESSION"] = true,
            ["MAXDOP"] = false,
            ["ONLINE"] = true,
            ["SORT_IN_TEMPDB"] = true,
            ["XML_COMPRESSION"] = true,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Compression levels <c>DATA_COMPRESSION</c> takes. A name outside the set
    /// is Msg 102 on real rather than the option-name Msg 155, since the value
    /// slot is a closed keyword list rather than an identifier.
    /// </summary>
    private static readonly System.Collections.Frozen.FrozenSet<string> DataCompressionLevels = new[]
    {
        "COLUMNSTORE",
        "COLUMNSTORE_ARCHIVE",
        "NONE",
        "PAGE",
        "ROW",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses <c>ALTER TABLE &lt;table&gt; REBUILD [PARTITION = { ALL |
    /// &lt;number&gt; }] [WITH ( option = value [, …] )]</c>. Rebuilding
    /// re-lays-out a table's physical storage, which the simulator's flat page
    /// list has no notion of, so the statement validates and succeeds without
    /// touching a row — probe-confirmed that real leaves the data identical.
    /// </summary>
    /// <remarks>
    /// What is modeled is the validation. An option name outside real's list is
    /// <b>Msg 155</b> in its ALTER TABLE wording, a bad <c>DATA_COMPRESSION</c>
    /// level or an empty option list is <b>Msg 102</b>, and a partition number
    /// where nothing is partitioned splits three ways exactly as real's does:
    /// <b>Msg 7729</b> State 1 naming the table's clustered index (in real's own
    /// "alter index statement" wording, whichever statement raised it),
    /// <b>Msg 7735</b> naming the table when it carries no index, and
    /// <b>Msg 7729</b> State 3 for the <c>ON PARTITIONS (…)</c> sub-clause.
    /// </remarks>
    private static bool TryParseAlterTableRebuild(ParserContext context, MultiPartName tableName)
    {
        context.MoveNextOptional();
        var namedPartition = ParseOptionalIndexPartitionClause(context);
        var namedPartitionList = ParseOptionalRebuildOptions(context);

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        if (namedPartitionList)
            throw SimulatedSqlException.PartitionNumberOnUnpartitionedTable(table.Name);
        if (namedPartition)
        {
            throw table.KeyConstraints.Count > 0
                ? SimulatedSqlException.PartitionNumberOnUnpartitionedIndex(table.KeyConstraints[0].Name)
                : SimulatedSqlException.RebuildPartitionOnUnpartitioned(alterIndex: false, indexName: null, table.Name);
        }

        return true;
    }

    /// <summary>
    /// Parses REBUILD's own <c>WITH ( … )</c> block, returning
    /// <see langword="true"/> when an <c>ON PARTITIONS (…)</c> sub-clause was
    /// written — which nothing here is partitioned enough to satisfy, so the
    /// caller raises real's refusal once the table has resolved. Cursor on
    /// entry: the token after the PARTITION clause. On exit: past the block.
    /// </summary>
    private static bool ParseOptionalRebuildOptions(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.With })
            return false;
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var onPartitions = false;
        while (true)
        {
            if (context.GetNextRequired() is not (StringToken or ReservedKeyword))
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var optionName = context.Token.Source.ToString();
            if (!RebuildOptions.TryGetValue(optionName, out var bareWordValue))
                throw SimulatedSqlException.UnrecognizedAlterTableOption(optionName);
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var value = context.GetNextRequired();
            if (!bareWordValue)
            {
                if (value is not Numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (DataCompressionOption.Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                if (!DataCompressionLevels.Contains(value.Source.ToString()))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (value is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off })
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.On })
            {
                if (context.GetNextRequired() is not Name partitionsWord
                    || !partitionsWord.Value.Equals("PARTITIONS", StringComparison.OrdinalIgnoreCase))
                {
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                onPartitions = true;
                if (context.GetNextRequired() is not Operator { Character: '(' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                SkipBalancedParens(context);
                context.MoveNextRequired();
            }

            if (context.Token is not Operator { Character: ',' })
                break;
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return onPartitions;
    }

    private const string DataCompressionOption = "DATA_COMPRESSION";
}
