namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 155 at state 2: a <c>CHANGE_RETENTION</c> unit
    /// other than <c>DAYS</c> / <c>HOURS</c> / <c>MINUTES</c> — the singular
    /// spellings included (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException UnrecognizedChangeRetentionUnit(string unit) =>
        new($"'{unit}' is not a recognized CHANGE RETENTION UNIT option.", 155, 15, 2);

    /// <summary>
    /// Mimics SQL Server error 1718: <c>ALTER TABLE … ENABLE CHANGE_TRACKING</c>
    /// in a database whose change tracking is off, <c>#temp</c> tables naming
    /// <c>tempdb</c>.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingNotEnabledOnDatabaseForTable(string databaseName, string tableName) =>
        new($"Change tracking must be enabled on database '{databaseName}' before it can be enabled on table '{tableName}'.", 1718, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 3735, then Msg 3727: dropping the primary key of
    /// a change-tracked table.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackedPrimaryKeyDrop(string constraintName, string tableName) =>
        FollowedByConstraintNotDropped(new($"The primary key constraint '{constraintName}' on table '{tableName}' cannot be dropped because change tracking is enabled on the table. Change tracking requires a primary key constraint on the table. Disable change tracking before dropping the constraint.", 3735, 16, 1));

    /// <summary>Mimics SQL Server error 4996: <c>ENABLE CHANGE_TRACKING</c> on a tracked table.</summary>
    internal static SimulatedSqlException ChangeTrackingAlreadyEnabledOnTable(string tableName) =>
        new($"Change tracking is already enabled for table '{tableName}'.", 4996, 16, 1);

    /// <summary>Mimics SQL Server error 4997: <c>ENABLE CHANGE_TRACKING</c> on a table without a primary key.</summary>
    internal static SimulatedSqlException ChangeTrackingRequiresPrimaryKey(string tableName) =>
        new($"Cannot enable change tracking on table '{tableName}'. Change tracking requires a primary key on the table. Create a primary key on the table before enabling change tracking.", 4997, 16, 1);

    /// <summary>Mimics SQL Server error 4998: <c>DISABLE CHANGE_TRACKING</c> on an untracked table.</summary>
    internal static SimulatedSqlException ChangeTrackingNotEnabledOnTableForDisable(string tableName) =>
        new($"Change tracking is not enabled on table '{tableName}'.", 4998, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 5088, then Msg 5069: <c>SET CHANGE_TRACKING = ON</c>
    /// on a database already tracking, with or without an options block.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingAlreadyEnabledOnDatabase(string databaseName) =>
        FollowedByAlterDatabaseFailed(new($"Change tracking is already enabled for database '{databaseName}'.", 5088, 16, 1));

    /// <summary>
    /// Mimics SQL Server error 5089, then Msg 5069: a database that isn't
    /// tracking asked to stop (state 1) or to change its options (state 2).
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingDisabledOnDatabase(string databaseName, byte state) =>
        FollowedByAlterDatabaseFailed(new($"Change tracking is disabled for database '{databaseName}'. Change tracking must be enabled on a database to modify change tracking settings.", 5089, 16, state));

    /// <summary>
    /// Mimics SQL Server error 5090, then Msg 5069: any <c>SET CHANGE_TRACKING</c>
    /// form naming one of the four system databases, <c>msdb</c> included.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingOnSystemDatabase(string databaseName) =>
        FollowedByAlterDatabaseFailed(new($"Database '{databaseName}' is a system database. Change tracking settings cannot be modified for system databases.", 5090, 16, 1));

    /// <summary>Mimics SQL Server error 5091: one change tracking option written twice in the block.</summary>
    internal static SimulatedSqlException ChangeTrackingOptionRepeated(string optionName) =>
        new($"ALTER DATABASE change tracking option '{optionName}' was specified more than once. Each option can be specified only once.", 5091, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 5092: a <c>CHANGE_RETENTION</c> of zero, or one
    /// whose length in minutes passes <c>int</c>.
    /// </summary>
    internal static SimulatedSqlException ChangeRetentionOutOfRange(string optionName) =>
        new($"The value for change tracking option '{optionName}' is not valid. The value must be between 1 and 2147483647 minutes.", 5092, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 22101: a <c>CHANGE_TRACKING_IS_COLUMN_IN_MASK</c>
    /// mask shorter than the eight bytes the smallest real mask carries.
    /// </summary>
    internal static SimulatedSqlException InvalidChangeColumnsMask() =>
        new("The value supplied for the change_columns argument of CHANGE_TRACKING_IS_COLUMN_IN_MASK function is not valid. The value must be a bitmask returned by the CHANGETABLE(CHANGES ...) function.", 22101, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22103: a <c>CHANGETABLE(VERSION …)</c> value list
    /// whose length differs from its column list.
    /// </summary>
    internal static SimulatedSqlException ChangeTableVersionArgumentsInvalid() =>
        new("The arguments supplied are not valid for the VERSION option of the CHANGETABLE function.", 22103, 16, 1);

    /// <summary>Mimics SQL Server error 22104: a <c>CHANGETABLE</c> source written without an alias.</summary>
    internal static SimulatedSqlException ChangeTableRequiresAlias() =>
        new("A table returned by the CHANGETABLE function must be aliased.", 22104, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22105: <c>CHANGETABLE</c> over a table that isn't
    /// tracked, which is every table of a database whose tracking is off.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingNotEnabledOnTable(string tableName) =>
        new($"Change tracking is not enabled on table '{tableName}'.", 22105, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22107: <c>CHANGETABLE</c> over a view, a catalog
    /// view or a <c>#temp</c> table, named as written.
    /// </summary>
    internal static SimulatedSqlException ChangeTableObjectNotSupported(string writtenName) =>
        new($"Object '{writtenName}' is of a data type that is not supported by the CHANGETABLE function. The object must be a user-defined table.", 22107, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22109: a <c>WITH CHANGE_TRACKING_CONTEXT</c>
    /// variable that isn't <c>varbinary(n)</c> with n at most 128 (<c>binary(n)</c>
    /// passes).
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingContextTypeInvalid() =>
        new("The \"context\" argument for the CHANGE_TRACKING_CONTEXT WITH clause must be of type varbinary data type with a maximum length of 128.", 22109, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22110: a <c>CHANGETABLE(VERSION …)</c> column list
    /// whose length differs from the primary key's.
    /// </summary>
    internal static SimulatedSqlException ChangeTableVersionColumnCount(string tableName) =>
        new($"The number of columns specified in the CHANGETABLE(VERSION ...) function does not match the number of primary key columns for table '{tableName}'.", 22110, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22111: a <c>CHANGETABLE(VERSION …)</c> column
    /// outside the primary key, checked before the list's length.
    /// </summary>
    internal static SimulatedSqlException ChangeTableVersionColumnNotInKey(string columnName, string tableName) =>
        new($"The column '{columnName}' specified in the CHANGETABLE(VERSION ...) function is not part of the primary key for table '{tableName}'.", 22111, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 22114, then Msg 5069: <c>SET CHANGE_TRACKING</c>
    /// sharing its <c>SET</c> list with another option.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingCombinedWithOtherOptions() =>
        FollowedByAlterDatabaseFailed(new("Change tracking options for ALTER DATABASE cannot be combined with other ALTER DATABASE options.", 22114, 16, 1));

    /// <summary>
    /// Mimics SQL Server error 22115, then Msg 5069: <c>SET CHANGE_TRACKING = OFF</c>
    /// while a table still tracks.
    /// </summary>
    internal static SimulatedSqlException ChangeTrackingTablesStillEnabled(string databaseName) =>
        FollowedByAlterDatabaseFailed(new($"Change tracking is enabled for one or more tables in database '{databaseName}'. Disable change tracking on each table before disabling it for the database. Use the sys.change_tracking_tables catalog view to obtain a list of tables for which change tracking is enabled.", 22115, 16, 1));
}
