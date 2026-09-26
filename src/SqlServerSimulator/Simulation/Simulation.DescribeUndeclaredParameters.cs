using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SqlType[] UndeclaredParameterSchema =
    [
        SqlType.Int32, SqlType.SystemName, SqlType.Int32, SqlType.SystemName, SqlType.SmallInt, SqlType.TinyInt,
        SqlType.TinyInt, SqlType.Int32, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, NVarcharSqlType.Get(4000, Collation.Baseline, Coercibility.Implicit),
        SqlType.Int32, SqlType.SystemName, SqlType.SystemName, SqlType.SystemName, SqlType.Bit, SqlType.Bit,
        SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.SystemName, SqlType.Int32, SqlType.Int32,
    ];

    private static readonly string[] UndeclaredParameterColumnNames =
    [
        "parameter_ordinal", "name", "suggested_system_type_id", "suggested_system_type_name", "suggested_max_length", "suggested_precision",
        "suggested_scale", "suggested_user_type_id", "suggested_user_type_database", "suggested_user_type_schema", "suggested_user_type_name", "suggested_assembly_qualified_type_name",
        "suggested_xml_collection_id", "suggested_xml_collection_database", "suggested_xml_collection_schema", "suggested_xml_collection_name", "suggested_is_xml_document", "suggested_is_case_sensitive",
        "suggested_is_fixed_length_clr_type", "suggested_is_input", "suggested_is_output", "formal_parameter_name", "suggested_tds_type_id", "suggested_tds_length",
    ];

    /// <summary>
    /// <c>sp_describe_undeclared_parameters @tsql [, @params]</c> — one row per
    /// parameter <c>@tsql</c> uses but neither declares nor finds in
    /// <c>@params</c>, with the type the batch implies for it: what ODBC's
    /// <c>SQLDescribeParam</c> (so pyodbc typing a <c>None</c>) and JDBC's
    /// parameter metadata ask the server. The batch compiles under
    /// <c>SET FMTONLY ON</c> with each undeclared parameter declared under a
    /// placeholder type, and the binder records what each place a parameter
    /// meets implies (see <see cref="UndeclaredParameterDeduction"/>). Output
    /// shape and the refusals — Msg 11508 for a parameter used twice, 11503 /
    /// 11506 / 11507 for one no type fits, a compile error followed by Msg
    /// 11501 — probed 2026-09-26 against SQL Server 2025. An output parameter
    /// (<c>SELECT @p = 1</c>) isn't described.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDescribeUndeclaredParameters(BatchContext batch)
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
                : throw SimulatedSqlException.InvalidProcedureParameters("sp_describe_undeclared_parameters");
            switch (slot)
            {
                case 0: tsql = argument.Value; break;
                case 1: parameters = argument.Value; break;
                default: throw SimulatedSqlException.TooManyArgumentsToFunction("sp_describe_undeclared_parameters");
            }
        }
        if (tsql is not { } statement)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_describe_undeclared_parameters", "tsql", state: 20);
        if (statement.IsNull)
            throw SimulatedSqlException.ProcedureExpectsNVarcharMaxParameter("tsql");

        var rows = this.DescribeUndeclaredParameters(batch, statement.AsString, parameters);
        yield return new SimulatedSqlResultSet(UndeclaredParameterSchema, UndeclaredParameterColumnNames,
            rows.ConvertAll(row => RowEncoder.EncodeRow(UndeclaredParameterSchema, row)));
    }

    private List<SqlValue[]> DescribeUndeclaredParameters(BatchContext batch, string tsql, SqlValue? parameters)
    {
        var connection = batch.Connection;
        var declared = new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer);
        if (parameters is { IsNull: false } parameterText)
        {
            foreach (var parameter in ParseSpExecuteSqlParamDefinitions(parameterText.AsString, connection))
                declared[parameter.Name] = new VariableSlot(parameter.Type, declaredMaxLength: null, SqlValue.Null(parameter.Type), parameter: null);
        }

        // An undeclared name surfaces as the batch's Msg 137; each one found
        // joins the placeholder declarations and the batch compiles again.
        var undeclared = new HashSet<string>(BatchContext.VariableNameComparer);
        UndeclaredParameterDeduction deduction;
        while (true)
        {
            deduction = new UndeclaredParameterDeduction(undeclared);
            var slots = new Dictionary<string, VariableSlot>(declared, BatchContext.VariableNameComparer);
            foreach (var name in undeclared)
                slots[name] = new VariableSlot(SqlType.Int32, declaredMaxLength: null, SqlValue.Null(SqlType.Int32), parameter: null);

            var savedFmtOnly = connection.FmtOnly;
            var savedDeduction = UndeclaredParameterDeduction.Current;
            connection.FmtOnly = true;
            UndeclaredParameterDeduction.Current = deduction;
            try
            {
                foreach (var _ in this.ExecuteDynamicBatch(batch, tsql, slots))
                {
                }
                break;
            }
            catch (SimulatedSqlException error) when (error.Number == 137 && UndeclaredName(error) is { } name
                && !declared.ContainsKey(name) && undeclared.Add(name))
            {
            }
            catch (SimulatedSqlException error)
            {
                // A refusal the deduction already settled outranks what the
                // placeholder type then tripped over downstream of it.
                throw deduction.Failure
                    ?? SimulatedSqlException.Aggregate([error, SimulatedSqlException.BatchCouldNotBeAnalyzed(state: 2)]);
            }
            finally
            {
                connection.FmtOnly = savedFmtOnly;
                UndeclaredParameterDeduction.Current = savedDeduction;
            }
        }

        var ordered = VariablesInOrderOfUse(tsql, connection);
        var rows = new List<SqlValue[]>();
        foreach (var (name, uses) in ordered)
        {
            if (!undeclared.Contains(name))
                continue;
            if (uses > 1)
                throw SimulatedSqlException.UndeclaredParameterUsedTwice(name);
        }
        if (deduction.Failure is { } failure)
            throw failure;
        foreach (var (name, _) in ordered)
        {
            if (!undeclared.Contains(name))
                continue;
            if (!deduction.Deduced.TryGetValue(name, out var deduced))
                throw SimulatedSqlException.ParameterTypeNotUnique(name);
            rows.Add(DescribeUndeclaredParameter(rows.Count + 1, name, deduced.Type, deduced.Numeric));
        }
        return rows;
    }

    // The name Msg 137 reports, as `Must declare the scalar variable "@name".` spells it.
    private static string? UndeclaredName(SimulatedSqlException error)
    {
        var message = error.Errors[0].Message;
        var start = message.IndexOf("\"@", StringComparison.Ordinal);
        var end = message.LastIndexOf('"');
        return start >= 0 && end > start + 2 ? message[(start + 2)..end] : null;
    }

    // Every @name the text uses, in order of first use, with its use count. A
    // text the tokenizer refuses has already failed to compile.
    private static List<(string Name, int Uses)> VariablesInOrderOfUse(string tsql, SimulatedDbConnection connection)
    {
        var counts = new Dictionary<string, int>(BatchContext.VariableNameComparer);
        var order = new List<string>();
        var index = 0;
        var collation = connection.CurrentDatabase.Collation;
        while (Tokenizer.NextToken(tsql, ref index, collation) is { } token)
        {
            if (token is not AtPrefixedString variable)
                continue;
            var name = variable.Span.ToString();
            if (counts.TryGetValue(name, out var uses))
            {
                counts[name] = uses + 1;
            }
            else
            {
                counts[name] = 1;
                order.Add(name);
            }
        }
        return order.ConvertAll(name => (name, counts[name]));
    }

    private static SqlValue[] DescribeUndeclaredParameter(int ordinal, string name, SqlType type, bool numeric)
    {
        var (maxLength, precision, scale) = BuiltInResources.GetSysColumnMetadata(new HeapColumn(string.Empty, type, maxLength: null, nullable: true));
        var (tdsType, tdsLength) = DescribeTdsType(type, notNull: false, numeric, maxLength);
        var collation = type.Category == SqlTypeCategory.String ? type.Collation : null;
        var nullName = SqlValue.Null(SqlType.SystemName);
        var nullInt = SqlValue.Null(SqlType.Int32);
        return
        [
            SqlValue.FromInt32(ordinal),
            SqlValue.FromSystemName("@" + name),
            SqlValue.FromInt32(numeric ? 108 : type.SystemTypeId),
            SqlValue.FromSystemName(DescribeTypeName(type, numeric)),
            SqlValue.FromInt16(maxLength),
            SqlValue.FromByte(precision),
            SqlValue.FromByte(scale),
            nullInt, nullName, nullName, nullName, SqlValue.Null(UndeclaredParameterSchema[11]),
            nullInt, nullName, nullName, nullName,
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(type is XmlSqlType || (collation is not null && collation.Name.Contains("_CS", StringComparison.OrdinalIgnoreCase))),
            SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(true),
            SqlValue.FromBoolean(false),
            nullName,
            SqlValue.FromInt32(tdsType),
            SqlValue.FromInt32(tdsLength),
        ];
    }
}
