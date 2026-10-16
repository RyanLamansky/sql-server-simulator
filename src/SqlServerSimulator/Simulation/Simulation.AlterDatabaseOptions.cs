using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

// The ALTER DATABASE … SET options whose values or refusals need a grammar of
// their own, beside the accept-list kinds in Simulation.Alter.cs. Every shape
// and refusal here was probed 2026-10-09 against SQL Server 2025.
partial class Simulation
{
    /// <summary>
    /// Raises real's refusal of an ON / OFF switch a system database pins:
    /// HONOR_BROKER_PRIORITY in <c>master</c> and <c>model</c> (followed by
    /// Msg 5069), MIXED_PAGE_ALLOCATION in all four, SUPPLEMENTAL_LOGGING in
    /// <c>master</c> and <c>tempdb</c> — each Msg 5058 at a state of its own.
    /// </summary>
    private static void RejectPinnedSwitch(Database target, string name)
    {
        var master = BuiltInToken.Equals(target.Name, MasterDatabaseName);
        var tempdb = BuiltInToken.Equals(target.Name, TempdbDatabaseName);
        var model = BuiltInToken.Equals(target.Name, ModelDatabaseName);
        var msdb = BuiltInToken.Equals(target.Name, MsdbDatabaseName);
        if (BuiltInToken.Equals(name, "HONOR_BROKER_PRIORITY") && (master || model))
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.OptionCannotBeSetInDatabase("HONOR_BROKER_PRIORITY", target.Name, 10));
        if (BuiltInToken.Equals(name, "MIXED_PAGE_ALLOCATION") && (master || tempdb || model || msdb))
            throw SimulatedSqlException.OptionCannotBeSetInDatabase("MIXED_PAGE_ALLOCATION", target.Name, 9);
        if (BuiltInToken.Equals(name, "SUPPLEMENTAL_LOGGING") && (master || tempdb))
            throw SimulatedSqlException.OptionCannotBeSetInDatabase("SUPPLEMENTAL_LOGGING", target.Name, master ? (byte)2 : (byte)1);
    }

    /// <summary>
    /// <c>ONLINE | OFFLINE | EMERGENCY</c>, each with an optional termination
    /// clause, moving <see cref="Database.State"/>; <c>master</c> and
    /// <c>tempdb</c> refuse each as they refuse READ_ONLY. A move waits for
    /// every other session using the database to leave it, <c>NO_WAIT</c>
    /// refuses at once (Msg 5070), and <c>ROLLBACK</c> ends the work in the
    /// way — every other session going offline, only those holding a
    /// transaction going into emergency — announced by Msg 5060 twice. A
    /// session taking its own database offline lands in <c>master</c> with Msg
    /// 5068. Probed 2026-10-10 against SQL Server 2025.
    /// </summary>
    private static bool ConsumeDatabaseStateOption(ParserContext context, Database target, string name)
    {
        if (!TryConsumeTerminationClause(context, out var termination, out var rollbackAfterSeconds))
            return false;
        var batch = context.Batch;
        if (batch.IsSkipping)
            return true;
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        var requested = upper switch
        {
            "EMERGENCY" => DatabaseState.Emergency,
            "OFFLINE" => DatabaseState.Offline,
            _ => DatabaseState.Online,
        };
        if (BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, TempdbDatabaseName))
            throw SimulatedSqlException.OptionCannotBeSetInDatabase(name.ToUpperInvariant(), target.Name);
        // Asking for the state the database is in still logs, and restarts nothing.
        LogDatabaseOption(batch, target, name.ToUpperInvariant(), "ON");
        if (target.State == requested)
            return true;

        var connection = batch.Connection;
        switch (termination)
        {
            case TerminationClause.NoWait:
                if (OtherSessionsUsing(connection, target).Count != 0)
                    throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.DatabaseStateInUse(target.Name));
                break;
            case TerminationClause.Rollback:
                // Emergency ends only the sessions holding a transaction.
                RollBackSessionsInTheWay(batch, target, rollbackAfterSeconds,
                    () => OtherSessionsUsing(connection, target),
                    requested == DatabaseState.Emergency ? static other => other.CurrentTransaction is not null : static _ => true);
                break;
            default:
                _ = WaitForOtherSessionsToLeave(batch, target, timeout: null);
                break;
        }

        target.State = requested;
        // A cached plan read the database while it was open.
        _ = Interlocked.Increment(ref connection.Simulation.SchemaVersion);
        if (requested != DatabaseState.Offline)
        {
            LogDatabaseStartup(batch, target);
            if (requested == DatabaseState.Emergency)
                LogDatabaseNotRecovered(batch, target, "EMERGENCY_MODE");
        }
        if (requested == DatabaseState.Offline && ReferenceEquals(connection.CurrentDatabase, target))
        {
            SwitchDatabase(connection, MasterDatabaseName);
            connection.PendingMessages.Enqueue(SimulatedSqlException.CurrentDatabaseSwitchedToMasterMessage(batch));
        }
        return true;
    }

    /// <summary>
    /// <c>SINGLE_USER | RESTRICTED_USER | MULTI_USER</c>, each with an optional
    /// termination clause, recorded on <see cref="Database.UserAccess"/>.
    /// Moving to <c>SINGLE_USER</c> or <c>RESTRICTED_USER</c> waits for every
    /// other session using the database to leave it, a sysadmin's included,
    /// <c>NO_WAIT</c> refusing at once (Msg 5070) and <c>ROLLBACK</c> ending
    /// them, announced by Msg 5060 twice, as the state options do (probed
    /// 2026-10-11 against SQL Server 2022 and 2025).
    /// </summary>
    private static bool ConsumeAccessModeOption(ParserContext context, Database target, string name)
    {
        if (!TryConsumeTerminationClause(context, out var termination, out var rollbackAfterSeconds))
            return false;
        var batch = context.Batch;
        if (batch.IsSkipping)
            return true;
        var access = BuiltInToken.Equals(name, "RESTRICTED_USER") ? (byte)2
            : BuiltInToken.Equals(name, "SINGLE_USER") ? (byte)1
            : (byte)0;
        var connection = batch.Connection;
        // Restricting a database another session holds single-user is the
        // single user's, as taking it to MULTI_USER is (below).
        if (access == 1 || (access == 2 && target.SingleUserOccupant(connection) is null))
        {
            switch (termination)
            {
                case TerminationClause.NoWait:
                    if (SessionsHolding(connection, target).Count != 0)
                        throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.DatabaseStateInUse(target.Name));
                    break;
                case TerminationClause.Rollback:
                    RollBackSessionsInTheWay(batch, target, rollbackAfterSeconds, () => SessionsHolding(connection, target), static _ => true);
                    break;
                default:
                    _ = WaitForOtherSessionsToLeave(batch, target, timeout: null, singleUserToo: true);
                    break;
            }
            target.UserAccess = access;
            Volatile.Write(ref target.SingleUserHolder, access == 1 ? connection.Session : null);
            connection.Simulation.NoteDatabaseAdmissionChanged();
            LogDatabaseOption(batch, target, name.ToUpperInvariant(), "ON");
            return true;
        }

        // Only the single user may take the database out of single-user mode;
        // anyone else waits about eight seconds for it to leave, then is
        // refused (probed 2026-10-11 against SQL Server 2022 and 2025).
        if (target.SingleUserOccupant(connection) is not null)
        {
            var deadline = Environment.TickCount64 + SingleUserChangeWaitMillis;
            while (target.SingleUserOccupant(connection) is not null)
            {
                if (Environment.TickCount64 >= deadline)
                    throw SimulatedSqlException.SingleUserDatabaseInUse(target.Name);
                batch.PollCancellation();
                Thread.Sleep(5);
            }
        }
        target.UserAccess = access;
        Volatile.Write(ref target.SingleUserHolder, null);
        connection.Simulation.NoteDatabaseAdmissionChanged();
        LogDatabaseOption(batch, target, name.ToUpperInvariant(), "ON");
        return true;
    }

    /// <summary>How long a session other than a single-user database's single user waits to change its mode before Msg 5064.</summary>
    private const int SingleUserChangeWaitMillis = 8000;

    /// <summary>
    /// How long an <c>ALTER DATABASE … WITH ROLLBACK</c> that found a session
    /// in its way holds the database in transition once it has ended them:
    /// about three seconds, whatever the number of sessions, whether they held
    /// a transaction, and after the <c>AFTER n</c> wait (probed 2026-10-11
    /// against SQL Server 2022 and 2025, for SINGLE_USER, RESTRICTED_USER,
    /// READ_ONLY and OFFLINE alike). Internal so a test can shorten it.
    /// </summary>
    internal int RollbackSettleMillis = 3000;

    /// <summary>
    /// The <c>ROLLBACK IMMEDIATE | AFTER n</c> termination clause: when
    /// <paramref name="inTheWay"/> names a session <paramref name="ends"/>
    /// would end, holds <paramref name="database"/> in transition
    /// (<see cref="Database.TransitionConnection"/>) from the start — through
    /// the whole <c>AFTER n</c> wait, which a session leaving on its own
    /// doesn't cut short — ends the sessions still in the way, announced by
    /// Msg 5060 twice, then settles for <see cref="RollbackSettleMillis"/>
    /// before the change lands (probed 2026-10-11 against SQL Server 2025:
    /// <c>AFTER 2 SECONDS</c> takes five seconds whether or not its one session
    /// leaves after half of one). Finding no one costs nothing. A restore
    /// harness's application pool meets the window as fast login failures,
    /// which is what keeps its reconnects out of the restore behind. A cancel or
    /// <c>CommandTimeout</c> ends the waits.
    /// </summary>
    private static void RollBackSessionsInTheWay(BatchContext batch, Database database, int afterSeconds, Func<List<SimulatedDbConnection>> inTheWay, Func<SimulatedDbConnection, bool> ends)
    {
        var connection = batch.Connection;
        var found = inTheWay();
        if (found.Count == 0)
            return;
        if (!found.Exists(other => ends(other)))
        {
            connection.PendingMessages.Enqueue(SimulatedSqlException.NonqualifiedTransactionsRolledBackMessage(batch, 0));
            connection.PendingMessages.Enqueue(SimulatedSqlException.NonqualifiedTransactionsRolledBackMessage(batch, 100));
            return;
        }
        database.TransitionConnection = connection;
        connection.Simulation.NoteDatabaseAdmissionChanged();
        try
        {
            Pause(batch, afterSeconds * 1000L);
            var others = inTheWay();
            if (others.Count != 0)
            {
                connection.PendingMessages.Enqueue(SimulatedSqlException.NonqualifiedTransactionsRolledBackMessage(batch, 0));
                foreach (var other in others)
                {
                    if (ends(other))
                        other.Kill();
                }
                connection.PendingMessages.Enqueue(SimulatedSqlException.NonqualifiedTransactionsRolledBackMessage(batch, 100));
            }
            Pause(batch, connection.Simulation.RollbackSettleMillis);
        }
        finally
        {
            database.TransitionConnection = null;
            connection.Simulation.NoteDatabaseAdmissionChanged();
        }
    }

    private static void Pause(BatchContext batch, long millis)
    {
        var deadline = Environment.TickCount64 + millis;
        while (Environment.TickCount64 < deadline)
        {
            batch.PollCancellation();
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// <see cref="OtherSessionsUsing"/>, plus the session holding the
    /// database's single-user slot when another one does, wherever it is.
    /// </summary>
    private static List<SimulatedDbConnection> SessionsHolding(SimulatedDbConnection connection, Database database)
    {
        var others = OtherSessionsUsing(connection, database);
        if (database.SingleUserOccupant(connection) is { } holder && !others.Contains(holder))
            others.Add(holder);
        return others;
    }

    /// <summary>
    /// The other open sessions using <paramref name="database"/>: as their
    /// current database, the database of a module they are running inside, or
    /// one their transaction touched — the sessions <c>sys.dm_tran_locks</c>
    /// lists a <c>DATABASE</c> lock for.
    /// </summary>
    private static List<SimulatedDbConnection> OtherSessionsUsing(SimulatedDbConnection connection, Database database)
    {
        var others = new List<SimulatedDbConnection>();
        foreach (var other in connection.Simulation.SnapshotConnections())
        {
            if (ReferenceEquals(other, connection) || other.State != System.Data.ConnectionState.Open)
                continue;
            if (ReferenceEquals(other.CurrentDatabase, database)
                || Array.IndexOf(other.EnclosingDatabases, database) >= 0
                || (other.CurrentTransaction is { } transaction && Array.IndexOf(transaction.TouchedDatabases, database) >= 0))
            {
                others.Add(other);
            }
        }
        return others;
    }

    /// <summary>
    /// Waits, past any <c>LOCK_TIMEOUT</c> as real does, until no other
    /// session uses <paramref name="database"/> — nor, with
    /// <paramref name="singleUserToo"/>, holds its single-user slot — or
    /// <paramref name="timeout"/> passes; a cancel or <c>CommandTimeout</c>
    /// ends the wait. True when they all left.
    /// </summary>
    private static bool WaitForOtherSessionsToLeave(BatchContext batch, Database database, TimeSpan? timeout, bool singleUserToo = false)
    {
        var deadline = timeout is { } limit ? Environment.TickCount64 + (long)limit.TotalMilliseconds : long.MaxValue;
        while ((singleUserToo ? SessionsHolding(batch.Connection, database) : OtherSessionsUsing(batch.Connection, database)).Count != 0)
        {
            if (Environment.TickCount64 >= deadline)
                return false;
            batch.PollCancellation();
            Thread.Sleep(5);
        }
        return true;
    }

    /// <summary>Reads the value grammar of each <see cref="AlterDatabaseOptionKind.Special"/> option, the cursor on its name.</summary>
    private static bool ConsumeSpecialDatabaseOption(ParserContext context, Database target, string name)
    {
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "AUTOMATIC_TUNING" => ConsumeAutomaticTuning(context, target),
            "CONTAINMENT" => ConsumeContainment(context, target),
            "DELAYED_DURABILITY" => ConsumeDelayedDurability(context, target),
            "ENCRYPTION" => ConsumeEncryption(context),
            "FILESTREAM" => ConsumeFileStream(context, target),
            "REMOTE_DATA_ARCHIVE" => ConsumeRemoteDataArchive(context),
            "SUSPEND_FOR_SNAPSHOT_BACKUP" => ConsumeSuspendForSnapshotBackup(context, target),
            _ => ConsumeContainedDatabaseOption(context, name),
        };
    }

    /// <summary>
    /// <c>= AUTO | CUSTOM</c> or <c>( FORCE_LAST_GOOD_PLAN = { ON | OFF | DEFAULT } [, …] )</c>
    /// — parsed and discarded, there being no plan regressions to correct.
    /// <c>INHERIT</c> and every other tuning option are Azure SQL Database's,
    /// so they are Msg 102; <c>master</c> and <c>tempdb</c> refuse tuning
    /// with Msg 15702. The <c>=</c> form is logged, the option list isn't
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool ConsumeAutomaticTuning(ParserContext context, Database target)
    {
        string? mode = null;
        switch (context.GetNextRequired())
        {
            case Operator { Character: '=' }:
                if (!IsBareWord(context.GetNextRequired(), "AUTO") && !IsBareWord(context.Token, "CUSTOM"))
                    return false;
                mode = IsBareWord(context.Token, "AUTO") ? "auto" : "custom";
                break;
            case Operator { Character: '(' }:
                do
                {
                    if (!IsBareWord(context.GetNextRequired(), "FORCE_LAST_GOOD_PLAN")
                        || context.GetNextRequired() is not Operator { Character: '=' }
                        || context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off or Keyword.Default })
                    {
                        return false;
                    }
                }
                while (context.GetNextRequired() is Operator { Character: ',' });
                if (context.Token is not Operator { Character: ')' })
                    return false;
                break;
            default:
                return false;
        }
        if (!context.Batch.IsSkipping && BuiltInToken.EqualsAny(target.Name, MasterDatabaseName, TempdbDatabaseName))
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.AutomaticTuningOnSystemDatabase(target.Name));
        if (!context.Batch.IsSkipping && mode is not null)
            LogDatabaseOption(context.Batch, target, "automatic_tuning", mode);
        return true;
    }

    /// <summary>
    /// <c>= NONE | PARTIAL</c>. Every database is non-contained, so NONE
    /// changes nothing; PARTIAL needs the <c>contained database
    /// authentication</c> server option (Msg 12824), past which contained
    /// databases aren't modeled.
    /// </summary>
    private static bool ConsumeContainment(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;
        var partial = IsBareWord(context.GetNextRequired(), "PARTIAL");
        if (!partial && !IsBareWord(context.Token, "NONE"))
            return false;
        if (partial && !context.Batch.IsSkipping)
        {
            if (context.Batch.Connection.Simulation.ConfigurationInUse(ContainedDatabaseAuthenticationConfigurationId) == 0)
                throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.ContainedDatabaseAuthenticationRequired());
            throw new NotSupportedException("The simulator does not model contained databases.");
        }
        // Real's line names the option CONTAINED.
        if (!context.Batch.IsSkipping)
            LogDatabaseOption(context.Batch, target, "contained", "none");
        return true;
    }

    /// <summary><c>= DISABLED | ALLOWED | FORCED</c>, recorded on <see cref="Database.DelayedDurability"/>.</summary>
    private static bool ConsumeDelayedDurability(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;
        var token = context.GetNextRequired();
        byte? setting = IsBareWord(token, "DISABLED") ? 0 : IsBareWord(token, "ALLOWED") ? 1 : IsBareWord(token, "FORCED") ? 2 : null;
        if (setting is null)
            return false;
        if (!context.Batch.IsSkipping)
        {
            target.DelayedDurability = setting.Value;
            LogDatabaseOption(context.Batch, target, "delayed_durability", setting.Value switch { 0 => "disabled", 1 => "allowed", _ => "forced" });
        }
        return true;
    }

    /// <summary>
    /// <c>ON | OFF | SUSPEND | RESUME</c>: no database encryption key can
    /// exist here, so every one is real's refusal without one (Msg 33106).
    /// </summary>
    private static bool ConsumeEncryption(ParserContext context)
    {
        var token = context.GetNextRequired();
        if (token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } && !IsBareWord(token, "SUSPEND") && !IsBareWord(token, "RESUME"))
            return false;
        if (!context.Batch.IsSkipping)
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.NoDatabaseEncryptionKey());
        return true;
    }

    /// <summary>
    /// <c>( NON_TRANSACTED_ACCESS = { OFF | READ_ONLY | FULL } | DIRECTORY_NAME = 'name' [, …] )</c>,
    /// recorded for <c>sys.database_filestream_options</c>; a system database
    /// refuses it (Msg 33401).
    /// </summary>
    private static bool ConsumeFileStream(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            return false;
        byte? access = null;
        string? directory = null;
        var directoryGiven = false;
        // Each entry as real logs it, in the order written.
        var logged = new List<(string Option, string Value)>(2);
        do
        {
            var option = context.GetNextRequired();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                return false;
            var value = context.GetNextRequired();
            if (IsBareWord(option, "NON_TRANSACTED_ACCESS"))
            {
                access = value is ReservedKeyword { Keyword: Keyword.Off } ? 0
                    : IsBareWord(value, "READ_ONLY") ? 1
                    : value is ReservedKeyword { Keyword: Keyword.Full } ? 2
                    : null;
                if (access is null)
                    return false;
                logged.Add(("NON_TRANSACTED_ACCESS", access switch { 0 => "OFF", 1 => "READ_ONLY", _ => "FULL" }));
            }
            else if (IsBareWord(option, "DIRECTORY_NAME"))
            {
                if (value is not Literal { Value: { IsNull: false } literal })
                    return false;
                directory = literal.AsString;
                directoryGiven = true;
                logged.Add(("DIRECTORY_NAME", directory));
            }
            else
            {
                return false;
            }
        }
        while (context.GetNextRequired() is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            return false;
        if (context.Batch.IsSkipping)
            return true;
        if (IsSystemDatabaseName(target.Name))
            throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.FileStreamOptionsOnSystemDatabase(target.Name));
        if (access is not null)
            target.FileStreamNonTransactedAccess = access.Value;
        if (directoryGiven)
            target.FileStreamDirectoryName = directory;
        foreach (var (option, value) in logged)
            LogDatabaseOption(context.Batch, target, "FILESTREAM " + option, value);
        return true;
    }

    /// <summary>
    /// <c>= OFF</c>, which a database never stretched to Azure takes as it
    /// stands, or <c>= ON ( SERVER = …, { CREDENTIAL = … | FEDERATED_SERVICE_ACCOUNT = ON } )</c>,
    /// which without both parts is Msg 10770 and with them reaches Stretch
    /// Database, which isn't modeled.
    /// </summary>
    private static bool ConsumeRemoteDataArchive(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' })
            return false;
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Off })
            return true;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On } || context.GetNextRequired() is not Operator { Character: '(' })
            return false;
        var server = false;
        var credential = false;
        do
        {
            var option = context.GetNextRequired();
            if (context.GetNextRequired() is not Operator { Character: '=' })
                return false;
            var value = context.GetNextRequired();
            server |= IsBareWord(option, "SERVER");
            credential |= IsBareWord(option, "CREDENTIAL") || (IsBareWord(option, "FEDERATED_SERVICE_ACCOUNT") && value is ReservedKeyword { Keyword: Keyword.On });
        }
        while (context.GetNextRequired() is Operator { Character: ',' });
        if (context.Token is not Operator { Character: ')' })
            return false;
        if (!server || !credential)
            throw SimulatedSqlException.RemoteDataArchiveOptionsRequired();
        if (!context.Batch.IsSkipping)
            throw new NotSupportedException("The simulator does not model Stretch Database (REMOTE_DATA_ARCHIVE).");
        return true;
    }

    /// <summary>
    /// <c>= OFF</c>, which on a database never suspended is Msg 3082's notice
    /// alone, or <c>= ON</c>, a snapshot backup's I/O freeze, which isn't
    /// modeled.
    /// </summary>
    private static bool ConsumeSuspendForSnapshotBackup(ParserContext context, Database target)
    {
        if (context.GetNextRequired() is not Operator { Character: '=' }
            || context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } toggle)
        {
            return false;
        }
        var batch = context.Batch;
        if (batch.IsSkipping)
            return true;
        if (toggle.Keyword == Keyword.On)
            throw new NotSupportedException("The simulator does not model SUSPEND_FOR_SNAPSHOT_BACKUP.");
        batch.Connection.PendingMessages.Enqueue(SimulatedSqlException.NotSuspendedForSnapshotBackupMessage(batch, target.Name));
        return true;
    }

    /// <summary>
    /// The options only a contained database takes — DEFAULT_LANGUAGE,
    /// DEFAULT_FULLTEXT_LANGUAGE, NESTED_TRIGGERS, TRANSFORM_NOISE_WORDS and
    /// TWO_DIGIT_YEAR_CUTOFF — each Msg 12807 on every database here, after
    /// TWO_DIGIT_YEAR_CUTOFF's own range check (Msg 190 outside 1753–9999).
    /// A value of the wrong shape is Msg 102 near the option's name.
    /// </summary>
    private static bool ConsumeContainedDatabaseOption(ParserContext context, string name)
    {
        var atName = context.SaveCheckpoint();
        var value = context.GetNextRequired() is Operator { Character: '=' } ? context.GetNextRequired() : null;
        var cutoff = BuiltInToken.Equals(name, "TWO_DIGIT_YEAR_CUTOFF");
        var shapeFits = value switch
        {
            Numeric { Value.IsNull: false } => !BuiltInToken.Equals(name, "NESTED_TRIGGERS") && !BuiltInToken.Equals(name, "TRANSFORM_NOISE_WORDS"),
            ReservedKeyword { Keyword: Keyword.On or Keyword.Off } => BuiltInToken.Equals(name, "NESTED_TRIGGERS") || BuiltInToken.Equals(name, "TRANSFORM_NOISE_WORDS"),
            Name => BuiltInToken.Equals(name, "DEFAULT_LANGUAGE") || BuiltInToken.Equals(name, "DEFAULT_FULLTEXT_LANGUAGE"),
            _ => false,
        };
        if (!shapeFits)
        {
            context.RestoreCheckpoint(atName);
            return false;
        }
        if (context.Batch.IsSkipping)
            return true;
        if (cutoff && value is Numeric { Value: var year } && year.AsInt32 is < 1753 or > 9999)
            throw SimulatedSqlException.InvalidDateOrTimeInStatement();
        Span<char> upper = stackalloc char[name.Length];
        _ = name.AsSpan().ToUpperInvariant(upper);
        var spelled = upper switch
        {
            "DEFAULT_FULLTEXT_LANGUAGE" => "default_fulltext_language",
            "DEFAULT_LANGUAGE" => "default_language",
            "NESTED_TRIGGERS" => "nested_triggers",
            "TRANSFORM_NOISE_WORDS" => "transform_noise_words",
            _ => "two_digit_year_cutoff",
        };
        throw SimulatedSqlException.FollowedByAlterDatabaseFailed(SimulatedSqlException.OptionNeedsContainedDatabase(spelled));
    }

    private static bool IsSystemDatabaseName(string name) =>
        BuiltInToken.EqualsAny(name, MasterDatabaseName, TempdbDatabaseName) || BuiltInToken.EqualsAny(name, ModelDatabaseName, MsdbDatabaseName);
}
