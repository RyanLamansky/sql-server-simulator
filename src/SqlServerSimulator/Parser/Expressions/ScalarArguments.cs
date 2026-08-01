using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Narrows the integer arguments scalar functions take — object / database /
/// index ids, positions, counts, offsets, code points — to the integer type
/// the parameter is declared as, raising SQL Server's own conversion error
/// instead of leaking .NET's <see cref="OverflowException"/>.
/// </summary>
/// <remarks>
/// Real reports an argument that doesn't fit through the ordinary
/// conversion-overflow family rather than any function-specific error, which
/// is why this routes through the same chooser CAST, column assignment and
/// ALTER COLUMN use: a <c>bigint</c> or <c>numeric</c> argument gives the
/// generic Msg 8115 naming the target, a <c>float</c> / <c>real</c> one the
/// value-bearing Msg 232, a <c>money</c> one Msg 237, and an <c>int</c>
/// argument narrowing to <c>smallint</c> the value-bearing Msg 220.
/// Probe-confirmed 2026-07-31 across the catalog-id scalars, the date-part
/// builders, <c>SWITCHOFFSET</c> and the spatial constructors.
/// </remarks>
internal static class ScalarArguments
{
    /// <summary>
    /// Narrows to <c>int</c> — the declared type of nearly every id /
    /// position / count parameter.
    /// </summary>
    public static int CoerceToInt(SqlValue value) => Narrow(value, SqlType.Int32).AsInt32;

    /// <summary>
    /// Narrows to <c>smallint</c>, which a handful of parameters declare:
    /// <c>FILEGROUP_NAME</c>'s filegroup id, <c>INDEXKEY_PROPERTY</c>'s key
    /// ordinal, and the minute offset <c>SWITCHOFFSET</c> /
    /// <c>TODATETIMEOFFSET</c> take.
    /// </summary>
    public static short CoerceToSmallInt(SqlValue value) => Narrow(value, SqlType.SmallInt).AsInt16;

    /// <summary>
    /// Narrows a system procedure's integer parameter, given the type that
    /// parameter is declared as. The procedure-parameter boundary reports
    /// differently from a function argument: real converts the supplied value
    /// to the declared type and reports one that doesn't fit as Msg 8114
    /// naming both families — <c>"Error converting data type bigint to
    /// int."</c> — rather than through the arithmetic-overflow family.
    /// Probe-confirmed 2026-07-31 for <c>sp_getapplock</c>'s
    /// <c>@LockTimeout</c> (int), <c>sp_columns_100</c>'s <c>@ODBCVer</c>
    /// (int), and <c>sp_datatype_info_100</c>'s <c>@data_type</c> (int) /
    /// <c>@ODBCVer</c> (tinyint), whose narrower slot names <c>tinyint</c>.
    /// An exact-numeric source reaches the same Msg 8114 — real spells the
    /// family <c>numeric</c> there for a literal argument (the only decimal
    /// form the EXEC argument grammar admits) and <c>decimal</c> only for a
    /// declared-<c>decimal</c> variable, a distinction that needs the
    /// deferred column/variable-source name; the literal spelling wins.
    /// </summary>
    public static int CoerceProcedureParameter(SqlValue value, SqlType target)
    {
        try
        {
            return value.CoerceTo(target).CoerceTo(SqlType.Int32).AsInt32;
        }
        catch (OverflowException)
        {
            throw ProcedureParameterConversionError(value, target);
        }
        catch (SimulatedSqlException e) when (e.Number is 8115 or 232 or 237 or 220)
        {
            // The conversion-overflow family a decimal / float / money source
            // raises internally still reports as the procedure-parameter
            // Msg 8114 at this boundary.
            throw ProcedureParameterConversionError(value, target);
        }
    }

    private static SimulatedSqlException ProcedureParameterConversionError(SqlValue value, SqlType target) =>
        value.Type is DecimalSqlType
            ? SimulatedSqlException.ConvertingDataTypeError("numeric", target.SqlServerName)
            : SimulatedSqlException.ConvertingDataTypeError(value.Type, target.SqlServerName);

    /// <summary>
    /// Gates the argument slots real refuses to convert into at all, reporting
    /// Msg 8116 naming the offending family: the object-id first argument of
    /// <c>COLUMNPROPERTY</c> / <c>INDEXPROPERTY</c> / <c>INDEXKEY_PROPERTY</c>
    /// and <c>CONVERT</c>'s style. Only the four signed / unsigned integer
    /// families pass (<c>int</c>, <c>bigint</c>, <c>smallint</c>,
    /// <c>tinyint</c>) — <c>bit</c>, the exact numerics, <c>float</c> /
    /// <c>real</c>, money, the string families and the date families all
    /// raise. The gate is a compile-time one on real, so it precedes the
    /// NULL short-circuit: a typed NULL raises where a bare <c>NULL</c>
    /// (which carries the placeholder <c>int</c> type) passes through.
    /// Probe-confirmed 2026-08-01 across all four slots; the sibling id
    /// scalars (<c>OBJECT_NAME</c>, <c>COL_NAME</c>, <c>OBJECTPROPERTY</c>,
    /// <c>TYPE_NAME</c>, <c>DB_NAME</c>, <c>INDEX_COL</c>) and the string
    /// scalars' position arguments carry no such gate — they convert, so an
    /// out-of-range value reaches the ordinary Msg 8115.
    /// <c>reportsNumeric</c> carries the argument expression's
    /// decimal-family naming (<c>numeric</c> for a literal, <c>decimal</c>
    /// for a declared decimal); real spells the two apart here exactly as it
    /// does in a projected column's type name.
    /// </summary>
    public static void RequireIntegerArgument(SqlValue value, bool reportsNumeric, int argumentIndex, string functionName)
    {
        if (SqlType.IsIntegerCategory(value.Type) && value.Type != SqlType.Bit)
            return;
        var typeWord = reportsNumeric && value.Type is DecimalSqlType ? "numeric" : value.Type.SqlServerName;
        throw SimulatedSqlException.InvalidArgumentDataType(typeWord, argumentIndex, functionName);
    }

    private static SqlValue Narrow(SqlValue value, SqlType target)
    {
        try
        {
            return value.CoerceTo(target);
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.TryConversionOverflow(value, target)
                ?? SimulatedSqlException.ArithmeticOverflow(target.SqlServerName);
        }
    }
}
