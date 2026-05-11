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
}
