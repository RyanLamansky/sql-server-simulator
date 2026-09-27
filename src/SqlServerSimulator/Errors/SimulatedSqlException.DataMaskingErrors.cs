namespace SqlServerSimulator;

// Dynamic Data Masking error factories (Msg 16002 – 16007), all raised at
// state 0 (probed 2026-09-27 against SQL Server 2025).
public sealed partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 16002: a masking function name that isn't one of
    /// the five, or an empty function text.
    /// </summary>
    internal static SimulatedSqlException InvalidMaskingFunction(string columnName) =>
        new($"Invalid data masking function in column '{columnName}'.", 16002, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 16003: a masking function the column's type
    /// doesn't take, named in lowercase however it was written.
    /// </summary>
    internal static SimulatedSqlException MaskingFunctionUnsupportedType(string columnName, string functionName) =>
        new($"The data type of column '{columnName}' does not support data masking function '{functionName}'.", 16003, 16, 0);

    /// <summary>
    /// Mimics SQL Server error 16004: the wrong number of arguments, or text
    /// after the closing parenthesis; the function is named as written.
    /// </summary>
    internal static SimulatedSqlException MaskingParameterCount(string functionName, string columnName) =>
        new($"Incorrect number of parameters for data masking function '{functionName}' for column '{columnName}'.", 16004, 16, 0);

    /// <summary>Mimics SQL Server error 16005: an argument the function (or the column's type) refuses.</summary>
    internal static SimulatedSqlException InvalidMaskingArgument(string functionName, string columnName) =>
        new($"Invalid argument for data masking function '{functionName}' for column '{columnName}'.", 16005, 16, 0);

    /// <summary>Mimics SQL Server error 16006: a function text with no parenthesized argument list.</summary>
    internal static SimulatedSqlException InvalidMaskingFormat(string functionName, string columnName) =>
        new($"Invalid data masking format for function '{functionName}' in column '{columnName}'.", 16006, 16, 0);

    /// <summary>Mimics SQL Server error 16007: <c>ALTER COLUMN … DROP MASKED</c> on a column with no mask.</summary>
    internal static SimulatedSqlException ColumnHasNoMaskingFunction(string columnName) =>
        new($"The column '{columnName}' does not have a data masking function.", 16007, 16, 0);
}
