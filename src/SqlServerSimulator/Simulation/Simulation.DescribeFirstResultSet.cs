using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SqlType[] DescribeSchema =
    [
        SqlType.Bit, SqlType.Int32, SqlType.SystemName, SqlType.Bit, SqlType.Int32, NVarcharSqlType.Get(256, Collation.Baseline, Coercibility.Implicit),
        SqlType.SmallInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.SystemName, SqlType.Int32, SqlType.SystemName,
        SqlType.SystemName, SqlType.SystemName, NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit), SqlType.Int32, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.Bit, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.SmallInt, SqlType.SmallInt, SqlType.SmallInt, SqlType.Int32,
        SqlType.Int32, SqlType.Int32, SqlType.TinyInt,
    ];

    private static readonly string[] DescribeColumnNames =
    [
        "is_hidden", "column_ordinal", "name", "is_nullable", "system_type_id", "system_type_name",
        "max_length", "precision", "scale", "collation_name", "user_type_id", "user_type_database",
        "user_type_schema", "user_type_name", "assembly_qualified_type_name", "xml_collection_id", "xml_collection_database", "xml_collection_schema",
        "xml_collection_name", "is_xml_document", "is_case_sensitive", "is_fixed_length_clr_type", "source_server", "source_database",
        "source_schema", "source_table", "source_column", "is_identity_column", "is_part_of_unique_key", "is_updateable",
        "is_computed_column", "is_sparse_column_set", "ordinal_in_order_by_list", "order_by_is_descending", "order_by_list_length", "tds_type_id",
        "tds_length", "tds_collation_id", "tds_collation_sort_id",
    ];

    /// <summary>
    /// <c>sp_describe_first_result_set @tsql [, @params [, @browse_information_mode]]</c>
    /// — one row per column of the first result set <c>@tsql</c> would return,
    /// found by running it under <c>SET FMTONLY ON</c> (a SELECT yields its
    /// metadata and no rows, and a data-modifying statement is suppressed).
    /// A batch that doesn't compile reports its errors followed by Msg 11501,
    /// and one with no result set describes nothing. Column shapes and values
    /// probed 2026-09-24 against SQL Server 2025; browse information (the
    /// <c>source_*</c> columns and key membership) isn't built, so every
    /// browse mode answers as mode 0 does.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDescribeFirstResultSet(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        SqlValue? tsql = null, parameters = null;
        var positional = 0;
        foreach (var argument in arguments)
        {
            var slot = argument.Name is null
                ? positional++
                : argument.Name.Equals("tsql", StringComparison.OrdinalIgnoreCase) ? 0
                : argument.Name.Equals("params", StringComparison.OrdinalIgnoreCase) ? 1
                : argument.Name.Equals("browse_information_mode", StringComparison.OrdinalIgnoreCase) ? 2
                : throw SimulatedSqlException.InvalidProcedureParameters("sp_describe_first_result_set");
            switch (slot)
            {
                case 0: tsql = argument.Value; break;
                case 1: parameters = argument.Value; break;
                case 2: break;
                default: throw SimulatedSqlException.TooManyArgumentsToFunction("sp_describe_first_result_set");
            }
        }
        if (tsql is not { IsNull: false } text)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_describe_first_result_set", "tsql");

        var declared = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        if (parameters is { IsNull: false } parameterText)
        {
            foreach (var parameter in ParseSpExecuteSqlParamDefinitions(parameterText.AsString, batch.Connection))
                declared[parameter.Name] = new VariableSlot(parameter.Type, declaredMaxLength: null, SqlValue.Null(parameter.Type), parameter: null);
        }

        var connection = batch.Connection;
        var savedFmtOnly = connection.FmtOnly;
        connection.FmtOnly = true;
        SimulatedQueryResult? first = null;
        try
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, text.AsString, declared))
            {
                if (outcome is SimulatedQueryResult result)
                {
                    first = result;
                    break;
                }
            }
        }
        catch (SimulatedSqlException error)
        {
            // A name that resolves only at run time is the metadata question
            // every path fails (Msg 11529); anything else is a compile error.
            throw SimulatedSqlException.Aggregate([error, error.Number == 208 ? SimulatedSqlException.MetadataCouldNotBeDetermined() : SimulatedSqlException.BatchCouldNotBeAnalyzed()]);
        }
        finally
        {
            connection.FmtOnly = savedFmtOnly;
        }

        var rows = new List<SqlValue[]>();
        if (first is not null)
        {
            for (var i = 0; i < first.Schema.Length; i++)
                rows.Add(DescribeColumn(first, i));
        }
        yield return new SimulatedSqlResultSet(DescribeSchema, DescribeColumnNames, rows.ConvertAll(row => RowEncoder.EncodeRow(DescribeSchema, row)));
    }

    private static SqlValue[] DescribeColumn(SimulatedQueryResult result, int index)
    {
        var type = result.Schema[index];
        var numeric = type is DecimalSqlType && result.ColumnReportsNumeric is { } spelled && spelled[index];
        var nullable = result.ColumnNullability is not { } nullability || nullability[index];
        var origin = result.ColumnOrigins?[index];
        var name = result.ColumnNames[index];
        var (maxLength, precision, scale) = BuiltInResources.GetSysColumnMetadata(new HeapColumn(string.Empty, type, maxLength: null, nullable: true));
        var collation = type.Category == SqlTypeCategory.String ? type.Collation : null;
        var (tdsType, tdsLength) = DescribeTdsType(type, !nullable, numeric, maxLength);
        var codec = collation is null ? null : Network.TdsCollationCodec.For(collation);
        var nullName = SqlValue.Null(SqlType.SystemName);
        var nullBit = SqlValue.Null(SqlType.Bit);
        var nullSmall = SqlValue.Null(SqlType.SmallInt);
        var nullInt = SqlValue.Null(SqlType.Int32);
        return
        [
            SqlValue.FromBoolean(false),
            SqlValue.FromInt32(index + 1),
            string.IsNullOrEmpty(name) ? nullName : SqlValue.FromSystemName(name),
            SqlValue.FromBoolean(nullable),
            SqlValue.FromInt32(numeric ? 108 : type.SystemTypeId),
            SqlValue.FromNVarchar(DescribeTypeName(type, numeric)),
            SqlValue.FromInt16(maxLength),
            SqlValue.FromByte(precision),
            SqlValue.FromByte(scale),
            collation is null ? nullName : SqlValue.FromSystemName(collation.Name),
            nullInt, nullName, nullName, nullName, SqlValue.Null(NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit)),
            nullInt, nullName, nullName, nullName,
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(type is XmlSqlType || (collation is not null && collation.Name.Contains("_CS", StringComparison.OrdinalIgnoreCase))),
            SqlValue.FromBoolean(false),
            nullName, nullName, nullName, nullName, nullName,
            SqlValue.FromBoolean(origin?.Identity is not null),
            nullBit,
            SqlValue.FromBoolean(!result.IsGrouped && origin is { Identity: null, Computed: null } && type != SqlType.RowVersion),
            SqlValue.FromBoolean(origin?.Computed is not null || (origin is null && result.ColumnIsComputed is { } computed && computed[index])),
            SqlValue.FromBoolean(false),
            nullSmall, nullSmall, nullSmall,
            SqlValue.FromInt32(tdsType),
            SqlValue.FromInt32(tdsLength),
            codec is null ? nullInt : SqlValue.FromInt32((int)codec.Info),
            codec is null ? SqlValue.Null(SqlType.TinyInt) : SqlValue.FromByte(codec.SortId),
        ];
    }

    /// <summary>The declaration real writes in <c>system_type_name</c>: <c>varchar(10)</c>, <c>numeric(5,2)</c>, <c>nvarchar(max)</c>.</summary>
    private static string DescribeTypeName(SqlType type, bool numeric) => type switch
    {
        DecimalSqlType d => $"{(numeric ? "numeric" : "decimal")}({d.precision},{d.scale})",
        SystemNameSqlType => "nvarchar(128)",
        _ => type.ToString()!.Replace("(MAX)", "(max)", StringComparison.Ordinal),
    };

    /// <summary>
    /// The TDS type token and length real reports for a column: the
    /// fixed-length token for a NOT NULL fixed-width type and the nullable
    /// variant otherwise (as COLMETADATA carries them), 17 for decimal, 65535
    /// for a MAX type and 8100 for xml.
    /// </summary>
    private static (int TypeId, int Length) DescribeTdsType(SqlType type, bool notNull, bool numeric, short maxLength) => type switch
    {
        TinyIntSqlType => (notNull ? 48 : 38, 1),
        SmallIntSqlType => (notNull ? 52 : 38, 2),
        Int32SqlType => (notNull ? 56 : 38, 4),
        BigIntSqlType => (notNull ? 127 : 38, 8),
        BitSqlType => (notNull ? 50 : 104, 1),
        RealSqlType => (notNull ? 59 : 109, 4),
        FloatSqlType => (notNull ? 62 : 109, 8),
        SmallMoneySqlType => (notNull ? 122 : 110, 4),
        MoneySqlType => (notNull ? 60 : 110, 8),
        SmallDateTimeSqlType => (notNull ? 58 : 111, 4),
        DateTimeSqlType => (notNull ? 61 : 111, 8),
        DecimalSqlType => (numeric ? 108 : 106, 17),
        XmlSqlType => (241, 8100),
        _ => (type.SystemTypeId, maxLength < 0 ? 65535 : maxLength),
    };
}
