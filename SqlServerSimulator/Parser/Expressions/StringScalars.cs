using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared implicit-coerce helpers for the string scalar expressions
/// (<see cref="Length"/>, <see cref="Lower"/>, <see cref="Upper"/>,
/// <see cref="LeftTrim"/>, <see cref="RightTrim"/>, <see cref="Reverse"/>,
/// <see cref="Left"/>, <see cref="Right"/>, <see cref="Replace"/>). Mirrors
/// the <see cref="MathScalars"/> pattern: centralizes the "non-string
/// operand implicitly casts to varchar" rule that real SQL Server applies
/// to every string scalar. Probe-confirmed against SQL Server 2025
/// (2026-05-22): <c>LOWER(12345)</c> / <c>LEN(CAST('2024-01-15' AS DATE))</c>
/// / <c>REPLACE(CAST('2024-01-15' AS DATE), '-', '/')</c> all parse, with
/// the result column reading as <c>varchar</c> regardless of the source
/// family (integer, decimal, float, money, date/time).
/// </summary>
internal static class StringScalars
{
    /// <summary>
    /// Returns <paramref name="value"/> typed at a string family, applying
    /// SQL Server's implicit-cast rule when the input isn't already a
    /// string. Numeric / date-time / uniqueidentifier sources flow through
    /// <see cref="SqlValue.CoerceTo"/> targeting <c>varchar</c> in the
    /// active database's collation — same path real SQL Server uses to
    /// render the value before applying the surrounding string function.
    /// Source families outside this set (varbinary, xml, spatial, table
    /// types) raise Msg 8116 via
    /// <see cref="SimulatedSqlException.InvalidArgumentDataType"/> so the
    /// caller surfaces the same error wording real SQL Server uses for
    /// genuinely-unsupported operands.
    /// </summary>
    public static SqlValue CoerceToVarchar(SqlValue value, BatchContext batch, string functionLowerName, int argumentIndex = 1)
    {
        if (SqlType.IsStringCategory(value.Type))
            return value;
        if (!IsCoerceableToVarchar(value.Type))
            throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, argumentIndex, functionLowerName);
        var target = VarcharSqlType.Get(0, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
        return value.CoerceTo(target);
    }

    /// <summary>
    /// Returns the projection-schema string type for a string scalar
    /// applied to an input typed at <paramref name="sourceType"/>. String
    /// sources pass through (the function preserves the input type);
    /// implicit-castable sources promote to <c>varchar</c> in the active
    /// database collation, matching the runtime coercion in
    /// <see cref="CoerceToVarchar"/>; everything else passes through (the
    /// runtime path raises the same Msg 8116 at that point — projection
    /// schema only needs to be roughly correct since the value never
    /// materializes).
    /// </summary>
    public static SqlType ResolveResultType(SqlType sourceType, BatchContext batch) =>
        SqlType.IsStringCategory(sourceType) || !IsCoerceableToVarchar(sourceType)
            ? sourceType
            : VarcharSqlType.Get(0, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    private static bool IsCoerceableToVarchar(SqlType type) =>
        SqlType.IsIntegerCategory(type)
            || type is DecimalSqlType
            || SqlType.IsMoneyCategory(type)
            || SqlType.IsApproximateNumericCategory(type)
            || SqlType.IsDateTimeCategory(type)
            || type == SqlType.UniqueIdentifier
            || type is VarbinarySqlType or BinarySqlType;
}
