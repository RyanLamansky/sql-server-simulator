using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    internal static SimulatedSqlException IdentifierTooLong(ReadOnlySpan<char> first128)
        => new($"The identifier that starts with '{first128}' is too long. Maximum length is 128.", 103, 15, 4);

    internal static SimulatedSqlException InvalidColumnName(string name) => new($"Invalid column name '{name}'.", 207, 16, 1);

    internal static SimulatedSqlException InvalidColumnName(MultiPartName name) => InvalidColumnName(name.ToString());

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
    internal static SimulatedSqlException InsufficientArgumentsToFunction(string name) =>
        new($"An insufficient number of arguments were supplied for the procedure or function {name}.", 313, 16, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 8144 — fired by a function/procedure call that
    /// supplies more arguments than the parameter list. Wording probe-confirmed.
    /// </summary>
    internal static SimulatedSqlException TooManyArgumentsToFunction(string name) =>
        new($"Procedure or function {name} has too many arguments specified.", 8144, 16, 2);

    /// <summary>
    /// Mimics SQL Server's Msg 217 — fired when scalar UDF / proc / trigger /
    /// view recursion exceeds the 32-level cap (probe-confirmed verbatim).
    /// Backed by <see cref="SimulatedDbConnection.NestingLevel"/>; the call
    /// site checks the depth before incrementing.
    /// </summary>
    internal static SimulatedSqlException MaximumNestingLevelExceeded() =>
        new("Maximum stored procedure, function, trigger, or view nesting level exceeded (limit 32).", 217, 16, 1);

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
}
