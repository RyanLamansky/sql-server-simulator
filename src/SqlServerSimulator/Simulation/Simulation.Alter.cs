using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Dispatches the <c>ALTER</c> statements by the word after <c>ALTER</c>;
    /// <c>ALTER DATABASE</c> and <c>ALTER DATABASE SCOPED CONFIGURATION</c>
    /// are parsed here.
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
            case UnquotedString { ContextualKeyword: ContextualKeyword.Partition }:
                return TryParseAlterPartition(context);
            case ReservedKeyword { Keyword: Keyword.Schema }:
                return TryParseAlterSchemaTransfer(context);
            case ReservedKeyword { Keyword: Keyword.Table }:
                return TryParseAlterTable(context);
            case ReservedKeyword { Keyword: Keyword.Index }:
                return TryParseAlterIndex(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Role }:
                return TryParseAlterRole(context);
            case ReservedKeyword { Keyword: Keyword.User }:
                return TryParseAlterUser(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Login }:
                return TryParseAlterLogin(context);
            case Name serverWord when serverWord.Value.Equals("SERVER", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterServerRole(context);
            case Name appWord when appWord.Value.Equals("APPLICATION", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterApplicationRole(context);
            case Name credentialWord when credentialWord.Value.Equals("CREDENTIAL", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterCredential(context);
            case Name securityWord when securityWord.Value.Equals("SECURITY", StringComparison.OrdinalIgnoreCase):
                return TryParseAlterSecurityPolicy(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.FullText }:
                return TryParseAlterFullText(context);
            case ReservedKeyword { Keyword: Keyword.Authorization }:
                return TryParseAlterAuthorization(context);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Xml }:
                return TryParseAlterXmlSchemaCollection(context);
            case ReservedKeyword { Keyword: Keyword.Database }:
                break;
            default:
                return RejectPredicateClauseWord(context);
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

        // A target that can't be altered still has the rest of the statement
        // read before the refusal, as real parses it before running it: skip
        // mode walks the tail without applying it, so the error ends the whole
        // statement rather than leaving a `SET …` tail to be dispatched as a
        // statement of its own.
        Database target;
        SimulatedSqlException? refusal = null;
        try
        {
            target = ResolveAlterDatabaseTarget(context, afterDatabase);
        }
        catch (SimulatedSqlException ex) when (ex.Number == 5011)
        {
            refusal = ex;
            target = context.CurrentDatabase;
        }

        var batch = context.Batch;
        var savedSkip = batch.SkipModeFlag;
        if (refusal is not null)
            batch.SkipModeFlag = true;
        bool parsed;
        // Only SET reports a refusal with Msg 5069 after it; the COLLATE,
        // MODIFY, ADD and REMOVE forms name a database that doesn't exist with
        // Msg 911 instead, and a read-only one with a plain Msg 3906 (probed
        // 2026-09-27 against SQL Server 2025).
        var verb = context.GetNextRequired();
        var isSet = verb is ReservedKeyword { Keyword: Keyword.Set };
        try
        {
            if (!isSet && refusal is not null && afterDatabase is Name missing
                && !context.Connection.Simulation.Databases.ContainsKey(missing.Value)
                && verb switch
                {
                    ReservedKeyword { Keyword: Keyword.Collate or Keyword.Add } => true,
                    Name { Value: var word } => BuiltInToken.Equals(word, "MODIFY") || BuiltInToken.Equals(word, "REMOVE"),
                    _ => false,
                })
            {
                throw SimulatedSqlException.DatabaseDoesNotExist(missing.Value);
            }
            parsed = verb switch
            {
                ReservedKeyword { Keyword: Keyword.Set } => TryParseAlterDatabaseSet(context, target),
                ReservedKeyword { Keyword: Keyword.Collate } => TryParseAlterDatabaseCollate(context, target),
                Name modify when BuiltInToken.Equals(modify.Value, "MODIFY") => context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.File } => TryParseAlterDatabaseModifyFile(context, target),
                    Name { Value: var what } when BuiltInToken.Equals(what, "FILEGROUP") => TryParseAlterDatabaseModifyFilegroup(context, target),
                    _ => TryParseAlterDatabaseModifyName(context, target),
                },
                ReservedKeyword { Keyword: Keyword.Add } => TryParseAlterDatabaseFilegroup(context, target, add: true),
                Name remove when BuiltInToken.Equals(remove.Value, "REMOVE") => TryParseAlterDatabaseFilegroup(context, target, add: false),
                _ => false,
            };
        }
        catch (SimulatedSqlException ex) when (ex.Number is 3906 or 12438 && (isSet || verb is ReservedKeyword { Keyword: Keyword.Collate }))
        {
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(ex);
        }
        finally
        {
            batch.SkipModeFlag = savedSkip;
        }

        if (parsed && refusal is not null)
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(refusal);
        if (parsed)
            RecordServerDdlEvent(context, "ALTER_DATABASE", target.Name, loginName: null);
        return parsed;
    }

    /// <summary>
    /// The database an <c>ALTER DATABASE</c> statement targets: the session's
    /// for <c>CURRENT</c>, else the named one. A name this
    /// <see cref="Simulation"/> doesn't host raises Msg 5011 (state 5), and a
    /// principal without ALTER on the database it did find raises the same
    /// number at state 9 — probe-confirmed, so a restricted caller can't tell
    /// the two apart. The caller follows the refusal with Msg 5069 once the
    /// statement has parsed. Resolution is suppressed in skip mode, where the
    /// statement parses but doesn't run.
    /// </summary>
    private static Database ResolveAlterDatabaseTarget(ParserContext context, Token afterDatabase)
    {
        if (afterDatabase is not Name && !context.Batch.IsSkipping && SystemDatabaseNames.Contains(context.CurrentDatabase.Name))
            throw SimulatedSqlException.AlterCurrentSystemDatabase(context.CurrentDatabase.Name);
        var target = afterDatabase is not Name named ? context.CurrentDatabase
            : context.Connection.Simulation.Databases.TryGetValue(named.Value, out var named_) ? named_
            : context.Batch.IsSkipping ? context.CurrentDatabase
            : throw SimulatedSqlException.CannotAlterDatabase(named.Value);
        return context.Batch.IsSkipping || PermissionEnforcement.HasDatabasePermission(context.Batch, target, Permission.Alter)
            ? target
            : throw SimulatedSqlException.AlterDatabasePermissionDenied(target.Name);
    }

    /// <summary>
    /// Parses <c>ALTER DATABASE name MODIFY NAME = newname</c>, entered with the
    /// cursor on <c>NAME</c>, and renames the database — re-keying
    /// <see cref="Databases"/> — with real's Msg 5021 notice, plus Msg 5701
    /// when the session sits in the renamed database. A system database is
    /// Msg 5016 and a name another database holds Msg 1801 state 4; renaming a
    /// database to its own name, in any case, succeeds (all probed 2026-09-25
    /// against SQL Server 2025). Other sessions in the database aren't
    /// consulted, as with <c>DROP DATABASE</c>.
    /// </summary>
    private static bool TryParseAlterDatabaseModifyName(ParserContext context, Database target)
    {
        if (context.Token is not Name nameWord || !BuiltInToken.Equals(nameWord.Value, "NAME"))
            return false;
        if (context.GetNextRequired() is not Operator { Character: '=' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Name newNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // The rename takes no further clause: a comma is Msg 102 before
        // anything is renamed (probed 2026-09-25).
        if (context.GetNextOptional() is Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RejectTrailingToken();
        if (context.Batch.IsSkipping)
            return true;

        // An offline database can't be renamed (probed 2026-10-10 against SQL
        // Server 2025), though its SET options still move.
        target.RejectWhenOffline();
        RenameDatabaseTo(context.Batch, target, newNameToken.Value);
        return true;
    }

    /// <summary>
    /// Renames <paramref name="target"/>, re-keying <see cref="Databases"/>,
    /// with real's Msg 5021 notice and, for a session sitting in it, Msg 5701 —
    /// the rename <c>ALTER DATABASE … MODIFY NAME</c> and <c>sp_rename</c>'s
    /// <c>DATABASE</c> form share. A system database is Msg 5016 and a name
    /// another database holds Msg 1801 state 4.
    /// </summary>
    private static void RenameDatabaseTo(BatchContext batch, Database target, string newName)
    {
        if (SystemDatabaseNames.Contains(target.Name))
            throw SimulatedSqlException.CannotRenameSystemDatabase(target.Name);
        var databases = batch.Connection.Simulation.Databases;
        lock (databases)
        {
            if (databases.TryGetValue(newName, out var holder) && holder != target)
                throw SimulatedSqlException.DatabaseAlreadyExists(newName, state: 4);
            _ = databases.TryRemove(target.Name, out _);
            target.Name = newName;
            databases[newName] = target;
        }
        var messages = batch.Connection.PendingMessages;
        messages.Enqueue(SimulatedSqlException.DatabaseNameSetMessage(batch, newName));
        if (batch.CurrentDatabase == target)
            messages.Enqueue(SimulatedSqlException.DatabaseContextChangedMessage(batch, newName));
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
        // Whether the statement may run depends on how it ends — a termination
        // clause refuses a snapshot-isolation change (Msg 5083, probed
        // 2026-09-24 against SQL Server 2025) — while each option applies as it
        // parses, so a skip-mode pass reads the whole statement first.
        var batch = context.Batch;
        var start = context.SaveCheckpoint();
        var savedSkip = batch.SkipModeFlag;
        batch.SkipModeFlag = true;
        bool parsed;
        bool changesSnapshotIsolation;
        bool terminated;
        bool combinesChangeTracking;
        try
        {
            parsed = TryParseAlterDatabaseSetList(context, target, out changesSnapshotIsolation, out terminated, out combinesChangeTracking);
        }
        finally
        {
            batch.SkipModeFlag = savedSkip;
        }
        if (!parsed || batch.IsSkipping)
            return parsed;
        if (changesSnapshotIsolation && terminated)
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.TerminationWithVersioningChange());
        if (combinesChangeTracking)
            throw SimulatedSqlException.ChangeTrackingCombinedWithOtherOptions();

        context.RestoreCheckpoint(start);
        return TryParseAlterDatabaseSetList(context, target, out _, out _, out _);
    }

    /// <summary>
    /// The options of one <c>SET</c> — a comma-separated list (probed
    /// 2026-09-24 against SQL Server 2025), each leaving the cursor on its own
    /// last token — then an optional termination clause, which
    /// <c>READ_ONLY</c> and its access-mode siblings read themselves.
    /// </summary>
    private static bool TryParseAlterDatabaseSetList(ParserContext context, Database target, out bool changesSnapshotIsolation, out bool terminated, out bool combinesChangeTracking)
    {
        changesSnapshotIsolation = false;
        // CHANGE_TRACKING must stand alone in its SET list (Msg 22114, probed
        // 2026-09-27 against SQL Server 2025).
        var options = 0;
        var changeTracking = false;
        var queryStore = false;
        var broker = false;
        combinesChangeTracking = false;
        while (true)
        {
            options++;
            var beforeOption = context.SaveCheckpoint();
            var optionName = context.GetNextOptional() as UnquotedString;
            changeTracking |= optionName is not null && BuiltInToken.Equals(optionName.Value, "CHANGE_TRACKING");
            // One QUERY_STORE clause per statement (Msg 12417, probed
            // 2026-09-29 against SQL Server 2025), refused before the second
            // clause's own values are read.
            if (optionName is not null && BuiltInToken.Equals(optionName.Value, "QUERY_STORE"))
            {
                if (queryStore)
                    throw SimulatedSqlException.QueryStoreOptionGivenTwice();
                queryStore = true;
            }
            // One Service Broker switch per list (Msg 5062, probed 2026-09-30
            // against SQL Server 2025), naming the second.
            if (optionName is not null && RecognizedDatabaseOptions.TryGetValue(optionName.Value, out var optionKind) && optionKind == AlterDatabaseOptionKind.Broker)
            {
                if (broker)
                    throw SimulatedSqlException.DatabaseOptionConflicts(optionName.Value.ToUpperInvariant());
                broker = true;
            }
            context.RestoreCheckpoint(beforeOption);
            if (!TryParseAlterDatabaseSetOption(context, target, ref changesSnapshotIsolation))
            {
                terminated = false;
                return false;
            }
            var afterOption = context.SaveCheckpoint();
            if (context.GetNextOptional() is not Operator { Character: ',' })
            {
                context.RestoreCheckpoint(afterOption);
                break;
            }
        }
        combinesChangeTracking = changeTracking && options > 1;

        var beforeTermination = context.SaveCheckpoint();
        terminated = context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.With };
        context.RestoreCheckpoint(beforeTermination);
        return ConsumeTerminationClause(context);
    }

    private static bool TryParseAlterDatabaseSetOption(ParserContext context, Database target, ref bool changesSnapshotIsolation)
    {
        context.MoveNextRequired();
        changesSnapshotIsolation |= context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Allow_Snapshot_Isolation };
        // Load-bearing options keep their dedicated handlers. Routing on
        // ContextualKeyword first means the existing 3 paths are unchanged
        // and the new parse-and-discard surface lives on a parallel dict.
        return context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Compatibility_Level } => TryParseAlterDatabaseSetCompatibilityLevel(context, target),
            UnquotedString changeTracking when BuiltInToken.Equals(changeTracking.Value, "CHANGE_TRACKING") => TryParseAlterDatabaseSetChangeTracking(context, target),
            UnquotedString { ContextualKeyword: ContextualKeyword.Allow_Snapshot_Isolation } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.AllowSnapshotIsolation),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Committed_Snapshot } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.ReadCommittedSnapshot),
            UnquotedString { ContextualKeyword: ContextualKeyword.Recursive_Triggers } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.RecursiveTriggers),
            UnquotedString { ContextualKeyword: ContextualKeyword.Trustworthy } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.Trustworthy),
            UnquotedString { ContextualKeyword: ContextualKeyword.Db_Chaining } => TryParseAlterDatabaseSetBooleanOption(context, target, DatabaseBooleanOption.CrossDatabaseChaining),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Only } => TryParseAlterDatabaseSetAccessMode(context, target, readOnly: true),
            UnquotedString { ContextualKeyword: ContextualKeyword.Read_Write } => TryParseAlterDatabaseSetAccessMode(context, target, readOnly: false),
            UnquotedString recovery when recovery.Value.Equals("RECOVERY", StringComparison.OrdinalIgnoreCase) => TryParseAlterDatabaseSetRecovery(context, target),
            UnquotedString unquoted when RecognizedDatabaseOptions.TryGetValue(unquoted.Value, out var kind) => ConsumeDatabaseOptionTail(context, target, unquoted.Value, kind),
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
    /// <see cref="ConsumeTerminationClause"/> with SINGLE_USER / MULTI_USER /
    /// RESTRICTED_USER. Unlike those, this one is load-bearing:
    /// <see cref="Database.IsReadOnly"/> gates every write to the database.
    /// <para><c>master</c> and <c>tempdb</c> pin the option and raise
    /// <strong>Msg 5058</strong> for either value asked for, at their own states
    /// (5 and 4); <c>model</c> and <c>msdb</c> accept it. All probe-confirmed
    /// against SQL Server 2025 (2026-08-04).</para>
    /// </summary>
    private static bool TryParseAlterDatabaseSetAccessMode(ParserContext context, Database target, bool readOnly)
    {
        if (!ConsumeTerminationClause(context))
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
    /// takes effect immediately. A termination clause after the list is read
    /// by <see cref="TryParseAlterDatabaseSetList"/>, which refuses it for
    /// ALLOW_SNAPSHOT_ISOLATION (Msg 5083).
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
        /// <summary>
        /// A bare Service Broker switch (ENABLE_BROKER / DISABLE_BROKER /
        /// NEW_BROKER / ERROR_BROKER_CONVERSATIONS), recorded on
        /// <see cref="Database.BrokerEnabled"/>; at most one per SET list.
        /// </summary>
        Broker,
        /// <summary>
        /// A bare database state (ONLINE / OFFLINE / EMERGENCY) with an optional
        /// termination clause, read by <see cref="ConsumeDatabaseStateOption"/>.
        /// </summary>
        State,
        /// <summary>
        /// An option with a value grammar of its own, read by
        /// <see cref="ConsumeSpecialDatabaseOption"/>.
        /// </summary>
        Special,
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
        ["ANSI_NULL_DEFAULT"] = AlterDatabaseOptionKind.OnOff,
        ["ANSI_NULLS"] = AlterDatabaseOptionKind.OnOff,
        ["ANSI_PADDING"] = AlterDatabaseOptionKind.OnOff,
        ["ANSI_WARNINGS"] = AlterDatabaseOptionKind.OnOff,
        ["ARITHABORT"] = AlterDatabaseOptionKind.OnOff,
        ["CONCAT_NULL_YIELDS_NULL"] = AlterDatabaseOptionKind.OnOff,
        ["NUMERIC_ROUNDABORT"] = AlterDatabaseOptionKind.OnOff,
        ["QUOTED_IDENTIFIER"] = AlterDatabaseOptionKind.OnOff,
        ["TORN_PAGE_DETECTION"] = AlterDatabaseOptionKind.OnOff,
        ["TEMPORAL_HISTORY_RETENTION"] = AlterDatabaseOptionKind.OnOff,
        ["MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT"] = AlterDatabaseOptionKind.OnOff,
        ["AUTO_CLOSE"] = AlterDatabaseOptionKind.OnOff,
        ["AUTO_SHRINK"] = AlterDatabaseOptionKind.OnOff,
        ["AUTO_CREATE_STATISTICS"] = AlterDatabaseOptionKind.OnOff,
        ["AUTO_UPDATE_STATISTICS"] = AlterDatabaseOptionKind.OnOff,
        ["AUTO_UPDATE_STATISTICS_ASYNC"] = AlterDatabaseOptionKind.OnOff,
        ["CURSOR_CLOSE_ON_COMMIT"] = AlterDatabaseOptionKind.OnOff,
        ["DATE_CORRELATION_OPTIMIZATION"] = AlterDatabaseOptionKind.OnOff,
        ["HONOR_BROKER_PRIORITY"] = AlterDatabaseOptionKind.OnOff,
        ["MIXED_PAGE_ALLOCATION"] = AlterDatabaseOptionKind.OnOff,
        ["SUPPLEMENTAL_LOGGING"] = AlterDatabaseOptionKind.OnOff,
        ["PARAMETERIZATION"] = AlterDatabaseOptionKind.EnumIdent,
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
        ["ENABLE_BROKER"] = AlterDatabaseOptionKind.Broker,
        ["DISABLE_BROKER"] = AlterDatabaseOptionKind.Broker,
        ["NEW_BROKER"] = AlterDatabaseOptionKind.Broker,
        ["ERROR_BROKER_CONVERSATIONS"] = AlterDatabaseOptionKind.Broker,
        ["ONLINE"] = AlterDatabaseOptionKind.State,
        ["OFFLINE"] = AlterDatabaseOptionKind.State,
        ["EMERGENCY"] = AlterDatabaseOptionKind.State,
        ["AUTOMATIC_TUNING"] = AlterDatabaseOptionKind.Special,
        ["CONTAINMENT"] = AlterDatabaseOptionKind.Special,
        ["DEFAULT_FULLTEXT_LANGUAGE"] = AlterDatabaseOptionKind.Special,
        ["DEFAULT_LANGUAGE"] = AlterDatabaseOptionKind.Special,
        ["DELAYED_DURABILITY"] = AlterDatabaseOptionKind.Special,
        ["ENCRYPTION"] = AlterDatabaseOptionKind.Special,
        ["FILESTREAM"] = AlterDatabaseOptionKind.Special,
        ["NESTED_TRIGGERS"] = AlterDatabaseOptionKind.Special,
        ["REMOTE_DATA_ARCHIVE"] = AlterDatabaseOptionKind.Special,
        ["SUSPEND_FOR_SNAPSHOT_BACKUP"] = AlterDatabaseOptionKind.Special,
        ["TRANSFORM_NOISE_WORDS"] = AlterDatabaseOptionKind.Special,
        ["TWO_DIGIT_YEAR_CUTOFF"] = AlterDatabaseOptionKind.Special,
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
    private static bool ConsumeDatabaseOptionTail(ParserContext context, Database target, string name, AlterDatabaseOptionKind kind)
    {
        switch (kind)
        {
            case AlterDatabaseOptionKind.OnOff:
                var atName = context.SaveCheckpoint();
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle)
                {
                    // An `=` where the bare toggle belongs is Msg 102 near the
                    // option's name (probed 2026-10-09 against SQL Server 2025).
                    if (context.Token is Operator { Character: '=' })
                        context.RestoreCheckpoint(atName);
                    return false;
                }
                // AUTO_CREATE_STATISTICS ON takes an optional (INCREMENTAL = ON | OFF).
                bool? incremental = null;
                if (toggle.Keyword == Keyword.On && name.Equals("AUTO_CREATE_STATISTICS", StringComparison.OrdinalIgnoreCase))
                {
                    var afterToggle = context.SaveCheckpoint();
                    if (context.GetNextOptional() is Operator { Character: '(' })
                    {
                        if (context.GetNextRequired() is not UnquotedString { Value: var option } || !option.Equals("INCREMENTAL", StringComparison.OrdinalIgnoreCase)
                            || context.GetNextRequired() is not Operator { Character: '=' })
                        {
                            return false;
                        }
                        incremental = context.GetNextRequired() switch
                        {
                            ReservedKeyword { Keyword: Keyword.On } => true,
                            ReservedKeyword { Keyword: Keyword.Off } => false,
                            _ => null,
                        };
                        if (incremental is null || context.GetNextRequired() is not Operator { Character: ')' })
                            return false;
                    }
                    else
                    {
                        context.RestoreCheckpoint(afterToggle);
                    }
                }
                if (!context.Batch.IsSkipping)
                {
                    RejectPinnedSwitch(target, name);
                    // The batch compiled its CREATE TABLEs' nullability under
                    // the value it began with (probed 2026-09-28 against SQL
                    // Server 2025).
                    if (BuiltInToken.Equals(name, "ANSI_NULL_DEFAULT"))
                        _ = (context.Batch.CompiledAnsiNullDefaults ??= []).TryAdd(target, (target.Switches & DatabaseSwitches.AnsiNullDefault) != 0);
                    RecordDatabaseSwitch(target, name, toggle.Keyword == Keyword.On, incremental);
                }
                return true;
            case AlterDatabaseOptionKind.EqualsOnOff:
                return ConsumeEqualsOnOff(context);
            case AlterDatabaseOptionKind.EnumIdent:
                if (context.GetNextRequired() is not (Name or ReservedKeyword))
                    return false;
                if (!context.Batch.IsSkipping)
                    RecordDatabaseEnumOption(target, name, context.Token!.Source.ToString());
                return true;
            case AlterDatabaseOptionKind.IntegerWithUnit:
                return ConsumeIntegerWithUnit(context, target);
            case AlterDatabaseOptionKind.QueryStore:
                return ParseQueryStoreTail(context, target);
            case AlterDatabaseOptionKind.Broker:
                if (!context.Batch.IsSkipping)
                    target.BrokerEnabled = !BuiltInToken.Equals(name, "DISABLE_BROKER");
                return true;
            case AlterDatabaseOptionKind.State:
                return ConsumeDatabaseStateOption(context, target, name);
            case AlterDatabaseOptionKind.Special:
                return ConsumeSpecialDatabaseOption(context, target, name);
            case AlterDatabaseOptionKind.AccessMode:
                if (!ConsumeTerminationClause(context))
                    return false;
                if (!context.Batch.IsSkipping)
                {
                    target.UserAccess = BuiltInToken.Equals(name, "RESTRICTED_USER") ? (byte)2
                        : BuiltInToken.Equals(name, "SINGLE_USER") ? (byte)1
                        : (byte)0;
                }
                return true;
            default:
                return false;
        }
    }

    /// <summary>Records an ON / OFF option on <paramref name="target"/>'s <see cref="Database.Switches"/>.</summary>
    private static void RecordDatabaseSwitch(Database target, string name, bool on, bool? incremental)
    {
        // TORN_PAGE_DETECTION is PAGE_VERIFY's legacy spelling.
        // Turning it off clears only torn-page detection itself: a CHECKSUM
        // database keeps CHECKSUM (probed 2026-09-30 against SQL Server 2025).
        if (BuiltInToken.Equals(name, "TORN_PAGE_DETECTION"))
        {
            target.PageVerify = on ? (byte)1 : target.PageVerify == 1 ? (byte)0 : target.PageVerify;
            return;
        }
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        var flag = upper switch
        {
            "ANSI_NULLS" => DatabaseSwitches.AnsiNulls,
            "ANSI_NULL_DEFAULT" => DatabaseSwitches.AnsiNullDefault,
            "ANSI_PADDING" => DatabaseSwitches.AnsiPadding,
            "ANSI_WARNINGS" => DatabaseSwitches.AnsiWarnings,
            "ARITHABORT" => DatabaseSwitches.ArithAbort,
            "AUTO_CLOSE" => DatabaseSwitches.AutoClose,
            "AUTO_CREATE_STATISTICS" => DatabaseSwitches.AutoCreateStatistics,
            "AUTO_SHRINK" => DatabaseSwitches.AutoShrink,
            "AUTO_UPDATE_STATISTICS" => DatabaseSwitches.AutoUpdateStatistics,
            "AUTO_UPDATE_STATISTICS_ASYNC" => DatabaseSwitches.AutoUpdateStatisticsAsync,
            "CONCAT_NULL_YIELDS_NULL" => DatabaseSwitches.ConcatNullYieldsNull,
            "CURSOR_CLOSE_ON_COMMIT" => DatabaseSwitches.CursorCloseOnCommit,
            "DATE_CORRELATION_OPTIMIZATION" => DatabaseSwitches.DateCorrelationOptimization,
            "HONOR_BROKER_PRIORITY" => DatabaseSwitches.HonorBrokerPriority,
            "MEMORY_OPTIMIZED_ELEVATE_TO_SNAPSHOT" => DatabaseSwitches.MemoryOptimizedElevateToSnapshot,
            "MIXED_PAGE_ALLOCATION" => DatabaseSwitches.MixedPageAllocation,
            "NUMERIC_ROUNDABORT" => DatabaseSwitches.NumericRoundAbort,
            "QUOTED_IDENTIFIER" => DatabaseSwitches.QuotedIdentifier,
            "SUPPLEMENTAL_LOGGING" => DatabaseSwitches.SupplementalLogging,
            "TEMPORAL_HISTORY_RETENTION" => DatabaseSwitches.TemporalHistoryRetention,
            _ => DatabaseSwitches.None,
        };
        target.Switches = on ? target.Switches | flag : target.Switches & ~flag;
        // Turning automatic statistics creation off takes its incremental mode with it.
        if (flag == DatabaseSwitches.AutoCreateStatistics && (!on || incremental is not null))
        {
            target.Switches = on && incremental == true
                ? target.Switches | DatabaseSwitches.AutoCreateStatisticsIncremental
                : target.Switches & ~DatabaseSwitches.AutoCreateStatisticsIncremental;
        }
    }

    /// <summary>Records a PAGE_VERIFY / CURSOR_DEFAULT / PARAMETERIZATION value on <paramref name="target"/>.</summary>
    private static void RecordDatabaseEnumOption(Database target, string name, string value)
    {
        if (BuiltInToken.Equals(name, "CURSOR_DEFAULT"))
        {
            SetSwitch(target, DatabaseSwitches.LocalCursorDefault, BuiltInToken.Equals(value, "LOCAL"));
        }
        else if (BuiltInToken.Equals(name, "PAGE_VERIFY"))
        {
            target.PageVerify = BuiltInToken.Equals(value, "CHECKSUM") ? (byte)2
                : BuiltInToken.Equals(value, "TORN_PAGE_DETECTION") ? (byte)1
                : (byte)0;
        }
        else if (BuiltInToken.Equals(name, "PARAMETERIZATION"))
        {
            SetSwitch(target, DatabaseSwitches.ParameterizationForced, BuiltInToken.Equals(value, "FORCED"));
        }

        static void SetSwitch(Database database, DatabaseSwitches flag, bool on) =>
            database.Switches = on ? database.Switches | flag : database.Switches & ~flag;
    }

    /// <summary>
    /// Cursor on an option's last token — an access-mode name (SINGLE_USER /
    /// MULTI_USER / RESTRICTED_USER / READ_ONLY / READ_WRITE), or the end of a
    /// <c>SET</c> list. Consumes an optional trailing
    /// <c>WITH &lt;termination&gt;</c> clause (ROLLBACK IMMEDIATE /
    /// ROLLBACK AFTER n [SECONDS] / NO_WAIT), leaving the cursor on the
    /// clause's last token (or where it was when no WITH follows), per the
    /// leave-on-last-token convention the other tail consumers use.
    /// </summary>
    private static bool ConsumeTerminationClause(ParserContext context) =>
        TryConsumeTerminationClause(context, out _, out _);

    /// <summary>Which termination clause an <c>ALTER DATABASE … SET</c> option ended with.</summary>
    private enum TerminationClause
    {
        None,
        NoWait,
        Rollback,
    }

    /// <summary>
    /// <see cref="ConsumeTerminationClause"/>, reporting which clause it read and,
    /// for <c>ROLLBACK AFTER n</c>, its seconds (0 for <c>ROLLBACK IMMEDIATE</c>).
    /// </summary>
    private static bool TryConsumeTerminationClause(ParserContext context, out TerminationClause clause, out int rollbackAfterSeconds)
    {
        clause = TerminationClause.None;
        rollbackAfterSeconds = 0;
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
        {
            clause = TerminationClause.NoWait;
            return true;
        }
        if (termination is not ReservedKeyword { Keyword: Keyword.Rollback })
            return false;
        clause = TerminationClause.Rollback;
        var rollbackKind = context.GetNextRequired();
        if (IsBareWord(rollbackKind, "IMMEDIATE"))
            return true;
        if (!IsBareWord(rollbackKind, "AFTER"))
            return false;
        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } seconds })
            return false;
        rollbackAfterSeconds = seconds.AsInt32;
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

    /// <summary>
    /// <c>TARGET_RECOVERY_TIME = n { SECONDS | MINUTES }</c>, recorded on
    /// <see cref="Database.TargetRecoveryTimeSeconds"/>.
    /// </summary>
    private static bool ConsumeIntegerWithUnit(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' }
            || context.GetNextRequired() is not Numeric { Value: { IsNull: false } amount }
            || context.GetNextRequired() is not UnquotedString { Value: var unit })
        {
            return false;
        }
        if (!context.Batch.IsSkipping)
        {
            if (BuiltInToken.Equals(unit, "SECONDS"))
                target.TargetRecoveryTimeSeconds = amount.AsInt32;
            else if (BuiltInToken.Equals(unit, "MINUTES"))
                target.TargetRecoveryTimeSeconds = amount.AsInt32 * 60;
        }
        return true;
    }

    /// <summary>
    /// Cursor on the QUERY_STORE name token. Accepts four shapes per probe:
    /// <c>= OFF</c>, <c>= ON</c> [optional <c>( … )</c> options block], a bare
    /// options block, and <c>CLEAR [ALL]</c>. Every form lands on
    /// <see cref="Database.QueryStore"/>, whose state and capture mode decide
    /// what the store records.
    /// </summary>
    /// <remarks>
    /// The whole tail parses into a copy and swaps in only at the end, so an
    /// options block that raises partway through leaves the configuration as it
    /// was. A bare options block configures without moving the state — an OFF
    /// store stays OFF (probed 2026-09-29) — while its <c>OPERATION_MODE</c>
    /// moves an enabled one. <c>CLEAR</c> / <c>CLEAR ALL</c> forget everything
    /// captured and touch neither the state nor the configuration. The value
    /// checks raise as the batch compiles, so a batch carrying one runs none of
    /// its statements.
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
            if (!context.Batch.IsSkipping)
                target.QueryStoreData.Clear();
            return true;
        }
        if (next is Operator { Character: '(' })
        {
            var wasOff = pending.DesiredState == QueryStoreState.Off;
            if (!ParseQueryStoreOptionsBlock(context, pending))
                return false;
            if (wasOff)
                pending.DesiredState = QueryStoreState.Off;
        }
        else if (next is not Operator { Character: '=' })
        {
            return false;
        }
        else
        {
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
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            context.MoveNextRequired();
            if (context.Token is not UnquotedString sub)
                return false;
            // A repeated entry is refused ahead of its value (Msg 12401,
            // probed 2026-09-29 against SQL Server 2025).
            if (!seen.Add(sub.Value) && IsQueryStoreSubOption(sub.Value))
                throw SimulatedSqlException.QueryStoreOptionRepeated(sub.Value.ToUpperInvariant());
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
                if (!TryReadInteger(context, out pending.FlushIntervalSeconds))
                    return false;
                if (pending.FlushIntervalSeconds < 60)
                    throw SimulatedSqlException.QueryStoreOptionInvalid("flush_interval_seconds", 15, 5);
                return true;
            case "INTERVAL_LENGTH_MINUTES":
                if (!TryReadInteger(context, out pending.IntervalLengthMinutes))
                    return false;
                if (pending.IntervalLengthMinutes is not (1 or 5 or 10 or 15 or 30 or 60 or 1440))
                    throw SimulatedSqlException.QueryStoreOptionInvalid("interval_length_minutes", 16, 6);
                return true;
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
                    if (executions < 1)
                        throw SimulatedSqlException.QueryStoreCapturePolicyValueInvalid(executions, "execution_count", 1);
                    pending.CapturePolicyExecutionCount = (int)executions;
                    break;
                case "STALE_CAPTURE_POLICY_THRESHOLD":
                    if (!TryReadInteger(context, out var threshold))
                        return false;
                    var unit = context.GetNextRequired();
                    var hoursPerUnit = IsBareWord(unit, "DAYS") || IsBareWord(unit, "DAY") ? 24
                        : IsBareWord(unit, "HOURS") || IsBareWord(unit, "HOUR") ? 1
                        : throw SimulatedSqlException.SyntaxErrorNear(entry);
                    if (threshold * hoursPerUnit is < 1 or > 7 * 24)
                        throw SimulatedSqlException.QueryStoreStaleThresholdInvalid();
                    pending.CapturePolicyStaleThresholdHours = (int)threshold * hoursPerUnit;
                    break;
                case "TOTAL_COMPILE_CPU_TIME_MS":
                    if (!TryReadInteger(context, out pending.CapturePolicyTotalCompileCpuTimeMs))
                        return false;
                    if (pending.CapturePolicyTotalCompileCpuTimeMs < 1)
                        throw SimulatedSqlException.QueryStoreCapturePolicyValueInvalid(pending.CapturePolicyTotalCompileCpuTimeMs, "total_compile_cpu_time_ms", 1);
                    break;
                case "TOTAL_EXECUTION_CPU_TIME_MS":
                    if (!TryReadInteger(context, out pending.CapturePolicyTotalExecutionCpuTimeMs))
                        return false;
                    if (pending.CapturePolicyTotalExecutionCpuTimeMs < 1)
                        throw SimulatedSqlException.QueryStoreCapturePolicyValueInvalid(pending.CapturePolicyTotalExecutionCpuTimeMs, "total_execution_cpu_time_ms", 2);
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
    /// Reads the integer value after a sub-option's <c>=</c>: an unsigned
    /// <c>int</c> literal. One past <c>int</c> range is Msg 102 at the number
    /// and a sign Msg 102 at the sign, as real's grammar has them (probed
    /// 2026-09-29 against SQL Server 2025).
    /// </summary>
    private static bool TryReadInteger(ParserContext context, out long value)
    {
        value = 0;
        if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } numeric } token)
            return false;
        if (numeric.Type is not Int32SqlType)
            throw SimulatedSqlException.SyntaxErrorNear(token);
        value = numeric.AsInt32;
        return true;
    }

    /// <summary>Whether <paramref name="name"/> is one of the top-level QUERY_STORE sub-options, which may each appear once.</summary>
    private static bool IsQueryStoreSubOption(string name) => BuiltInToken.EqualsAny(name,
        "CLEANUP_POLICY", "DATA_FLUSH_INTERVAL_SECONDS", "INTERVAL_LENGTH_MINUTES", "MAX_PLANS_PER_QUERY", "MAX_STORAGE_SIZE_MB",
        "OPERATION_MODE", "QUERY_CAPTURE_MODE", "QUERY_CAPTURE_POLICY", "SIZE_BASED_CLEANUP_MODE", "WAIT_STATS_CAPTURE_MODE");

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

    /// <summary>
    /// Parses <c>ALTER SEQUENCE [schema.]name [RESTART [WITH n]] [INCREMENT BY n]
    /// [MINVALUE n | NO MINVALUE] [MAXVALUE n | NO MAXVALUE] [CYCLE | NO CYCLE]
    /// [CACHE [n] | NO CACHE]</c>. Entered with <see cref="ParserContext.Token"/>
    /// on the <c>SEQUENCE</c> contextual keyword. The options are read whole —
    /// their grammar refusals (Msg 11710 / 11711 / 11712 / 11715) are real's
    /// parse errors — then checked against the sequence and applied together,
    /// so a refused statement leaves it as it was.
    /// </summary>
    /// <remarks>
    /// Probed 2026-10-04 against SQL Server 2025: <c>NO MINVALUE</c> /
    /// <c>NO MAXVALUE</c> restore the type's bounds; equal bounds are Msg
    /// 11705; a <c>RESTART WITH</c> outside the new range is Msg 11703, and
    /// without one a current value outside it is Msg 11704; without
    /// <c>RESTART</c> the next draw follows the last one by the new increment
    /// (<see cref="Sequence.RepositionAfterAlter"/>); the statement rolls back
    /// with its transaction; and it sends Msg 11729 when it leaves the cache
    /// longer than the values left.
    /// </remarks>
    private static bool TryParseAlterSequence(ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Name)
            return false;
        var sequenceName = BatchContext.ParseObjectName(context);

        var seen = new SequenceOptionsSeen();
        Int128? restartWith = null;
        bool restartFractional = false, incrementFractional = false, minFractional = false, maxFractional = false;
        Int128? increment = null;
        (Int128? Value, bool Written) minValue = default, maxValue = default;
        bool? cycle = null;
        (long? Size, bool Written) cache = default;
        var any = false;
        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Restart }:
                    {
                        NoteSequenceOption(ref seen.Restart, "RESTART");
                        var afterRestart = context.SaveCheckpoint();
                        if (context.MoveNext() && context.Token is ReservedKeyword { Keyword: Keyword.With })
                            restartWith = ReadSignedIntegerLiteral(context, out restartFractional);
                        else
                            context.RestoreCheckpoint(afterRestart);
                        break;
                    }
                case UnquotedString { ContextualKeyword: ContextualKeyword.Start }:
                    throw SimulatedSqlException.SequenceArgumentNotInAlter("START WITH");
                case ReservedKeyword { Keyword: Keyword.As }:
                    throw SimulatedSqlException.SequenceArgumentNotInAlter("AS");
                case UnquotedString { ContextualKeyword: ContextualKeyword.Increment }:
                    NoteSequenceOption(ref seen.Increment, "INCREMENT BY");
                    if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.By })
                        return false;
                    increment = ReadSignedIntegerLiteral(context, out incrementFractional);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                    NoteSequenceOption(ref seen.MinValue, "MINVALUE");
                    minValue = (ReadSignedIntegerLiteral(context, out minFractional), true);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                    NoteSequenceOption(ref seen.MaxValue, "MAXVALUE");
                    maxValue = (ReadSignedIntegerLiteral(context, out maxFractional), true);
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                    NoteSequenceOption(ref seen.Cycle, "CYCLE");
                    cycle = true;
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.No }:
                    switch (context.GetNextRequired())
                    {
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Cycle }:
                            NoteSequenceOption(ref seen.Cycle, "CYCLE");
                            cycle = false;
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                            NoteSequenceOption(ref seen.Cache, "CACHE");
                            cache = (0, true);
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.MinValue }:
                            NoteSequenceOption(ref seen.MinValue, "MINVALUE");
                            minValue = (null, true);
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.MaxValue }:
                            NoteSequenceOption(ref seen.MaxValue, "MAXVALUE");
                            maxValue = (null, true);
                            break;
                        default:
                            return false;
                    }
                    break;
                case UnquotedString { ContextualKeyword: ContextualKeyword.Cache }:
                    NoteSequenceOption(ref seen.Cache, "CACHE");
                    cache = (ReadOptionalCacheSize(context), true);
                    if (cache.Size == 0)
                        throw SimulatedSqlException.SequenceCacheMustBePositive(sequenceName.ToString());
                    break;
                default:
                    goto optionsRead;
            }
            any = true;
        }
    optionsRead:
        if (!any)
            throw SimulatedSqlException.AlterSequenceWithoutOptions();
        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveSequence(sequenceName, out var sequence))
            throw SimulatedSqlException.CannotAlterSequence(sequenceName.Leaf);
        // ALTER SEQUENCE needs ALTER on the sequence (schema ALTER / object
        // CONTROL cover it) — Msg 15151, the same record a missing sequence
        // earns, naming the leaf (probe-confirmed).
        sequence.Schema.Database.RejectWriteWhenReadOnly();
        if (!PermissionEnforcement.HasObjectAlter(
                context.Batch, context.Batch.DatabaseFor(sequence), sequence.ObjectId, sequence.SchemaId))
        {
            throw SimulatedSqlException.CannotAlterSequence(sequenceName.Leaf);
        }
        // TryResolveSequence took Sch-S; upgrade to Sch-M, to the
        // transaction's end, before mutating the sequence's option fields.
        // Other connections reading the sequence (NEXT VALUE FOR) wait on it.
        context.Batch.LockDefinitionName(sequence.Schema, sequence.Name);
        context.Batch.LockDefinition(sequence.Schema, sequence);

        var displayName = sequenceName.ToString();
        var type = sequence.DeclaredType;
        var (typeMin, typeMax) = SequenceTypeBounds(type);
        if (increment == 0 && !incrementFractional)
            throw SimulatedSqlException.SequenceIncrementCannotBeZero(displayName);
        if (increment is { } writtenIncrement && (incrementFractional || IsOutsideSequenceType(type, writtenIncrement)))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("INCREMENT BY");
        if (minValue.Value is { } writtenMin && (minFractional || IsOutsideSequenceType(type, writtenMin)))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("MINVALUE");
        if (maxValue.Value is { } writtenMax && (maxFractional || IsOutsideSequenceType(type, writtenMax)))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("MAXVALUE");
        if (restartWith is { } writtenRestart && (restartFractional || IsOutsideSequenceType(type, writtenRestart)))
            throw SimulatedSqlException.SequenceArgumentOutOfRange("RESTART WITH");

        var newMin = minValue.Written ? minValue.Value ?? typeMin : sequence.MinValue;
        var newMax = maxValue.Written ? maxValue.Value ?? typeMax : sequence.MaxValue;
        if (newMin >= newMax)
            throw SimulatedSqlException.SequenceMinNotBelowMax(displayName);
        if (seen.Restart)
        {
            var restartAt = restartWith ?? sequence.StartValue;
            if (restartAt < newMin || restartAt > newMax)
                throw SimulatedSqlException.SequenceStartOutOfRange(displayName);
        }
        else
        {
            var current = sequence.LastUsedValue ?? sequence.CurrentValue;
            if (current < newMin || current > newMax)
                throw SimulatedSqlException.SequenceCurrentValueOutOfRange(current.ToString(System.Globalization.CultureInfo.InvariantCulture), displayName);
        }

        var before = (sequence.StartValue, sequence.CurrentValue, sequence.Increment, sequence.MinValue, sequence.MaxValue, sequence.Cycle, sequence.CacheSize, sequence.IsExhausted, sequence.LastUsedValue);
        sequence.MinValue = newMin;
        sequence.MaxValue = newMax;
        if (increment is { } newIncrement)
            sequence.Increment = newIncrement;
        if (cycle is { } newCycle)
            sequence.Cycle = newCycle;
        if (cache.Written)
            sequence.CacheSize = cache.Size;
        if (seen.Restart)
        {
            // RESTART WITH n moves the sequence's *start* as well as its
            // position — probe-confirmed against SQL Server 2025:
            // sys.sequences.start_value reports n afterwards, and a later bare
            // RESTART returns to n rather than to the value the sequence was
            // declared with. RESTART clears the last-used marker too.
            if (restartWith is { } restart)
                sequence.StartValue = restart;
            sequence.CurrentValue = sequence.StartValue;
            sequence.IsExhausted = false;
            sequence.LastUsedValue = null;
        }
        else
        {
            sequence.RepositionAfterAlter();
        }
        RecordDdlUndo(context, () =>
            (sequence.StartValue, sequence.CurrentValue, sequence.Increment, sequence.MinValue, sequence.MaxValue, sequence.Cycle, sequence.CacheSize, sequence.IsExhausted, sequence.LastUsedValue) = before);
        RecordDdlEvent(context, "ALTER_SEQUENCE", sequence.Schema.Name, sequence.Name, "SEQUENCE");
        SendSequenceCacheMessages(context.Batch, sequence);
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SCHEMA dest TRANSFER [ (OBJECT | TYPE | XML SCHEMA
    /// COLLECTION)::] source.obj</c>. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>SCHEMA</c> keyword. Routes
    /// the named object between schemas:
    /// <list type="bullet">
    /// <item><c>OBJECT</c> class (default if no prefix given): the
    /// shared-namespace kinds — tables, views, functions, procedures,
    /// sequences, synonyms, rules and defaults. Triggers and constraints are
    /// not directly transferable — they move along with their parent
    /// automatically (Msg 15347 if named directly).</item>
    /// <item><c>TYPE</c> class: table and alias types.</item>
    /// <item><c>XML SCHEMA COLLECTION</c> class.</item>
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
    /// <item>Source is a trigger or constraint → <strong>Msg 15347</strong>
    /// (each follows its parent's schema; can't be transferred directly).</item>
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

        // Optional class prefix: OBJECT::, TYPE:: or XML SCHEMA COLLECTION::.
        // Object and Type are contextual keywords, and the :: separator
        // tokenizes as two adjacent single-character ':' operators.
        var classIsType = false;
        var classIsXmlSchemaCollection = false;
        var afterTransfer = context.SaveCheckpoint();
        if (context.MoveNext() && context.Token is UnquotedString { ContextualKeyword: var ck }
            && ck is ContextualKeyword.Object or ContextualKeyword.Type or ContextualKeyword.Xml)
        {
            if (ck == ContextualKeyword.Xml)
            {
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Schema })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (context.GetNextRequired() is not Name { Value: var collectionWord } || !BuiltInToken.Equals(collectionWord, "COLLECTION"))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                classIsXmlSchemaCollection = true;
            }
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
        // The two system schemas take nothing (probed 2026-10-04 against SQL
        // Server 2025: Msg 2710 naming the schema).
        if (destSchema.SchemaId is Database.SysSchemaId or Database.InformationSchemaId)
            throw SimulatedSqlException.NotTheSpecifiedOwner(destSchema.Name);
        // ALTER on the destination schema is the first half of real's gate, and
        // reports the same Msg 15151 a missing destination earns.
        if (!PermissionEnforcement.HasSchemaAlter(context.Batch, destSchema))
            throw SimulatedSqlException.CannotAlterSchemaDoesNotExist(destSchemaName);

        if (!context.Batch.TryResolveSchema(sourceName, out var sourceSchema))
        {
            throw classIsType ? SimulatedSqlException.CannotFindType(sourceName.Leaf)
                : classIsXmlSchemaCollection ? SimulatedSqlException.CannotFindXmlSchemaCollection(sourceName.Leaf)
                : SimulatedSqlException.CannotFindObject(sourceName.Leaf);
        }
        // The second half is CONTROL on the object being moved — probe-confirmed
        // that ALTER on the *source* schema is not enough, and that the refusal
        // is its own Msg 15151 wording.
        RejectUnauthorizedSchemaTransfer(context, sourceSchema, sourceName, classIsType || classIsXmlSchemaCollection);

        var objectType = classIsType ? TransferType(sourceSchema, destSchema, sourceName.Leaf, context.Batch)
            : classIsXmlSchemaCollection ? TransferXmlSchemaCollection(sourceSchema, destSchema, sourceName.Leaf, context.Batch)
            : TransferObject(sourceSchema, destSchema, sourceName.Leaf, context.Batch);
        // An object moved to another schema leaves its permissions behind
        // (probed 2026-10-04 against SQL Server 2025).
        if (!classIsType && !classIsXmlSchemaCollection && !ReferenceEquals(sourceSchema, destSchema)
            && destSchema.TryFindInSharedNamespace(sourceName.Leaf, out var moved)
            && context.CurrentDatabase.Permissions.Exists(p => p.Class == PermissionChecker.ClassObject && p.MajorId == moved.ObjectId))
        {
            RecordSecurityUndo(context, context.CurrentDatabase);
            _ = context.CurrentDatabase.Permissions.RemoveAll(p => p.Class == PermissionChecker.ClassObject && p.MajorId == moved.ObjectId);
        }
        // Real reports the transferred object, not the schema — SchemaName is
        // the destination, ObjectName the object and ObjectType its kind
        // (TABLE, RULE, TYPE, XML SCHEMA COLLECTION …, probed 2026-09-26).
        RecordDdlEvent(context, "ALTER_SCHEMA", destSchemaName, sourceName.Leaf, objectType);
        return true;
    }

    /// <summary>
    /// The moved-object half of the <c>ALTER SCHEMA … TRANSFER</c> gate: CONTROL
    /// on the object (or the type's or XML schema collection's owning schema,
    /// since the simulator's GRANT surface carries neither securable class). Denial is Msg 15151
    /// <c>Cannot transfer the object '…'</c>. No-op when the name resolves to
    /// nothing — the caller's own not-found record still runs.
    /// </summary>
    private static void RejectUnauthorizedSchemaTransfer(ParserContext context, Schema sourceSchema, MultiPartName sourceName, bool classIsType)
    {
        if (classIsType)
        {
            if ((sourceSchema.TableTypes.ContainsKey(sourceName.Leaf) || sourceSchema.AliasTypes.ContainsKey(sourceName.Leaf) || sourceSchema.XmlSchemaCollections.ContainsKey(sourceName.Leaf))
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

    /// <summary>
    /// Moves a user-defined table or alias type between schemas, answering the
    /// DDL event's object type. A type of either kind already named so in the
    /// destination is Msg 15530 (probed 2026-09-26).
    /// </summary>
    private static string TransferType(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        var sameSchema = ReferenceEquals(sourceSchema, destSchema);
        var collides = destSchema.TableTypes.ContainsKey(leafName) || destSchema.AliasTypes.ContainsKey(leafName);
        if (sourceSchema.TableTypes.TryGetValue(leafName, out var tableType))
        {
            if (sameSchema)
                return "TYPE";
            if (collides)
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName, "type");
            batch.AcquireStatementLock(tableType.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.TableTypes.TryRemove(leafName, out _);
            destSchema.TableTypes[leafName] = tableType;
            tableType.Schema = destSchema;
            tableType.SchemaId = destSchema.SchemaId;
            RecordDdlUndo(batch, () =>
            {
                _ = destSchema.TableTypes.TryRemove(leafName, out _);
                sourceSchema.TableTypes[leafName] = tableType;
                tableType.Schema = sourceSchema;
                tableType.SchemaId = sourceSchema.SchemaId;
            });
            return "TYPE";
        }
        if (!sourceSchema.AliasTypes.TryGetValue(leafName, out var aliasType))
            throw SimulatedSqlException.CannotFindType(leafName);
        if (sameSchema)
            return "TYPE";
        if (collides)
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName, "type");
        _ = sourceSchema.AliasTypes.TryRemove(leafName, out _);
        destSchema.AliasTypes[leafName] = aliasType;
        aliasType.Schema = destSchema;
        RecordDdlUndo(batch, () =>
        {
            _ = destSchema.AliasTypes.TryRemove(leafName, out _);
            sourceSchema.AliasTypes[leafName] = aliasType;
            aliasType.Schema = sourceSchema;
        });
        return "TYPE";
    }

    /// <summary>
    /// Moves an XML schema collection between schemas; the xml columns and
    /// variables typed by it follow, since they hold the collection itself.
    /// </summary>
    private static string TransferXmlSchemaCollection(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        if (!sourceSchema.XmlSchemaCollections.TryGetValue(leafName, out var collection))
            throw SimulatedSqlException.CannotFindXmlSchemaCollection(leafName);
        if (ReferenceEquals(sourceSchema, destSchema))
            return "XML SCHEMA COLLECTION";
        if (destSchema.XmlSchemaCollections.ContainsKey(leafName))
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName, "xml schema collection");
        _ = sourceSchema.XmlSchemaCollections.TryRemove(leafName, out _);
        destSchema.XmlSchemaCollections[leafName] = collection;
        collection.SchemaId = destSchema.SchemaId;
        RecordDdlUndo(batch, () =>
        {
            _ = destSchema.XmlSchemaCollections.TryRemove(leafName, out _);
            sourceSchema.XmlSchemaCollections[leafName] = collection;
            collection.SchemaId = sourceSchema.SchemaId;
        });
        return "XML SCHEMA COLLECTION";
    }

    /// <summary>
    /// Moves an object between schemas, answering the DDL event's object type.
    /// Walks the source schema's shared-namespace dicts — first hit by leaf
    /// name wins. A trigger or a constraint raises Msg 15347, since each
    /// belongs to its parent and follows the parent's schema. After the move,
    /// HeapTable / View transfers reseat any attached triggers — they belong
    /// to the destination schema after the transfer.
    /// </summary>
    private static string TransferObject(Schema sourceSchema, Schema destSchema, string leafName, BatchContext batch)
    {
        if (sourceSchema.Triggers.ContainsKey(leafName) || sourceSchema.HasConstraintNamed(leafName))
            throw SimulatedSqlException.CannotTransferObjectOwnedByParent();

        var sameSchema = ReferenceEquals(sourceSchema, destSchema);

        if (sourceSchema.HeapTables.TryGetValue(leafName, out var heap))
        {
            if (sameSchema) return "TABLE";
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, heap);
            batch.AcquireStatementLock(heap.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.HeapTables.TryRemove(leafName, out _);
            destSchema.HeapTables[leafName] = heap;
            heap.SchemaId = destSchema.SchemaId;
            heap.OwningDatabase = destSchema.Database;
            ReseatAttachedTriggers(sourceSchema, destSchema, heap);
            RecordDdlUndo(batch, () =>
            {
                _ = destSchema.HeapTables.TryRemove(leafName, out _);
                sourceSchema.HeapTables[leafName] = heap;
                heap.SchemaId = sourceSchema.SchemaId;
                heap.OwningDatabase = sourceSchema.Database;
                ReseatAttachedTriggers(destSchema, sourceSchema, heap);
            });
            return "TABLE";
        }
        if (sourceSchema.Views.TryGetValue(leafName, out var view))
        {
            if (sameSchema) return "VIEW";
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, view);
            batch.AcquireStatementLock(view.SchemaLock, LockMode.SchemaModification);
            _ = sourceSchema.Views.TryRemove(leafName, out _);
            destSchema.Views[leafName] = view;
            view.Schema = destSchema;
            view.SchemaId = destSchema.SchemaId;
            ReseatAttachedTriggers(sourceSchema, destSchema, view);
            RecordDdlUndo(batch, () =>
            {
                _ = destSchema.Views.TryRemove(leafName, out _);
                sourceSchema.Views[leafName] = view;
                view.Schema = sourceSchema;
                view.SchemaId = sourceSchema.SchemaId;
                ReseatAttachedTriggers(destSchema, sourceSchema, view);
            });
            return "VIEW";
        }
        if (sourceSchema.Functions.TryGetValue(leafName, out var fn))
        {
            if (sameSchema) return "FUNCTION";
            if (destSchema.HasNameInSharedNamespace(leafName))
                throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
            RejectTransferOfSchemaBoundReferent(batch, fn);
            return MoveSchemaObject(sourceSchema.Functions, destSchema.Functions, fn, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "FUNCTION");
        }
        if (sourceSchema.Procedures.TryGetValue(leafName, out var proc))
            return sameSchema ? "PROCEDURE" : MoveSchemaObject(sourceSchema.Procedures, destSchema.Procedures, proc, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "PROCEDURE");
        if (sourceSchema.Sequences.TryGetValue(leafName, out var seq))
            return sameSchema ? "SEQUENCE" : MoveSchemaObject(sourceSchema.Sequences, destSchema.Sequences, seq, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "SEQUENCE");
        // A synonym moves as a plain name indirection: its stored base name is
        // untouched by the transfer (probe-confirmed — base_object_name still
        // reads [dbo].[t] after the synonym lands in another schema).
        if (sourceSchema.Synonyms.TryGetValue(leafName, out var synonym))
            return sameSchema ? "SYNONYM" : MoveSchemaObject(sourceSchema.Synonyms, destSchema.Synonyms, synonym, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "SYNONYM");
        if (sourceSchema.Rules.TryGetValue(leafName, out var rule))
            return sameSchema ? "RULE" : MoveSchemaObject(sourceSchema.Rules, destSchema.Rules, rule, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "RULE");
        if (sourceSchema.Defaults.TryGetValue(leafName, out var defaultObject))
            return sameSchema ? "DEFAULT" : MoveSchemaObject(sourceSchema.Defaults, destSchema.Defaults, defaultObject, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "DEFAULT");
        if (sourceSchema.SecurityPolicies.TryGetValue(leafName, out var policy))
            return sameSchema ? "SECURITY POLICY" : MoveSchemaObject(sourceSchema.SecurityPolicies, destSchema.SecurityPolicies, policy, leafName, sourceSchema, destSchema, batch, static (moved, schema) => moved.Schema = schema, "SECURITY POLICY");

        throw SimulatedSqlException.CannotFindObject(leafName);
    }

    /// <summary>
    /// The move shared by the kinds with nothing attached that follows them:
    /// out of the source schema's dictionary and into the destination's, the
    /// object's schema re-pointed, and all of it undone with the transaction.
    /// Answers <paramref name="objectType"/> for the caller's DDL event.
    /// </summary>
    private static string MoveSchemaObject<T>(
        System.Collections.Concurrent.ConcurrentDictionary<string, T> source,
        System.Collections.Concurrent.ConcurrentDictionary<string, T> destination,
        T moving,
        string leafName,
        Schema sourceSchema,
        Schema destSchema,
        BatchContext batch,
        Action<T, Schema> repoint,
        string objectType)
        where T : SchemaObject
    {
        if (destSchema.HasNameInSharedNamespace(leafName))
            throw SimulatedSqlException.ObjectAlreadyExistsInDestination(leafName);
        batch.AcquireStatementLock(moving.SchemaLock, LockMode.SchemaModification);
        _ = source.TryRemove(leafName, out _);
        destination[leafName] = moving;
        repoint(moving, destSchema);
        moving.SchemaId = destSchema.SchemaId;
        RecordDdlUndo(batch, () =>
        {
            _ = destination.TryRemove(leafName, out _);
            source[leafName] = moving;
            repoint(moving, sourceSchema);
            moving.SchemaId = sourceSchema.SchemaId;
        });
        return objectType;
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
            context.Batch.AcquireTableRedefinitionLock(alterTarget);
            RecordTableDdlUndo(context, alterTarget);
        }
        else if (!context.Batch.IsSkipping && context.Batch.TryResolveView(tableName, out _))
        {
            throw SimulatedSqlException.AlterTableNonTable(tableName.ToString());
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
                {
                    // ADD defaults to validate; only explicit WITH NOCHECK skips.
                    // A variable in what it adds is Msg 112, where a SWITCH's
                    // partition number takes one (probed 2026-10-06).
                    using var refused = ParserScope.Enter(ref context.VariablesRefusedIn, "ALTER TABLE");
                    using var options = ParserScope.Enter(ref context.ColumnIndexOptions, IndexOptionStatement.AlterTable);
                    return TryParseAlterTableAddConstraint(context, tableName, withNoCheck: withCheckExplicit == false);
                }
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
                var beforeAlterTarget = context.SaveCheckpoint();
                if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Index })
                    return TryParseAlterTableAlterIndex(context, tableName);
                context.RestoreCheckpoint(beforeAlterTarget);
                return TryParseAlterTableAlterColumn(context, tableName);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Rebuild }:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableRebuild(context, tableName);
            case Name switchWord when switchWord.Value.Equals("SWITCH", StringComparison.OrdinalIgnoreCase):
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return TryParseAlterTableSwitch(context, tableName);
            case UnquotedString { ContextualKeyword: ContextualKeyword.Enable or ContextualKeyword.Disable } toggle:
                if (withCheckExplicit.HasValue)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                return IsChangeTrackingAhead(context)
                    ? TryParseAlterTableChangeTracking(context, tableName, disable: toggle.ContextualKeyword == ContextualKeyword.Disable)
                    : TryParseAlterTableTriggerToggle(context, tableName, disable: toggle.ContextualKeyword == ContextualKeyword.Disable);
            // No ALTER TABLE form starts with AS — a table can't be turned
            // into a node or edge table (probed 2026-10-05 against SQL Server
            // 2025).
            case ReservedKeyword { Keyword: Keyword.As } asKeyword:
                throw SimulatedSqlException.SyntaxErrorNearKeyword(asKeyword);
            default:
                throw new NotSupportedException("ALTER TABLE supports only SET, ADD / DROP / ALTER COLUMN, ADD / DROP CONSTRAINT, CHECK / NOCHECK CONSTRAINT, ENABLE / DISABLE TRIGGER, SWITCH and REBUILD shapes.");
        }
    }

    /// <summary>
    /// Parses <c>ALTER TABLE … { ENABLE | DISABLE } TRIGGER { ALL | name [, …] }</c>,
    /// the table-scoped sibling of the standalone <c>ENABLE / DISABLE TRIGGER</c>
    /// statement. A name that isn't one of the table's triggers is Msg 4920 and
    /// toggles none of them (probed 2026-09-25 against SQL Server 2025). Cursor
    /// on entry: <c>ENABLE</c> / <c>DISABLE</c>.
    /// </summary>
    private static bool TryParseAlterTableTriggerToggle(ParserContext context, MultiPartName tableName, bool disable)
    {
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Trigger })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var allTriggers = false;
        var triggerNames = new List<string>();
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.All })
        {
            allTriggers = true;
            context.MoveNextOptional();
        }
        else
        {
            while (true)
            {
                if (context.Token is not Name triggerName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                triggerNames.Add(triggerName.Value);
                if (context.GetNextOptional() is not Operator { Character: ',' })
                    break;
                context.MoveNextRequired();
            }
        }

        if (context.Batch.IsSkipping)
            return true;
        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());

        var tableTriggers = new List<Trigger>();
        foreach (var (_, schema) in context.CurrentDatabase.Schemas)
        {
            foreach (var (_, trigger) in schema.Triggers)
            {
                if (ReferenceEquals(trigger.Parent, table))
                    tableTriggers.Add(trigger);
            }
        }
        if (allTriggers)
        {
            foreach (var trigger in tableTriggers)
                SetTriggerDisabled(context.Batch, trigger, disable);
            return true;
        }

        var collation = context.CurrentDatabase.Collation;
        var named = new List<Trigger>(triggerNames.Count);
        foreach (var name in triggerNames)
        {
            named.Add(tableTriggers.Find(trigger => collation.Equals(trigger.Name, name))
                ?? throw SimulatedSqlException.AlterTableTriggerMissing(name, tableName.Leaf));
        }
        foreach (var trigger in named)
            SetTriggerDisabled(context.Batch, trigger, disable);
        return true;
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

        // LOCK_ESCALATION = {TABLE | DISABLE | AUTO}, which SSMS's table
        // designer scripts: recorded for sys.tables, and DISABLE stops the
        // row-lock escalation to table-X (AUTO is TABLE for an unpartitioned
        // table). A SYSTEM_VERSIONING option may follow it after a comma
        // (probed 2026-09-25 against SQL Server 2025).
        if (context.GetNextRequired() is StringToken option && option.Span.Equals("LOCK_ESCALATION", StringComparison.OrdinalIgnoreCase))
        {
            if (context.GetNextRequired() is not Operator { Character: '=' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var escalation = context.GetNextRequired() switch
            {
                ReservedKeyword { Keyword: Keyword.Table } => (byte)0,
                StringToken value when value.Span.Equals("DISABLE", StringComparison.OrdinalIgnoreCase) => (byte)1,
                StringToken value when value.Span.Equals("AUTO", StringComparison.OrdinalIgnoreCase) => (byte)2,
                StringToken value => throw SimulatedSqlException.NotARecognizedAlterTableOption(value.Span.ToString()),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
            var afterOption = context.GetNextRequired();
            if (afterOption is not Operator { Character: ')' or ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (!context.Batch.IsSkipping)
            {
                if (!context.Batch.TryResolveTable(tableName, out var escalatingTable))
                    throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
                RejectOnMemoryOptimized(escalatingTable, "The option 'LOCK_ESCALATION'", 127);
                escalatingTable.LockEscalation = escalation;
            }
            if (afterOption is Operator { Character: ')' })
                return true;
            context.MoveNextRequired();
        }

        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.System_Versioning })
            throw new NotSupportedException("Only ALTER TABLE … SET (SYSTEM_VERSIONING | LOCK_ESCALATION = …) is supported.");

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
            RecordTableDdlUndo(context, historyTable);
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
        if (baseTable.SystemVersioning is null && !baseTable.KeyConstraints.Exists(static key => key.Kind == KeyConstraintKind.PrimaryKey))
            throw SimulatedSqlException.TemporalTableRequiresPrimaryKey(QualifyTableName(baseTable, context.CurrentDatabase));

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
            CheckHistoryConsistency(context, baseTable, existingHistory, options);
            RecordTableDdlUndo(context, existingHistory);
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
            // A transaction that rolls back takes the built table with it
            // (probed 2026-10-04 against SQL Server 2025).
            RecordSlotUndo<HeapTable>(context, historySchema.HeapTables, resolvedHistory.Name, null);
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
        foreach (var (_, schema) in context.CurrentDatabase.Schemas)
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
        var partitionAll = IsPartitionAll(context);
        var partitionNumber = ParseOptionalIndexPartitionClause(context);
        var namedPartitionList = ParseOptionalRebuildOptions(context, out var compressionLevel, out var xmlCompression, out var partitionCompressions, out var xmlPartitionCompressions);
        // A rebuild of every partition says so to list some (probed
        // 2026-10-05 against SQL Server 2025), the data compression's
        // refusal ahead of the XML compression's (probed 2026-10-06).
        if (partitionCompressions is not null && !partitionAll && partitionNumber is null)
            throw SimulatedSqlException.CompressionPartitionsWithoutPartitionAll();
        if (xmlPartitionCompressions is not null && !partitionAll && partitionNumber is null)
            throw SimulatedSqlException.XmlCompressionPartitionsWithoutPartitionAll();

        if (context.Batch.IsSkipping)
            return true;

        if (!context.Batch.TryResolveTable(tableName, out var table))
            throw SimulatedSqlException.CannotFindObjectForAlterTable(tableName.ToString());
        RejectOnMemoryOptimized(table, "The operation 'ALTER TABLE REBUILD'", 126);
        if (namedPartitionList && table.Partitioning is null)
            throw SimulatedSqlException.PartitionNumberOnUnpartitionedTable(table.Name);
        long? rebuiltPartition = null;
        if (partitionNumber is not null)
        {
            // The rebuild is of the heap or the clustered index, which a
            // partition number is checked against by name.
            var number = ReadPartitionNumber(context.Batch, partitionNumber, "ALTER TABLE", "table", table.Name);
            rebuiltPartition = number;
            var clusteredName = table.KeyConstraints.Count > 0 ? table.KeyConstraints[0].Name : null;
            if (clusteredName is not null)
                RejectPartitionNumber(number, table.Partitioning, clusteredName, table.Name);
            else if (table.Partitioning is not { } heapPlacement)
                throw SimulatedSqlException.RebuildPartitionOnUnpartitioned(alterIndex: false, indexName: null, table.Name);
            else if (number > heapPlacement.Fanout)
                throw SimulatedSqlException.AlterTablePartitionNotFound(number, table.Name);
        }

        // Rebuilding a clustered columnstore table recompresses its index
        // (probed 2026-09-26 against SQL Server 2025).
        if (compressionLevel is not null && table.Indexes.Find(index => index.IsColumnstore && index.IsClustered) is { } columnstore)
        {
            if (compressionLevel.Equals("COLUMNSTORE_ARCHIVE", StringComparison.OrdinalIgnoreCase))
                columnstore.ColumnstoreArchive = true;
            else if (compressionLevel.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase))
                columnstore.ColumnstoreArchive = false;
        }
        else if (compressionLevel is not null || xmlCompression is not null || partitionCompressions is not null || xmlPartitionCompressions is not null)
        {
            // A rowstore rebuild recompresses the rows: the heap's, or the
            // clustered index's that holds them (probed 2026-10-05), a
            // partition's alone when it names one.
            byte? level = compressionLevel is null ? null
                : compressionLevel.Equals("ROW", StringComparison.OrdinalIgnoreCase) ? (byte)1
                : compressionLevel.Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? (byte)2
                : (byte)0;
            var options = new IndexOptions(false, null, null, dataCompression: level, xmlCompression: xmlCompression,
                partitionCompressions: partitionCompressions, xmlPartitionCompressions: xmlPartitionCompressions);
            if (table.KeyConstraints.Find(static key => key.IsClustered) is { } clusteredKey)
            {
                ApplyRebuildCompressions(ref clusteredKey.DataCompression, ref clusteredKey.PartitionDataCompression, ref clusteredKey.XmlCompression, ref clusteredKey.PartitionXmlCompression,
                    options, rebuiltPartition, table.Partitioning, table.Name, "table");
            }
            else if (table.Indexes.Find(static index => index.IsClustered) is { } clusteredIndex)
            {
                ApplyRebuildCompressions(ref clusteredIndex.DataCompression, ref clusteredIndex.PartitionDataCompression, ref clusteredIndex.XmlCompression, ref clusteredIndex.PartitionXmlCompression,
                    options, rebuiltPartition, table.Partitioning, table.Name, "table");
            }
            else
            {
                ApplyRebuildCompressions(ref table.HeapDataCompression, ref table.HeapPartitionDataCompression, ref table.HeapXmlCompression, ref table.HeapPartitionXmlCompression,
                    options, rebuiltPartition, table.Partitioning, table.Name, "table");
            }
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
    private static bool ParseOptionalRebuildOptions(ParserContext context, out string? compressionLevel, out bool? xmlCompression, out List<PartitionCompressionClause>? partitionCompressions, out List<PartitionCompressionClause>? xmlPartitionCompressions)
    {
        compressionLevel = null;
        xmlCompression = null;
        partitionCompressions = null;
        xmlPartitionCompressions = null;
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
            string? writtenLevel = null;
            bool? writtenXml = null;
            if (!bareWordValue)
            {
                if (value is not Numeric)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (DataCompressionOption.Equals(optionName, StringComparison.OrdinalIgnoreCase))
            {
                if (!DataCompressionLevels.Contains(value.Source.ToString()))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                writtenLevel = value.Source.ToString();
            }
            else if (value is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle)
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            else if (optionName.Equals("XML_COMPRESSION", StringComparison.OrdinalIgnoreCase))
            {
                writtenXml = toggle.Keyword == Keyword.On;
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
                var ranges = ReadPartitionRanges(context);
                if (writtenLevel is not null)
                {
                    // Named for the whole table and for partitions is Msg 7711
                    // (probed 2026-10-05 against SQL Server 2025).
                    if (compressionLevel is not null)
                        throw SimulatedSqlException.DataCompressionSpecifiedTwice();
                    var partitionLevel = writtenLevel.Equals("ROW", StringComparison.OrdinalIgnoreCase) ? (byte)1
                        : writtenLevel.Equals("PAGE", StringComparison.OrdinalIgnoreCase) ? (byte)2
                        : (byte)0;
                    (partitionCompressions ??= []).Add(new PartitionCompressionClause(partitionLevel, ranges));
                    writtenLevel = null;
                }
                if (writtenXml is { } partitionXml)
                {
                    // So is XML_COMPRESSION, Msg 7741 then Msg 1750 state 0 (probed
                    // 2026-10-06).
                    if (xmlCompression is not null)
                        throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.XmlCompressionSpecifiedTwice(state: 1), state: 0);
                    (xmlPartitionCompressions ??= []).Add(new PartitionCompressionClause(partitionXml ? (byte)1 : (byte)0, ranges));
                    writtenXml = null;
                }
                context.MoveNextRequired();
            }
            if (writtenLevel is not null)
            {
                if (partitionCompressions is not null)
                    throw SimulatedSqlException.DataCompressionSpecifiedTwice();
                compressionLevel = writtenLevel;
            }
            if (writtenXml is not null)
            {
                if (xmlCompression is not null || xmlPartitionCompressions is not null)
                    throw SimulatedSqlException.FollowedByConstraintNotCreated(SimulatedSqlException.XmlCompressionSpecifiedTwice(state: 1), state: 0);
                xmlCompression = writtenXml;
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
