using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// DBCC OPENTRAN and DBCC HELP, probed 2026-09-28 against SQL Server 2025.
partial class Simulation
{
    private static readonly string[] OpenTranRowNames =
        ["OLDACT_SPID", "OLDACT_UID", "OLDACT_NAME", "OLDACT_RECOVERYUNITID", "OLDACT_LSN", "OLDACT_STARTTIME", "OLDACT_SID"];

    /// <summary>
    /// <c>DBCC OPENTRAN [( database | 0 )] [WITH TABLERESULTS, NO_INFOMSGS]</c>:
    /// the oldest transaction that has written to the database — changed one of
    /// its rows or its catalog, or holds a write lock there (IX, SIX, X or
    /// Sch-M, which an <c>UPDATE</c> finding no row still takes), as a
    /// transaction real has logged a change in does — so one that has only read
    /// or done nothing yet isn't listed. Named for its <c>BEGIN TRAN</c>, else
    /// <c>user_transaction</c> or <c>implicit_transaction</c>. <c>TABLERESULTS</c>
    /// returns the lines as rows under a column named for the database.
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccOpenTran(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages | DbccOptions.TableResults);
        dbcc.RequireArgumentCount(0, 1);
        var database = ResolveDbccDatabase(batch, dbcc.Arguments.Count == 1 ? dbcc.Arguments[0] : null, 1);
        // Real closes even this refusal with Msg 2528 (probed 2026-10-04
        // against SQL Server 2025).
        try
        {
            RequireDbccDatabaseOwner(batch, database, "opentran");
        }
        catch (SimulatedSqlException refused) when (!dbcc.Has(DbccOptions.NoInfoMessages))
        {
            throw SimulatedSqlException.FollowedByDbccCompleted(refused, batch.Connection.Language);
        }
        var informational = !dbcc.Has(DbccOptions.NoInfoMessages);
        if (OldestWritingTransaction(batch.Connection.Simulation, database) is not var (connection, transaction))
        {
            if (informational)
                Info(batch, SimulatedSqlException.NoActiveOpenTransactionsMessage(batch));
            return DbccCompleted(batch, dbcc, []);
        }

