using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException IdentifierTooLong(ReadOnlySpan<char> first128)
        => new($"The identifier that starts with '{first128}' is too long. Maximum length is 128.", 103, 15, 4);

    internal static SimulatedSqlException InvalidColumnName(string name) => new($"Invalid column name '{name}'.", 207, 16, 1);

    // Msg 207 renders only the leaf identifier — real SQL Server drops any
    // table / alias qualifier (probe-confirmed: `col.is_replicated` surfaces
    // as "Invalid column name 'is_replicated'.").
    internal static SimulatedSqlException InvalidColumnName(MultiPartName name) => InvalidColumnName(name.Leaf);

    /// <summary>
    /// Mimics SQL Server's Msg 209 — fired when an unqualified column
    /// reference matches columns in more than one source after a JOIN.
    /// The fix is to add a qualifier (table or alias prefix) disambiguating
    /// which source the reference targets.
    /// </summary>
    internal static SimulatedSqlException AmbiguousColumnName(string name) =>
        new($"Ambiguous column name '{name}'.", 209, 16, 1);

    internal static SimulatedSqlException InvalidObjectName(MultiPartName name) => new($"Invalid object name '{name}'.", 208, 16, 1);

    internal static SimulatedSqlException MustDeclareScalarVariable(string name) => new($"Must declare the scalar variable \"@{name}\".", 137, 15, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 1087 — fired when a DML target or FROM source
    /// references a table-variable name (<c>@t</c>) that hasn't been
    /// <c>DECLARE</c>d in the current batch. Note the <c>@</c> prefix is
    /// included in the wording (probe-confirmed: <c>"Must declare the table
    /// variable \"@t\"."</c>). Class 15 State 2 — mirrors the
    /// <see cref="MustDeclareScalarVariable"/> shape since both are
    /// missing-variable errors at parse / bind time.
    /// </summary>
    internal static SimulatedSqlException MustDeclareTableVariable(string name) =>
        new($"Must declare the table variable \"{name}\".", 1087, 15, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 213 — fired when an OUTPUT INTO clause's
    /// projection-column count doesn't match the target's column count
    /// (probe-confirmed wording: <c>"Column name or number of supplied
    /// values does not match table definition."</c>). Real SQL Server uses
    /// the same Msg 213 for column-count / column-name mismatches in
    /// regular INSERT shapes; the simulator reuses it for OUTPUT INTO
    /// dispatch consistency.
    /// </summary>
    internal static SimulatedSqlException OutputIntoColumnCountMismatch() =>
        new("Column name or number of supplied values does not match table definition.", 213, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 134 — fired when a <c>DECLARE</c> names a
    /// variable that already exists in the batch (either a previous
    /// <c>DECLARE</c> or a SqlClient parameter of the same name —
    /// probe-confirmed parameters and declared variables share a
    /// namespace).
    /// </summary>
    internal static SimulatedSqlException VariableAlreadyDeclared(string name) =>
        new($"The variable name '@{name}' has already been declared. Variable names must be unique within a query batch or stored procedure.", 134, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 141 — fired when a <c>SELECT</c> mixes
    /// variable assignment (<c>@v = expr</c>) with non-assignment
    /// projection elements in the same projection list.
    /// </summary>
    internal static SimulatedSqlException SelectAssignmentMixedWithRetrieval() =>
        new("A SELECT statement that assigns a value to a variable must not be combined with data-retrieval operations.", 141, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 326 — a dotted name that reads both ways at
    /// once: a spatial column's property (<c>Location.Lat</c>) and a column of
    /// a source aliased with the same word. Probe-confirmed wording, which
    /// names the whole dotted identifier and then both readings.
    /// </summary>
    internal static SimulatedSqlException AmbiguousSpatialPropertyOrColumn(string qualifier, string leaf) =>
        new($"Multi-part identifier '{qualifier}.{leaf}' is ambiguous. Both columns '{qualifier}' and '{qualifier}.{leaf}' exist.", 326, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 4104: the OUTPUT clause references an
    /// identifier that doesn't exist in either the INSERTED/DELETED virtual
    /// tables or the MERGE source alias.
    /// </summary>
    internal static SimulatedSqlException MultiPartIdentifierCouldNotBeBound(string name) =>
        new($"The multi-part identifier \"{name}\" could not be bound.", 4104, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4121 — fired when a schema-qualified function
    /// call (<c>SELECT schema.fn(x)</c>) names something that isn't a known
    /// UDF / aggregate / column. Verbatim wording probe-confirmed against
    /// SQL Server 2025: the message references the dotted name in the
    /// "or the user-defined function or aggregate" slot and shows the schema
    /// qualifier in the "column" slot. Distinct from Msg 195 ("not a
    /// recognized built-in function name") which fires for bare 1-part
    /// calls.
    /// </summary>
    internal static SimulatedSqlException CannotFindUserDefinedFunction(MultiPartName name) =>
        new($"Cannot find either column \"{name.ImmediateQualifier}\" or the user-defined function or aggregate \"{name}\", or the name is ambiguous.", 4121, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 313 — fired by a function/procedure call that
    /// supplies fewer arguments than the parameter list. Probe-confirmed that
    /// this fires even when omitted parameters have declared defaults — the
    /// <c>DEFAULT</c> keyword is the only legal omission path.
    /// </summary>
    internal static SimulatedSqlException InsufficientArgumentsToFunction(string name, byte state = 2) =>
        new($"An insufficient number of arguments were supplied for the procedure or function {name}.", 313, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 8144 — fired by a function/procedure call that
    /// supplies more arguments than the parameter list. Wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException TooManyArgumentsToFunction(string name, byte state = 2) =>
        new($"Procedure or function {name} has too many arguments specified.", 8144, 16, state);

    /// <summary>
    /// Mimics SQL Server's Msg 217 — fired when scalar UDF / proc / trigger /
    /// view recursion exceeds the 32-level cap (probe-confirmed verbatim).
    /// Backed by <see cref="SimulatedDbConnection.NestingLevel"/>; the call
    /// site checks the depth before incrementing.
    /// </summary>
    internal static SimulatedSqlException MaximumNestingLevelExceeded() =>
        new("Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).", 217, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 2812 — EXEC named a stored procedure that
    /// doesn't exist. Distinct error number from Msg 208 / 3701; the State
    /// (62) is probe-confirmed. Wording mirrors real SQL Server verbatim.
    /// </summary>
    internal static SimulatedSqlException CouldNotFindStoredProcedure(string name) =>
        new($"Could not find stored procedure '{name}'.", 2812, 16, 62);

    /// <summary>
    /// Mimics SQL Server's Msg 201 — EXEC failed to supply a required
    /// parameter (either a named argument referenced an unknown parameter,
    /// leaving a required one un-supplied, or the call simply omitted a
    /// parameter that has no default). The State (4) and verbatim wording
    /// were probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ProcedureExpectsParameter(string procedureName, string parameterName) =>
        new($"Procedure or function '{procedureName}' expects parameter '@{parameterName}', which was not supplied.", 201, 16, 4);

    /// <summary>
    /// Mimics SQL Server's Msg 8143 — a single EXEC supplied the same named
    /// argument twice. State 1, exact wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException ParameterSuppliedMultipleTimes(string parameterName) =>
        new($"Parameter '@{parameterName}' was supplied multiple times.", 8143, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 119 — an EXEC mixed positional and named
    /// arguments incorrectly: once a <c>@name = value</c> appeared, every
    /// following argument must also be in that form. State 1 / class 15;
    /// verbatim wording probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException MustPassParameterAsNamed() =>
        new("Must pass parameter number 2 and subsequent parameters as '@name = value'. After the form '@name = value' has been used, all subsequent parameters must be passed in the form '@name = value'.", 119, 15, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 487 — fired by <c>CREATE FUNCTION</c> when an
    /// invalid option appears in the <c>WITH</c> clause (e.g. <c>WITH RETURNS
    /// NULL ON NULL INPUT</c> on an inline TVF — that option is scalar-only).
    /// Verbatim wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException InvalidOptionForCreateFunction() =>
        new("An invalid option was specified for the statement \"CREATE/ALTER FUNCTION\".", 487, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4514 — fired at <c>CREATE FUNCTION</c> when an
    /// inline TVF's body projects an unnamed column. Distinct from
    /// <c>SELECT INTO</c>'s Msg 1038 (different wording, different error
    /// number). The 1-based column position is embedded in the message.
    /// </summary>
    internal static SimulatedSqlException InlineTvfMissingColumnName(int columnPosition) =>
        new($"CREATE FUNCTION failed because a column name is not specified for column {columnPosition}.", 4514, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4506 — fired at <c>CREATE VIEW</c> /
    /// <c>CREATE FUNCTION</c> when the body projects two columns with the
    /// same name. Probe-confirmed wording embeds both the column name and
    /// the object name (the literal "view or function" text comes verbatim
    /// from SQL Server since views and inline TVFs share the projection-
    /// uniqueness rule).
    /// </summary>
    internal static SimulatedSqlException DuplicateColumnInViewOrFunction(string columnName, string objectName) =>
        new($"Column names in each view or function must be unique. Column name '{columnName}' in view or function '{objectName}' is specified more than once.", 4506, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4511 — fired at <c>CREATE VIEW</c> when the
    /// body projects an unnamed column (an expression without an <c>AS</c>
    /// alias). Distinct from inline TVF's Msg 4514 ("CREATE FUNCTION
    /// failed...") and SELECT INTO's Msg 1038. The 1-based column position
    /// is embedded in the message.
    /// </summary>
    internal static SimulatedSqlException CreateViewMissingColumnName(int columnPosition) =>
        new($"Create View or Function failed because no column name was specified for column {columnPosition}.", 4511, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4403 — DML through a view whose body has an
    /// aggregate / DISTINCT / GROUP BY / PIVOT / UNPIVOT, none of which
    /// preserve a 1:1 row correspondence with the underlying base. The same
    /// wording fires for INSERT, UPDATE, and DELETE through such a view —
    /// the simulator collapses these into one factory matching SQL Server's
    /// uniform message. Probe-confirmed verbatim against SQL Server 2025
    /// (2026-05-12).
    /// </summary>
    internal static SimulatedSqlException CannotUpdateNonUpdatableView(string viewName) =>
        new($"Cannot update the view or function '{viewName}' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", 4403, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4405 — DML through a view whose body has
    /// multiple base tables (JOIN form) and the modification affects more
    /// than one of them. The simulator's v1 of updatable views rejects
    /// JOIN-bodied views uniformly with this Msg even for single-base-table
    /// modifications (SQL Server is more permissive — single-base UPDATE
    /// through a JOIN view works there — but the simpler always-reject
    /// shape is closer to the common case in real apps). Probe-confirmed
    /// verbatim wording against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ViewUpdateAffectsMultipleTables(string viewName) =>
        new($"View or function '{viewName}' is not updatable because the modification affects multiple base tables.", 4405, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 4406 — INSERT or UPDATE through a view
    /// touched a projection column that's not a direct column reference
    /// (an arithmetic expression, function call, CAST, etc.). The view as
    /// a whole may still be updatable (other projections can be direct
    /// refs), and DELETE through such a view works fine — the rejection
    /// is per-touched-column. Probe-confirmed verbatim against SQL Server
    /// 2025.
    /// </summary>
    internal static SimulatedSqlException ViewDmlTouchesDerivedField(string viewName) =>
        new($"Update or insert of view or function '{viewName}' failed because it contains a derived or constant field.", 4406, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 550 — INSERT or UPDATE through a view with
    /// (or spanning) <c>WITH CHECK OPTION</c> would leave a row not visible
    /// in the view. Triggers a per-row check after the row is constructed
    /// (INSERT) or computed (UPDATE) against every CHECK OPTION-bearing
    /// level in the view chain; any miss raises this. Probe-confirmed
    /// verbatim against SQL Server 2025; real SQL Server also fires the
    /// follow-on Msg 3621 "The statement has been terminated" — the
    /// simulator surfaces Msg 550 alone (Msg 3621 is the wrapper, not
    /// load-bearing).
    /// </summary>
    internal static SimulatedSqlException ViewCheckOptionViolation() =>
        new("The attempted insert or update failed because the target view either specifies WITH CHECK OPTION or spans a view that specifies WITH CHECK OPTION and one or more rows resulting from the operation did not qualify under the CHECK OPTION constraint.", 550, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 2020 — <c>sys.dm_sql_referenced_entities</c>
    /// reached a reference it couldn't resolve, so the column detail it reported
    /// may be short. Probe-confirmed verbatim (including the double space before
    /// "Before rerunning"), and probe-confirmed to follow the rows rather than
    /// replace them: the DMV hands back what it found and then raises.
    /// </summary>
    internal static SimulatedSqlException DependencyReportMayBeIncomplete(string entityName) =>
        new($"The dependencies reported for entity \"{entityName}\" might not include references to all columns. This is either because the entity references an object that does not exist or because of an error in one or more statements in the entity.  Before rerunning the query, ensure that there are no errors in the entity and that all objects referenced by the entity exist.", 2020, 16, 1);
}
