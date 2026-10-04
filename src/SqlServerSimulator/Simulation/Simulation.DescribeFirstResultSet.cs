using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    internal static readonly SqlType[] DescribeSchema =
    [
        SqlType.Bit, SqlType.Int32, SqlType.SystemName, SqlType.Bit, SqlType.Int32, NVarcharSqlType.Get(128, Collation.Baseline, Coercibility.Implicit),
        SqlType.SmallInt, SqlType.TinyInt, SqlType.TinyInt, SqlType.SystemName, SqlType.Int32, SqlType.SystemName,
        SqlType.SystemName, SqlType.SystemName, NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit), SqlType.Int32, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.SystemName, SqlType.SystemName,
        SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.Bit, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.SmallInt, SqlType.Bit, SqlType.SmallInt, SqlType.Int32,
        SqlType.Int32, SqlType.Int32, SqlType.TinyInt,
    ];

    internal static readonly string[] DescribeColumnNames =
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

        SqlValue? tsql = null, parameters = null, browseMode = null;
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
                case 2: browseMode = argument.Value; break;
                default: throw SimulatedSqlException.TooManyArgumentsToFunction("sp_describe_first_result_set");
            }
        }
        // A missing statement is Msg 201 state 20, and a NULL or non-Unicode
        // one Msg 214, at line 1 (probed 2026-10-04 against SQL Server 2025);
        // a browse mode outside 0 .. 2 is Msg 11552.
        if (tsql is not { } text)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_describe_first_result_set", "tsql", state: 20);
        if (text.IsNull || !SqlType.IsNationalStringCategory(text.Type))
            throw SimulatedSqlException.ProcedureExpectsNVarcharMaxParameter("tsql");
        if (browseMode is { IsNull: false } mode && (!SqlType.IsIntegerCategory(mode.Type) || mode.CoerceTo(SqlType.BigInt).AsInt64 is < 0 or > 2))
            throw SimulatedSqlException.BrowseInformationModeNotValid();

        var rows = this.DescribeFirstResult(batch, text.AsString, parameters);
        yield return new SimulatedSqlResultSet(DescribeSchema, DescribeColumnNames, rows.ConvertAll(row => RowEncoder.EncodeRow(DescribeSchema, row)));
    }

    /// <summary>
    /// The engine <c>sp_describe_first_result_set</c> and
    /// <c>sys.dm_exec_describe_first_result_set</c> share: one row per column
    /// of the first result set <paramref name="tsql"/> would return, found by
    /// running it under <c>SET FMTONLY ON</c> (a SELECT yields its metadata
    /// and no rows, and a data-modifying statement is suppressed), and none
    /// when it has no result set. A batch — or a parameter declaration list —
    /// that doesn't compile raises its errors followed by Msg 11501.
    /// </summary>
    internal List<SqlValue[]> DescribeFirstResult(BatchContext batch, string tsql, SqlValue? parameters)
    {
        var connection = batch.Connection;
        var savedFmtOnly = connection.FmtOnly;
        connection.FmtOnly = true;
        // Describing runs nothing, so it opens no IMPLICIT_TRANSACTIONS
        // transaction (probed 2026-09-28 against SQL Server 2025), where a
        // SET FMTONLY ON query does.
        var savedImplicitTransactions = connection.ImplicitTransactions;
        connection.ImplicitTransactions = false;
        SimulatedQueryResult? first = null;
        try
        {
            var declared = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
            Dictionary<string, HeapTable>? tables = null;
            if (parameters is { IsNull: false } parameterText)
            {
                var declarations = ParseSpExecuteSqlParamDefinitions(parameterText.AsString, connection);
                foreach (var parameter in declarations)
                {
                    if (parameter.TableType is null)
                        declared[parameter.Name] = new VariableSlot(parameter.Type, declaredMaxLength: null, SqlValue.Null(parameter.Type), parameter: null);
                }
                tables = EmptyTableValuedParameters(declarations, batch);
            }

            foreach (var outcome in this.ExecuteDynamicBatch(batch, tsql, declared, tableVariables: tables))
            {
                if (outcome is SimulatedQueryResult result)
                {
                    first = result;
                    break;
                }

                // A CLR procedure ahead of the first result set ends the
                // question outright, however the batch runs on past errors.
                if (outcome is SimulatedErrorOutcome { Exception.Number: 11515 } unanswerable)
                    throw unanswerable.Exception.CopyOfError();
            }
        }
        catch (SimulatedSqlException error) when (error.Number != 11515)
        {
            // A name that resolves only at run time is the metadata question
            // every path fails (Msg 11529); anything else is a compile error.
            // The follow-up reports the line of the error it follows, and a
            // name miss, found while running the batch, names the procedure on
            // both (probed 2026-09-28 against SQL Server 2025).
            var followUp = error.Number == 208 ? SimulatedSqlException.MetadataCouldNotBeDetermined() : SimulatedSqlException.BatchCouldNotBeAnalyzed();
            var last = error.Errors[^1];
            if (error.Number == 208)
            {
                foreach (var entry in error.Errors)
                {
                    if (entry.Procedure.Length == 0)
                        entry.Procedure = "sp_describe_first_result_set";
                }
            }
            followUp.ResolveDiagnostics(0, last.LineNumber, error.Number == 208 ? "sp_describe_first_result_set" : last.Procedure);
            throw SimulatedSqlException.Aggregate([error, followUp]);
        }
        finally
        {
            connection.FmtOnly = savedFmtOnly;
            connection.ImplicitTransactions = savedImplicitTransactions;
        }

        var rows = new List<SqlValue[]>();
        if (first is not null)
        {
            for (var i = 0; i < first.Schema.Length; i++)
                rows.Add(DescribeColumn(first, i));
        }
        return rows;
    }

    private static readonly VarcharSqlType Float16DescribeType = VarcharSqlType.Get(-1, Collation.Get("Latin1_General_100_BIN2_UTF8"), Coercibility.Implicit);

    /// <summary>
    /// <c>tds_collation_id</c>: the wire's collation word, except that a
    /// <c>_BIN2_UTF8</c> collation keeps the binary-sort bit the wire drops,
    /// as <c>COLLATIONPROPERTY</c>'s form does (probed 2026-09-29 against SQL
    /// Server 2025).
    /// </summary>
    private static int DescribedCollationInfo(Network.TdsCollationCodec codec, Collation collation)
    {
        if (!collation.Name.EndsWith("_BIN2_UTF8", StringComparison.OrdinalIgnoreCase))
            return (int)codec.Info;
        var property = codec.PropertyBytes(binary2: true);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(property);
    }

    private static SqlValue[] DescribeColumn(SimulatedQueryResult result, int index)
    {
        // An unsized string result describes as the width the wire reports
        // for it (COLMETADATA's 8000 bytes).
        var type = result.Schema[index] switch
        {
            NVarcharSqlType { length: 0 } unsized => NVarcharSqlType.Get(4000, unsized.Collation, unsized.Coercibility),
            VarcharSqlType { length: 0 } unsized => VarcharSqlType.Get(8000, unsized.Collation, unsized.Coercibility),
            // A float16 vector describes as the varchar(max) text real sends
            // every client for it (probed 2026-09-29 against SQL Server 2025).
            VectorSqlType { IsFloat16: true } => Float16DescribeType,
            var declared => declared,
        };
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
        var alias = result.ColumnAliasTypes?[index];
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
            alias is not null ? SqlValue.FromInt32(alias.UserTypeId) : type is VectorSqlType ? SqlValue.FromInt32(type.UserTypeId) : nullInt,
            alias is null ? nullName : SqlValue.FromSystemName(alias.Schema.Database.Name),
            alias is null ? nullName : SqlValue.FromSystemName(alias.Schema.Name),
            alias is null ? nullName : SqlValue.FromSystemName(alias.Name),
            SqlValue.Null(NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit)),
            nullInt, nullName, nullName, nullName,
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(type is XmlSqlType || (collation is not null && (collation.Name.Contains("_CS", StringComparison.OrdinalIgnoreCase) || collation.Name.Contains("_BIN", StringComparison.OrdinalIgnoreCase)))),
            SqlValue.FromBoolean(false),
            nullName, nullName, nullName, nullName, nullName,
            SqlValue.FromBoolean((origin?.Identity ?? origin?.IdentitySource ?? result.ColumnIdentitySources?[index]) is not null),
            nullBit,
            // The COLMETADATA flags trace a column through views, derived
            // tables and inline functions to what it reads: updatable (0x08)
            // or computed (0x20) (probed 2026-09-26 against SQL Server 2025).
            SqlValue.FromBoolean(result.ColumnWireFlags is { } updatableFlags
                ? !result.IsGrouped && (updatableFlags[index] & 0x08) != 0
                : !result.IsGrouped && origin is { Identity: null } && (origin.Computed is null || origin.GraphKind != GraphColumnKind.None) && type != SqlType.RowVersion),
            SqlValue.FromBoolean(result.ColumnWireFlags is { } computedFlags
                ? (computedFlags[index] & 0x20) != 0
                : origin is { Computed: not null, GraphKind: GraphColumnKind.None } || (origin is null && result.ColumnIsComputed is { } computed && computed[index])),
            SqlValue.FromBoolean(false),
            nullSmall, SqlValue.Null(SqlType.Bit), nullSmall,
            SqlValue.FromInt32(tdsType),
            SqlValue.FromInt32(tdsLength),
            codec is null ? nullInt : SqlValue.FromInt32(DescribedCollationInfo(codec, collation!)),
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
    /// for a MAX type and 8100 for xml, and a vector's own 245.
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
        // rowversion travels as BIGBINARY and sql_variant at 8009 (probed
        // 2026-10-04 against SQL Server 2025).
        SqlVariantSqlType => (98, 8009),
        _ when type == SqlType.RowVersion => (173, 8),
        // A vector-aware client's own token for the type (probed 2026-09-26
        // against SQL Server 2025).
        VectorSqlType => (245, maxLength),
        _ => (type.SystemTypeId, maxLength < 0 ? 65535 : maxLength),
    };
}
