using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The DBCC commands past SHRINK*, SHOW_STATISTICS, CHECKIDENT and INPUTBUFFER:
// one front end that reads any DBCC statement's shape, then a handler per
// subcommand. Every behavior here was probed 2026-09-28 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// Parses and runs every <c>DBCC</c> statement the dedicated parsers ahead
    /// of it pass on: <c>DBCC name [( argument, … )] [WITH option, …]</c>.
    /// The statement's shape is settled while it parses — a <c>WITH</c> word
    /// that is no DBCC option at all is Msg 195 then, so the batch never runs —
    /// and everything else when it runs: an unknown subcommand (Msg 2526), an
    /// option the subcommand doesn't take (Msg 2532), a wrong argument count
    /// (Msg 2583), a missing permission and a bad argument (Msg 2560).
    /// The cursor is left on the statement's last token.
    /// </summary>
    private static List<SimulatedStatementOutcome> ParseDbccCommand(ParserContext context, BatchContext batch)
    {
        // The subcommand is a bare word; a bracketed one is a syntax error.
        if (context.GetNextRequired() is not UnquotedString commandToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var arguments = new List<DbccArgument>();
        var afterName = context.SaveCheckpoint();
        context.MoveNextOptional();
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            if (context.Token is not Operator { Character: ')' })
            {
                while (true)
                {
                    arguments.Add(DbccArgument.Parse(context));
                    context.MoveNextRequired();
                    if (context.Token is Operator { Character: ')' })
                        break;
                    if (context.Token is not Operator { Character: ',' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    context.MoveNextRequired();
                }
            }
            afterName = context.SaveCheckpoint();
            context.MoveNextOptional();
        }

        var options = DbccOptions.None;
        var maxDop = 0;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            while (true)
            {
                if (context.GetNextRequired() is not Name option)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var parsed = ParseDbccOption(option.Value);
                afterName = context.SaveCheckpoint();
                context.MoveNextOptional();
                if (parsed == DbccOptions.MaxDop)
                {
                    if (context.Token is not Operator { Character: '=' })
                        throw SimulatedSqlException.DbccOptionNotRecognized(option.Value);
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } degree })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    maxDop = degree.CoerceTo(SqlType.Int32).AsInt32;
                    afterName = context.SaveCheckpoint();
                    context.MoveNextOptional();
                }
                else if (context.Token is Operator { Character: '=' })
                {
                    throw SimulatedSqlException.SyntaxErrorNear(option);
                }
                options |= parsed;
                if (context.Token is not Operator { Character: ',' })
                    break;
            }
        }
        context.RestoreCheckpoint(afterName);

        // Under SET FMTONLY a DBCC statement neither runs nor reports.
        if (batch.IsSkipping || batch.Connection.FmtOnly)
            return [];

        var dbcc = new DbccInvocation(commandToken.Value, arguments, options, maxDop);
        var name = commandToken.Value;
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "CHECKALLOC" => RunDbccCheckAlloc(batch, dbcc),
            "CHECKCATALOG" => RunDbccCheckCatalog(batch, dbcc),
            "CHECKCONSTRAINTS" => RunDbccCheckConstraints(batch, dbcc),
            "CHECKDB" => RunDbccCheckDatabase(batch, dbcc, "CHECKDB"),
            "CHECKFILEGROUP" => RunDbccCheckDatabase(batch, dbcc, "CHECKFILEGROUP"),
            "CHECKTABLE" => RunDbccCheckTable(batch, dbcc),
            "CLEANTABLE" => RunDbccCleanTable(batch, dbcc),
            "DBREINDEX" => RunDbccDbReindex(batch, dbcc),
            "DROPCLEANBUFFERS" => RunDbccDropCleanBuffers(batch, dbcc),
            "FREEPROCCACHE" => RunDbccFreeProcCache(batch, dbcc),
            "FREESESSIONCACHE" => RunDbccFreeSessionCache(batch, dbcc),
            "FREESYSTEMCACHE" => RunDbccFreeSystemCache(batch, dbcc),
            "HELP" => RunDbccHelp(batch, dbcc),
            "INDEXDEFRAG" => RunDbccIndexDefrag(batch, dbcc),
            "LOGINFO" => RunDbccLogInfo(batch, dbcc),
            "OPENTRAN" => RunDbccOpenTran(batch, dbcc),
            "SQLPERF" => RunDbccSqlPerf(batch, dbcc),
            "TRACEOFF" => RunDbccTraceOnOff(batch, dbcc, on: false),
            "TRACEON" => RunDbccTraceOnOff(batch, dbcc, on: true),
            "TRACESTATUS" => RunDbccTraceStatus(batch, dbcc),
            "UPDATEUSAGE" => RunDbccUpdateUsage(batch, dbcc),
            "USEROPTIONS" => RunDbccUserOptions(batch, dbcc),
            _ => IsUnbuiltDbccCommand(upper)
                ? throw new NotSupportedException($"DBCC {name.ToUpperInvariant()} isn't modeled.")
                : throw SimulatedSqlException.DbccStatementIncorrect(),
        };
    }

    /// <summary>
    /// The subcommands real knows that the simulator hasn't built — documented
    /// ones and the undocumented ones in common use — which answer
    /// <see cref="NotSupportedException"/> rather than real's Msg 2526 for an
    /// unknown name.
    /// </summary>
    private static bool IsUnbuiltDbccCommand(ReadOnlySpan<char> upper) => upper switch
    {
        "BUFFER" or "CLONEDATABASE" or "DBINFO" or "DBTABLE" or "EXTENTINFO" or "FILEHEADER" or "FLUSHAUTHCACHE" or "IND" or "LOG"
            or "MEMORYSTATUS" or "OUTPUTBUFFER" or "PAGE" or "PINTABLE" or "PROCCACHE" or "SHOWCONTIG" or "SHOWFILESTATS" or "SQLMGRSTATS"
            or "STACKDUMP" or "TUPLEMOVER" or "UNPINTABLE" or "WRITEPAGE" => true,
        _ => false,
    };

    /// <summary>
    /// One <c>WITH</c> word of a DBCC statement: the options any DBCC command
    /// takes, all of which the parser knows whichever command they follow.
    /// Anything else is Msg 195 — <c>MAXDOP</c> too unless a value follows.
    /// </summary>
    private static DbccOptions ParseDbccOption(string option)
    {
        Span<char> upper = stackalloc char[option.Length];
        _ = option.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "ALL_CONSTRAINTS" => DbccOptions.AllConstraints,
            "ALL_ERRORMSGS" => DbccOptions.AllErrorMessages,
            "ALL_INDEXES" or "ALL_LEVELS" or "DENSITY_VECTOR" or "FAST" or "HISTOGRAM" or "STAT_HEADER" or "STATS_STREAM" => DbccOptions.Other,
            "COUNT_ROWS" => DbccOptions.CountRows,
            "DATA_PURITY" => DbccOptions.DataPurity,
            "ESTIMATEONLY" => DbccOptions.EstimateOnly,
            "EXTENDED_LOGICAL_CHECKS" => DbccOptions.ExtendedLogicalChecks,
            "MARK_IN_USE_FOR_REMOVAL" => DbccOptions.MarkInUseForRemoval,
            "MAXDOP" => DbccOptions.MaxDop,
            "NO_INFOMSGS" => DbccOptions.NoInfoMessages,
            "PHYSICAL_ONLY" => DbccOptions.PhysicalOnly,
            "TABLERESULTS" => DbccOptions.TableResults,
            "TABLOCK" => DbccOptions.TabLock,
            _ => throw SimulatedSqlException.DbccOptionNotRecognized(option),
        };
    }

    /// <summary>
    /// Whether the session runs as a <c>sysadmin</c> login — the server-scope
    /// DBCC gate. A database-scoped identity (<c>EXECUTE AS USER</c>, a
    /// module's own frame) carries no login and never passes.
    /// </summary>
    private static bool IsSysadminSession(BatchContext batch)
    {
        var effective = batch.Connection.Security.Effective;
        return !effective.IsDatabaseScoped && batch.Connection.Simulation.IsLoginSysadmin(effective.LoginName);
    }

    /// <summary>Raises Msg 2571 unless <see cref="IsSysadminSession"/>.</summary>
    private static void RequireDbccSysadmin(BatchContext batch, string command, byte state)
    {
        if (!IsSysadminSession(batch))
            throw SimulatedSqlException.DbccPermissionDenied(batch.Connection.Security.Effective.DatabasePrincipalName, command, state);
    }

    /// <summary>
    /// Queues Msg 2528 unless <c>NO_INFOMSGS</c> — ahead of the statement's
    /// outcomes when it returns none, or after its rows when it does
    /// (<see cref="AfterDbccRows"/>).
    /// </summary>
    private static List<SimulatedStatementOutcome> DbccCompleted(BatchContext batch, DbccInvocation dbcc, List<SimulatedStatementOutcome> outcomes)
    {
        if (dbcc.Has(DbccOptions.NoInfoMessages))
            return outcomes;
        var completed = SimulatedSqlException.DbccExecutionCompletedMessage(batch);
        if (outcomes.Count == 0)
            batch.Connection.PendingMessages.Enqueue(completed);
        else
            AfterDbccRows(batch, outcomes, completed);
        return outcomes;
    }

    /// <summary>
    /// Appends a message a DBCC command sends after its rows. Real closes the
    /// rows with their own counted DONE first — a client reports the row
    /// count ahead of the message — and the statement with a second DONE after
    /// it, which a TDS session gets here as a count-less stand-in.
    /// </summary>
    private static void AfterDbccRows(BatchContext batch, List<SimulatedStatementOutcome> outcomes, SimulatedError message)
    {
        outcomes.Add(new SimulatedInfoOutcome(message));
        if (batch.Connection.FramesEveryStatement)
            outcomes.Add(StatementDone(batch, StatementDoneKind.Dbcc));
    }

    /// <summary>
    /// A result set a DBCC command returns, which leaves <c>@@ROWCOUNT</c> at
    /// its row count — where a command returning nothing leaves it alone.
    /// </summary>
    private static List<SimulatedStatementOutcome> DbccRows(BatchContext batch, SqlType[] schema, string[] names, List<SqlValue[]> rows)
    {
        var encoded = new byte[rows.Count][];
        for (var i = 0; i < rows.Count; i++)
            encoded[i] = RowEncoder.EncodeRow(schema, rows[i]);
        batch.Connection.LastStatementRowCount = rows.Count;
        return [new SimulatedSqlResultSet(schema, names, encoded)];
    }

    /// <summary>
    /// A DBCC database argument: a name (quoted or not), a database id, or 0
    /// for the current database; absent, the current database. An unknown
    /// name is Msg 2520, an unknown id Msg 2521, anything else Msg 2560.
    /// </summary>
    private static Database ResolveDbccDatabase(BatchContext batch, DbccArgument? argument, int position)
    {
        if (argument is null)
            return batch.CurrentDatabase;
        var simulation = batch.Connection.Simulation;
        if (argument.Kind == DbccArgumentKind.Name)
        {
            return simulation.Databases.TryGetValue(argument.Name.Leaf, out var named)
                ? named
                : throw SimulatedSqlException.CouldNotFindDatabase(argument.Name.Leaf, 5);
        }
        var value = argument.Evaluate(batch);
        if (value.IsNull)
            throw SimulatedSqlException.DbccParameterIsIncorrect(position);
        if (SqlType.IsStringCategory(value.Type))
        {
            var text = value.AsString;
            return simulation.Databases.TryGetValue(text, out var byName)
                ? byName
                : throw SimulatedSqlException.CouldNotFindDatabase(text, 5);
        }
        if (!SqlType.IsIntegerCategory(value.Type))
            throw SimulatedSqlException.DbccParameterIsIncorrect(position);
        var id = value.CoerceTo(SqlType.BigInt).AsInt64;
        if (id == 0)
            return batch.CurrentDatabase;
        if (id < 0)
            throw SimulatedSqlException.DbccParameterIsIncorrect(position);
        foreach (var (database, databaseId) in DbId.DatabasesWithIds(simulation))
        {
            if (databaseId == id)
                return database;
        }
        throw SimulatedSqlException.CouldNotFindDatabaseId(id);
    }

    /// <summary>
    /// <c>CHECKPOINT [duration]</c>: flushes nothing — every write is already
    /// in the simulator's heap — but checks real's permission (<c>db_owner</c>
    /// or <c>db_backupoperator</c>, else Msg 3505) and resets <c>@@ROWCOUNT</c>.
    /// The duration is a positive integer literal, anything else a syntax error.
    /// </summary>
    private static bool ParseCheckpoint(ParserContext context, BatchContext batch)
    {
        var afterKeyword = context.SaveCheckpoint();
        if (context.GetNextOptional() is Numeric { Value: { IsNull: false } duration } numeric)
        {
            if (!SqlType.IsIntegerCategory(duration.Type) || duration.Type != SqlType.Int32 || duration.AsInt32 <= 0)
                throw SimulatedSqlException.SyntaxErrorNear(numeric);
        }
        else if (context.Token is Operator { Character: '-' or '+' } or Literal or AtPrefixedString)
        {
            throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        else
        {
            context.RestoreCheckpoint(afterKeyword);
        }

        if (batch.IsSkipping)
            return true;
        var database = batch.CurrentDatabase;
        if (!PermissionEnforcement.IsOwnerOrBackupOperator(batch, database))
            throw SimulatedSqlException.CheckpointPermissionDenied(database.Name);
        batch.Connection.LastStatementRowCount = 0;
        return true;
    }

    /// <summary>
    /// <c>DBCC FREEPROCCACHE [( plan_handle | sql_handle | pool_name )] [WITH
    /// NO_INFOMSGS]</c>. Empties the plan cache — with it the compiled-batch
    /// memo and the token memo — or, given a <c>sql_handle</c>, the plans of
    /// the one command text it names; a plan handle names no plan here, the
    /// simulator exposing none. Of the two resource pools, <c>default</c> holds
    /// every plan and <c>internal</c> none.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccFreeProcCache(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 1);
        RequireDbccSysadmin(batch, "freeproccache", 9);
        var simulation = batch.Connection.Simulation;
        if (dbcc.Arguments.Count == 0)
        {
            simulation.ClearPlanCache();
            return DbccCompleted(batch, dbcc, []);
        }

        var value = dbcc.Arguments[0].Evaluate(batch);
        if (value.IsNull)
            throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        switch (value.Type)
        {
            case VarbinarySqlType or BinarySqlType:
                var handle = value.AsBytes;
                if (handle.Length < BuiltInResources.SqlHandleLength)
                    throw SimulatedSqlException.DbccParameterIsIncorrect(1, 110);
                if (handle[0] == 0x02)
                    simulation.ClearPlanCache(sqlHandle: handle);
                break;
            case { Category: SqlTypeCategory.String }:
                if (BuiltInToken.Equals(value.AsString, "default"))
                    simulation.ClearPlanCache();
                else if (!BuiltInToken.Equals(value.AsString, "internal"))
                    throw SimulatedSqlException.DbccParameterIsIncorrect(1);
                break;
            default:
                throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        }
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary><c>DBCC DROPCLEANBUFFERS [WITH NO_INFOMSGS]</c>: the simulator's pages never leave memory, so there is nothing to drop.</summary>
    private static List<SimulatedStatementOutcome> RunDbccDropCleanBuffers(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 0);
        RequireDbccSysadmin(batch, "dropcleanbuffers", 16);
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary><c>DBCC FREESESSIONCACHE [WITH NO_INFOMSGS]</c>: the distributed-query connection cache it empties has no counterpart here.</summary>
    private static List<SimulatedStatementOutcome> RunDbccFreeSessionCache(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 0);
        RequireDbccSysadmin(batch, "freesessioncache", 1);
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// The cache stores <c>DBCC FREESYSTEMCACHE</c> names beyond a database's
    /// own name and its <c>ObjPerm - </c> store, as
    /// <c>sys.dm_os_memory_cache_counters</c> lists them on a SQL Server 2025
    /// instance (the per-login security stores excepted).
    /// </summary>
    private static readonly string[] SystemCacheStoreNames =
    [
        "AdHocCEFeedbackCache", "Bound Trees", "Broker dormant rowsets", "Column store object pool", "ConversationPriorityCache",
        "EventNotificationCache", "Extended Stored Procedures", "FTSTOPLIST_CACHESTORE", "model_msdb", "model_replicatedmaster",
        "mssqlsystemresource", "Notification Store", "Object Plans", "QDSContextSettingsManager", "QDSRuntimeStatsManager",
        "QDSStmtStore", "SchemaMgr Store", "SEARCH_PROPERTY_LIST_CACHESTORE", "Service broker configuration", "Service broker dialog cache",
        "Service Broker Dialog Security Header Cache", "Service Broker Key Exchange Key Cache", "Service broker mapping table",
        "Service Broker Null Remote Service Binding Cache", "Service broker routing cache", "Service Broker Transmission Object Cache",
        "Service Broker user certificates lookup result cache", "SESHAREDCOLMETADATACACHE", "SOS_StackFramesStore", "SQL Plans",
        "sxcCacheStore", "SystemRowsetStore", "Temporary Tables & Table Variables", "TokenAndPermUserStore", "View Definition Cache",
        "XMLDBCACHE", "XStoreAADTokenCache",
    ];

    /// <summary>
    /// <c>DBCC FREESYSTEMCACHE ( 'ALL' | cache_name [, pool_name] ) [WITH
    /// MARK_IN_USE_FOR_REMOVAL, NO_INFOMSGS]</c>. <c>ALL</c> and
    /// <c>SQL Plans</c> — the store an ad hoc batch's plan lives in — empty the
    /// plan cache as <c>FREEPROCCACHE</c> does; every other store has nothing
    /// to free here.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccFreeSystemCache(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.MarkInUseForRemoval);
        dbcc.RequireArgumentCount(1, 2);
        RequireDbccSysadmin(batch, "freesystemcache", 11);
        var simulation = batch.Connection.Simulation;
        var store = dbcc.StringArgument(batch, 0) ?? throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        if (dbcc.Arguments.Count == 2
            && !BuiltInToken.EqualsAny(dbcc.StringArgument(batch, 1) ?? throw SimulatedSqlException.DbccParameterIsIncorrect(2), "default", "internal"))
        {
            throw SimulatedSqlException.DbccParameterIsIncorrect(2);
        }
        var known = BuiltInToken.EqualsAny(store, "ALL") || Array.Exists(SystemCacheStoreNames, name => BuiltInToken.Equals(name, store));
        if (!known)
        {
            foreach (var database in simulation.Databases.Values)
            {
                if (BuiltInToken.Equals(database.Name, store) || BuiltInToken.Equals($"ObjPerm - {database.Name}", store))
                {
                    known = true;
                    break;
                }
            }
        }
        if (!known)
            throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        if (BuiltInToken.EqualsAny(store, "ALL", "SQL Plans"))
            simulation.ClearPlanCache();
        return DbccCompleted(batch, dbcc, []);
    }

    private static readonly string[] UserOptionsColumnNames = ["Set Option", "Value"];

    /// <summary>
    /// <c>DBCC USEROPTIONS [WITH NO_INFOMSGS]</c>: the session's <c>SET</c>
    /// options in real's fixed order — the value-taking ones always, the
    /// switches only while on, the isolation level last (with <c>read committed
    /// snapshot</c> when the database reads committed rows by versioning).
    /// <c>QUOTED_IDENTIFIER</c> reads what the batch's compile left, as
    /// <c>@@OPTIONS</c> does. Real lists <c>ansi_defaults</c> while all seven of
    /// its options are on.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccUserOptions(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 0);
        var connection = batch.Connection;
        var collation = batch.CurrentDatabase.Collation;
        SqlType[] schema = [NVarcharSqlType.Get(128, collation, Coercibility.Implicit), NVarcharSqlType.Get(46, collation, Coercibility.Implicit)];
        var rows = new List<SqlValue[]>();
        void Row(string option, string value) => rows.Add([SqlValue.FromNVarchar(option), SqlValue.FromNVarchar(value)]);
        void Switch(string option, bool on)
        {
            if (on)
                Row(option, "SET");
        }

        var quotedIdentifier = batch.ProcFrame is null && batch.TriggerFrame is null && batch.UdfFrame is null
            ? batch.QuotedIdentifiersAfterParse ?? connection.QuotedIdentifiers
            : connection.QuotedIdentifiers;
        int textSize = connection.TextSize, lockTimeout = connection.LockTimeoutMillis;
        var rowCount = connection.RowCountLimit;
        var dateFirst = connection.DateFirst;
        Row("textsize", textSize.ToString(CultureInfo.InvariantCulture));
        if (rowCount != 0)
            Row("rowcount", rowCount.ToString(CultureInfo.InvariantCulture));
        Row("language", connection.Language.Name);
        Row("dateformat", connection.DateFormat.Name);
        Row("datefirst", dateFirst.ToString(CultureInfo.InvariantCulture));
        var listed = connection.ListedOnlyOptions;
        Switch("statistics time", connection.StatisticsTime);
        Switch("statistics io", connection.StatisticsIo);
        Switch("statistics profile", (listed & ListedOnlyOptions.StatisticsProfile) != 0);
        Switch("statistics XML", (listed & ListedOnlyOptions.StatisticsXml) != 0);
        Row("lock_timeout", lockTimeout.ToString(CultureInfo.InvariantCulture));
        Switch("quoted_identifier", quotedIdentifier);
        Switch("arithabort", connection.Arithabort);
        Switch("arithignore", connection.ArithIgnore);
        Switch("numeric_roundabort", connection.NumericRoundabort);
        Switch("nocount", connection.NoCount);
        Switch("forceplan", (listed & ListedOnlyOptions.ForcePlan) != 0);
        Switch("remote_proc_transactions", (listed & ListedOnlyOptions.RemoteProcTransactions) != 0);
        Switch("ansi_null_dflt_on", connection.AnsiNullDefaultOn);
        Switch("ansi_null_dflt_off", connection.AnsiNullDefaultOff);
        Switch("ansi_defaults", quotedIdentifier && connection.AnsiNulls && connection.AnsiNullDefaultOn && connection.AnsiPadding
            && connection.AnsiWarnings && connection.CursorCloseOnCommit && connection.ImplicitTransactions);
        Switch("xact_abort", connection.XactAbort);
        Switch("ansi_warnings", connection.AnsiWarnings);
        Switch("ansi_padding", connection.AnsiPadding);
        Switch("ansi_nulls", connection.AnsiNulls);
        Switch("no_browsetable", connection.NoBrowseTable);
        Switch("concat_null_yields_null", connection.ConcatNullYieldsNull);
        Switch("cursor_close_on_commit", connection.CursorCloseOnCommit);
        Switch("implicit_transactions", connection.ImplicitTransactions);
        Row("isolation level", connection.SessionIsolationLevel switch
        {
            System.Data.IsolationLevel.ReadUncommitted => "read uncommitted",
            System.Data.IsolationLevel.RepeatableRead => "repeatable read",
            System.Data.IsolationLevel.Serializable => "serializable",
            System.Data.IsolationLevel.Snapshot => "snapshot",
            _ => batch.CurrentDatabase.ReadCommittedSnapshot ? "read committed snapshot" : "read committed",
        });
        return DbccCompleted(batch, dbcc, DbccRows(batch, schema, UserOptionsColumnNames, rows));
    }

    /// <summary>
    /// <c>DBCC TRACEON / TRACEOFF ( trace# [, …] [, -1] ) [WITH NO_INFOMSGS]</c>:
    /// the flags turn on or off for the session, or server-wide when <c>-1</c>
    /// is among them. A flag past real's range is Msg 2560 (state 17 on, 30
    /// off) and changes none of them; flag 0 is accepted and never listed.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccTraceOnOff(BatchContext batch, DbccInvocation dbcc, bool on)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        RequireDbccSysadmin(batch, on ? "TRACEON" : "TRACEOFF", 3);
        var flags = new List<int>(dbcc.Arguments.Count);
        var global = false;
        for (var i = 0; i < dbcc.Arguments.Count; i++)
        {
            var flag = dbcc.IntegerArgument(batch, i);
            if (flag == -1)
                global = true;
            else if (flag is < 0 or > MaxTraceFlag)
                throw SimulatedSqlException.DbccParameterIsIncorrect(i + 1, on ? (byte)17 : (byte)30);
            else if (flag != 0)
                flags.Add((int)flag);
        }
        var simulation = batch.Connection.Simulation;
        var target = global ? simulation.GlobalTraceFlags : batch.Connection.TraceFlags;
        lock (target)
        {
            foreach (var flag in flags)
                _ = on ? target.Add(flag) : target.Remove(flag);
        }
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>The highest trace flag number <c>DBCC TRACEON</c> accepts.</summary>
    private const int MaxTraceFlag = 17798;

    private static readonly string[] TraceStatusColumnNames = ["TraceFlag", "Status", "Global", "Session"];

    private static readonly SqlType[] TraceStatusSchema = [SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt];

    /// <summary>
    /// <c>DBCC TRACESTATUS [( trace# [, …] [, -1] )] [WITH NO_INFOMSGS]</c>: every
    /// flag on — server-wide or for the session — when no flag or <c>-1</c> is
    /// named, else a row per named flag whether on or not. No row at all sends
    /// no result set. Open to every session.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccTraceStatus(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        var session = batch.Connection.TraceFlags;
        var global = batch.Connection.Simulation.GlobalTraceFlags;
        int[] globalFlags;
        lock (global)
            globalFlags = [.. global];
        var named = new List<int>(dbcc.Arguments.Count);
        var all = dbcc.Arguments.Count == 0;
        for (var i = 0; i < dbcc.Arguments.Count; i++)
        {
            var flag = dbcc.IntegerArgument(batch, i);
            if (flag == -1)
                all = true;
            else if (flag is > 0 and <= short.MaxValue && !named.Contains((int)flag))
                named.Add((int)flag);
        }
        if (all)
        {
            named = [.. globalFlags.Union(session).Order()];
        }

        var rows = new List<SqlValue[]>(named.Count);
        foreach (var flag in named)
        {
            var isGlobal = Array.IndexOf(globalFlags, flag) >= 0;
            var isSession = !isGlobal && session.Contains(flag);
            rows.Add([
                SqlValue.FromInt16((short)flag),
                SqlValue.FromInt16(isGlobal || isSession ? (short)1 : (short)0),
                SqlValue.FromInt16(isGlobal ? (short)1 : (short)0),
                SqlValue.FromInt16(isSession ? (short)1 : (short)0),
            ]);
        }
        return DbccCompleted(batch, dbcc, rows.Count == 0 ? [] : DbccRows(batch, TraceStatusSchema, TraceStatusColumnNames, rows));
    }

    private static readonly string[] SqlPerfColumnNames = ["Database Name", "Log Size (MB)", "Log Space Used (%)", "Status"];

    /// <summary>
    /// The log space <c>DBCC SQLPERF(LOGSPACE)</c> reports in use: a fixed 54
    /// pages, which is what a freshly created database reports on SQL Server
    /// 2025. The simulator keeps no log, so the figure never moves.
    /// </summary>
    private const long ReportedLogBytesUsed = 54 * 8192;

    /// <summary>
    /// <c>DBCC SQLPERF ( LOGSPACE | 'sys.dm_os_wait_stats' , CLEAR | 'sys.dm_os_latch_stats' , CLEAR )
    /// [WITH NO_INFOMSGS]</c>. <c>LOGSPACE</c> lists every database in id order
    /// with its log file's size less the header page, as real reports it; the
    /// space used is <see cref="ReportedLogBytesUsed"/>. The two <c>CLEAR</c>
    /// forms reset statistics the simulator doesn't gather.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccSqlPerf(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(1, 2);
        var first = dbcc.Arguments[0];
        var keyword = (first.Kind == DbccArgumentKind.Name ? first.Name.Leaf : dbcc.StringArgument(batch, 0))
            ?? throw SimulatedSqlException.DbccStatementIncorrect(12);
        if (BuiltInToken.EqualsAny(keyword, "sys.dm_os_wait_stats", "sys.dm_os_latch_stats"))
        {
            if (dbcc.Arguments.Count != 2 || dbcc.Arguments[1] is not { Kind: DbccArgumentKind.Name, Name.Leaf: var clear } || !BuiltInToken.Equals(clear, "CLEAR"))
                throw SimulatedSqlException.DbccStatementIncorrect(15);
            if (!IsSysadminSession(batch))
                throw SimulatedSqlException.UserLacksPermissionForAction();
            return DbccCompleted(batch, dbcc, []);
        }
        if (!BuiltInToken.Equals(keyword, "LOGSPACE") || dbcc.Arguments.Count != 1)
            throw SimulatedSqlException.DbccStatementIncorrect(12);
        if (!IsSysadminSession(batch)
            && !batch.Connection.Simulation.HoldsServerPermission(batch.Connection.Security.Effective.LoginName, Permission.ViewServerState))
        {
            throw SimulatedSqlException.UserLacksPermissionForAction();
        }

        SqlType[] schema = [NVarcharSqlType.Get(128, batch.CurrentDatabase.Collation, Coercibility.Implicit), SqlType.Real, SqlType.Real, SqlType.Int32];
        var rows = new List<SqlValue[]>();
        foreach (var (database, _) in DbId.DatabasesWithIds(batch.Connection.Simulation))
        {
            var logBytes = LogFileBytes(database) - 8192;
            rows.Add([
                SqlValue.FromNVarchar(database.Name),
                SqlValue.FromSingle((float)(logBytes / 1048576.0)),
                SqlValue.FromSingle((float)(100.0 * ReportedLogBytesUsed / logBytes)),
                SqlValue.FromInt32(0),
            ]);
        }
        return DbccCompleted(batch, dbcc, DbccRows(batch, schema, SqlPerfColumnNames, rows));
    }

    /// <summary>The size of <paramref name="database"/>'s log files, in bytes.</summary>
    private static long LogFileBytes(Database database)
    {
        long pages = 0;
        foreach (var file in database.Files)
        {
            if (file.IsLog)
                pages += file.SizePages;
        }
        return Math.Max(pages, 128) * 8192;
    }

    private static readonly string[] LogInfoColumnNames = ["RecoveryUnitId", "FileId", "FileSize", "StartOffset", "FSeqNo", "Status", "Parity", "CreateLSN"];

    /// <summary>
    /// The sequence number of the one active virtual log file
    /// <c>DBCC LOGINFO</c> reports, and the first part of every LSN
    /// <c>DBCC OPENTRAN</c> prints. The simulator keeps no log to number.
    /// </summary>
    private const int ActiveLogSequence = 34;

    /// <summary>
    /// <c>DBCC LOGINFO [( database )] [WITH NO_INFOMSGS]</c>: the log file cut
    /// into virtual log files the way real cuts a log under 64 MB — four, each
    /// a whole number of 64 KB units, the last taking the remainder — with the
    /// first active. Sysadmin only (Msg 2571).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccLogInfo(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(0, 1);
        RequireDbccSysadmin(batch, "loginfo", 1);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments.Count == 1 ? dbcc.Arguments[0] : null, 1);
        var logFile = database.Files.Find(file => file.IsLog);
        var usable = LogFileBytes(database) - 8192;
        var unit = usable / 4 / 65536 * 65536;
        SqlType[] schema = [SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.BigInt, SqlType.Int32, SqlType.Int32, SqlType.TinyInt, DecimalSqlType.Get(25, 0)];
        var rows = new List<SqlValue[]>(4);
        for (var i = 0; i < 4; i++)
        {
            var size = i < 3 ? unit : usable - (3 * unit);
            rows.Add([
                SqlValue.FromInt32(0),
                SqlValue.FromInt32(logFile?.FileId ?? 2),
                SqlValue.FromInt64(size),
                SqlValue.FromInt64(8192 + (i * unit)),
                SqlValue.FromInt32(i == 0 ? ActiveLogSequence : 0),
                SqlValue.FromInt32(i == 0 ? 2 : 0),
                SqlValue.FromByte(i == 0 ? (byte)64 : (byte)0),
                SqlValue.FromInt32(0).CoerceTo(schema[7]),
            ]);
        }
        return DbccCompleted(batch, dbcc, DbccRows(batch, schema, LogInfoColumnNames, rows));
    }
}

