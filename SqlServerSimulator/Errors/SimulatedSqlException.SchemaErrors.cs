using SqlServerSimulator.Storage;

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
    /// Mimics SQL Server error 11720: <c>NEXT VALUE FOR</c> was used in a
    /// clause that disallows it (TOP / OVER / OUTPUT / ON / WHERE / GROUP BY
    /// / HAVING / ORDER BY). Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException NextValueForNotAllowedHere() =>
        new("NEXT VALUE FOR function is not allowed in the TOP, OVER, OUTPUT, ON, WHERE, GROUP BY, HAVING, or ORDER BY clauses.", 11720, 15, 1);

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
    /// Mimics SQL Server error 8147: a column cannot be both <c>IDENTITY</c>
    /// and <c>NULL</c>.
    /// </summary>
    internal static SimulatedSqlException IdentityOnNullableColumn(string columnName, string tableName) =>
        new($"Could not create IDENTITY attribute on nullable column '{columnName}', table '{tableName}'.", 8147, 16, 1);

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
        new($"Cannot add multiple PRIMARY KEY constraints to table '{tableName}'.", 8110, 16, 0);

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
    /// <c>ALTER TABLE … SET (SYSTEM_VERSIONING = ON …)</c> targeted a base
    /// table that doesn't have a <c>PERIOD FOR SYSTEM_TIME</c> declaration.
    /// SQL Server reports a related Msg 13558 family; the simulator surfaces
    /// the requirement as a single canonical wording until verbatim
    /// probe-matching lands.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningOnRequiresPeriod(string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because table '{qualifiedTableName}' does not have a PERIOD FOR SYSTEM_TIME declaration.", 13558, 16, 1);

    /// <summary>
    /// <c>ALTER TABLE … SET (SYSTEM_VERSIONING = ON …)</c> targeted a base
    /// table that's already system-versioned. SQL Server's matching error is
    /// in the 13530+ range; the simulator's wording carries the same intent
    /// until verbatim probe-matching lands.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningAlreadyOn(string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because table '{qualifiedTableName}' already has SYSTEM_VERSIONING turned ON.", 13530, 16, 1);

    /// <summary>
    /// <c>ALTER TABLE … SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = name))</c>
    /// named a history table that's already serving as another base table's
    /// history sibling (<c>IsHistoryTable=true</c>) or is itself a
    /// system-versioned base. SQL Server's matching error is in the 13533
    /// family; canonical simulator wording until verbatim probe-matching.
    /// </summary>
    internal static SimulatedSqlException HistoryTableAlreadyInUse(string qualifiedTableName) =>
        new($"Setting SYSTEM_VERSIONING to ON failed because history table '{qualifiedTableName}' is already in use as a temporal table sibling.", 13533, 16, 1);

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
    /// Mimics SQL Server error 1776: a FOREIGN KEY's referenced column list
    /// doesn't match any PRIMARY KEY or UNIQUE constraint on the referenced
    /// table. Real SQL Server pairs this with a trailing Msg 1750 / 1753
    /// "Could not create constraint or index"; the simulator collapses the
    /// pair into a single Msg 1776 since the second is purely informational.
    /// Probe-confirmed verbatim wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForeignKeyNoMatchingKey(string referencedTable, string foreignKeyName) =>
        new($"There are no primary or candidate keys in the referenced table '{referencedTable}' that match the referencing column list in the foreign key '{foreignKeyName}'.", 1776, 16, 0);

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
    /// (distinct from the runtime INSERT-time Msg 2627). The simulator
    /// renders the key tuple via <see cref="SqlValue"/>'s default ToString
    /// per column, comma-joined inside parens.
    /// </summary>
    internal static SimulatedSqlException DuplicateKeyOnCreate(string qualifiedTableName, string indexName, SqlValue[] keyValues)
    {
        var rendered = string.Join(", ", keyValues.Select(v => v.IsNull ? "<NULL>" : v.ToString()));
        return new($"The CREATE UNIQUE INDEX statement terminated because a duplicate key was found for the object name '{qualifiedTableName}' and the index name '{indexName}'. The duplicate key value is ({rendered}).", 1505, 16, 1);
    }

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
    /// Mimics SQL Server error 1088: <c>CREATE INDEX</c> (or any
    /// catalog-scoped reference) named a target object that doesn't exist.
    /// Distinct from Msg 208 (which surfaces from DML) — Msg 1088 is the
    /// CREATE-INDEX / sp_help-shaped diagnostic. State 12 probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException CannotFindObjectForCreateIndex(string qualifiedName) =>
        new($"Cannot find the object \"{qualifiedName}\" because it does not exist or you do not have permissions.", 1088, 16, 12);

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
    internal static SimulatedSqlException DropColumnHasDependenciesMixed(string columnName, IReadOnlyList<(string Name, bool IsIndex)> blockers)
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
    internal static SimulatedSqlException AlterColumnHasDependencies(string columnName, IReadOnlyList<(string Name, AlterColumnBlockerKind Kind)> blockers)
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
    /// Mimics SQL Server error 13510: <c>CREATE TABLE ... WITH (SYSTEM_VERSIONING = ON)</c>
    /// was issued without an accompanying <c>PERIOD FOR SYSTEM_TIME</c> declaration
    /// (and with no <c>LEDGER=ON</c> option). Probe-confirmed wording against SQL
    /// Server 2025.
    /// </summary>
    internal static SimulatedSqlException SystemVersioningRequiresPeriod() =>
        new($"Cannot set SYSTEM_VERSIONING to ON when SYSTEM_TIME period is not defined and the LEDGER=ON option is not specified.", 13510, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 12002: <c>CREATE SPATIAL INDEX</c> referenced a
    /// column whose type isn't <c>geometry</c> or <c>geography</c>. Probe-
    /// confirmed wording against SQL Server 2025 (note the trailing space
    /// before the period in `or geography .`, which the real server emits).
    /// </summary>
    internal static SimulatedSqlException SpatialIndexRequiresSpatialColumn(string columnName, string tableName) =>
        new($"The requested spatial index on column '{columnName}' of table '{tableName}' could not be created because the column type is not geometry or geography . Specify a column name that refers to a column with a geometry or geography data type.", 12002, 16, 1);
}
