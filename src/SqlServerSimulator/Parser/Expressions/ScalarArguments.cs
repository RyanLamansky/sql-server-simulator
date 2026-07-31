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
    /// </summary>
    public static int CoerceProcedureParameter(SqlValue value, SqlType target)
    {
        try
        {
            return value.CoerceTo(target).CoerceTo(SqlType.Int32).AsInt32;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ConvertingDataTypeError(value.Type, target.SqlServerName);
        }
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