/// <summary>The <c>WITH</c> options of a DBCC statement.</summary>
[Flags]
internal enum DbccOptions
{
    None = 0,
    NoInfoMessages = 1 << 0,
    TableResults = 1 << 1,
    AllErrorMessages = 1 << 2,
    AllConstraints = 1 << 3,
    PhysicalOnly = 1 << 4,
    EstimateOnly = 1 << 5,
    TabLock = 1 << 6,
    DataPurity = 1 << 7,
    ExtendedLogicalChecks = 1 << 8,
    MaxDop = 1 << 9,
    CountRows = 1 << 10,
    MarkInUseForRemoval = 1 << 11,

    /// <summary>An option DBCC knows that no modeled subcommand takes (<c>HISTOGRAM</c>, <c>FAST</c>, …).</summary>
    Other = 1 << 12,
}

/// <summary>How a DBCC argument was written.</summary>
internal enum DbccArgumentKind
{
    /// <summary>A string, number, binary literal or <c>NULL</c>.</summary>
    Literal,

    /// <summary>An identifier, dotted or bracketed, standing for a name or a keyword.</summary>
    Name,

    /// <summary>A variable, read when the statement runs.</summary>
    Variable,
}

/// <summary>One argument of a DBCC statement, as written.</summary>
internal sealed class DbccArgument(DbccArgumentKind kind, SqlValue literal, MultiPartName name, string written)
{
    public readonly DbccArgumentKind Kind = kind;

