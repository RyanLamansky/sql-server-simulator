using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 1701: the schema declares a fixed-length row
    /// that cannot ever fit within the per-row size limit (8060 bytes).
    /// </summary>
    internal static SimulatedSqlException RowSizeExceedsMaximum(string tableName, int requested, int max) =>
        new($"Cannot create the table '{tableName}' because the row size ({requested} bytes) exceeds the maximum allowable table row size ({max} bytes).", 1701, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15048: the integer supplied to
    /// <c>SET COMPATIBILITY_LEVEL</c> is not one of the supported values.
    /// </summary>
    /// <remarks>
    /// The valid-values list and exact phrasing are version-dependent: SQL
    /// Server periodically drops the oldest legacy modes as new releases
    /// add a higher level. The text here was validated against SQL Server
    /// 2025 (compatibility level 170); future releases may need the list
    /// updated to drop earlier values.
    /// </remarks>
    internal static SimulatedSqlException InvalidCompatibilityLevel() =>
        new($"Valid values of the database compatibility level are 100, 110, 120, 130, 140, 150, 160 or 170.", 15048, 16, 1);

    internal static SimulatedSqlException ThereIsAlreadyAnObject(string name) => new($"There is already an object named '{name}' in the database.", 2714, 16, 6);

    /// <summary>
    /// Mimics SQL Server error 2010: an <c>ALTER</c> (or the ALTER leg of a
    /// <c>CREATE OR ALTER</c>) names an existing object whose kind differs
    /// from the statement's own verb — <c>ALTER VIEW</c> over a table,
    /// <c>ALTER FUNCTION</c> over a procedure, or an <c>ALTER FUNCTION</c>
    /// that would change the function's own kind (scalar ↔ inline TVF ↔
    /// multi-statement TVF). Probe-confirmed against SQL Server 2025
    /// (2026-07-31): Class 16 State 1, and the name echoes what the statement
    /// wrote (an unqualified reference stays unqualified; brackets are
    /// stripped).
    /// </summary>
    internal static SimulatedSqlException CannotAlterIncompatibleObjectType(MultiPartName name) =>
        new($"Cannot perform alter on '{name}' because it is an incompatible object type.", 2010, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 166: a <c>CREATE</c> / <c>ALTER</c> /
    /// <c>CREATE OR ALTER</c> of a programmable module carries a
    /// database-name prefix. <paramref name="moduleKind"/> is the bare kind
    /// keyword (<c>VIEW</c> / <c>FUNCTION</c> / <c>PROCEDURE</c> /
    /// <c>TRIGGER</c>) — real always names the statement in the combined
    /// <c>'CREATE/ALTER X'</c> form regardless of which verb was written, and
    /// rejects a prefix naming the current database as readily as any other
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ModuleNameMayNotBeDatabaseQualified(string moduleKind) =>
        new($"'CREATE/ALTER {moduleKind}' does not allow specifying the database name as a prefix to the object name.", 166, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 117: an object name carries more prefixes than
    /// the statement allows — a four-part (server-qualified) name where at
    /// most <c>database.schema</c> is legal.
    /// </summary>
    internal static SimulatedSqlException TooManyNamePrefixes(MultiPartName name, int maximum) =>
        new($"The object name '{name}' contains more than the maximum number of prefixes. The maximum is {maximum}.", 117, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 7202: an <c>OPENQUERY</c> (or four-part name)
    /// references a linked-server name that isn't registered in
    /// <c>sys.servers</c>. Wording probe-confirmed against SQL Server 2025;
    /// Class 11 State 2.
    /// </summary>
    internal static SimulatedSqlException LinkedServerNotFound(string serverName) =>
        new($"Could not find server '{serverName}' in sys.servers. Verify that the correct server name was specified. If necessary, execute the stored procedure sp_addlinkedserver to add the server to sys.servers.", 7202, 11, 2);

    /// <summary>
    /// Mimics SQL Server error 911: <c>USE &lt;db&gt;</c> targets a database
    /// that doesn't exist in this <see cref="Simulation"/>. Wording
    /// probe-confirmed against SQL Server 2025: literal database name in
    /// single quotes, full sentence with the "Make sure that the name is
    /// entered correctly." suffix. Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException DatabaseDoesNotExist(string databaseName) =>
        new($"Database '{databaseName}' does not exist. Make sure that the name is entered correctly.", 911, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5011: <c>ALTER DATABASE &lt;name&gt;</c> names a
    /// database this <see cref="Simulation"/> doesn't host. Wording
    /// probe-confirmed against SQL Server 2025 (severity 14, state 5), where a
    /// trailing Msg 5069 (<c>ALTER DATABASE statement failed.</c>) follows it —
    /// the simulator surfaces the informative first error alone, the same
    /// stance it takes on the Msg 300 / 297 pair.
    /// </summary>
    internal static SimulatedSqlException CannotAlterDatabase(string databaseName) =>
        new($"User does not have permission to alter database '{databaseName}', the database does not exist, or the database is not in a state that allows access checks.", 5011, 14, 5);

    /// <summary>
    /// Mimics SQL Server error 15309: <c>ALTER DATABASE … SET TRUSTWORTHY</c>
    /// names <c>model</c> or <c>tempdb</c>, whose trustworthy state real pins
    /// (<c>master</c> and <c>msdb</c> both accept the option). Probe-confirmed
    /// against SQL Server 2025 — class 16 state 1, with a trailing Msg 5069 the
    /// simulator omits like every other ALTER DATABASE failure.
    /// </summary>
    internal static SimulatedSqlException CannotAlterTrustworthyState() =>
        new("Cannot alter the trustworthy state of the model or tempdb databases.", 15309, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5600: <c>ALTER DATABASE … SET DB_CHAINING</c>
    /// names <c>master</c>, <c>model</c> or <c>tempdb</c>, whose chaining state
    /// real pins on (<c>msdb</c>, also chained by default, accepts the option).
    /// Probe-confirmed against SQL Server 2025 — class 16 state 2, raised for
    /// either value asked for, with a trailing Msg 5069 the simulator omits.
    /// </summary>
    internal static SimulatedSqlException CannotSetCrossDatabaseChaining() =>
        new("The Cross Database Chaining option cannot be set to the specified value on the specified database.", 5600, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 3906: a write reached a database whose access
    /// mode is <c>READ_ONLY</c>. Probe-confirmed against SQL Server 2025
    /// (2026-08-04) — class 16 state 1, with the database name in double
    /// quotes, and the identical wording for DML and DDL alike. Real names the
    /// database that would have been written, so a three-part write out of
    /// another session database reports the target's name.
    /// </summary>
    /// <remarks>
    /// Real varies the state at a few sites (<c>ALTER TABLE</c> reports state
    /// 12) and follows the <c>ALTER DATABASE</c> refusal with a trailing Msg
    /// 5069, both of which the simulator flattens the way it flattens the other
    /// ALTER DATABASE failures above.
    /// </remarks>
    internal static SimulatedSqlException DatabaseIsReadOnly(string databaseName) =>
        new($"Failed to update database \"{databaseName}\" because the database is read-only.", 3906, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5058: <c>ALTER DATABASE … SET</c> names an
    /// option that database pins — <c>READ_ONLY</c> / <c>READ_WRITE</c> on
    /// <c>master</c> or <c>tempdb</c>. Probe-confirmed against SQL Server 2025
    /// (2026-08-04): class 16, state <strong>5</strong> for <c>master</c> and
    /// <strong>4</strong> for <c>tempdb</c>, echoing the option asked for.
    /// <c>model</c> and <c>msdb</c> both accept it.
    /// </summary>
    internal static SimulatedSqlException OptionCannotBeSetInDatabase(string option, string databaseName) =>
        new($"Option '{option}' cannot be set in database '{databaseName}'.", 5058, 16,
            Collation.Baseline.Equals(databaseName, Simulation.TempdbDatabaseName) ? (byte)4 : (byte)5);

    /// <summary>
    /// Mimics SQL Server error 15664: a <c>sp_set_session_context</c> call
    /// targeted a key previously set with <c>@read_only = 1</c> in this
    /// session. Wording probe-confirmed against SQL Server 2025. Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException SessionContextKeyIsReadOnly(string key) =>
        new($"Cannot set key '{key}' in the session context. The key has been set as read_only for this session.", 15664, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 225: the parameters supplied to a system
    /// procedure are not valid — raised here for a NULL / missing <c>@key</c>
    /// to <c>sp_set_session_context</c>. Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException InvalidProcedureParameters(string procedureName) =>
        new($"The parameters supplied for the procedure \"{procedureName}\" are not valid.", 225, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2520: <c>DBCC SHRINKDATABASE(&lt;name&gt;)</c>
    /// names a database not present in this <see cref="Simulation"/>. Distinct
    /// from the <c>USE</c> path's Msg 911 — DBCC reports its own wording.
    /// Probe-confirmed against SQL Server 2025: Class 16 State 12, the
    /// "querying the sys.databases catalog view" suffix.
    /// </summary>
    internal static SimulatedSqlException CouldNotFindDatabase(string databaseName) =>
        new($"Could not find database '{databaseName}'. The database either does not exist, or was dropped before a statement tried to use it. Verify if the database exists by querying the sys.databases catalog view.", 2520, 16, 12);

    /// <summary>
    /// Mimics SQL Server error 2501: <c>DBCC SHOW_STATISTICS</c> could not
    /// resolve its first argument to a table or object. Probe-confirmed against
    /// SQL Server 2025: Class 16 State 45, and the name echoes the caller's raw
    /// (bracketed) input verbatim inside double quotes.
    /// </summary>
    internal static SimulatedSqlException CannotFindTableOrObject(string name) =>
        new($"Cannot find a table or object with the name \"{name}\". Check the system catalog.", 2501, 16, 45);

    /// <summary>
    /// Mimics SQL Server error 2767: <c>DBCC SHOW_STATISTICS</c> resolved the
    /// table but the named statistic isn't backed by any index on it. Probe-
    /// confirmed against SQL Server 2025: Class 16 State 1.
    /// </summary>
    internal static SimulatedSqlException CouldNotLocateStatistics(string statisticsName) =>
        new($"Could not locate statistics '{statisticsName}' in the system catalogs.", 2767, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2560: a <c>DBCC</c> statement's first parameter
    /// is missing or NULL. Probe-confirmed against SQL Server 2025 for
    /// <c>DBCC SHOW_STATISTICS(NULL, NULL)</c>: Class 16 State 9.
    /// </summary>
    internal static SimulatedSqlException DbccParameterIsIncorrect(int parameterNumber) =>
        new($"Parameter {parameterNumber} is incorrect for this DBCC statement.", 2560, 16, 9);

    /// <summary>
    /// Mimics SQL Server error 2760: a statement referenced a schema that
    /// doesn't exist (or whose principal the caller can't access). Probe-
    /// confirmed wording — real SQL Server uses the same Msg / wording for
    /// <c>CREATE TABLE missingschema.t</c>, <c>CREATE SCHEMA dbo</c> (when
    /// targeting a built-in / reserved schema), and a few other lookups.
    /// </summary>
    internal static SimulatedSqlException SpecifiedSchemaNameDoesNotExist(string schemaName) =>
        new($"The specified schema name \"{schemaName}\" either does not exist or you do not have permission to use it.", 2760, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2705: <c>SELECT … INTO target</c> produced a
    /// projection with two columns of the same name. Wording probe-confirmed
    /// against SQL Server 2025: the table name is the SELECT INTO target
    /// (not yet created at this point — the error fires during the schema
    /// inference walk, before the heap table is allocated).
    /// </summary>
    internal static SimulatedSqlException DuplicateColumnInSelectInto(string columnName, string targetTableName) =>
        new($"Column names in each table must be unique. Column name '{columnName}' in table '{targetTableName}' is specified more than once.", 2705, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 1038: a <c>SELECT INTO</c> projection
    /// contains an unnamed column (an arithmetic expression / function call
    /// without an explicit <c>AS alias</c>). Regular SELECTs allow unnamed
    /// columns (the column is just rendered with an empty header); SELECT
    /// INTO needs a name to declare on the destination table. Verbatim
    /// wording from SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException SelectIntoMissingColumnName() =>
        new("An object or column name is missing or empty. For SELECT INTO statements, verify each column has a name. For other statements, look for empty alias names. Aliases defined as \"\" or [] are not allowed. Change the alias to a valid name.", 1038, 15, 5);

    /// <summary>
    /// Mimics SQL Server error 3701: <c>DROP TABLE</c> targeted a name that
    /// doesn't exist. Real SQL Server suppresses this when <c>IF EXISTS</c>
    /// is present; callers should branch on the parsed flag and only construct
    /// this when the table is genuinely missing. Wording probe-confirmed
    /// against SQL Server 2025 for both regular and temp table targets
    /// (the name is shown verbatim, so <c>#foo</c> appears with its hash).
    /// </summary>
    internal static SimulatedSqlException CannotDropTableDoesNotExist(string name) =>
        new($"Cannot drop the table '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 1801: <c>CREATE DATABASE</c> named a database
    /// that already exists. Probe-confirmed verbatim wording (Class 16,
    /// State 3) against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException DatabaseAlreadyExists(string name) =>
        new($"Database '{name}' already exists. Choose a different database name.", 1801, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>database</c> wording variant:
    /// <c>DROP DATABASE</c> named a database that doesn't exist (and
    /// <c>IF EXISTS</c> was absent). Distinct Class / State from the DROP TABLE
    /// 3701 variant — probe-confirmed Class 11, State 1 against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropDatabaseNotFound(string name) =>
        new($"Cannot drop the database '{name}', because it does not exist or you do not have permission.", 3701, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 3702: <c>DROP DATABASE</c> targeted a database
    /// currently in use by a session (its <c>CurrentDatabase</c>). The name
    /// appears double-quoted in the canonical message — probe-confirmed verbatim
    /// (Class 16, State 3) against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropDatabaseInUse(string name) =>
        new($"Cannot drop database \"{name}\" because it is currently in use.", 3702, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 3708: <c>DROP DATABASE</c> targeted a system
    /// database (<c>master</c> / <c>tempdb</c> / <c>model</c> / <c>msdb</c>).
    /// The offending name is shown verbatim — probe-confirmed (Class 16,
    /// State 1) against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropSystemDatabase(string name) =>
        new($"Cannot drop the database '{name}' because it is a system database.", 3708, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>synonym</c> wording variant:
    /// <c>DROP SYNONYM</c> targeted a name that doesn't exist (and
    /// <c>IF EXISTS</c> was absent). Probe-confirmed verbatim against SQL
    /// Server 2025 (2026-07-21).
    /// </summary>
    internal static SimulatedSqlException CannotDropSynonymDoesNotExist(string name) =>
        new($"Cannot drop the synonym '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 3705: a <c>DROP</c> statement named an object of
    /// the wrong kind — <c>DROP TABLE</c> over a synonym, or <c>DROP SYNONYM</c>
    /// over a table / view / procedure / function / sequence / trigger. The
    /// message names the statement that was issued, the object's actual kind,
    /// and the <c>DROP</c> form that would work. Probe-confirmed verbatim
    /// against SQL Server 2025 for every kind above; the object name appears
    /// exactly as written in the statement.
    /// </summary>
    /// <param name="attemptedDropKind">The object kind the statement named (<c>TABLE</c> / <c>SYNONYM</c>).</param>
    /// <param name="name">The target name as written.</param>
    /// <param name="actualKind">Lower-case noun for what the object really is ("synonym", "table valued function", …).</param>
    /// <param name="correctDropKind">The kind word of the <c>DROP</c> form that would succeed.</param>
    internal static SimulatedSqlException CannotUseDropWithObjectKind(string attemptedDropKind, string name, string actualKind, string correctDropKind) =>
        new($"Cannot use DROP {attemptedDropKind} with '{name}' because '{name}' is a {actualKind}. Use DROP {correctDropKind}.", 3705, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5313: a reference resolved to a synonym whose
    /// base object doesn't exist. Synonym base names bind lazily — <c>CREATE
    /// SYNONYM</c> succeeds over a missing (or cross-database missing) base and
    /// the failure surfaces at first use. Probe-confirmed verbatim against SQL
    /// Server 2025, with the synonym name rendered as written at the use site
    /// (unqualified stays unqualified).
    /// </summary>
    /// <param name="name">The synonym name as written at the use site.</param>
    /// <param name="state">
    /// State 1 when the base name resolves to nothing at all; State 224 when it
    /// resolves to an object of a kind the reference can't use (a procedure or
    /// sequence named in a FROM clause) — both probe-confirmed.
    /// </param>
    internal static SimulatedSqlException SynonymRefersToInvalidObject(string name, byte state) =>
        new($"Synonym '{name}' refers to an invalid object.", 5313, 16, state);

    /// <summary>
    /// Mimics SQL Server error 470: a synonym's base object is itself a
    /// synonym. Real SQL Server accepts the <c>CREATE SYNONYM</c> and rejects
    /// the chain at first use; the message double-quotes both names, the
    /// referencing one as written at the use site and the referenced one as
    /// stored. Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException SynonymChainingNotAllowed(string referencing, string referenced) =>
        new($"The synonym \"{referencing}\" referenced synonym \"{referenced}\". Synonym chaining is not allowed.", 470, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>function</c> wording variant:
    /// <c>DROP FUNCTION</c> targeted a name that doesn't exist. Real SQL Server
    /// reuses Msg 3701 (Class 11, State 5) across DROP TABLE / DROP FUNCTION /
    /// DROP PROCEDURE / etc., swapping only the object-type noun in the
    /// message body. Probe-confirmed verbatim against SQL Server 2025 for
    /// DROP FUNCTION.
    /// </summary>
    internal static SimulatedSqlException CannotDropFunctionDoesNotExist(string name) =>
        new($"Cannot drop the function '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>view</c> wording variant.
    /// Real SQL Server reuses Msg 3701 across DROP TABLE / FUNCTION / VIEW /
    /// PROCEDURE / etc., swapping only the object-type noun. Probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropViewDoesNotExist(string name) =>
        new($"Cannot drop the view '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>procedure</c> wording variant.
    /// Probe-confirmed verbatim against SQL Server 2025 (2026-05-12): same
    /// number / class / state as DROP TABLE / FUNCTION / VIEW, with only the
    /// object-type noun changed.
    /// </summary>
    internal static SimulatedSqlException CannotDropProcedureDoesNotExist(string name) =>
        new($"Cannot drop the procedure '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>sequence</c> wording variant.
    /// Probe-confirmed verbatim against SQL Server 2025: same number / class /
    /// state as DROP TABLE / FUNCTION / VIEW / PROCEDURE; the object-type
    /// noun is the only difference.
    /// </summary>
    internal static SimulatedSqlException CannotDropSequenceDoesNotExist(string name) =>
        new($"Cannot drop the sequence '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>trigger</c> wording variant.
    /// Probe-confirmed verbatim against SQL Server 2025 (2026-05-13): same
    /// number / class / state as DROP TABLE / FUNCTION / VIEW / PROCEDURE /
    /// SEQUENCE; the object-type noun is the only difference.
    /// </summary>
    internal static SimulatedSqlException CannotDropTriggerDoesNotExist(string name) =>
        new($"Cannot drop the trigger '{name}', because it does not exist or you do not have permission.", 3701, 11, 5);

    /// <summary>
    /// Mimics SQL Server error 8197 — <c>CREATE TRIGGER</c> referenced a
    /// table that doesn't exist or isn't valid as a trigger parent
    /// (views aren't supported as AFTER-trigger parents — INSTEAD OF only).
    /// Probe-confirmed verbatim against SQL Server 2025: Class 16, State 4.
    /// </summary>
    internal static SimulatedSqlException ObjectDoesNotExistForTrigger(string name) =>
        new($"The object '{name}' does not exist or is invalid for this operation.", 8197, 16, 4);

    /// <summary>
    /// Mimics SQL Server error 2111: a second <c>INSTEAD OF &lt;action&gt;</c>
    /// trigger was declared on the same target. SQL Server allows at most
    /// one INSTEAD OF trigger per action per object; the parent-kind label
    /// is <c>table</c> when the parent is a heap table and <c>view</c>
    /// when the parent is a view. Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException InsteadOfTriggerAlreadyExists(string triggerName, string parentKind, string parentName, string actionName) =>
        new($"Cannot create trigger '{triggerName}' on {parentKind} '{parentName}' because an INSTEAD OF {actionName} trigger already exists on this object.", 2111, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2110: an <c>ALTER TRIGGER</c> (or the ALTER leg
    /// of a <c>CREATE OR ALTER</c>) names an existing trigger but an
    /// <c>ON</c> target other than the one it is attached to. Both names echo
    /// what the statement wrote. Probe-confirmed against SQL Server 2025:
    /// Class 15, State 1.
    /// </summary>
    internal static SimulatedSqlException CannotAlterTriggerOnDifferentObject(MultiPartName triggerName, MultiPartName parentName) =>
        new($"Cannot alter trigger '{triggerName}' on '{parentName}' because this trigger does not belong to this object. Specify the correct trigger name or the correct target object name.", 2110, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 334: a DML statement whose <c>OUTPUT</c> clause
    /// returns rows to the client (no <c>INTO</c>) targets a table carrying an
    /// enabled trigger. Both channels would be the statement's result set, and
    /// real refuses the combination rather than interleaving them.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025, including two things the
    /// message text doesn't say. The gate is a trigger for the statement's
    /// <b>own action</b> — an INSERT-only trigger blocks
    /// <c>INSERT … OUTPUT</c> but not <c>UPDATE … OUTPUT</c> — despite the
    /// wording claiming "any enabled triggers"; and <paramref name="target"/>
    /// is the target <b>as written</b>, so a qualified reference reports
    /// <c>dbo.m</c>, a bare one <c>m</c>, and MERGE reports its alias.
    /// INSTEAD OF counts alongside AFTER, a disabled trigger doesn't, and
    /// <c>OUTPUT … INTO</c> is exempt. The check is compile-time: it fires
    /// from an un-taken <c>IF</c> branch too.
    /// </remarks>
    internal static SimulatedSqlException OutputWithoutIntoOnTriggeredTarget(string target) =>
        new($"The target table '{target}' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.", 334, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3616: an error of severity 11 or higher was
    /// raised while a trigger body ran and the body's own <c>TRY</c> /
    /// <c>CATCH</c> handled it. Real still aborts the batch and rolls back the
    /// firing statement together with everything the trigger wrote — handling
    /// the error inside the body doesn't rescue it (probe-confirmed verbatim
    /// against SQL Server 2025). An error the body leaves *un*handled
    /// propagates with its own number instead, so this fires only for the
    /// swallowed case.
    /// </summary>
    internal static SimulatedSqlException ErrorRaisedDuringTriggerExecution() =>
        new("An error was raised during trigger execution. The batch has been aborted and the user transaction, if any, has been rolled back.", 3616, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11700: <c>CREATE SEQUENCE</c> declared
    /// <c>INCREMENT BY 0</c>. Probe-confirmed verbatim wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException SequenceIncrementCannotBeZero(string fullName) =>
        new($"The increment for sequence object '{fullName}' cannot be zero.", 11700, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11702: <c>CREATE SEQUENCE</c> declared a
    /// type that isn't one of the integer family or <c>decimal(p, 0)</c> /
    /// <c>numeric(p, 0)</c>. Probe-confirmed verbatim wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException SequenceInvalidType(string fullName) =>
        new($"The sequence object '{fullName}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, or any user-defined data type that is based on one of the above integer data types.", 11702, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 11703: <c>CREATE SEQUENCE</c> declared a
    /// <c>START WITH</c> outside the <c>[MINVALUE, MAXVALUE]</c> range
    /// (either explicit values, or one explicit and the other defaulted).
    /// Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException SequenceStartOutOfRange(string fullName) =>
        new($"The start value for sequence object '{fullName}' must be between the minimum and maximum value of the sequence object.", 11703, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11731 — a multi-row <c>VALUES</c> row
    /// constructor references a sequence that a target column's DEFAULT also
    /// uses, without that column appearing in the INSERT's column list.
    /// Probe-confirmed against SQL Server 2025: the single-row form is
    /// <em>accepted</em> (both references then yield the same value); only the
    /// multi-row constructor is rejected, at bind time.
    /// </summary>
    internal static SimulatedSqlException SequenceDefaultColumnMustBeListed() =>
        new("A column that uses a sequence object in the default constraint must be present in the target columns list, if the same sequence object appears in a row constructor.", 11731, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11720: <c>NEXT VALUE FOR</c> was used in a
    /// clause that disallows it (TOP / OVER / OUTPUT / ON / WHERE / GROUP BY
    /// / HAVING / ORDER BY). Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException NextValueForNotAllowedHere() =>
        new("NEXT VALUE FOR function is not allowed in the TOP, OVER, OUTPUT, ON, WHERE, GROUP BY, HAVING, or ORDER BY clauses.", 11720, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 11719: <c>NEXT VALUE FOR</c> inside a nested
    /// query or a stored expression — a derived table, a CTE, a subquery, an
    /// <c>APPLY</c> body, a view / user-defined-function body, a computed
    /// column, a <c>CHECK</c> constraint, a table type's column default.
    /// Probe-confirmed (SQL Server 2025) wording, severity and state; real
    /// binds it at parse, so the whole batch is refused and the sequence
    /// does not advance.
    /// </summary>
    internal static SimulatedSqlException NextValueForNotAllowedNested() =>
        new(
            "NEXT VALUE FOR function is not allowed in check constraints, default objects, computed columns, views, "
            + "user-defined functions, user-defined aggregates, user-defined table types, sub-queries, common table "
            + "expressions, derived tables or return statements.",
            11719, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 11741: <c>NEXT VALUE FOR</c> inside an arm of
    /// the conditional family. Probe-confirmed — real's message names
    /// <c>CHOOSE</c> as well, but a <c>CHOOSE</c> argument is accepted there,
    /// so the simulator accepts it too.
    /// </summary>
    internal static SimulatedSqlException NextValueForNotAllowedInConditional() =>
        new("NEXT VALUE FOR function cannot be used within CASE, CHOOSE, COALESCE, IIF, ISNULL and NULLIF.", 11741, 15, 1);

    /// <summary>Mimics SQL Server error 11725: <c>NEXT VALUE FOR</c> passed to an aggregate. Probe-confirmed.</summary>
    internal static SimulatedSqlException NextValueForNotAllowedInAggregate() =>
        new("An expression that contains a NEXT VALUE FOR function cannot be passed as an argument to an aggregate.", 11725, 15, 1);

    /// <summary>Mimics SQL Server error 11721: <c>NEXT VALUE FOR</c> in a statement carrying DISTINCT or a set operator. Probe-confirmed.</summary>
    internal static SimulatedSqlException NextValueForNotAllowedWithDedup() =>
        new(
            "NEXT VALUE FOR function cannot be used directly in a statement that uses a DISTINCT, UNION, UNION ALL, EXCEPT or INTERSECT operator.",
            11721, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 11723: <c>NEXT VALUE FOR</c> in a statement
    /// that carries an <c>ORDER BY</c>, where the reference names no
    /// <c>OVER</c> clause of its own. Probe-confirmed — an <c>OVER</c> is the
    /// one thing that lifts this refusal, and it lifts no other one.
    /// </summary>
    internal static SimulatedSqlException NextValueForNotAllowedWithOrderBy() =>
        new(
            "NEXT VALUE FOR function cannot be used directly in a statement that contains an ORDER BY clause unless the OVER clause is specified.",
            11723, 15, 1);

    /// <summary>Mimics SQL Server error 11738: <c>NEXT VALUE FOR</c> in a statement real declines to define it in, such as <c>PRINT</c>. Probe-confirmed.</summary>
    internal static SimulatedSqlException NextValueForNotAllowedInThisContext() =>
        new("The use of NEXT VALUE FOR function is not allowed in this context.", 11738, 15, 1);

    /// <summary>Mimics SQL Server error 11739: <c>NEXT VALUE FOR</c> in a row-limited statement (<c>TOP</c> or <c>OFFSET</c>). Probe-confirmed.</summary>
    internal static SimulatedSqlException NextValueForNotAllowedWithRowLimit() =>
        new(
            "NEXT VALUE FOR function cannot be used if ROWCOUNT option has been set, or the query contains TOP or OFFSET.",
            11739, 15, 1);

    /// <summary>Mimics SQL Server error 11742: <c>NEXT VALUE FOR</c> written into a <c>MERGE</c> action. Probe-confirmed.</summary>
    internal static SimulatedSqlException NextValueForNotAllowedInMergeAction() =>
        new(
            "NEXT VALUE FOR function can only be used with MERGE if it is defined within a default constraint on the target table for insert actions.",
            11742, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 11726: <c>NEXT VALUE FOR</c> resolved to an
    /// object that isn't a sequence. Probe-confirmed: real SQL Server uses
    /// the qualified <c>schema.name</c> form in the message.
    /// </summary>
    internal static SimulatedSqlException ObjectIsNotASequence(string fullName) =>
        new($"Object '{fullName}' is not a sequence object.", 11726, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11728: a no-cycle sequence reached its
    /// boundary and a further <c>NEXT VALUE FOR</c> tried to advance past
    /// it. Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException SequenceExhausted(string fullName) =>
        new($"The sequence object '{fullName}' has reached its minimum or maximum value. Restart the sequence object to allow new values to be generated.", 11728, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 219: <c>CREATE TYPE</c> targeted a name that
    /// already exists in the schema's type namespace. Probe-confirmed verbatim
    /// against SQL Server 2025 (2026-05-12) — the wording's
    /// "or you do not have permission to create it" tail mirrors the real
    /// server's permission/existence ambiguity (distinct from Msg 2714 which
    /// uses the cross-namespace object collision wording).
    /// </summary>
    internal static SimulatedSqlException TypeAlreadyExists(string fullName) =>
        new($"The type '{fullName}' already exists, or you do not have permission to create it.", 219, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 222: <c>CREATE TYPE name FROM &lt;basetype&gt;</c>
    /// referenced a base type that doesn't resolve to a built-in. Probe-
    /// confirmed verbatim wording against SQL Server 2025 — the base-type name
    /// appears in double-quotes inside the message text.
    /// </summary>
    internal static SimulatedSqlException InvalidBaseTypeForAlias(string baseTypeName) =>
        new($"The base type \"{baseTypeName}\" is not a valid base type for the alias data type.", 222, 16, 1);

    /// <summary>
    /// Alias-type variant of Msg 2716: an alias-typed column / parameter /
    /// variable declaration carries a width specifier (e.g.
    /// <c>c dbo.MyAlias(100)</c>). Probe-confirmed State 3 against SQL Server
    /// 2025 — distinct from <c>CannotSpecifyColumnWidth</c> (State 1, used
    /// for builtin types). The alias's fully-qualified name
    /// (<c>schema.name</c>) lands in the message verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotSpecifyColumnWidthOnAlias(string aliasFullName, int index) =>
        new($"Column, parameter, or variable #{index}: Cannot specify a column width on data type {aliasFullName}.", 2716, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 218: <c>DROP TYPE</c> targeted a name that
    /// doesn't exist (suppressed by <c>IF EXISTS</c>). Probe-confirmed
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException TypeDoesNotExist(string fullName) =>
        new($"Could not find the type '{fullName}'. Either it does not exist or you do not have the necessary permission.", 218, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3732: <c>DROP TYPE</c> targeted a type that is
    /// still referenced by at least one procedure (or function / view, when
    /// those grow TVP parameters). Probe-confirmed verbatim wording against
    /// SQL Server 2025: the error names a single referencing object even
    /// when more than one exists, and the trailing "There may be other
    /// objects that reference this type." line is part of the canonical
    /// message.
    /// </summary>
    internal static SimulatedSqlException CannotDropTypeBecauseReferenced(string typeFullName, string referencingObject) =>
        new($"Cannot drop type '{typeFullName}' because it is being referenced by object '{referencingObject}'. There may be other objects that reference this type.", 3732, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15233: <c>sp_addextendedproperty</c> rejected
    /// a duplicate property name on the same target. Probe-confirmed verbatim
    /// wording against SQL Server 2025 — the target label varies by level
    /// (<c>'object specified'</c> for DB-level, <c>'&lt;schema&gt;'</c> for
    /// schema, <c>'&lt;schema&gt;.&lt;name&gt;'</c> for table / view / proc / func,
    /// <c>'&lt;schema&gt;.&lt;table&gt;.&lt;col&gt;'</c> for column).
    /// </summary>
    internal static SimulatedSqlException ExtendedPropertyAlreadyExists(string propertyName, string targetLabel) =>
        new($"Property cannot be added. Property '{propertyName}' already exists for '{targetLabel}'.", 15233, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15217: <c>sp_updateextendedproperty</c> /
    /// <c>sp_dropextendedproperty</c> targeted a missing property. Same
    /// target-label convention as <see cref="ExtendedPropertyAlreadyExists"/>.
    /// </summary>
    internal static SimulatedSqlException ExtendedPropertyDoesNotExist(string propertyName, string targetLabel) =>
        new($"Property cannot be updated or deleted. Property '{propertyName}' does not exist for '{targetLabel}'.", 15217, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15135: an extended-property sproc named an
    /// object that doesn't exist. Probe-confirmed wording against SQL Server
    /// 2025 — the target token is the missing-name (e.g. <c>'no_such_schema'</c>
    /// for level0, <c>'dbo.no_such_table'</c> for level1, <c>'dbo.t1.no_such_col'</c>
    /// for level2).
    /// </summary>
    internal static SimulatedSqlException ExtendedPropertyTargetMissing(string targetLabel) =>
        new($"Object is invalid. Extended properties are not permitted on '{targetLabel}', or the object does not exist.", 15135, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15600: an extended-property sproc received an
    /// unknown / unsupported parameter (positional rather than named, an
    /// out-of-range level type like <c>'BOGUS'</c>, a missing required arg).
    /// Verbatim wording probed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException InvalidExtendedPropertyParameter(string procLabel) =>
        new($"An invalid parameter or option was specified for procedure '{procLabel}'.", 15600, 15, 1);

    /// <summary>
    /// Shares Msg 15600 wording with
    /// <see cref="InvalidExtendedPropertyParameter"/> — real SQL Server
    /// surfaces the same error number for any system-procedure parameter
    /// validation miss. Used by the linked-server sprocs
    /// (<c>sp_addlinkedserver</c> / <c>sp_dropserver</c> /
    /// <c>sp_addlinkedsrvlogin</c> / etc.).
    /// </summary>
    internal static SimulatedSqlException InvalidLinkedServerParameter(string procLabel) =>
        new($"An invalid parameter or option was specified for procedure '{procLabel}'.", 15600, 15, 1);

    /// <summary>
    /// Msg 15600 for <c>sp_settriggerorder</c> — an <c>@order</c> outside
    /// First / Last / None, or an <c>@stmttype</c> outside INSERT / UPDATE /
    /// DELETE. Shares the wording every system-procedure parameter miss uses.
    /// </summary>
    internal static SimulatedSqlException InvalidTriggerOrderParameter() =>
        new("An invalid parameter or option was specified for procedure 'sys.sp_settriggerorder'.", 15600, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 15165: <c>sp_settriggerorder</c> named an
    /// object that doesn't resolve. Real folds "missing" and "no permission"
    /// into one message so the two are indistinguishable to the caller.
    /// Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CouldNotFindObjectOrNoPermission(string name) =>
        new($"Could not find object '{name}' or you do not have permission.", 15165, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15125: the trigger named doesn't handle the
    /// requested statement type. The action is **lowercased** in the message
    /// whatever case the caller passed — unlike Msg 15130, which echoes both
    /// words verbatim (probe-confirmed against SQL Server 2025).
    /// </summary>
#pragma warning disable CA1308 // Real lowercases this action; the message text is the contract.
    internal static SimulatedSqlException TriggerIsNotATriggerForAction(string triggerName, string action) =>
        new($"Trigger '{triggerName}' is not a trigger for '{action.ToLowerInvariant()}'.", 15125, 16, 1);
#pragma warning restore CA1308

    /// <summary>
    /// Mimics SQL Server error 15130: a <c>First</c> (or <c>Last</c>) trigger
    /// is already registered for that action on the same table. Both the order
    /// word and the statement type are echoed **as the caller wrote them**
    /// (probe-confirmed: passing lowercase yields
    /// <c>'first' … 'insert'</c>). Re-ordering the trigger that already holds
    /// the slot is not a conflict.
    /// </summary>
    internal static SimulatedSqlException TriggerOrderAlreadyExists(string order, string statementType) =>
        new($"There already exists a '{order}' trigger for '{statementType}'.", 15130, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15133: <c>INSTEAD OF</c> triggers have no
    /// ordering — at most one exists per action, so First / Last are
    /// meaningless. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException InsteadOfTriggerCannotBeOrdered(string triggerName) =>
        new($"INSTEAD OF trigger '{triggerName}' cannot be associated with an order.", 15133, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15123: <c>sp_configure</c> named an option that
    /// isn't in the configuration catalog — or one that is, but is advanced
    /// while <c>show advanced options</c> is installed as 0. Real folds both
    /// cases into the one message (probe-confirmed verbatim).
    /// </summary>
    internal static SimulatedSqlException ConfigurationOptionDoesNotExist(string name) =>
        new($"The configuration option '{name}' does not exist, or it may be an advanced option.", 15123, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15124: the name <c>sp_configure</c> was given is
    /// a prefix of more than one option. Real also returns the matching names as
    /// a <c>duplicate_options</c> result set; the simulator raises only.
    /// </summary>
    internal static SimulatedSqlException ConfigurationOptionNotUnique(string name) =>
        new($"The configuration option '{name}' is not unique.", 15124, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15129: the value <c>sp_configure</c> was given
    /// falls outside the option's minimum / maximum. The value is single-quoted
    /// even though it's numeric (probe-confirmed verbatim).
    /// </summary>
    internal static SimulatedSqlException InvalidConfigurationValue(int value, string name) =>
        new($"'{value}' is not a valid value for configuration option '{name}'.", 15129, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15015: <c>sp_dropserver</c> referenced a
    /// linked-server name that isn't registered. Wording probe-confirmed
    /// against SQL Server 2025 — the server name appears in single quotes;
    /// the trailing sentence points users at <c>sys.servers</c> /
    /// <c>sp_helpserver</c> for the available set.
    /// </summary>
    internal static SimulatedSqlException LinkedServerDoesNotExist(string serverName) =>
        new($"The server '{serverName}' does not exist. Use sp_helpserver to show available servers.", 15015, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2715: a <c>DECLARE</c> / <c>CREATE
    /// PROCEDURE</c> / <c>CREATE FUNCTION</c> parameter referenced a type
    /// name that doesn't resolve. Probe-confirmed two-line wording against
    /// SQL Server 2025 (the "Parameter or variable" suffix is part of the
    /// canonical message).
    /// </summary>
    internal static SimulatedSqlException CannotFindDataType(int parameterIndex, string typeFullName, string parameterName) =>
        new($"Column, parameter, or variable #{parameterIndex}: Cannot find data type {typeFullName}.{Environment.NewLine}Parameter or variable '{parameterName}' has an invalid data type.", 2715, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 352: a <c>CREATE PROCEDURE</c> / <c>CREATE
    /// FUNCTION</c> parameter was typed by a user-defined table type but
    /// didn't include the mandatory <c>READONLY</c> keyword. Probe-confirmed
    /// verbatim wording against SQL Server 2025 — the message names the
    /// parameter (with leading <c>@</c>) in double-quotes.
    /// </summary>
    internal static SimulatedSqlException TableValuedParameterMustBeReadOnly(string parameterName) =>
        new($"The table-valued parameter \"{parameterName}\" must be declared with the READONLY option.", 352, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 10700: a procedure body attempted to mutate
    /// (INSERT / UPDATE / DELETE / MERGE) a parameter declared with
    /// <c>READONLY</c> (the only shape supported is the table-valued
    /// parameter). Probe-confirmed verbatim wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException TableValuedParameterIsReadOnly(string parameterName) =>
        new($"The table-valued parameter \"{parameterName}\" is READONLY and cannot be modified.", 10700, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 500: a TVP value supplied via ADO.NET (a
    /// <see cref="System.Data.DataTable"/> or <see cref="System.Data.IDataReader"/>)
    /// has a column count that doesn't match the target table type's column
    /// count. Probe-confirmed verbatim wording against SQL Server 2025: the
    /// real server raises this from the client-side batch encoder when the
    /// row schema mismatch is detected before the command reaches the
    /// engine; the simulator mirrors the wording at parameter materialization.
    /// </summary>
    internal static SimulatedSqlException TableValuedParameterColumnCountMismatch(int suppliedColumns, int requiredColumns) =>
        new($"Trying to pass a table-valued parameter with {suppliedColumns} column(s) where the corresponding user-defined table type requires {requiredColumns} column(s).", 500, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1077: a TVP value supplied via ADO.NET
    /// included an explicit value for an identity column on the target
    /// table type. Probe-confirmed verbatim wording against SQL Server 2025
    /// — real SQL Server rejects this because <c>SET IDENTITY_INSERT @t</c>
    /// is not a legal statement (the only path to insert explicit identity
    /// values into a table variable / TVP).
    /// </summary>
    internal static SimulatedSqlException InsertIntoIdentityColumnNotAllowedOnTableVariables() =>
        new("INSERT into an identity column not allowed on table variables.", 1077, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 302: <c>newsequentialid()</c> appeared anywhere
    /// other than as the entire DEFAULT expression of a <c>uniqueidentifier</c>
    /// column. Wording verified against SQL Server 2025 — the simulator
    /// surfaces this whenever the parser sees the call outside that narrow
    /// context, including bare <c>SELECT NEWSEQUENTIALID()</c> and uses
    /// nested inside an arithmetic / function expression.
    /// </summary>
    internal static SimulatedSqlException NewSequentialIdNotInDefault() =>
        new("The newsequentialid() built-in function can only be used in a DEFAULT expression for a column of type 'uniqueidentifier' in a CREATE TABLE or ALTER TABLE statement. It cannot be combined with other operators to form a complex scalar expression.", 302, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 271: an INSERT or UPDATE targeted a column
    /// that the engine doesn't allow direct writes to — the canonical case
    /// here is a computed column. Message is verbatim from SQL Server 2025;
    /// the UNION-result wording is part of the standard text even though the
    /// simulator only triggers it from the computed-column path.
    /// </summary>
    internal static SimulatedSqlException ColumnCannotBeModified(string columnName) =>
        new($"The column \"{columnName}\" cannot be modified because it is either a computed column or is the result of a UNION operator.", 271, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1759: a computed column's expression refers
    /// to another computed column. SQL Server forbids this regardless of
    /// PERSISTED on either side; the simulator validates the rule during
    /// CREATE TABLE's two-pass column resolution.
    /// </summary>
    internal static SimulatedSqlException ComputedColumnReferencedInComputed(string referencedColumn, string tableName) =>
        new($"Computed column '{referencedColumn}' in table '{tableName}' is not allowed to be used in another computed-column definition.", 1759, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 8183: the column-constraint set declared on
    /// a computed column is incompatible with non-persisted storage. SQL
    /// Server fires this for IDENTITY, DEFAULT, explicit NULL/NOT NULL on a
    /// non-persisted computed column, and PERSISTED-with-explicit-NULL.
    /// Message text is fixed.
    /// </summary>
    internal static SimulatedSqlException ComputedColumnConstraintRequiresPersisted() =>
        new("Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.", 8183, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 544: an INSERT supplied an explicit value for
    /// an identity column without first issuing <c>SET IDENTITY_INSERT ... ON</c>
    /// for the destination table.
    /// </summary>
    internal static SimulatedSqlException CannotInsertExplicitIdentity(string tableName) =>
        new($"Cannot insert explicit value for identity column in table '{tableName}' when IDENTITY_INSERT is set to OFF.", 544, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8101: a value would land in an identity column
    /// without a column list naming it. The simulator raises it from
    /// <c>OUTPUT … INTO &lt;target&gt;</c> when the projection is wider than the
    /// target's non-identity columns, so the positional fill would have to
    /// write the identity column.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025: this one names the OUTPUT
    /// target <em>schema-qualified</em> (<c>'dbo.OUTPUTTGT'</c>, or the bare
    /// <c>'#tmp'</c> form for a temp table) — unlike the sibling Msg 544 on the
    /// column-list form, which names the DML statement's own target table.
    /// The two disagree about which table they name; both are mirrored as-is.
    /// </remarks>
    internal static SimulatedSqlException ExplicitIdentityNeedsColumnList(string qualifiedTableName) =>
        new($"An explicit value for the identity column in table '{qualifiedTableName}' can only be specified when a column list is used and IDENTITY_INSERT is ON.", 8101, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 545: <c>SET IDENTITY_INSERT ... ON</c> is
    /// active, so the INSERT must list the identity column and supply an
    /// explicit value rather than relying on auto-generation.
    /// </summary>
    internal static SimulatedSqlException ExplicitIdentityRequired(string tableName) =>
        new($"Explicit value must be specified for identity column in table '{tableName}' either when IDENTITY_INSERT is set to ON or when a replication user is inserting into a NOT FOR REPLICATION identity column.", 545, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8107: <c>SET IDENTITY_INSERT</c> is already ON
    /// for one table and another <c>SET IDENTITY_INSERT</c> targeted a
    /// different table without first turning the first one OFF.
    /// </summary>
    internal static SimulatedSqlException IdentityInsertAlreadyOn(string heldTable, string requestedTable) =>
        new($"IDENTITY_INSERT is already ON for table '{heldTable}'. Cannot perform SET operation for table '{requestedTable}'.", 8107, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8106: <c>SET IDENTITY_INSERT &lt;table&gt; ON</c>
    /// targeted a table with no identity column — probe-confirmed verbatim
    /// against SQL Server 2025 (2026-07-21).
    /// </summary>
    internal static SimulatedSqlException TableHasNoIdentityForSet(string tableName) =>
        new($"Table '{tableName}' does not have the identity property. Cannot perform SET operation.", 8106, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8147: a column cannot be both <c>IDENTITY</c>
    /// and <c>NULL</c>.
    /// </summary>
    internal static SimulatedSqlException IdentityOnNullableColumn(string columnName, string tableName) =>
        new($"Could not create IDENTITY attribute on nullable column '{columnName}', table '{tableName}'.", 8147, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8148: one column definition carries two inline
    /// constraints of the same kind — <c>b int CHECK (b &gt; 0) CHECK (b &lt; 10)</c>,
    /// two <c>DEFAULT</c>s, two <c>UNIQUE</c>s or two <c>PRIMARY KEY</c>s
    /// (probe-confirmed against SQL Server 2025 for each, including a named
    /// and an unnamed pair and a persisted computed column). Only the *column*
    /// form is restricted: a second table-level CHECK over the same column, or
    /// a later <c>ALTER TABLE … ADD CHECK</c>, is legal.
    /// <paramref name="constraintKind"/> is the keyword real echoes
    /// (<c>CHECK</c> / <c>DEFAULT</c> / <c>UNIQUE</c> / <c>PRIMARY KEY</c>).
    /// </summary>
    internal static SimulatedSqlException MultipleColumnConstraints(string constraintKind, string columnName, string tableName) =>
        new($"More than one column {constraintKind} constraint specified for column '{columnName}', table '{tableName}'.", 8148, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8151: one column definition carries both an
    /// inline <c>PRIMARY KEY</c> and an inline <c>UNIQUE</c> (either order).
    /// The same-kind pair is Msg 8148 instead.
    /// </summary>
    internal static SimulatedSqlException BothPrimaryKeyAndUniqueOnColumn(string columnName, string tableName) =>
        new($"Both a PRIMARY KEY and UNIQUE constraint have been defined for column '{columnName}', table '{tableName}'. Only one is allowed.", 8151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2749: the identity column's data type isn't in
    /// the supported list (int/bigint/smallint/tinyint/decimal-or-numeric with
    /// scale 0). The message text is the literal SQL Server wording.
    /// </summary>
    internal static SimulatedSqlException IdentityInvalidType(string columnName) =>
        new($"Identity column '{columnName}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.", 2749, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 2744: more than one column in a table was
    /// declared with the <c>IDENTITY</c> property.
    /// </summary>
    internal static SimulatedSqlException MultipleIdentityColumns(string tableName) =>
        new($"Multiple identity columns specified for table '{tableName}'. Only one identity column per table is allowed.", 2744, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 2753: <c>IDENTITY(seed, 0)</c> — increment
    /// must be non-zero (negative is allowed).
    /// </summary>
    internal static SimulatedSqlException IdentityInvalidIncrement(string columnName) =>
        new($"Identity column '{columnName}' contains invalid INCREMENT.", 2753, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 2761: the <c>ROWGUIDCOL</c> property was declared
    /// on a column whose type isn't <c>uniqueidentifier</c>. Probe-confirmed
    /// wording / number / state (SQL Server 2025, 2026-07-17).
    /// </summary>
    internal static SimulatedSqlException RowGuidColRequiresUniqueIdentifier() =>
        new("The ROWGUIDCOL property can only be specified on the uniqueidentifier data type.", 2761, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 8196: more than one column in a table was
    /// declared with the <c>ROWGUIDCOL</c> property. Probe-confirmed wording /
    /// number / state (SQL Server 2025, 2026-07-17).
    /// </summary>
    internal static SimulatedSqlException MultipleRowGuidColumns() =>
        new("Duplicate column specified as ROWGUIDCOL.", 8196, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8115's IDENTITY-specific wording: the next
    /// identity value would overflow the column's underlying integer type.
    /// Distinct from <see cref="ArithmeticOverflow"/> in only one word
    /// (<c>IDENTITY</c> vs <c>expression</c>) but real SQL Server emits the
    /// IDENTITY variant for this code path.
    /// </summary>
    internal static SimulatedSqlException IdentityOverflow(string targetTypeName) =>
        new($"Arithmetic overflow error converting IDENTITY to data type {targetTypeName}.", 8115, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8110: more than one column or table-level
    /// PRIMARY KEY clause was declared in a CREATE TABLE.
    /// </summary>
    internal static SimulatedSqlException MultiplePrimaryKey(string tableName) =>
        new($"Cannot add multiple PRIMARY KEY constraints to table '{tableName}'.", 8110, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8111: a PRIMARY KEY constraint named a column
    /// that allows NULLs. Real SQL Server fires this only when the column was
    /// explicitly declared <c>NULL</c>; bare PK with no nullability stated
    /// silently flips the column to NOT NULL instead.
    /// </summary>
    internal static SimulatedSqlException PrimaryKeyOnNullableColumn(string tableName) =>
        new($"Cannot define PRIMARY KEY constraint on nullable column in table '{tableName}'.", 8111, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1919: a column whose type SQL Server doesn't
    /// allow as a key column appeared in a PRIMARY KEY or UNIQUE constraint.
    /// Triggers for <c>text</c>, <c>ntext</c>, <c>image</c>, and any
    /// <c>varchar(MAX)</c> / <c>nvarchar(MAX)</c> / <c>varbinary(MAX)</c>.
    /// </summary>
    internal static SimulatedSqlException KeyColumnInvalidType(string columnName, string tableName) =>
        new($"Column '{columnName}' in table '{tableName}' is of a type that is invalid for use as a key column in an index.", 1919, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1711: <c>PRIMARY KEY</c> targeted a computed
    /// column that isn't <c>PERSISTED</c>. Probe-confirmed at CREATE TABLE
    /// (the ALTER-ADD-PK path raises Msg 8111 instead, via the non-persisted-
    /// implies-nullable shortcut). Real SQL Server uses identical wording.
    /// </summary>
    internal static SimulatedSqlException ComputedColumnPkRequiresPersisted(string columnName, string tableName) =>
        new($"Cannot define PRIMARY KEY constraint on column '{columnName}' in table '{tableName}'. The computed column has to be persisted and not nullable.", 1711, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8102: an UPDATE statement targeted an identity
    /// column. <c>SET IDENTITY_INSERT</c> only opens INSERT to identity
    /// values; UPDATE on an identity column is rejected unconditionally.
    /// </summary>
    internal static SimulatedSqlException CannotUpdateIdentityColumn(string columnName) =>
        new($"Cannot update identity column '{columnName}'.", 8102, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 273: an INSERT supplied an explicit value for
    /// a <c>rowversion</c> / <c>timestamp</c> column. The column is
    /// auto-generated; explicit values are never accepted (no
    /// <c>IDENTITY_INSERT</c> analog). Wording verbatim from the probed
    /// server, including the recommendation in the second sentence.
    /// </summary>
    internal static SimulatedSqlException CannotInsertExplicitTimestamp() =>
        new("Cannot insert an explicit value into a timestamp column. Use INSERT with a column list to exclude the timestamp column, or insert a DEFAULT into the timestamp column.", 273, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 272: an UPDATE statement's SET clause targeted
    /// a <c>rowversion</c> / <c>timestamp</c> column. The column is
    /// auto-bumped on every UPDATE; manual sets are never accepted.
    /// </summary>
    internal static SimulatedSqlException CannotUpdateTimestampColumn() =>
        new("Cannot update a timestamp column.", 272, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2738: a CREATE TABLE declared a second
    /// <c>rowversion</c> / <c>timestamp</c> column. SQL Server allows at most
    /// one per table. Wording verbatim — note the "timestamp" word in the
    /// message regardless of which spelling the user wrote.
    /// </summary>
    internal static SimulatedSqlException MultipleTimestampColumns(string tableName, string secondColumnName) =>
        new($"A table can only have one timestamp column. Because table '{tableName}' already has one, the column '{secondColumnName}' cannot be added.", 2738, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 15150: <c>DROP SCHEMA</c> targeted a built-in /
    /// reserved schema (<c>dbo</c>, <c>sys</c>, <c>INFORMATION_SCHEMA</c>,
    /// <c>guest</c>, …). Real SQL Server treats these as un-droppable even when
    /// empty; the simulator matches that rejection. Probe-confirmed verbatim
    /// against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropProtectedSchema(string schemaName) =>
        new($"Cannot drop the schema '{schemaName}'.", 15150, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 (drop-schema variant): <c>DROP SCHEMA</c>
    /// targeted a name that doesn't exist (suppressed by <c>IF EXISTS</c>).
    /// Probe-confirmed verbatim wording against SQL Server 2025; SQL Server
    /// reuses Msg 15151 for several "doesn't exist or no permission" lookups
    /// — the simulator surfaces the variant matching each statement's noun.
    /// </summary>
    internal static SimulatedSqlException CannotDropSchemaDoesNotExist(string schemaName) =>
        new($"Cannot drop the schema '{schemaName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 (alter-schema variant): <c>ALTER SCHEMA
    /// dest TRANSFER ...</c> named a destination schema that doesn't exist.
    /// Probe-confirmed verbatim wording — same Msg / Class / State as the
    /// drop-schema-missing variant, distinct only in the "alter" vs "drop"
    /// verb in the message body.
    /// </summary>
    internal static SimulatedSqlException CannotAlterSchemaDoesNotExist(string schemaName) =>
        new($"Cannot alter the schema '{schemaName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 (find-object variant): <c>ALTER SCHEMA
    /// ... TRANSFER source.obj</c> named a source object (table / view /
    /// function / procedure / sequence) that doesn't resolve. The object's
    /// leaf name is reported (probe-confirmed) — the qualifier doesn't appear
    /// in the canonical message.
    /// </summary>
    internal static SimulatedSqlException CannotFindObject(string objectLeafName) =>
        new($"Cannot find the object '{objectLeafName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 (find-type variant): <c>ALTER SCHEMA
    /// ... TRANSFER TYPE::source.name</c> named a user-defined type that
    /// doesn't resolve. Probe-confirmed wording variant — the canonical
    /// "type" noun replaces "object".
    /// </summary>
    internal static SimulatedSqlException CannotFindType(string typeLeafName) =>
        new($"Cannot find the type '{typeLeafName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3729: <c>DROP SCHEMA</c> rejected because the
    /// schema still contains at least one object. SQL Server names the first
    /// object found in the dependency-graph walk (which often happens to be
    /// an auto-named PK / UNIQUE / CHECK constraint rather than the table
    /// itself); the simulator picks a representative name from the same
    /// dict scan. Probe-confirmed verbatim wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotDropSchemaBecauseNotEmpty(string schemaName, string objectName) =>
        new($"Cannot drop schema '{schemaName}' because it is being referenced by object '{objectName}'.", 3729, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3729: a <c>DROP</c> targeted an object that a
    /// <c>WITH SCHEMABINDING</c> module references. <paramref name="statement"/>
    /// is the verb pair real echoes (<c>DROP TABLE</c> / <c>DROP VIEW</c> /
    /// <c>DROP FUNCTION</c>), the target is echoed as the statement spelled it
    /// (<c>'t'</c> unqualified, <c>'dbo.t'</c> qualified), and the blocking
    /// module surfaces as its bare leaf. Probe-confirmed verbatim against SQL
    /// Server 2025 — state 1, distinct from the ALTER form's state 3.
    /// </summary>
    internal static SimulatedSqlException CannotDropReferencedBySchemaBoundObject(
        string statement, string writtenName, string referencingName) =>
        new($"Cannot {statement} '{writtenName}' because it is being referenced by object '{referencingName}'.", 3729, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3729: an <c>ALTER</c> (or <c>CREATE OR
    /// ALTER</c>) of a view or function that a <c>WITH SCHEMABINDING</c>
    /// module references. Real omits the object kind here — the message reads
    /// <c>Cannot ALTER 'dbo.f'</c> — echoes the name as the statement spelled
    /// it, carries state 3, and attributes the error to the altered module as
    /// its Procedure. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotAlterReferencedBySchemaBoundObject(
        string writtenName, string moduleLeafName, string referencingName) =>
        new($"Cannot ALTER '{writtenName}' because it is being referenced by object '{referencingName}'.",
            new SimulatedError(@class: 16, lineNumber: 0,
                message: $"Cannot ALTER '{writtenName}' because it is being referenced by object '{referencingName}'.",
                number: 3729, procedure: moduleLeafName, server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 3));

    /// <summary>
    /// Mimics SQL Server error 15336: <c>sp_rename</c> targeted an object or
    /// column a <c>WITH SCHEMABINDING</c> module references. Real echoes the
    /// <c>@objname</c> as passed — <c>'dbo.t'</c> for the object form,
    /// <c>'dbo.t.c'</c> for the column form. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException RenameParticipatesInEnforcedDependencies(string objName) =>
        new($"Object '{objName}' cannot be renamed because the object participates in enforced dependencies.", 15336, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15348: <c>ALTER SCHEMA … TRANSFER</c> targeted
    /// an object a <c>WITH SCHEMABINDING</c> module references. The message
    /// names nothing — probe-confirmed verbatim, including real's unspaced
    /// "schemabound". Transferring the schema-bound module itself is allowed.
    /// </summary>
    internal static SimulatedSqlException CannotTransferSchemaBoundObject() =>
        new("Cannot transfer a schemabound object.", 15348, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4512: a <c>WITH SCHEMABINDING</c> body named a
    /// FROM-clause source with something other than a two-part name.
    /// <paramref name="moduleKind"/> is the word real echoes (<c>view</c> /
    /// <c>function</c>). Probe-confirmed verbatim against both a one-part
    /// (<c>FROM t</c>) and a three-part (<c>FROM master.dbo.t</c>) name.
    /// </summary>
    internal static SimulatedSqlException CannotSchemaBindInvalidName(
        string moduleKind, string qualifiedModuleName, string referencedName) =>
        new($"Cannot schema bind {moduleKind} '{qualifiedModuleName}' because name '{referencedName}' is invalid for schema binding. Names must be in two-part format and an object cannot reference itself.", 4512, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 4513: a <c>WITH SCHEMABINDING</c> body
    /// referenced a view or function that isn't itself schema bound — the rule
    /// that makes the dependency graph closed under schema binding.
    /// Probe-confirmed verbatim; state 2.
    /// </summary>
    internal static SimulatedSqlException CannotSchemaBindNotSchemaBound(
        string moduleKind, string qualifiedModuleName, string referencedName) =>
        new($"Cannot schema bind {moduleKind} '{qualifiedModuleName}'. '{referencedName}' is not schema bound.", 4513, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 15530: <c>ALTER SCHEMA dest TRANSFER source.obj</c>
    /// rejected because <paramref name="objectLeafName"/> is already present
    /// in the destination schema. The canonical wording surfaces only the
    /// object's leaf (the destination schema isn't part of the message).
    /// Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ObjectAlreadyExistsInDestination(string objectLeafName) =>
        new($"The object with name \"{objectLeafName}\" already exists.", 15530, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15347: <c>ALTER SCHEMA TRANSFER</c> targeted
    /// an object that's owned by a parent (the common case is a DML trigger,
    /// whose schema follows its parent table / view automatically — the
    /// trigger can't be transferred independently). Probe-confirmed verbatim
    /// wording.
    /// </summary>
    internal static SimulatedSqlException CannotTransferObjectOwnedByParent() =>
        new("Cannot transfer an object that is owned by a parent object.", 15347, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13501: a column declared <c>GENERATED ALWAYS
    /// AS ROW START / END</c> must be typed <c>datetime2</c>. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException TemporalGeneratedColumnInvalidType(string columnName) =>
        new($"Temporal generated always column '{columnName}' has invalid data type.", 13501, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13504: <c>PERIOD FOR SYSTEM_TIME (startCol, endCol)</c>
    /// was declared but no column was declared <c>GENERATED ALWAYS AS ROW
    /// START</c> to back it (or the start name didn't match any such column).
    /// Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException TemporalRowStartMissing() =>
        new("Temporal 'GENERATED ALWAYS AS ROW START' column definition missing.", 13504, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13505: symmetric to <see cref="TemporalRowStartMissing"/>
    /// — no <c>GENERATED ALWAYS AS ROW END</c> column matched the period
    /// definition's end name. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException TemporalRowEndMissing() =>
        new("Temporal 'GENERATED ALWAYS AS ROW END' column definition missing.", 13505, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13506: <c>PERIOD FOR SYSTEM_TIME (start, end)</c>
    /// names <c>start</c>, but the column either doesn't exist or isn't a
    /// <c>GENERATED ALWAYS AS ROW START</c> column. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException TemporalPeriodStartNotMatching() =>
        new("System-versioned table SYSTEM_TIME period definition start column name not matching 'GENERATED ALWAYS AS ROW START' column name.", 13506, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13507: symmetric to <see cref="TemporalPeriodStartNotMatching"/>
    /// — the period's end column name doesn't match any <c>GENERATED ALWAYS
    /// AS ROW END</c> column. Probe-confirmed wording (raised both when the
    /// referenced column doesn't exist and when it exists but isn't
    /// generated-as-row-end).
    /// </summary>
    internal static SimulatedSqlException TemporalPeriodEndNotMatching() =>
        new("System-versioned table SYSTEM_TIME period definition end column name not matching 'GENERATED ALWAYS AS ROW END' column name.", 13507, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13509: at least one column is declared
    /// <c>GENERATED ALWAYS AS ROW START / END</c> but the table has no
    /// <c>PERIOD FOR SYSTEM_TIME</c> declaration backing it. Probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException TemporalGeneratedColumnWithoutPeriod() =>
        new("Cannot create generated always column when SYSTEM_TIME period is not defined.", 13509, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13587: a period column on a system-versioned
    /// temporal table was declared with explicit <c>NULL</c>. Probe-confirmed
    /// wording (the implicit <c>NOT NULL</c> form is required).
    /// </summary>
    internal static SimulatedSqlException TemporalPeriodColumnNullable(string columnName) =>
        new($"Period column '{columnName}' in a system-versioned temporal table cannot be nullable.", 13587, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13536: <c>INSERT</c> supplied an explicit
    /// value for a column declared <c>GENERATED ALWAYS AS ROW START / END</c>.
    /// Period columns are engine-populated and not user-writable. Probe-
    /// confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException CannotInsertExplicitGeneratedAlways(string qualifiedTableName) =>
        new($"Cannot insert an explicit value into a GENERATED ALWAYS column in table '{qualifiedTableName}'. Use INSERT with a column list to exclude the GENERATED ALWAYS column, or insert a DEFAULT into GENERATED ALWAYS column.", 13536, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13537: <c>UPDATE</c> set a value on a column
    /// declared <c>GENERATED ALWAYS AS ROW START / END</c>. Probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException CannotUpdateGeneratedAlways(string qualifiedTableName) =>
        new($"Cannot update GENERATED ALWAYS columns in table '{qualifiedTableName}'.", 13537, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13559: a direct <c>INSERT</c> targeted the
    /// history sibling of a system-versioned temporal table. History rows
    /// are populated by the engine via UPDATE / DELETE on the parent.
    /// Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotInsertIntoTemporalHistoryTable(string qualifiedTableName) =>
        new($"Cannot insert rows in a temporal history table '{qualifiedTableName}'.", 13559, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13552: <c>DROP TABLE</c> rejected because the
    /// target is a system-versioned temporal table (parent or history). The
    /// caller must first <c>ALTER TABLE … SET (SYSTEM_VERSIONING = OFF)</c>.
    /// Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotDropTemporalTable(string qualifiedTableName) =>
        new($"Drop table operation failed on table '{qualifiedTableName}' because it is not a supported operation on system-versioned temporal tables.", 13552, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13591: <c>ALTER TABLE … SET (SYSTEM_VERSIONING
    /// = OFF)</c> targeted a table that isn't system-versioned. Fires for
    /// plain regular tables and for the history sibling itself (the history
    /// sibling carries the <c>HISTORY_TABLE</c> role but doesn't "have"
    /// versioning — only the parent does). Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningNotOn(string qualifiedTableName) =>
        new($"SYSTEM_VERSIONING is not turned ON for table '{qualifiedTableName}'.", 13591, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13596: <c>ALTER TABLE … SET (SYSTEM_VERSIONING
    /// = ON)</c> — with no <c>HISTORY_TABLE</c> option — targeted a base table
    /// that's already system-versioned. Naming the *current* history table
    /// instead is accepted (it's how a retention period is changed in place);
    /// naming a different one raises
    /// <see cref="TemporalHistoryTableNameNotCorrect"/> /
    /// <see cref="TemporalTableAlreadyHasHistoryTable"/>. Probe-confirmed
    /// wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningAlreadyOn(string qualifiedTableName) =>
        new($"SYSTEM_VERSIONING is already turned ON for table '{qualifiedTableName}'.", 13596, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13595: <c>ALTER TABLE … SET (SYSTEM_VERSIONING
    /// = ON (HISTORY_TABLE = name))</c> on an already-versioned base named an
    /// existing table other than the base's current history sibling.
    /// Probe-confirmed wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException TemporalHistoryTableNameNotCorrect(string qualifiedHistoryName, string qualifiedTableName) =>
        new($"Temporal history table name '{qualifiedHistoryName}' is not correct for current table '{qualifiedTableName}'.", 13595, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13757: same shape as
    /// <see cref="TemporalHistoryTableNameNotCorrect"/>, but the named history
    /// table doesn't resolve at all — real checks the existing link before it
    /// resolves the name, so an unresolvable name on an already-versioned base
    /// reports the link rather than Msg 4902. Probe-confirmed wording against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException TemporalTableAlreadyHasHistoryTable(string qualifiedTableName) =>
        new($"Temporal table '{qualifiedTableName}' already has history table defined. Consider dropping system_versioning first if you want to use different history table.", 13757, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13514: the named history table is already
    /// serving as another base table's history sibling
    /// (<c>IsHistoryTable=true</c>) or is itself a system-versioned base.
    /// Probe-confirmed wording against SQL Server 2025 — identical from the
    /// <c>CREATE TABLE</c> and <c>ALTER TABLE</c> paths.
    /// </summary>
    internal static SimulatedSqlException HistoryTableAlreadyInUse(string qualifiedTableName) =>
        new($"History table '{qualifiedTableName}' is already in use.", 13514, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13515: the candidate history table carries a
    /// PRIMARY KEY / UNIQUE constraint or a unique index of its own. Real
    /// checks this before the foreign-key, constraint and IDENTITY rejections
    /// and before any column-shape comparison. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableHasUniqueKeys(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has custom unique keys defined. Consider dropping all unique keys and trying again.", 13515, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13516: the candidate history table declares
    /// FOREIGN KEY constraints. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableHasForeignKeys(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has foreign keys defined. Consider dropping all foreign keys and trying again.", 13516, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13517: the candidate history table declares
    /// CHECK constraints. DEFAULT constraints are *not* covered — real accepts
    /// a history table carrying them (probe-confirmed). Probe-confirmed
    /// wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableHasConstraints(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has table or column constraints defined. Consider dropping all table and column constraints and trying again.", 13517, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13518: the candidate history table has an
    /// IDENTITY column. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableHasIdentityColumn(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedHistoryName}' has IDENTITY column specification. Consider dropping all IDENTITY column specifications and trying again.", 13518, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13523: base and candidate history table have
    /// different column counts. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableColumnCountMismatch(string qualifiedTableName, int baseColumnCount, string qualifiedHistoryName, int historyColumnCount) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because table '{qualifiedTableName}' has {baseColumnCount} columns and table '{qualifiedHistoryName}' has {historyColumnCount} columns.", 13523, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13524: the columns at the same 1-based ordinal
    /// have different names. Real walks the ordinals in order and reports the
    /// first column that differs in name, type, collation or nullability — so
    /// a name mismatch at a later ordinal loses to a type mismatch at an
    /// earlier one. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableColumnNameMismatch(string historyColumnName, int ordinal, string qualifiedHistoryName, string baseColumnName, string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because column '{historyColumnName}' at ordinal {ordinal} in history table '{qualifiedHistoryName}' has a different name than the column '{baseColumnName}' at the same ordinal in table '{qualifiedTableName}'.", 13524, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13525: matching columns have different declared
    /// types, rendered in their full declaration form (<c>nvarchar(60)</c>,
    /// <c>datetime2(3)</c>). Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableColumnTypeMismatch(string columnName, string historyType, string qualifiedHistoryName, string baseType, string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because column '{columnName}' has data type {historyType} in history table '{qualifiedHistoryName}' which is different from corresponding column type {baseType} in table '{qualifiedTableName}'.", 13525, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13526: matching columns resolve to different
    /// collations. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableColumnCollationMismatch(string columnName, string qualifiedTableName, string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because column '{columnName}' does not have the same collation in tables '{qualifiedTableName}' and '{qualifiedHistoryName}'.", 13526, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13531: matching columns differ in nullability,
    /// in either direction. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableColumnNullabilityMismatch(string columnName, string qualifiedTableName, string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because column '{columnName}' does not have the same nullability attribute in tables '{qualifiedTableName}' and '{qualifiedHistoryName}'.", 13531, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13574: the candidate history table declares a
    /// <c>PERIOD FOR SYSTEM_TIME</c> of its own — checked ahead of every other
    /// history-table rejection. Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException HistoryTableContainsPeriod(string qualifiedHistoryName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because temporal history table '{qualifiedHistoryName}' contains SYSTEM_TIME period.", 13574, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13743: <c>HISTORY_RETENTION_PERIOD</c> was given
    /// a count of zero or less. Probe-confirmed wording, which renders the
    /// offending number without quotes.
    /// </summary>
    internal static SimulatedSqlException InvalidHistoryRetentionPeriod(string value) =>
        new($"{value} is not a valid value for system versioning history retention period.", 13743, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13744: <c>HISTORY_RETENTION_PERIOD</c> named a
    /// unit outside DAY(S) / WEEK(S) / MONTH(S) / YEAR(S). Severity 15 (not
    /// 16 like the rest of the family) and the unit is echoed as written —
    /// both probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException InvalidHistoryRetentionUnit(string unit) =>
        new($"'{unit}' is not a valid history retention period unit for system versioning.", 13744, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 13765: a finite <c>HISTORY_RETENTION_PERIOD</c>
    /// was asked for on a base whose history table carries no clustered index
    /// leading with the period end column — the index real's aged-data cleanup
    /// task seeks through. State 1 when the history table has no clustered
    /// index at all, state 2 when it has one that leads with another column;
    /// both probe-confirmed, as is the wording and the database-qualified
    /// naming of both tables.
    /// </summary>
    internal static SimulatedSqlException FiniteRetentionRequiresHistoryClusteredIndex(string qualifiedTableName, string qualifiedHistoryName, byte state) =>
        new($"Setting finite retention period failed on system-versioned temporal table '{qualifiedTableName}' because the history table '{qualifiedHistoryName}' does not contain required clustered index. Consider creating a clustered columnstore or B-tree index starting with the column that matches end of SYSTEM_TIME period, on the history table.", 13765, 16, state);

    /// <summary>
    /// Mimics SQL Server error 13766: <c>DROP INDEX</c> targeted the clustered
    /// index a finite-retention history table needs. Real releases it once the
    /// corresponding base is back at INFINITE retention (or versioning is off),
    /// which is what the message's second sentence points at. Probe-confirmed
    /// wording; the index is named <c>schema.table.index</c> like Msg 3701's.
    /// </summary>
    internal static SimulatedSqlException CannotDropRetentionCleanupIndex(string qualifiedTableName, string indexName) =>
        new($"Cannot drop the clustered index '{qualifiedTableName}.{indexName}' because it is being used for automatic cleanup of aged data. Consider setting HISTORY_RETENTION_PERIOD to INFINITE on the corresponding system-versioned temporal table if you need to drop this index.", 13766, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4902: <c>ALTER TABLE</c> named a target that
    /// doesn't resolve to an existing table. The qualified name is reported
    /// verbatim in double-quotes (distinct from the single-quoted form Msg
    /// 208 uses for un-qualified DML name resolution). Probe-confirmed
    /// wording against SQL Server 2025: <c>ALTER TABLE dbo.tNoSuch SET
    /// (SYSTEM_VERSIONING = OFF)</c> on a missing target raises this rather
    /// than the generic Msg 208.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForAlterTable(string nameAsWritten) =>
        new($"Cannot find the object \"{nameAsWritten}\" because it does not exist or you do not have permissions.", 4902, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1762 — a FOREIGN KEY declares <c>SET DEFAULT</c>
    /// while one or more of its referencing columns is NOT NULL and carries no
    /// DEFAULT — the action would have nothing to set. Probe-confirmed against
    /// SQL Server 2025, including the <c>"</c>-quoted constraint name (Msg 1776
    /// beside it uses <c>'</c>), and that a *nullable* referencing column
    /// without a default is accepted, since NULL is then the settable value.
    /// Real raises this at CREATE / ALTER, not on the first cascading delete.
    /// </summary>
    internal static SimulatedSqlException ForeignKeySetDefaultWithoutDefault(string foreignKeyName) =>
        new($"Cannot create the foreign key \"{foreignKeyName}\" with the SET DEFAULT referential action, because one or more referencing not-nullable columns lack a default constraint.", 1762, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 8112: a single CREATE TABLE declares more than
    /// one CLUSTERED key constraint. Probe-confirmed against SQL Server 2025 —
    /// the inline-pair case has its own message, distinct from the Msg 1902 the
    /// CREATE INDEX and ALTER TABLE ADD CONSTRAINT paths raise (which names the
    /// existing clustered index; this one can't, since neither exists yet).
    /// </summary>
    internal static SimulatedSqlException MultipleClusteredConstraints(string tableName) =>
        new($"Cannot add more than one clustered index for constraints on table '{tableName}'.", 8112, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1776: a FOREIGN KEY's referenced column list
    /// doesn't match any PRIMARY KEY or UNIQUE constraint on the referenced
    /// table. Real SQL Server pairs this with a trailing Msg 1750 / 1753
    /// "Could not create constraint or index"; the simulator collapses the
    /// pair into a single Msg 1776 since the second is purely informational.
    /// Probe-confirmed verbatim wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyNoMatchingKey(string referencedTable, string foreignKeyName) =>
        new($"There are no primary or candidate keys in the referenced table '{referencedTable}' that match the referencing column list in the foreign key '{foreignKeyName}'.", 1776, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1773: a FOREIGN KEY written without a
    /// referenced column list points at a table carrying no PRIMARY KEY, so
    /// the implied column list has nothing to resolve against. Real reports
    /// this rather than Msg 1776, which is the explicit-list message, and
    /// pairs it with the same trailing informational Msg 1750 the simulator
    /// collapses away. Probe-confirmed verbatim wording against SQL Server
    /// 2025 — a referenced table carrying only a UNIQUE constraint reports it
    /// too, since the implied list reads the primary key alone.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyImplicitReferenceWithoutPrimaryKey(string foreignKeyName, string referencedTable) =>
        new($"Foreign key '{foreignKeyName}' has implicit reference to object '{referencedTable}' which does not have a primary key defined on it.", 1773, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1764: a FOREIGN KEY's referencing column is a
    /// computed column that isn't PERSISTED. Raised by the table-level
    /// <c>CREATE TABLE</c> and <c>ALTER TABLE ADD CONSTRAINT</c> forms; the
    /// inline column form raises Msg 8183 at parse instead, before the
    /// constraint reaches resolution (probe-confirmed split). Msg 1776 wins
    /// when both apply. Real pairs this with a trailing informational Msg 1750,
    /// which the simulator collapses away as it does for Msg 1776.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyOnNonPersistedComputedColumn(string columnName, string tableName) =>
        NonPersistedComputedColumnInConstraint(columnName, tableName, "FOREIGN KEY CONSTRAINT");

    /// <summary>
    /// Mimics SQL Server error 1764 for a CHECK constraint whose predicate
    /// references a computed column that isn't PERSISTED. Raised by the
    /// table-level <c>CREATE TABLE</c> / <c>DECLARE @t TABLE</c> /
    /// <c>CREATE TYPE … AS TABLE</c> forms, by <c>ALTER TABLE … ADD
    /// CONSTRAINT … CHECK</c> (<c>WITH NOCHECK</c> included), and by an
    /// inline CHECK on a persisted computed column that reaches a
    /// non-persisted computed peer. A CHECK inline on the non-persisted
    /// column itself raises Msg 8183 at parse instead. Probe-confirmed to
    /// beat Msg 8141, so this walk runs ahead of the peer-reference gate.
    /// </summary>
    internal static SimulatedSqlException CheckConstraintOnNonPersistedComputedColumn(string columnName, string tableName) =>
        NonPersistedComputedColumnInConstraint(columnName, tableName, "CHECK CONSTRAINT");

    /// <summary>
    /// Shared Msg 1764 body. Real names the offending constraint family in the
    /// quoted slot (<c>FOREIGN KEY CONSTRAINT</c> / <c>CHECK CONSTRAINT</c>)
    /// and capitalizes "Computed Column" — its wording, not a typo.
    /// </summary>
    private static SimulatedSqlException NonPersistedComputedColumnInConstraint(string columnName, string tableName, string constraintKind) =>
        new($"Computed Column '{columnName}' in table '{tableName}' is invalid for use in '{constraintKind}' because it is not persisted.", 1764, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1765: a FOREIGN KEY whose referencing column is
    /// computed declared <c>ON DELETE SET NULL</c> or <c>ON DELETE SET
    /// DEFAULT</c> — neither can write a computed column, so only NO ACTION and
    /// CASCADE (which removes the whole row) are allowed. Checked ahead of the
    /// ON UPDATE gate (Msg 1715), matching real's probed precedence.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyComputedColumnDeleteAction(string foreignKeyName, string columnName) =>
        new($"Foreign key '{foreignKeyName}' creation failed. Only NO ACTION and CASCADE referential delete actions are allowed for referencing computed column '{columnName}'.", 1765, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1715: a FOREIGN KEY whose referencing column is
    /// computed declared any ON UPDATE action other than NO ACTION. Every one
    /// of the others would have to write the computed column (CASCADE included,
    /// unlike the ON DELETE case), so NO ACTION is the only legal update action.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyComputedColumnUpdateAction(string foreignKeyName, string columnName) =>
        new($"Foreign key '{foreignKeyName}' creation failed. Only NO ACTION referential update action is allowed for referencing computed column '{columnName}'.", 1715, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3726: <c>DROP TABLE</c> targeted a table that
    /// is still referenced by at least one FOREIGN KEY constraint from
    /// another (or the same) table. The simulator names the parent target
    /// only; real SQL Server emits a trailing Msg 1753 with the FK name list
    /// which the simulator omits (informational only).
    /// </summary>
    internal static SimulatedSqlException CannotDropTableReferencedByForeignKey(string tableName) =>
        new($"Could not drop object '{tableName}' because it is referenced by a FOREIGN KEY constraint.", 3726, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1785: a newly declared FOREIGN KEY with a
    /// non-NO-ACTION referential action would close a cascade cycle or
    /// introduce multiple cascade paths to the same table. Real SQL Server
    /// performs this analysis at CREATE TABLE / ALTER TABLE ADD CONSTRAINT
    /// time; the simulator walks the existing FK graph during the same
    /// CREATE pass.
    /// </summary>
    internal static SimulatedSqlException CascadeCycleOrMultiplePathsRejected(string constraintName, string tableName) =>
        new($"Introducing FOREIGN KEY constraint '{constraintName}' on table '{tableName}' may cause cycles or multiple cascade paths. Specify ON DELETE NO ACTION or ON UPDATE NO ACTION, or modify other FOREIGN KEY constraints.", 1785, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 1779: <c>ALTER TABLE … ADD CONSTRAINT … PRIMARY
    /// KEY</c> attempted to add a second PRIMARY KEY to a table that already
    /// declares one. Probe-confirmed verbatim wording against SQL Server 2025
    /// (2026-05-13).
    /// </summary>
    internal static SimulatedSqlException PrimaryKeyAlreadyExists(string tableName) =>
        new($"Table '{tableName}' already has a primary key defined on it.", 1779, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 1505: <c>ALTER TABLE … ADD CONSTRAINT …
    /// PRIMARY KEY / UNIQUE</c> would create a unique index over existing
    /// data containing duplicate key tuples. Real SQL Server wraps the
    /// duplicate-on-create message in CREATE-UNIQUE-INDEX framing
    /// (distinct from the runtime INSERT-time Msg 2627). Also raised for
    /// <c>CREATE UNIQUE INDEX ON &lt;view&gt;</c> over duplicate view rows.
    /// <paramref name="formattedKeyValues"/> is the rendered tuple text without
    /// enclosing parens (via <c>FormatIndexKeyValues</c>).
    /// </summary>
    internal static SimulatedSqlException DuplicateKeyOnCreate(string qualifiedTableName, string indexName, string formattedKeyValues) =>
        new($"The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name '{qualifiedTableName}' and the index name '{indexName}'. The duplicate key value is ({formattedKeyValues}).", 1505, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1781: <c>ALTER TABLE … ADD CONSTRAINT …
    /// DEFAULT</c> for a column that already has a default constraint.
    /// Real SQL Server allows at most one default per column.
    /// </summary>
    internal static SimulatedSqlException ColumnAlreadyHasDefault() =>
        new("Column already has a DEFAULT bound to it.", 1781, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1752: <c>ALTER TABLE … ADD CONSTRAINT …
    /// DEFAULT (…) FOR col</c> named a column that doesn't exist on the
    /// target table.
    /// </summary>
    internal static SimulatedSqlException DefaultColumnInvalid(string columnName, string tableName) =>
        new($"Column '{columnName}' in table '{tableName}' is invalid for creating a default constraint.", 1752, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 1769: <c>ADD CONSTRAINT … FOREIGN KEY (col)
    /// REFERENCES …</c> named a child column that doesn't exist on the
    /// referencing table.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyInvalidColumn(string columnName, string tableName, string foreignKeyName) =>
        new($"Foreign key '{foreignKeyName}' references invalid column '{columnName}' in referencing table '{tableName}'.", 1769, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1911: <c>ADD CONSTRAINT … UNIQUE (col)</c>
    /// named a column that doesn't exist on the target table. Real SQL
    /// Server uses this same Msg for any "index-or-key referenced a missing
    /// column" path.
    /// </summary>
    internal static SimulatedSqlException IndexColumnMissing(string columnName) =>
        new($"Column name '{columnName}' does not exist in the target table, index or view.", 1911, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3728: <c>ALTER TABLE … DROP CONSTRAINT</c>
    /// named a constraint that doesn't exist on the target table. Probe-
    /// confirmed wording — name appears single-quoted.
    /// </summary>
    internal static SimulatedSqlException NotAConstraint(string name) =>
        new($"'{name}' is not a constraint.", 3728, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3725: <c>ALTER TABLE … DROP CONSTRAINT</c>
    /// targeted a PRIMARY KEY / UNIQUE constraint still referenced by an
    /// incoming FOREIGN KEY. Probe-confirmed wording verbatim.
    /// </summary>
    internal static SimulatedSqlException ConstraintReferencedByForeignKey(string constraintName, string referencingTable, string referencingFkName) =>
        new($"The constraint '{constraintName}' is being referenced by table '{referencingTable}', foreign key constraint '{referencingFkName}'.", 3725, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 4917: <c>ALTER TABLE … (CHECK | NOCHECK)
    /// CONSTRAINT name</c> named a constraint that doesn't exist on the
    /// target table. Probe-confirmed verbatim (distinct from Msg 3728's
    /// <c>'name' is not a constraint.</c> shape — same scope, different
    /// wording per the action verb).
    /// </summary>
    internal static SimulatedSqlException ConstraintDoesNotExist(string name) =>
        new($"Constraint '{name}' does not exist.", 4917, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 1913: <c>CREATE INDEX</c> with a name that
    /// already exists on the target table. Probe-confirmed verbatim
    /// against SQL Server 2025 — message names the index and the
    /// qualified table.
    /// </summary>
    internal static SimulatedSqlException IndexAlreadyExists(string indexName, string qualifiedTableName) =>
        new($"The operation failed because an index or statistics with name '{indexName}' already exists on table '{qualifiedTableName}'.", 1913, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1916: <c>IGNORE_DUP_KEY</c> was set on a
    /// <c>CREATE INDEX</c> that isn't UNIQUE. Probe-confirmed verbatim, including
    /// the lowercase option name and State 4, and probe-confirmed to fire ahead of
    /// table / column / duplicate-name resolution — it is a statement-shape check,
    /// so the message names nothing.
    /// </summary>
    internal static SimulatedSqlException IgnoreDupKeyOnNonUniqueIndex() =>
        new("CREATE INDEX options nonunique and ignore_dup_key are mutually exclusive.", 1916, 16, 4);

    /// <summary>
    /// Mimics SQL Server error 1915: <c>ALTER INDEX … SET (IGNORE_DUP_KEY = ON)</c>
    /// against a non-unique index. Distinct number <i>and</i> wording from the
    /// CREATE-time <see cref="IgnoreDupKeyOnNonUniqueIndex"/> (1916) — both
    /// probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException IgnoreDupKeyOnNonUniqueIndexAlter(string indexName) =>
        new($"Cannot alter a non-unique index with ignore_dup_key index option. Index '{indexName}' is non-unique.", 1915, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10618: <c>IGNORE_DUP_KEY = ON</c> on a filtered
    /// index. The verb differs between the two paths — <c>"Cannot create
    /// filtered index"</c> at CREATE, <c>"Cannot alter filtered index"</c> at
    /// ALTER — under one message number; both probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException IgnoreDupKeyOnFilteredIndex(string verb, string indexName, string tableName) =>
        new($"Cannot {verb} filtered index '{indexName}' on table '{tableName}' because the statement sets the IGNORE_DUP_KEY option to ON. Rewrite the statement so that it does not use the IGNORE_DUP_KEY option.", 10618, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1990: <c>IGNORE_DUP_KEY</c> on an index over a
    /// view. Probe-confirmed verbatim; names neither the view nor the index.
    /// </summary>
    internal static SimulatedSqlException IgnoreDupKeyOnViewIndex() =>
        new("Cannot define an index on a view with ignore_dup_key index option. Remove ignore_dup_key option and verify that view definition does not allow duplicates, or do not index view.", 1990, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1979: <c>ALTER INDEX … SET (IGNORE_DUP_KEY = …)</c>
    /// against the index backing a PRIMARY KEY or UNIQUE constraint. Real accepts
    /// the option in the constraint's own declaration but refuses to change it
    /// afterwards — both halves probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException IgnoreDupKeyOnConstraintIndex(string indexName) =>
        new($"Cannot use index option ignore_dup_key to alter index '{indexName}' as it enforces a primary or unique constraint.", 1979, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 10601: an <c>INCLUDE</c> list on a
    /// <c>CLUSTERED</c> index, which carries every non-key column already.
    /// Probe-confirmed verbatim and probe-confirmed to fire ahead of table /
    /// view / column resolution and ahead of Msg 1916 — it is a
    /// statement-shape check, so the message names neither index nor table.
    /// </summary>
    internal static SimulatedSqlException IncludedColumnsOnClusteredIndex() =>
        new("Cannot specify included columns for a clustered index.", 10601, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2727: <c>ALTER INDEX</c> named an index the table
    /// doesn't carry. Probe-confirmed verbatim, at the unusual Level 11.
    /// </summary>
    internal static SimulatedSqlException CannotFindIndex(string indexName) =>
        new($"Cannot find index '{indexName}'.", 2727, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 1088: <c>ALTER INDEX … ON &lt;table&gt;</c> named a
    /// missing object. Probe-confirmed verbatim (the object name is wrapped in
    /// double quotes, unlike most resolution errors' single quotes) at State 9.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForAlterIndex(string objectName) =>
        new($"Cannot find the object \"{objectName}\" because it does not exist or you do not have permissions.", 1088, 16, 9);

    /// <summary>
    /// Mimics SQL Server error 155: an unrecognized option name inside
    /// <c>ALTER INDEX … SET (…)</c>. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException UnrecognizedAlterIndexOption(string optionName) =>
        new($"'{optionName}' is not a recognized ALTER INDEX option.", 155, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1973: an operation that a disabled index can't
    /// take — <c>SET (…)</c> or <c>REORGANIZE</c> against one. Probe-confirmed
    /// verbatim.
    /// </summary>
    internal static SimulatedSqlException OperationOnDisabledIndex(string indexName, string tableName) =>
        new($"Cannot perform the specified operation on disabled index '{indexName}' on table '{tableName}'.", 1973, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 155: an unrecognized option name inside
    /// <c>ALTER INDEX … REORGANIZE WITH (…)</c>. REORGANIZE's option block has
    /// its own wording — probe-confirmed, the message names the form where the
    /// <c>SET</c> / <c>RESUME</c> blocks report the plain
    /// <see cref="UnrecognizedAlterIndexOption"/> text.
    /// </summary>
    internal static SimulatedSqlException UnrecognizedAlterIndexReorganizeOption(string optionName) =>
        new($"'{optionName}' is not a recognized ALTER INDEX REORGANIZE option.", 155, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 153: an index option given a value its grammar
    /// doesn't take (<c>LOB_COMPACTION = 1</c> where only <c>ON</c> / <c>OFF</c>
    /// is legal). Probe-confirmed verbatim, including the option name echoed as
    /// the statement spelled it and the generic "INDEX statement" framing.
    /// </summary>
    internal static SimulatedSqlException InvalidUsageOfIndexOption(string optionName) =>
        new($"Invalid usage of the option {optionName} in the INDEX statement.", 153, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 7729: a partition number given where nothing is
    /// partitioned. Real splits the message by which object it could name — the
    /// index for <c>ALTER INDEX &lt;name&gt; … PARTITION = n</c> (State 1), the
    /// table for the <c>ON PARTITIONS (…)</c> data-compression sub-clause
    /// (State 3) — while keeping "alter index statement" in the wording of the
    /// first even when an <c>ALTER TABLE … REBUILD</c> raised it.
    /// Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException PartitionNumberOnUnpartitionedIndex(string indexName) =>
        new($"Cannot specify partition number in the alter index statement as the index '{indexName}' is not partitioned.", 7729, 16, 1);

    /// <inheritdoc cref="PartitionNumberOnUnpartitionedIndex"/>
    internal static SimulatedSqlException PartitionNumberOnUnpartitionedTable(string tableName) =>
        new($"Cannot specify partition number in the alter table statement as the table '{tableName}' is not partitioned.", 7729, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 7735: the rebuild / reorganize flavour of the
    /// same refusal, which real raises where the statement named no single index
    /// — <c>ALTER INDEX ALL</c> and <c>ALTER TABLE … REBUILD</c>. The message
    /// names the first index the statement would have touched, or the table when
    /// there is none, and echoes the statement that raised it. Probe-confirmed
    /// verbatim in all four combinations.
    /// </summary>
    internal static SimulatedSqlException RebuildPartitionOnUnpartitioned(bool alterIndex, string? indexName, string tableName) =>
        new(
            indexName is null
                ? $"Cannot specify partition number in alter {(alterIndex ? "index" : "table")} statement to rebuild or reorganize a partition of table '{tableName}' as table is not partitioned."
                : $"Cannot specify partition number in alter {(alterIndex ? "index" : "table")} statement to rebuild or reorganize a partition of index '{indexName}' as index is not partitioned.",
            7735,
            16,
            1);

    /// <summary>
    /// Mimics SQL Server error 10638: <c>ALTER INDEX &lt;name&gt; …
    /// RESUME</c> / <c>PAUSE</c> / <c>ABORT</c> against an index carrying no
    /// paused resumable operation. The simulator never starts one, so every such
    /// statement lands here. Probe-confirmed verbatim, including the unqualified
    /// table name and real's State split — 1 for <c>RESUME</c>, 2 for
    /// <c>PAUSE</c> and <c>ABORT</c>.
    /// </summary>
    internal static SimulatedSqlException NoPendingResumableIndexOperation(string form, string indexName, string tableName) =>
        new(
            $"ALTER INDEX '{form}' failed. There is no pending resumable index operation for the index '{indexName}' on '{tableName}'.",
            10638,
            16,
            form == "RESUME" ? (byte)1 : (byte)2);

    /// <summary>
    /// Mimics SQL Server error 10680: the <c>ALTER INDEX ALL</c> flavour of
    /// <see cref="NoPendingResumableIndexOperation"/>, at real's unusual
    /// Level 11 and a single State for all three forms. Probe-confirmed
    /// verbatim.
    /// </summary>
    internal static SimulatedSqlException NoPendingResumableIndexOperationForAll(string form, string tableName) =>
        new($"ALTER INDEX ALL '{form}' failed. There is no pending resumable index operation on '{tableName}'.", 10680, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 8655: a table carrying a <i>disabled clustered</i>
    /// index can't be queried or modified at all, because the clustered index
    /// <i>is</i> the table's storage on real. Probe-confirmed verbatim for both
    /// SELECT and INSERT. DDL is exempt — <c>ALTER INDEX … REBUILD</c> and
    /// <c>DROP INDEX</c> still work, which is how the table is recovered.
    /// </summary>
    internal static SimulatedSqlException QueryProcessorDisabledIndex(string indexName, string tableName) =>
        new($"The query processor is unable to produce a plan because the index '{indexName}' on table or view '{tableName}' is disabled.", 8655, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1902: a <c>CREATE CLUSTERED INDEX</c> targeted a
    /// table that already carries a clustered index (a clustered PRIMARY KEY /
    /// UNIQUE constraint or a prior clustered index). Probe-confirmed verbatim
    /// against SQL Server 2025 — unqualified table name, names the existing
    /// clustered index, Level 16 State 3.
    /// </summary>
    internal static SimulatedSqlException MoreThanOneClusteredIndex(string tableName, string existingClusteredName) =>
        new($"Cannot create more than one clustered index on table '{tableName}'. Drop the existing clustered index '{existingClusteredName}' before creating another.", 1902, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 1088: <c>CREATE INDEX</c> (or any
    /// catalog-scoped reference) named a target object that doesn't exist.
    /// Distinct from Msg 208 (which surfaces from DML) — Msg 1088 is the
    /// CREATE-INDEX / sp_help-shaped diagnostic. State 12 probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForCreateIndex(string qualifiedName) =>
        new($"Cannot find the object \"{qualifiedName}\" because it does not exist or you do not have permissions.", 1088, 16, 12);

    /// <summary>
    /// The indexed-view qualifying battery real SQL Server runs at
    /// <c>CREATE INDEX</c>. Every message below is verbatim from SQL Server
    /// 2025, including its quoting: most quote the view with <c>"</c>, but
    /// Msg 10116 / 10138 / 1949 use <c>'</c>, and Msg 8662 alone names the
    /// index as well and carries State 0 where the rest carry State 1. The
    /// view name is database-qualified (<c>db.schema.view</c>) throughout.
    /// </summary>
    internal static SimulatedSqlException IndexedViewHasDistinct(string qualifiedViewName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it contains the DISTINCT keyword. Consider removing DISTINCT from the view or not indexing the view. Alternatively, consider replacing DISTINCT with GROUP BY or COUNT_BIG(*) to simulate DISTINCT on grouping columns.", 10100, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasTopOrOffset(string qualifiedViewName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it contains the TOP or OFFSET keyword. Consider removing the TOP or OFFSET or not indexing the view.", 10101, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasOuterJoin(string qualifiedViewName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it uses a LEFT, RIGHT, or FULL OUTER join, and no OUTER joins are allowed in indexed views. Consider using an INNER join instead.", 10113, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasSetOperator(string qualifiedViewName) =>
        new($"Cannot create index on view '{qualifiedViewName}' because it contains one or more UNION, INTERSECT, or EXCEPT operators. Consider creating a separate indexed view for each query that is an input to the UNION, INTERSECT, or EXCEPT operators of the original view.", 10116, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasDisallowedAggregate(string qualifiedViewName, string aggregateName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it uses aggregate \"{aggregateName}\". Consider eliminating the aggregate, not indexing the view, or using alternate aggregates. For example, for AVG substitute SUM and COUNT_BIG, or for COUNT, substitute COUNT_BIG.", 10125, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasSubquery(string qualifiedViewName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it contains one or more subqueries. Consider changing the view to use only joins instead of subqueries. Alternatively, consider not indexing this view.", 10127, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewUsesCount(string qualifiedViewName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it uses the aggregate COUNT. Use COUNT_BIG instead.", 10136, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewMissingCountBig(string qualifiedViewName) =>
        new($"Cannot create index on view '{qualifiedViewName}' because its select list does not include a proper use of COUNT_BIG. Consider adding COUNT_BIG(*) to select list.", 10138, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewSumsNullableExpression(string indexName, string qualifiedViewName) =>
        new($"Cannot create the clustered index \"{indexName}\" on view \"{qualifiedViewName}\" because the view references an unknown value (SUM aggregate of nullable expression). Consider referencing only non-nullable values in SUM. ISNULL() may be useful for this.", 8662, 16, 0);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewIsNondeterministic(string qualifiedViewName, string functionName) =>
        new($"Cannot create index on view '{qualifiedViewName}'. The function '{functionName}' yields nondeterministic results. Use a deterministic system function, or modify the user-defined function to return deterministic results.", 1949, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewReferencesCte(string qualifiedViewName, string cteName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\" because it references common table expression \"{cteName}\". Views referencing common table expressions cannot be indexed. Consider not indexing the view, or removing the common table expression from the view definition.", 10137, 16, 1);

    /// <inheritdoc cref="IndexedViewHasDistinct"/>
    internal static SimulatedSqlException IndexedViewHasSelfJoin(string qualifiedViewName, string qualifiedTableName) =>
        new($"Cannot create index on view \"{qualifiedViewName}\". The view contains a self join on \"{qualifiedTableName}\".", 1947, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1939: <c>CREATE INDEX</c> on a view that wasn't
    /// declared <c>WITH SCHEMABINDING</c>. Probe-confirmed wording (SQL Server
    /// 2025, 2026-07-17) uses the view's <b>leaf</b> name (unqualified),
    /// unlike Msg 1940 / 1941 which qualify it — mirrored verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotIndexViewNotSchemaBound(string viewLeafName) =>
        new($"Cannot create index on view '{viewLeafName}' because the view is not schema bound.", 1939, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1940: an index other than a UNIQUE CLUSTERED
    /// one is created on a schema-bound view that doesn't yet have a unique
    /// clustered index (the first index on a view must be unique clustered).
    /// Probe-confirmed wording; the view name is schema-qualified.
    /// </summary>
    internal static SimulatedSqlException CannotIndexViewNoUniqueClustered(string qualifiedViewName) =>
        new($"Cannot create index on view '{qualifiedViewName}'. It does not have a unique clustered index.", 1940, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1941: a non-unique CLUSTERED index is created on
    /// a view (only unique clustered indexes are allowed on a view). Probe-
    /// confirmed wording; the view name is schema-qualified.
    /// </summary>
    internal static SimulatedSqlException CannotIndexViewNonUniqueClustered(string qualifiedViewName) =>
        new($"Cannot create nonunique clustered index on view '{qualifiedViewName}' because only unique clustered indexes are allowed. Consider creating unique clustered index instead.", 1941, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3701 with the <c>index</c> wording variant:
    /// <c>DROP INDEX name ON table</c> targeted an index that doesn't
    /// exist. State 6 when the parent table itself is missing; State 7
    /// when the table exists but has no such index. Probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException CannotDropIndexDoesNotExist(string qualifiedTableName, string indexName, byte state) =>
        new($"Cannot drop the index '{qualifiedTableName}.{indexName}', because it does not exist or you do not have permission.", 3701, 11, state);

    /// <summary>
    /// Mimics SQL Server error 3723: <c>DROP INDEX</c> targeted a system
    /// index that backs a PRIMARY KEY or UNIQUE constraint. Real SQL
    /// Server forbids dropping the index directly; the caller must
    /// <c>ALTER TABLE … DROP CONSTRAINT</c> instead. Probe-confirmed
    /// wording.
    /// </summary>
    internal static SimulatedSqlException ExplicitDropIndexNotAllowed(string qualifiedTableName, string indexName, string constraintKindWord) =>
        new($"An explicit DROP INDEX is not allowed on index '{qualifiedTableName}.{indexName}'. It is being used for {constraintKindWord} constraint enforcement.", 3723, 16, 4);

    /// <summary>
    /// Mimics SQL Server error 4901: <c>ALTER TABLE ADD col TYPE NOT NULL</c>
    /// against a non-empty table when the column is neither nullable, has
    /// a DEFAULT, nor is IDENTITY / TIMESTAMP. Probe-confirmed verbatim
    /// against SQL Server 2025. Wording is the full canonical paragraph;
    /// it's one string by design.
    /// </summary>
    internal static SimulatedSqlException AddColumnRequiresDefaultOrNullable(string columnName, string tableName) =>
        new($"ALTER TABLE only allows columns to be added that can contain nulls, or have a DEFAULT definition specified, or the column being added is an identity or timestamp column, or alternatively if none of the previous conditions are satisfied the table must be empty to allow addition of this column. Column '{columnName}' cannot be added to non-empty table '{tableName}' because it does not satisfy these conditions.", 4901, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4924: <c>ALTER TABLE DROP COLUMN</c> named
    /// a column that doesn't exist on the target table. Probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException DropColumnDoesNotExist(string columnName, string tableName) =>
        new($"ALTER TABLE DROP COLUMN failed because column '{columnName}' does not exist in table '{tableName}'.", 4924, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5074: <c>ALTER TABLE DROP COLUMN</c>
    /// targeted a column referenced by at least one constraint, index, or
    /// other dependent object. The message body lists every blocker on
    /// its own line, in the form <c>"The object 'X' is dependent on
    /// column 'col'.\n[…]
    /// ALTER TABLE DROP COLUMN col failed because one or more objects
    /// access this column."</c>. Constraints surface as <c>The object</c>;
    /// indexes surface as <c>The index</c>. Probe-confirmed verbatim
    /// against SQL Server 2025; blocker enumeration order matches PK /
    /// UQ → FK → CHECK → DEFAULT → index in the simulator.
    /// </summary>
    internal static SimulatedSqlException DropColumnHasDependenciesMixed(string columnName, List<(string Name, bool IsIndex)> blockers)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < blockers.Count; i++)
            _ = sb.Append(blockers[i].IsIndex ? "The index '" : "The object '").Append(blockers[i].Name).Append("' is dependent on column '").Append(columnName).Append("'.\r\n");
        _ = sb.Append("ALTER TABLE DROP COLUMN ").Append(columnName).Append(" failed because one or more objects access this column.");
        return new(sb.ToString(), 5074, 16, 1);
    }

    /// <summary>
    /// Mimics SQL Server error 2705: <c>ALTER TABLE ADD col</c> named a
    /// column that already exists on the target table (or a duplicate
    /// within the same multi-column ADD). Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException ColumnNamesMustBeUnique(string columnName, string qualifiedTableName) =>
        new($"Column names in each table must be unique. Column name '{columnName}' in table '{qualifiedTableName}' is specified more than once.", 2705, 16, 4);

    /// <summary>
    /// Mimics SQL Server error 4924: <c>ALTER TABLE ALTER COLUMN</c> named
    /// a column that doesn't exist on the target table. Probe-confirmed
    /// (same error code as the DROP COLUMN variant, distinct phrasing).
    /// </summary>
    internal static SimulatedSqlException AlterColumnDoesNotExist(string columnName, string tableName) =>
        new($"ALTER TABLE ALTER COLUMN failed because column '{columnName}' does not exist in table '{tableName}'.", 4924, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4928: <c>ALTER TABLE ALTER COLUMN</c>
    /// targeted a column kind that can't be altered. <paramref name="kindWord"/>
    /// is the quoted descriptor (<c>"COMPUTED"</c> for computed columns,
    /// <c>"timestamp"</c> for rowversion). Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotAlterColumnOfKind(string columnName, string kindWord) =>
        new($"Cannot alter column '{columnName}' because it is '{kindWord}'.", 4928, 16, 1);

    /// <summary>
    /// Distinguishes blocker kinds for <see cref="AlterColumnHasDependencies"/>'s
    /// per-line prefix: <c>Object</c> renders as <c>"The object 'X'"</c>,
    /// <c>Index</c> as <c>"The index 'X'"</c>, <c>Column</c> as <c>"The column 'X'"</c>.
    /// </summary>
    internal enum AlterColumnBlockerKind
    {
        Object,
        Index,
        Column,
    }

    /// <summary>
    /// Mimics SQL Server error 5074: <c>ALTER TABLE ALTER COLUMN</c>
    /// targeted a column referenced by at least one constraint, index, or
    /// computed-column expression. Per-line prefix varies by blocker kind:
    /// <c>The object</c> for PK / UQ / FK / CHECK, <c>The index</c> for
    /// indexes, <c>The column</c> for computed-column dependencies.
    /// Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException AlterColumnHasDependencies(string columnName, List<(string Name, AlterColumnBlockerKind Kind)> blockers)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < blockers.Count; i++)
        {
            var prefix = blockers[i].Kind switch
            {
                AlterColumnBlockerKind.Index => "The index '",
                AlterColumnBlockerKind.Column => "The column '",
                _ => "The object '",
            };
            _ = sb.Append(prefix).Append(blockers[i].Name).Append("' is dependent on column '").Append(columnName).Append("'.\r\n");
        }
        _ = sb.Append("ALTER TABLE ALTER COLUMN ").Append(columnName).Append(" failed because one or more objects access this column.");
        return new(sb.ToString(), 5074, 16, 1);
    }

    /// <summary>
    /// Mimics SQL Server error 515: <c>ALTER COLUMN</c> flipped a column to
    /// NOT NULL but at least one existing row holds NULL in that column.
    /// Probe-confirmed wording reuses the standard INSERT-NULL message
    /// (<c>Cannot insert the value NULL into column 'X', table 'Y'</c>),
    /// followed by the standard "statement has been terminated" line.
    /// </summary>
    internal static SimulatedSqlException AlterColumnNullInNonNullColumn(string columnName, string qualifiedTableName) =>
        new($"Cannot insert the value NULL into column '{columnName}', table '{qualifiedTableName}'; column does not allow nulls. UPDATE fails.", 515, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 15151: an unknown principal name was referenced
    /// (GRANT/REVOKE/DENY ... TO &lt;unknown&gt;, ALTER ROLE ... ADD MEMBER &lt;unknown&gt;,
    /// CREATE USER ... FROM LOGIN &lt;unknown&gt;, etc.). Probe-confirmed wording.
    /// </summary>
    internal static SimulatedSqlException CannotFindPrincipal(string name) =>
        new($"Cannot find the user, login, role, or principal '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15023: <c>CREATE USER name</c> or <c>CREATE ROLE name</c>
    /// when a principal of that name already exists in the database. Probe-confirmed
    /// wording (the message is identical for the two CREATE cases; SQL Server uses
    /// the principal-type column to disambiguate in catalog views).
    /// </summary>
    internal static SimulatedSqlException PrincipalAlreadyExists(string name) =>
        new($"User, group, or role '{name}' already exists in the current database.", 15023, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15025: <c>CREATE LOGIN name</c> when a server
    /// principal of that name already exists. Wording is docs-derived (the
    /// reference instance's login lacks the server permission to reach this
    /// check, reporting Msg 15247 instead).
    /// </summary>
    internal static SimulatedSqlException ServerPrincipalAlreadyExists(string name) =>
        new($"The server principal '{name}' already exists.", 15025, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for <c>ALTER LOGIN</c> / <c>DROP LOGIN</c>
    /// on a nonexistent login. Same number as <see cref="CannotFindPrincipal"/>
    /// but a distinct verb-bearing login wording — probe-confirmed against SQL
    /// Server 2025: <c>Cannot alter the login 'x', because it does not exist
    /// or you do not have permission.</c> (and the same with "drop").
    /// </summary>
    internal static SimulatedSqlException CannotAlterOrDropLogin(string verb, string name) =>
        new($"Cannot {verb} the login '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15517: <c>EXECUTE AS USER = 'x'</c> when the
    /// target database principal doesn't exist, can't be impersonated, or the
    /// caller lacks IMPERSONATE. The name is double-quoted. Probe-confirmed
    /// (2026-07-21) — including the quirk that <c>EXECUTE AS USER = 'dbo'</c>
    /// raises this even for a sysadmin session.
    /// </summary>
    internal static SimulatedSqlException CannotExecuteAsDatabasePrincipal(string name) =>
        new($"Cannot execute as the database principal because the principal \"{name}\" does not exist, this type of principal cannot be impersonated, or you do not have permission.", 15517, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15406: <c>EXECUTE AS LOGIN = 'l'</c> when the
    /// target server principal doesn't exist, can't be impersonated, or the
    /// caller lacks IMPERSONATE. The name is double-quoted. Probe-confirmed
    /// (2026-07-21).
    /// </summary>
    internal static SimulatedSqlException CannotExecuteAsServerPrincipal(string name) =>
        new($"Cannot execute as the server principal because the principal \"{name}\" does not exist, this type of principal cannot be impersonated, or you do not have permission.", 15406, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 916: a session running under a restricted
    /// security context (an impersonated database user, or an authenticated
    /// login mapped to a non-<c>dbo</c> user) tried to switch databases via
    /// <c>USE</c> / <c>ChangeDatabase</c>. The principal name is a login name,
    /// or the WITHOUT-LOGIN user's <c>S-1-9-3-…</c> SID string. Session stays in
    /// the current database. Probe-confirmed (2026-07-21): severity 14, state 2.
    /// </summary>
    internal static SimulatedSqlException CannotAccessDatabaseUnderSecurityContext(string principalName, string databaseName) =>
        new($"The server principal \"{principalName}\" is not able to access the database \"{databaseName}\" under the current security context.", 916, 14, 2);

    /// <summary>
    /// Mimics SQL Server error 18456: an in-process connection-string login
    /// (<c>User ID=</c>) failed to authenticate against the <c>CREATE LOGIN</c>
    /// registry. Same severity-14 / state-1 shape the TDS endpoint writes
    /// (which emits the token directly rather than through this factory).
    /// </summary>
    internal static SimulatedSqlException LoginFailed(string userName) =>
        new($"Login failed for user '{userName}'.", 18456, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 4060: a login couldn't open the requested
    /// database (missing, or not accessible to the mapped principal). The
    /// database name is double-quoted, severity 11. On the wire this is
    /// followed by a Msg 18456; the in-process front door surfaces the 4060
    /// alone (single-error divergence documented in <c>permissions.md</c>).
    /// </summary>
    internal static SimulatedSqlException CannotOpenDatabaseRequestedByLogin(string databaseName) =>
        new($"Cannot open database \"{databaseName}\" requested by the login. The login failed.", 4060, 11, 1);

    /// <summary>
    /// Mimics SQL Server error 2749: an <c>ALTER COLUMN</c> on an IDENTITY
    /// column tried to change the underlying type to something outside the
    /// integer / decimal-scale-0 family. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException IdentityColumnMustBeIntegerType(string columnName) =>
        new($"Identity column '{columnName}' must be of data type int, bigint, smallint, tinyint, or decimal or numeric with a scale of 0, unencrypted, and constrained to be nonnullable.", 2749, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 13599: <c>ALTER COLUMN</c> targeted a column
    /// declared <c>GENERATED ALWAYS AS ROW START / END</c> on a
    /// system-versioned temporal table. Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException PeriodColumnCannotBeAltered(string columnName) =>
        new($"Period column '{columnName}' in a system-versioned temporal table cannot be altered.", 13599, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13510: <c>SYSTEM_VERSIONING = ON</c> was
    /// requested on a table without a <c>PERIOD FOR SYSTEM_TIME</c> declaration
    /// (and with no <c>LEDGER=ON</c> option). Probe-confirmed wording against
    /// SQL Server 2025, including the state split — <c>CREATE TABLE … WITH</c>
    /// reports state 2, <c>ALTER TABLE … SET</c> state 1.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningRequiresPeriod(byte state = 2) =>
        new($"Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.", 13510, 16, state);

    /// <summary>
    /// Mimics SQL Server error 12002: <c>CREATE SPATIAL INDEX</c> referenced a
    /// column whose type isn't <c>geometry</c> or <c>geography</c>. Probe-
    /// confirmed wording against SQL Server 2025 (note the trailing space
    /// before the period in `or geography .`, which the real server emits).
    /// </summary>
    internal static SimulatedSqlException SpatialIndexRequiresSpatialColumn(string columnName, string tableName) =>
        new($"The requested spatial index on column '{columnName}' of table '{tableName}' could not be created because the column type is not geometry or geography . Specify a column name that refers to a column with a geometry or geography data type.", 12002, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 6314: <c>XML_SCHEMA_NAMESPACE</c> named a
    /// schema/collection pair that doesn't resolve. Probe-confirmed wording
    /// against SQL Server 2025, including the space before the colon and
    /// that only the collection name (not the relational schema) is quoted;
    /// real raises this even for the built-in <c>sys</c> collection.
    /// </summary>
    internal static SimulatedSqlException XmlSchemaCollectionNotInMetadata(string collectionName) =>
        new($"Collection specified does not exist in metadata : '{collectionName}'", 6314, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15225: <c>sp_rename</c> could not find the
    /// object to rename (the table / object path, i.e. a NULL <c>@objtype</c>).
    /// Severity 11, state 1, probe-confirmed wording against SQL Server 2025
    /// (2026-07-23) — <paramref name="itemType"/> renders <c>(null)</c> when
    /// <c>@objtype</c> was omitted, else the supplied type verbatim. Attributed
    /// to <c>sp_rename</c> so <c>ERROR_PROCEDURE()</c> / <c>SqlException.Procedure</c>
    /// match real.
    /// </summary>
    internal static SimulatedSqlException RenameItemNotFound(string objectName, string databaseName, string itemType) =>
        RenameError($"No item by the name of '{objectName}' could be found in the current database '{databaseName}', given that @itemtype was input as '{itemType}'.", 15225);

    /// <summary>
    /// Mimics SQL Server error 15248: <c>sp_rename</c>'s <c>@objname</c> is
    /// ambiguous or the supplied <c>@objtype</c> is wrong — the path real takes
    /// when a COLUMN / INDEX target can't be resolved (missing parent table, or
    /// no such column / index on an existing table). Severity 11, state 1,
    /// probe-confirmed wording (SQL Server 2025, 2026-07-23).
    /// </summary>
    internal static SimulatedSqlException RenameAmbiguousOrWrongType(string objectType) =>
        RenameError($"Either the parameter @objname is ambiguous or the claimed @objtype ({objectType}) is wrong.", 15248);

    /// <summary>
    /// Mimics SQL Server error 15335: <c>sp_rename</c>'s <c>@newname</c> already
    /// exists — <paramref name="kind"/> is <c>COLUMN</c> / <c>INDEX</c> for those
    /// paths and the ungrammatical <c>object</c> for the table / object path
    /// (matched verbatim against real: "already in use as a object name").
    /// Severity 11, state 1, probe-confirmed wording (SQL Server 2025,
    /// 2026-07-23).
    /// </summary>
    internal static SimulatedSqlException RenameDuplicateName(string newName, string kind) =>
        RenameError($"Error: The new name '{newName}' is already in use as a {kind} name and would cause a duplicate that is not permitted.", 15335);

    private static SimulatedSqlException RenameError(string message, int number) =>
        new(message, new SimulatedError(@class: 11, lineNumber: 0, message, number, procedure: "sp_rename", server: SimulatedDbConnection.DataSourceName, source: SourceName, state: 1));

    /// <summary>
    /// Mimics SQL Server error 1934: the statement touches a feature that
    /// requires a fixed set of SET options and the session has one of them
    /// wrong. <paramref name="verb"/> names the statement real echoes —
    /// <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c> /
    /// <c>SELECT</c> / <c>CREATE TABLE</c> / <c>ALTER TABLE</c> /
    /// <c>CREATE INDEX</c> / <c>CREATE PRIMARY XML INDEX</c> — and
    /// <paramref name="options"/> the offending option names, quoted and
    /// comma-separated. Only the <c>QUOTED_IDENTIFIER</c> component is
    /// enforced; real's full required set is
    /// <c>ANSI_NULLS</c> / <c>ANSI_PADDING</c> / <c>ANSI_WARNINGS</c> /
    /// <c>ARITHABORT</c> / <c>CONCAT_NULL_YIELDS_NULL</c> ON and
    /// <c>NUMERIC_ROUNDABORT</c> OFF.
    /// Probe-confirmed wording, class 16 state 1 (SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException IncorrectSetOptions(string verb, string options) =>
        new($"{verb} failed because the following SET options have incorrect settings: '{options}'. Verify that SET options are correct for use with indexed views and/or indexes on computed columns and/or filtered indexes and/or query notifications and/or XML data type methods and/or spatial index operations.", 1934, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1934's spatial-only wording: <c>CREATE SPATIAL
    /// INDEX</c> names only spatial index operations in its verify clause,
    /// where every other 1934 site lists the full feature set
    /// (probe-confirmed against SQL Server 2025). The verb is still the bare
    /// <c>CREATE INDEX</c>.
    /// </summary>
    internal static SimulatedSqlException IncorrectSetOptionsForSpatialIndex(string options) =>
        new($"CREATE INDEX failed because the following SET options have incorrect settings: '{options}'. Verify that SET options are correct for use with spatial index operations.", 1934, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1935: <c>CREATE INDEX</c> targets a view whose
    /// own creation-time SET-option capture is wrong, which no amount of
    /// fixing the current session repairs — the view has to be recreated.
    /// Distinct from Msg 1934, which reports the <i>session's</i> settings:
    /// real raises 1935 for a view created under <c>QUOTED_IDENTIFIER OFF</c>
    /// even when the indexing session has it ON (probe-confirmed). The name
    /// is unqualified.
    /// </summary>
    internal static SimulatedSqlException CannotCreateIndexObjectCreatedWithOptionsOff(string viewName, string options) =>
        new($"Cannot create index. Object '{viewName}' was created with the following SET options off: '{options}'.", 1935, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151's user variant: <c>CREATE SCHEMA …
    /// AUTHORIZATION</c> named a principal the database doesn't carry. Probe-confirmed verbatim — the
    /// wording names the <i>user</i> where the sibling forms name the object
    /// kind they were looking for.
    /// </summary>
    internal static SimulatedSqlException CannotFindUser(string name) =>
        new($"Cannot find the user '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15151's object variant, which is what a
    /// <c>GRANT</c> element inside a <c>CREATE SCHEMA</c> reports for a
    /// securable that doesn't resolve. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForGrant(string name) =>
        new($"Cannot find the object '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 2759 — the trailer real appends to <i>every</i>
    /// failure inside a <c>CREATE SCHEMA</c>, whether the offending error came
    /// from the schema name itself, the AUTHORIZATION principal, or one of the
    /// statement's elements. Probe-confirmed verbatim in all four shapes.
    /// </summary>
    internal static SimulatedSqlException CreateSchemaFailed() =>
        new("CREATE SCHEMA failed due to previous errors.", 2759, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 15138: <c>DROP USER</c> / <c>DROP ROLE</c> of a
    /// principal that owns a schema. Probe-confirmed verbatim — the message
    /// names neither the principal nor the schema.
    /// </summary>
    internal static SimulatedSqlException PrincipalOwnsASchema() =>
        new("The database principal owns a schema in the database, and cannot be dropped.", 15138, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 155's ALTER TABLE wording: an unrecognized
    /// option name inside <c>ALTER TABLE … REBUILD WITH (…)</c>.
    /// Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException UnrecognizedAlterTableOption(string optionName) =>
        new($"'{optionName}' is not a recognized ALTER TABLE option.", 155, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 4925: <c>ALTER TABLE … ALTER COLUMN … ADD
    /// ROWGUIDCOL</c> where the table already carries one. Real follows it with
    /// a terminating Msg 1750, which the simulator omits. Probe-confirmed
    /// verbatim.
    /// </summary>
    internal static SimulatedSqlException TableAlreadyHasRowGuidCol(string tableName) =>
        new($"ALTER TABLE ALTER COLUMN ADD ROWGUIDCOL failed because a column already exists in table '{tableName}' with ROWGUIDCOL property.", 4925, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4926: <c>DROP ROWGUIDCOL</c> where no column
    /// carries the property. The message names the table but not the column the
    /// statement asked about — probe-confirmed, so real drops <i>the</i>
    /// ROWGUIDCOL rather than the named column's.
    /// </summary>
    internal static SimulatedSqlException NoRowGuidColToDrop(string tableName) =>
        new($"ALTER TABLE ALTER COLUMN DROP ROWGUIDCOL failed because a column does not exist in table '{tableName}' with ROWGUIDCOL property.", 4926, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1731: a column real refuses to make sparse. The
    /// message carries the whole rule rather than naming which half was
    /// violated, so nullability, IDENTITY, ROWGUIDCOL and the refused types all
    /// report it identically. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotCreateSparseColumn(string columnName, string tableName) =>
        new($"Cannot create the sparse column '{columnName}' in the table '{tableName}' because an option or data type specified is not valid. A sparse column must be nullable and cannot have the ROWGUIDCOL, IDENTITY, or FILESTREAM properties. A sparse column cannot be of the following data types: text, ntext, image, geometry, geography, or user-defined type.", 1731, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 11410: a column carrying a DEFAULT can't be made
    /// sparse — a separate refusal from <see cref="CannotCreateSparseColumn"/>,
    /// with its own advice. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException CannotMakeColumnSparseWithDefault(string columnName, string tableName) =>
        new($"Cannot modify the column '{columnName}' in the table '{tableName}' to a sparse column because the column has a default or rule bound to it. Unbind the rule or default from the column before designating the column as sparse.", 11410, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13592: <c>ALTER TABLE … DROP PERIOD FOR
    /// SYSTEM_TIME</c> while the table is still system-versioned. The period is
    /// what versioning reads, so it can only go once versioning is off.
    /// Probe-confirmed verbatim, naming the table three-part.
    /// </summary>
    internal static SimulatedSqlException CannotDropPeriodWhileVersioned(string qualifiedTableName) =>
        new($"Cannot drop SYSTEM_TIME period from table '{qualifiedTableName}' when SYSTEM_VERSIONING is ON.", 13592, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 13593: <c>DROP PERIOD FOR SYSTEM_TIME</c> on a
    /// table that carries no period — a table that never had one and one whose
    /// period was already dropped report identically. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException PeriodDoesNotExist(string qualifiedTableName) =>
        new($"SYSTEM_TIME period cannot be dropped from table '{qualifiedTableName}', because SYSTEM_TIME period does not exist.", 13593, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4936: a <c>PERSISTED</c> computed column whose
    /// expression is nondeterministic. Probe-confirmed verbatim at every
    /// declaration site — <c>CREATE TABLE</c>'s inline form, <c>ALTER TABLE …
    /// ADD</c> and <c>ALTER COLUMN … ADD PERSISTED</c> — with the table named
    /// unqualified.
    /// </summary>
    internal static SimulatedSqlException ComputedColumnCannotBePersisted(string columnName, string tableName) =>
        new($"Computed column '{columnName}' in table '{tableName}' cannot be persisted because the column is non-deterministic.", 4936, 16, 1);
}
