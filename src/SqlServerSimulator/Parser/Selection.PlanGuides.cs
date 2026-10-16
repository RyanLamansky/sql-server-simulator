using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// sys.fn_validate_plan_guide, whose shape was probed 2026-10-15 against SQL
// Server 2025.
partial class Selection
{
    private static readonly SqlType[] ValidatePlanGuideSchema =
    [
        SqlType.Int32, SqlType.TinyInt, SqlType.SmallInt, NVarcharSqlType.Get(1024, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit),
    ];

    private static readonly string[] ValidatePlanGuideColumnNames = ["msgnum", "severity", "state", "message"];

    /// <summary>
    /// Built-in system TVF <c>sys.fn_validate_plan_guide(plan_guide_id)</c>:
    /// one row naming the first error the guide meets compiling again — its
    /// number, severity, state and message — or none while it is valid. An id
    /// that is no guide of the current database's, NULL included, is Msg
    /// 10538.
    /// </summary>
    public static Selection ParseValidatePlanGuide(ParserContext context, string functionName)
    {
        var arguments = ParseSystemFunctionArguments(context, functionName, 1);
        return new Selection(ValidatePlanGuideSchema, ValidatePlanGuideColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) =>
            {
                var value = arguments[0].Run(new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch));
                var id = value.IsNull ? (int?)null : value.CoerceTo(SqlType.Int32).AsInt32;
                if (batch.Connection.Simulation.ValidatePlanGuide(batch, id) is not { } error)
                    return [];
                var entry = error.Errors[0];
                return [RowEncoder.EncodeRow(ValidatePlanGuideSchema,
                [
                    SqlValue.FromInt32(entry.Number),
                    SqlValue.FromByte(entry.Class),
                    SqlValue.FromInt16(entry.State),
                    SqlValue.FromNVarchar((NVarcharSqlType)ValidatePlanGuideSchema[3], entry.Message.Length > 1024 ? entry.Message[..1024] : entry.Message),
                ])];
            })
        {
            ColumnNullability = [false, false, true, false],
        };
    }
}