    /// <summary>The value of a <see cref="DbccArgumentKind.Literal"/>, sign applied.</summary>
    public readonly SqlValue Literal = literal;

    /// <summary>The name a <see cref="DbccArgumentKind.Name"/> spells.</summary>
    public readonly MultiPartName Name = name;

    /// <summary>The argument's text: a name as written, or a variable's name.</summary>
    public readonly string Written = written;

    /// <summary>Reads one argument, leaving the cursor on its last token.</summary>
    public static DbccArgument Parse(ParserContext context)
    {
        switch (context.Token)
        {
            case Operator { Character: '-' or '+' } sign:
                if (context.GetNextRequired() is not Numeric { Value: var magnitude })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var value = sign.Character == '+' || magnitude.IsNull ? magnitude : Negate(magnitude);
                return new(DbccArgumentKind.Literal, value, default, value.ToString() ?? "");
            case Numeric numeric:
                return new(DbccArgumentKind.Literal, numeric.Value, default, numeric.ToString());
            case Literal literal:
                return new(DbccArgumentKind.Literal, literal.Value, default, literal.ToString());
            case ReservedKeyword { Keyword: Keyword.Null }:
                return new(DbccArgumentKind.Literal, SqlValue.Null(SqlType.Int32), default, "NULL");
            case AtPrefixedString variable:
                return new(DbccArgumentKind.Variable, default, default, variable.Value);
            case Parser.Tokens.Name:
                var name = BatchContext.ParseObjectName(context);
                return new(DbccArgumentKind.Name, default, name, name.ToString());
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        static SqlValue Negate(SqlValue magnitude) =>
            SqlType.IsIntegerCategory(magnitude.Type)
                ? magnitude.CoerceTo(SqlType.BigInt).AsInt64 is var number && -number is >= int.MinValue and <= int.MaxValue
                    ? SqlValue.FromInt32((int)-number)
                    : SqlValue.FromInt64(-number)
                : SqlValue.FromNVarchar("-" + magnitude.CoerceTo(SqlType.NVarchar).AsString);
    }

    /// <summary>The argument's value: a literal's, a variable's, or a name's text as <c>nvarchar</c>.</summary>
    public SqlValue Evaluate(BatchContext batch) => this.Kind switch
    {
        DbccArgumentKind.Literal => this.Literal,
        DbccArgumentKind.Variable => batch.GetVariableSlot(this.Written).Value,
        _ => SqlValue.FromNVarchar(this.Name.ToString()),
    };
}

/// <summary>A DBCC statement as parsed, ready to run.</summary>
internal sealed class DbccInvocation(string command, List<DbccArgument> arguments, DbccOptions options, int maxDop)
{
    public readonly string Command = command;
    public readonly List<DbccArgument> Arguments = arguments;
    public readonly DbccOptions Options = options;

