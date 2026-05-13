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
}