        var name = transaction.Name ?? (transaction.BegunImplicitly ? "implicit_transaction" : "user_transaction");
        var lsn = $"({ActiveLogSequence}:{transaction.TransactionId}:1)";
        var startTime = SqlValue.FromDateTime(transaction.BeginTimeUtc).CoerceDateTimeToStringWithStyle(SqlType.NVarchar, 109).AsString;
        var login = connection.Security.OriginalLoginName;
        var sid = "0x" + Convert.ToHexString(BuiltInToken.Equals(login, "sa") || !connection.Simulation.Logins.ContainsKey(login) ? [0x01] : BuiltInResources.DeriveLoginSid(login));
        if (dbcc.Has(DbccOptions.TableResults))
        {
            var collation = batch.CurrentDatabase.Collation;
            SqlType[] schema = [NVarcharSqlType.Get(128, collation, Coercibility.Implicit), NVarcharSqlType.Get(46, collation, Coercibility.Implicit)];
            string[] values = [connection.Spid.ToString(CultureInfo.InvariantCulture), "-1", name, "0", lsn, startTime, sid];
            var rows = new List<SqlValue[]>(values.Length);
            for (var i = 0; i < values.Length; i++)
                rows.Add([SqlValue.FromNVarchar(OpenTranRowNames[i]), SqlValue.FromNVarchar(values[i])]);
            return DbccCompleted(batch, dbcc, DbccRows(batch, schema, [database.Name, "OPENTRAN"], rows));
        }
        if (informational)
        {
            foreach (var message in SimulatedSqlException.OldestActiveTransactionMessages(batch, database.Name, connection.Spid, name, lsn, startTime, sid))
                Info(batch, message);
        }
        return DbccCompleted(batch, dbcc, []);
    }

    /// <summary>
    /// The session and transaction <see cref="RunDbccOpenTran"/> reports for
    /// <paramref name="database"/>: among transactions that have written there
    /// — for <c>tempdb</c>, to a temporary table — the one that began first.
    /// </summary>
    private static (SimulatedDbConnection Connection, SimulatedDbTransaction Transaction)? OldestWritingTransaction(Simulation simulation, Database database)
    {
        var tables = new List<HeapTable>();
        foreach (var (_, schema) in database.Schemas)
            tables.AddRange(schema.HeapTables.EnumerateValues());
        var connections = simulation.SnapshotConnections();
        if (database.Name == TempdbDatabaseName)
        {
            tables.AddRange(simulation.GlobalTempTables.EnumerateValues());
            foreach (var connection in connections)
                tables.AddRange(connection.TempTables.EnumerateValues());
        }

        var heaps = new HashSet<Heap>(ReferenceEqualityComparer.Instance);
        foreach (var table in tables)
            _ = heaps.Add(table.Heap);
        var isTempdb = database.Name == TempdbDatabaseName;

        (SimulatedDbConnection, SimulatedDbTransaction)? oldest = null;
        foreach (var connection in connections)
        {
            if (connection.CurrentTransaction is not { } transaction
                || (oldest is var (_, found) && found.TransactionId <= transaction.TransactionId))
            {
                continue;
            }
            bool changedCatalog;
            lock (transaction.CatalogChanges)
                changedCatalog = transaction.CatalogChanges.Contains(database);
            if (changedCatalog || transaction.UndoLog.Changes(heaps, isTempdb) || HoldsWriteLock(simulation, tables, connection.Session))
                oldest = (connection, transaction);
        }
        return oldest;

        static bool HoldsWriteLock(Simulation simulation, List<HeapTable> tables, SessionToken session)
        {
            lock (simulation.LockManager.gate)
                return tables.Exists(table => IsWriteHold(table.TableDataLock, session));
        }

        static bool IsWriteHold(LockResource resource, SessionToken session) =>
            resource.Holders.Exists(hold => hold.Owner == session
                && hold.Mode is LockMode.IntentExclusive or LockMode.SharedIntentExclusive or LockMode.Exclusive or LockMode.SchemaModification);
    }

    /// <summary>
    /// The help text <c>DBCC HELP</c> prints for each documented subcommand,
    /// verbatim as asked for in lowercase, in the order <c>DBCC HELP('?')</c>
    /// lists them.
    /// </summary>
    private static readonly (string Name, string Text)[] DbccHelpTexts =
    [
        ("checkalloc", "dbcc checkalloc \n(\n     [ { 'database_name' | database_id | 0 } ]\n     [ , NOINDEX |\n     { REPAIR_ALLOW_DATA_LOSS\n     | REPAIR_FAST\n     | REPAIR_REBUILD\n     } ]\n)\n     [ WITH\n         {\n             [ ALL_ERRORMSGS ]\n                [ , [ NO_INFOMSGS ] ]\n                [ , [ TABLOCK ] ]\n             [ , [ ESTIMATEONLY ] ]\n         }\n     ]\n"),
        ("checkcatalog", "dbcc checkcatalog \n[\n    ( { 'database_name' | database_id | 0 } )\n]\n    [ WITH NO_INFOMSGS ]\n"),
        ("checkconstraints", "dbcc checkconstraints \n[\n    ( { 'table_name' | table_id | 'constraint_name' | constraint_id } )\n]\n    [ WITH\n        { ALL_CONSTRAINTS | ALL_ERRORMSGS }\n        [ , [ NO_INFOMSGS ] ]\n    ]\n"),
        ("checkdb", "dbcc checkdb \n(\n    { 'database_name' | database_id | 0 }\n    [ , NOINDEX\n    | { REPAIR_ALLOW_DATA_LOSS\n    | REPAIR_FAST\n    | REPAIR_REBUILD\n    } ]\n)\n    [ WITH\n        {\n            [ ALL_ERRORMSGS ]\n            [ , [ NO_INFOMSGS ] ]\n            [ , [ TABLOCK ] ]\n            [ , [ ESTIMATEONLY ] ]\n            [ , [ PHYSICAL_ONLY ] ]\n            [ , [ DATA_PURITY ] ]\n            [ , [ EXTENDED_LOGICAL_CHECKS  ] ]\n            [ , [ MAXDOP = <dop> ] ]\n        }\n    ]\n"),
        ("checkfilegroup", "dbcc checkfilegroup \n(\n    [ { 'filegroup_name' | filegroup_id | 0 } ]\n    [ , NOINDEX ]\n)\n    [ WITH\n        {\n            [ ALL_ERRORMSGS ]\n            [ , [ NO_INFOMSGS ] ]\n            [ , [ TABLOCK ] ]\n            [ , [ ESTIMATEONLY ] ]\n            [ , [ MAXDOP = <dop> ] ]\n        }\n    ]\n"),
        ("checkident", "dbcc checkident \n(\n    'table_name'\n    [ , { NORESEED\n        | { RESEED [ , new_reseed_value ] }\n    } ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("checktable", "dbcc checktable \n(\n    { 'table_name' | 'view_name' }\n    [ , NOINDEX\n    | index_id\n    | { REPAIR_ALLOW_DATA_LOSS\n    | REPAIR_FAST\n    | REPAIR_REBUILD\n    } ]\n)\n    [ WITH\n        {\n            [ ALL_ERRORMSGS ]\n            [ , [ NO_INFOMSGS ] ]\n            [ , [ TABLOCK ] ]\n            [ , [ ESTIMATEONLY ] ]\n            [ , [ PHYSICAL_ONLY ] ]\n            [ , [ MAXDOP = <dop> ] ]\n            [ , [ EXTENDED_LOGICAL_CHECKS  ] ]\n        }\n    ]\n"),
        ("cleantable", "dbcc cleantable \n(\n    { 'database_name' | database_id | 0 }\n    , { 'table_name' | table_id | 'view_name' | view_id }\n    [ , batch_size ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("dbreindex", "dbcc dbreindex \n(\n    'table_name'\n    [ , 'index_name' [ , fillfactor ] ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("dropcleanbuffers", "dbcc dropcleanbuffers [([dbid] [, fileid] [, pageid] )] [ WITH NO_INFOMSGS ]\n"),
        ("flushauthcache", "dbcc flushauthcache \n"),
        ("free", "dbcc dll_name( FREE ) [ WITH NO_INFOMSGS ]\ne.g. dbcc xp_sample( FREE )\n"),
        ("freeproccache", "dbcc freeproccache \n[ ( @HANDLE | 'POOL NAME' ) ]\n[ WITH NO_INFOMSGS ]\n"),
        ("freesessioncache", "dbcc freesessioncache \n"),
        ("freesystemcache", "dbcc freesystemcache \n(\n    'ALL' [, 'POOL NAME']\n)\n    [ WITH\n        {\n            [ MARK_IN_USE_FOR_REMOVAL ]\n            [, [ NO_INFOMSGS ] ]\n        }\n    ]\n"),
        ("help", "dbcc help \n(\n    { 'dbcc_statement' | @dbcc_statement_var | '?' }\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("indexdefrag", "dbcc indexdefrag \n(\n    { 'database_name' | database_id | 0 }\n    , { 'table_name' | table_id | 'view_name' | view_id }\n    [ , { 'index_name' | index_id }\n    [ , { partition_number | 0 } ] ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("inputbuffer", "dbcc inputbuffer \n(\n    session_id [ , request_id ]\n)\n    [WITH NO_INFOMSGS ]\n"),
        ("opentran", "dbcc opentran \n[\n    ( [ { 'database_name' | database_id | 0 } ] )\n]\n    [ WITH\n        {\n            [ TABLERESULTS ]\n            [ , [ NO_INFOMSGS ]\n        }\n    ]\n"),
        ("outputbuffer", "dbcc outputbuffer \n(\n    session_id [ , request_id ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("pintable", "dbcc pintable (database_id, table_id)\n"),
        ("proccache", "dbcc proccache [ WITH NO_INFOMSGS ]\n"),
        ("show_statistics", "dbcc show_statistics \n(\n    { 'table_name' | 'view_name' }\n    , target\n)\n    [ WITH\n        {\n            [ NO_INFOMSGS ]\n            < option > [ , n ]\n        }\n    ]\n< option > ::=\n    STAT_HEADER | DENSITY_VECTOR | HISTOGRAM\n"),
        ("showcontig", "dbcc showcontig \n[ (\n    { 'table_name' | table_id | 'view_name' | view_id }\n    [ , { 'index_name' | index_id } ]\n) ]\n    [ WITH\n        {\n            [ ALL_INDEXES\n            | FAST [ , ALL_INDEXES ]\n            | TABLERESULTS [ , { ALL_INDEXES } ] ]\n            [ , { FAST | ALL_LEVELS } ]\n            [ , NO_INFOMSGS ]\n        }\n    ]\n"),
        ("shrinkdatabase", "dbcc shrinkdatabase \n(\n    { 'database_name' | database_id | 0 }\n    [ , target_percent ]\n    [ , { NOTRUNCATE | TRUNCATEONLY } ]\n)\n    [ WITH\n        {\n            [ WAIT_AT_LOW_PRIORITY\n				[(\n                  <wait_at_low_priority_option_list>\n				)]\n            ]\n            [ , NO_INFOMSGS ]\n        }\n    ]\n< wait_at_low_priority_option_list > ::=\n    <wait_at_low_priority_option>\n   | <wait_at_low_priority_option_list> , <wait_at_low_priority_option>\n< wait_at_low_priority_option > ::=\n    MAX_DURATION = { 'timeout' } [ MINUTES ]\n    | , ABORT_AFTER_WAIT = { NONE | SELF | BLOCKERS }\n"),
        ("shrinkfile", "dbcc shrinkfile \n(\n    { 'file_name' | file_id }\n    {\n        [ , EMPTYFILE]\n        | [ [, target_size ] [ , { NOTRUNCATE | TRUNCATEONLY } ] [ , database_id  ] ]\n    }\n)\n    [ WITH\n        {\n            [ WAIT_AT_LOW_PRIORITY\n				[(\n					<wait_at_low_priority_option_list>\n				)]\n            ]\n            [ , NO_INFOMSGS ]\n        }\n    ]\n< wait_at_low_priority_option_list > ::=\n    <wait_at_low_priority_option>\n   | <wait_at_low_priority_option_list> , <wait_at_low_priority_option>\n< wait_at_low_priority_option > ::=\n    MAX_DURATION = { 'timeout' } [ MINUTES ]\n    | , ABORT_AFTER_WAIT = { NONE | SELF | BLOCKERS }\n"),
        ("sqlperf", "dbcc sqlperf ( LOGSPACE ) [ WITH NO_INFOMSGS ]\n"),
        ("traceoff", "dbcc traceoff \n(\n    trace# [ , ...n ] [, -1]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("traceon", "dbcc traceon \n(\n    trace# [ , ...n ] [, -1]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("tracestatus", "dbcc tracestatus \n(\n    [ [ trace# [ , ...n ] ] [ , -1 ] ]\n)\n    [ WITH NO_INFOMSGS ]\n"),
        ("tuplemover", "dbcc tuplemover ( rowsetid [,'dbname' | dbid] )\n"),
        ("unpintable", "dbcc unpintable (dbid, table_id)\n"),
        ("updateusage", "dbcc updateusage \n(\n    { 'database_name' | database_id | 0 }\n    [ , { 'table_name' | 'view_name' | object_id }\n    [ , { 'index_name' | index_id } ] ]\n)\n    [ WITH\n        {\n            [ NO_INFOMSGS ]\n            [ , [ COUNT_ROWS ] ]\n        }\n    ]\n"),
        ("useroptions", "dbcc useroptions [ WITH NO_INFOMSGS ]\n"),
    ];

    /// <summary>
    /// <c>DBCC HELP ( 'statement' | @variable | '?' ) [WITH NO_INFOMSGS]</c>: a
    /// subcommand's syntax, or with <c>'?'</c> every documented subcommand's
    /// name, each as a message numbered 0 at line 0. A subcommand real knows but
    /// doesn't document is Msg 8987 state 1, any other word state 2. Sysadmin
    /// only (Msg 2571).
    /// </summary>
    private static List<SimulatedStatementOutcome> RunDbccHelp(BatchContext batch, DbccInvocation dbcc)
    {
        dbcc.AllowOptions(DbccOptions.NoInfoMessages);
        dbcc.RequireArgumentCount(1, 1);
        RequireDbccSysadmin(batch, "help", 1);
        var statement = dbcc.StringArgument(batch, 0) ?? throw SimulatedSqlException.DbccParameterIsIncorrect(1);
        if (statement == "?")
        {
            foreach (var (name, _) in DbccHelpTexts)
                Info(batch, HelpLine(batch, name + "\n"));
            return DbccCompleted(batch, dbcc, []);
        }
        foreach (var (name, text) in DbccHelpTexts)
        {
            if (BuiltInToken.Equals(name, statement))
            {
                // The text echoes the name as the statement wrote it.
                var prefix = $"dbcc {name}";
                Info(batch, HelpLine(batch, text.StartsWith(prefix, StringComparison.Ordinal) ? $"dbcc {statement}{text[prefix.Length..]}" : text));
                return DbccCompleted(batch, dbcc, []);
            }
        }
        Span<char> upper = stackalloc char[statement.Length];
        _ = statement.AsSpan().ToUpperInvariant(upper);
        throw SimulatedSqlException.DbccNoHelpAvailable(statement, IsUnbuiltDbccCommand(upper) || upper is "LOGINFO" or "PAGE" ? (byte)1 : (byte)2);

        static SimulatedError HelpLine(BatchContext batch, string text)
        {
            var line = batch.InfoMessage(@class: 0, state: 0, number: 0, text);
            line.LineNumber = 0;
            return line;
        }
    }
}