    /// <summary>The <c>MAXDOP = n</c> value, 0 when absent.</summary>
    public readonly int MaxDop = maxDop;

    public bool Has(DbccOptions option) => (this.Options & option) != 0;

    /// <summary>Msg 2532 for an option outside <paramref name="allowed"/>.</summary>
    public void AllowOptions(DbccOptions allowed)
    {
        if ((this.Options & ~allowed) != 0)
            throw SimulatedSqlException.DbccWithOptionNotValid();
    }

    /// <summary>Msg 2583 for an argument count outside the range.</summary>
    public void RequireArgumentCount(int minimum, int maximum)
    {
        if (this.Arguments.Count < minimum || this.Arguments.Count > maximum)
            throw SimulatedSqlException.DbccWrongParameterCount();
    }

    /// <summary>
    /// The argument at <paramref name="index"/> as text — a string's value or a
    /// name as written — or null for a NULL or a value of another type.
    /// </summary>
    public string? StringArgument(BatchContext batch, int index)
    {
        var argument = this.Arguments[index];
        if (argument.Kind == DbccArgumentKind.Name)
            return argument.Name.ToString();
        var value = argument.Evaluate(batch);
        return !value.IsNull && SqlType.IsStringCategory(value.Type) ? value.AsString : null;
    }

    /// <summary>The argument at <paramref name="index"/> as an integer, Msg 2560 when it isn't one.</summary>
    public long IntegerArgument(BatchContext batch, int index)
    {
        var argument = this.Arguments[index];
        var value = argument.Kind == DbccArgumentKind.Name ? SqlValue.Null(SqlType.Int32) : argument.Evaluate(batch);
        return !value.IsNull && SqlType.IsIntegerCategory(value.Type)
            ? value.CoerceTo(SqlType.BigInt).AsInt64
            : throw SimulatedSqlException.DbccParameterIsIncorrect(index + 1);
    }
}
