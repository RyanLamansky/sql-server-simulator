namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server's verbose truncation error (Msg 2628): a string value
    /// would not fit within the destination column's declared maximum length.
    /// The displayed "truncated value" is the prefix of the offending value
    /// clipped to the column's max length.
    /// </summary>
    /// <remarks>
    /// Introduced in SQL Server 2019 (compatibility level 150) behind trace
    /// flag 460 or <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON</c>;
    /// became the default in SQL Server 2022+ (compatibility level 160+),
    /// superseding the legacy <see cref="StringOrBinaryWouldBeTruncatedLegacy"/>
    /// (Msg 8152). The simulator selects between the two via
    /// <see cref="SimulatedDbConnection.IsVerboseTruncationActive"/>.
    /// </remarks>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncated(string tableName, string columnName, string value, int max)
    {
        var prefix = value.Length <= max ? value : value[..max];
        return new($"String or binary data would be truncated in table '{tableName}', column '{columnName}'. Truncated value: '{prefix}'.", 2628, 16, 1);
    }

    /// <summary>
    /// Binary overload of the verbose truncation factory: renders the
    /// truncated prefix as a SQL hex literal (<c>0xABCD…</c>), matching SQL
    /// Server's varbinary formatting in Msg 2628.
    /// </summary>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncated(string tableName, string columnName, byte[] value, int max)
    {
        var prefix = value.Length <= max ? value : value[..max];
        var hex = $"0x{Convert.ToHexString(prefix)}";
        return new($"String or binary data would be truncated in table '{tableName}', column '{columnName}'. Truncated value: '{hex}'.", 2628, 16, 1);
    }

    /// <summary>
    /// Mimics the legacy SQL Server truncation error (Msg 8152): same trigger
    /// as the verbose factory above but without the table, column, or value
    /// detail. Default behavior on compatibility levels before 160 (SQL Server
    /// 2022) and on older levels with the verbose option off.
    /// </summary>
    internal static SimulatedSqlException StringOrBinaryWouldBeTruncatedLegacy() =>
        new("String or binary data would be truncated.", 8152, 16, 14);

    /// <summary>
    /// Mimics SQL Server error 511: an INSERT or UPDATE produced a row that
    /// exceeds the per-row size limit even after pushing every variable-length
    /// column to row-overflow / LOB storage. Distinct from Msg 1701 (schema
    /// is impossible) — Msg 511 fires per-row when the values supplied happen
    /// to exceed the limit despite the schema being legal.
    /// </summary>
    internal static SimulatedSqlException RowSizeExceedsAllowableMaximum(int requested, int max) =>
        new($"Cannot create a row of size {requested} which is greater than the allowable maximum row size of {max}.", 511, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 515: an INSERT or UPDATE supplied (or fell
    /// through to) <c>NULL</c> for a column whose declaration disallows
    /// nulls. The fixed text uses the database-qualified table name; the
    /// simulator's single-database model emits <c>"claude.dbo.&lt;t&gt;"</c>'s
    /// shape with the simulator's default database name.
    /// <paramref name="verb"/> picks between <c>"INSERT"</c> (the default)
    /// and <c>"UPDATE"</c> for the trailing <c>"… fails."</c> clause —
    /// real SQL Server emits the per-statement verb verbatim there.
    /// </summary>
    internal static SimulatedSqlException CannotInsertNull(string columnName, string tableName, string verb = "INSERT") =>
        new($"Cannot insert the value NULL into column '{columnName}', table '{Simulation.DefaultDatabaseName}.dbo.{tableName}'; column does not allow nulls. {verb} fails.", 515, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 547: an INSERT / UPDATE / MERGE row failed a
    /// CHECK constraint's predicate. SQL Server's wording adds a
    /// <c>column 'X'</c> suffix only for inline single-column CHECKs;
    /// table-level CHECK omits it — matching the real-server probe. The DB
    /// name slot uses <see cref="Simulation.DefaultDatabaseName"/>.
    /// </summary>
    internal static SimulatedSqlException CheckConstraintViolation(string constraintName, string tableName, string? inlineColumn, string verb = "INSERT")
    {
        var columnSuffix = inlineColumn is null ? "" : $", column '{inlineColumn}'";
        return new($"The {verb} statement conflicted with the CHECK constraint \"{constraintName}\". The conflict occurred in database \"{Simulation.DefaultDatabaseName}\", table \"dbo.{tableName}\"{columnSuffix}.", 547, 16, 0);
    }

    /// <summary>
    /// Mimics SQL Server error 2627: an INSERT or UPDATE produced a row whose
    /// PRIMARY KEY or UNIQUE-constraint key tuple already existed in the
    /// table. SQL Server uses Msg 2627 for both PK and UNIQUE *constraint*
    /// violations (Msg 2601 is for unique-index violations from
    /// <c>CREATE UNIQUE INDEX</c>, raised via
    /// <see cref="ViolationOfUniqueIndex"/>).
    /// <paramref name="kindWord"/> selects between <c>"PRIMARY KEY"</c> and
    /// <c>"UNIQUE KEY"</c>; <paramref name="formattedKeyValues"/> is the
    /// rendered tuple text without enclosing parens (e.g. <c>"1, &lt;NULL&gt;"</c>).
    /// </summary>
    internal static SimulatedSqlException ViolationOfKeyConstraint(string kindWord, string constraintName, string tableName, string formattedKeyValues) =>
        new($"Violation of {kindWord} constraint '{constraintName}'. Cannot insert duplicate key in object 'dbo.{tableName}'. The duplicate key value is ({formattedKeyValues}).", 2627, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 2601: an INSERT or UPDATE produced a row
    /// whose key tuple already existed in a <c>CREATE UNIQUE INDEX</c>-
    /// declared index. Distinct from Msg 2627 (unique <em>constraint</em>
    /// violation) — same scenario semantically, different error number
    /// per the surface. Probe-confirmed wording (the trailing
    /// <c>"The statement has been terminated."</c> phrase is emitted as a
    /// separate informational line by real SQL Server; the simulator
    /// folds it into the primary error message).
    /// </summary>
    internal static SimulatedSqlException ViolationOfUniqueIndex(string indexName, string qualifiedTableName, string formattedKeyValues) =>
        new($"Cannot insert duplicate key row in object '{qualifiedTableName}' with unique index '{indexName}'. The duplicate key value is ({formattedKeyValues}).", 2601, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 547 on the child side: an <c>INSERT</c> /
    /// <c>UPDATE</c> / <c>MERGE</c> placed a value in a FOREIGN KEY column
    /// that doesn't match any row of the referenced parent. Probe-confirmed
    /// wording variants against SQL Server 2025 on 2026-05-13:
    /// <list type="bullet">
    /// <item><c>FOREIGN KEY</c> for a normal FK; <c>FOREIGN KEY SAME TABLE</c>
    /// when the child and parent are the same table (self-reference).</item>
    /// <item>Single-column FK appends <c>, column 'X'</c>; multi-column FK
    /// omits the column phrase entirely.</item>
    /// </list>
    /// </summary>
    internal static SimulatedSqlException ForeignKeyConflictOnChild(
        string verb, string constraintName, string referencedSchema, string referencedTable, string? referencedColumn, bool isSelfReferencing)
    {
        var keyKindWord = isSelfReferencing ? "FOREIGN KEY SAME TABLE" : "FOREIGN KEY";
        var columnSuffix = referencedColumn is null ? "" : $", column '{referencedColumn}'";
        return new(
            $"The {verb} statement conflicted with the {keyKindWord} constraint \"{constraintName}\". The conflict occurred in database \"{Simulation.DefaultDatabaseName}\", table \"{referencedSchema}.{referencedTable}\"{columnSuffix}.",
            547, 16, 0);
    }

    /// <summary>
    /// Mimics SQL Server error 547 on the parent side: a <c>DELETE</c> /
    /// <c>UPDATE</c> on a referenced row left a child orphaned (the FK action
    /// is <c>NO ACTION</c>). Wording difference from the child-side variant:
    /// the constraint phrase is <c>REFERENCE constraint</c> rather than
    /// <c>FOREIGN KEY constraint</c>, and the table / column slot describes
    /// the child (referring) side, not the referenced side (probe-confirmed).
    /// </summary>
    internal static SimulatedSqlException ForeignKeyConflictOnParent(
        string verb, string constraintName, string childSchema, string childTable, string? childColumn)
    {
        var columnSuffix = childColumn is null ? "" : $", column '{childColumn}'";
        return new(
            $"The {verb} statement conflicted with the REFERENCE constraint \"{constraintName}\". The conflict occurred in database \"{Simulation.DefaultDatabaseName}\", table \"{childSchema}.{childTable}\"{columnSuffix}.",
            547, 16, 0);
    }

    /// <summary>
    /// Mimics SQL Server error 547 in the <c>ALTER TABLE ADD CONSTRAINT</c>
    /// existing-data-validation variant for a new FOREIGN KEY. Same shape as
    /// <see cref="ForeignKeyConflictOnChild"/> but with the verb fixed to
    /// <c>"ALTER TABLE"</c>. Probe-confirmed against SQL Server 2025
    /// (2026-05-13).
    /// </summary>
    internal static SimulatedSqlException AlterForeignKeyConflict(string constraintName, string referencedTableQualified, string? referencedColumn, bool isSelfReferencing)
    {
        var keyKindWord = isSelfReferencing ? "FOREIGN KEY SAME TABLE" : "FOREIGN KEY";
        var columnSuffix = referencedColumn is null ? "" : $", column '{referencedColumn}'";
        return new(
            $"The ALTER TABLE statement conflicted with the {keyKindWord} constraint \"{constraintName}\". The conflict occurred in database \"{Simulation.DefaultDatabaseName}\", table \"{referencedTableQualified}\"{columnSuffix}.",
            547, 16, 0);
    }

    /// <summary>
    /// Mimics SQL Server error 547 in the <c>ALTER TABLE ADD CONSTRAINT</c>
    /// existing-data-validation variant for a new CHECK constraint. Same
    /// shape as the runtime CHECK variant but with the verb fixed to
    /// <c>"ALTER TABLE"</c>. Probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException AlterCheckConstraintConflict(string constraintName, string tableName, string? inlineColumn)
    {
        var columnSuffix = inlineColumn is null ? "" : $", column '{inlineColumn}'";
        return new(
            $"The ALTER TABLE statement conflicted with the CHECK constraint \"{constraintName}\". The conflict occurred in database \"{Simulation.DefaultDatabaseName}\", table \"dbo.{tableName}\"{columnSuffix}.",
            547, 16, 0);
    }
}
