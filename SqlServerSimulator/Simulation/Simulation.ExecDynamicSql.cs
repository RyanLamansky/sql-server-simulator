using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses <c>EXEC ( &lt;string-expression&gt; )</c> — dynamic SQL via
    /// EXEC. The string operand is evaluated in the caller's batch (so
    /// concatenation works: <c>EXEC ('SELECT ' + @col + ' FROM t')</c>),
    /// then the resulting string is re-tokenized and dispatched as a fresh
    /// batch inside its own <see cref="BatchContext"/>. Outer variables
    /// are NOT visible inside the dynamic batch (probe-confirmed: real SQL
    /// Server raises Msg 137 if the dynamic SQL references an outer
    /// <c>@var</c>). Result sets from the dynamic batch propagate to the
    /// outer caller.
    /// </summary>
    /// <remarks>
    /// Cursor on entry: the opening <c>(</c> after EXEC. Cursor on exit:
    /// the token after the closing <c>)</c>. Skip-mode evaluates the
    /// expression (cursor advance) but suppresses the dispatch.
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseExecDynamicSql(BatchContext batch, string? returnCodeVar)
    {
        var context = batch.Parser;
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        var sqlExpression = Expression.Parse(context);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        if (batch.IsSkipping)
            yield break;

        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        var sqlValue = sqlExpression.Run(new RuntimeContext(
            name => throw SimulatedSqlException.MustDeclareScalarVariable(name.Leaf),
            batch));
        if (sqlValue.IsNull)
            yield break; // dynamic SQL of NULL → no-op (matches real SQL Server's lenient handling)

        var sqlText = sqlValue.CoerceTo(VarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault)).AsString;
        foreach (var outcome in ExecuteDynamicBatch(batch, sqlText, preDeclaredVariables: null))
            yield return outcome;

        // EXEC (@sql) doesn't expose a return code in the standard sense
        // (the dynamic batch is opaque to the caller); the caller's @rc
        // is left at 0. Probe shows no observable rc from this form.
        if (returnCodeVar is not null)
        {
            var rcSlot = batch.GetVariableSlot(returnCodeVar);
            rcSlot.Value = SqlValue.FromInt32(0).CoerceTo(rcSlot.DeclaredType);
        }
    }

    /// <summary>
    /// Parses an <c>EXEC sp_executesql N'sql', N'@p1 type [OUTPUT], ...',
    /// @p1 = arg1, ...</c> call. The first argument is the SQL text; the
    /// second (optional) is a parameter-declaration string that pre-declares
    /// the dynamic batch's <c>@</c>-variables; remaining args bind values
    /// (with optional <c>OUTPUT</c> for writeback). Outer <c>@</c>-variables
    /// are NOT visible inside the dynamic batch.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cursor on entry: first token after the parsed <c>sp_executesql</c>
    /// procedure name (a literal, an <c>@</c>-variable, or end-of-args).
    /// Cursor on exit: the trailing statement boundary.
    /// </para>
    /// <para>
    /// The parameter-declaration string is mini-parsed: comma-separated
    /// entries each shaped like <c>@name type [OUTPUT]</c>. Values for each
    /// declared parameter come from the trailing arg list (positional or
    /// named); OUTPUT parameters write back to the caller's variable slot
    /// at exit. Probe-confirmed against SQL Server 2025.
    /// </para>
    /// </remarks>
    private IEnumerable<SimulatedStatementOutcome> ParseSpExecuteSql(BatchContext batch, string? returnCodeVar)
    {
        var context = batch.Parser;

        // Argument 1: SQL text (literal or @-variable, coerced to string).
        var (sqlRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
        var sqlValue = sqlRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
        var hasMoreArgs = context.Token is Operator { Character: ',' };

        // Argument 2 (optional): parameter-declaration string.
        List<SpExecuteSqlParam>? declaredParams = null;
        if (hasMoreArgs)
        {
            context.MoveNextRequired();
            var (paramDefsRaw, _) = ParseSpExecuteSqlValueArg(context, batch);
            var paramDefs = paramDefsRaw.CoerceTo(NVarcharSqlType.Get(-1, Collation.Baseline, Coercibility.CoercibleDefault));
            if (!paramDefs.IsNull)
                declaredParams = ParseSpExecuteSqlParamDefinitions(paramDefs.AsString, batch.Connection);
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        // Remaining args: positional/named values bound to declared params.
        var argumentValues = new List<(string? Name, SqlValue Value, VariableSlot? OutputSlot)>();
        while (hasMoreArgs)
        {
            context.MoveNextRequired();
            string? argName = null;
            if (context.Token is AtPrefixedString candidateName)
            {
                var checkpoint = context.SaveCheckpoint();
                context.MoveNextRequired();
                if (context.Token is Operator { Character: '=' })
                {
                    argName = candidateName.Value;
                    context.MoveNextRequired();
                }
                else
                {
                    context.RestoreCheckpoint(checkpoint);
                }
            }
            var (argValue, argOutputSlot) = ParseSpExecuteSqlValueArg(context, batch);
            argumentValues.Add((argName, argValue, argOutputSlot));
            hasMoreArgs = context.Token is Operator { Character: ',' };
        }

        if (batch.IsSkipping)
            yield break;

        if (sqlValue.IsNull)
            yield break;

        var sqlText = sqlValue.AsString;
        var connection = batch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        // Bind declared params: positional fill first, then named lookup.
        var preDeclared = new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase);
        var outputBindings = new List<(SpExecuteSqlParam Param, VariableSlot CallerSlot)>();
        if (declaredParams is not null)
        {
            var positional = 0;
            var bound = new SqlValue?[declaredParams.Count];
            var boundOutputSlots = new VariableSlot?[declaredParams.Count];
            foreach (var (name, value, outputSlot) in argumentValues)
            {
                int idx;
                if (name is null)
                {
                    idx = positional++;
                    if (idx >= declaredParams.Count)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                }
                else
                {
                    idx = -1;
                    for (var i = 0; i < declaredParams.Count; i++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(declaredParams[i].Name, name))
                        {
                            idx = i;
                            break;
                        }
                    }
                    if (idx < 0)
                        throw SimulatedSqlException.MustDeclareScalarVariable(name);
                }
                bound[idx] = value;
                boundOutputSlots[idx] = outputSlot;
            }
            for (var i = 0; i < declaredParams.Count; i++)
            {
                var param = declaredParams[i];
                var initialValue = bound[i]?.CoerceTo(param.Type) ?? SqlValue.Null(param.Type);
                var slot = new VariableSlot(param.Type, declaredMaxLength: null, initialValue, parameter: null);
                preDeclared[param.Name] = slot;
                if (param.IsOutput && boundOutputSlots[i] is { } caller)
                    outputBindings.Add((param, caller));
            }
        }

        foreach (var outcome in ExecuteDynamicBatch(batch, sqlText, preDeclared))
            yield return outcome;

        // Writeback: sp_executesql's OUTPUT params copy the dynamic batch's
        // final variable values back to the caller's slots.
        foreach (var (param, callerSlot) in outputBindings)
        {
            if (preDeclared.TryGetValue(param.Name, out var slot))
                callerSlot.Value = slot.Value.CoerceTo(callerSlot.DeclaredType);
        }

        if (returnCodeVar is not null)
        {
            var rcSlot = batch.GetVariableSlot(returnCodeVar);
            rcSlot.Value = SqlValue.FromInt32(0).CoerceTo(rcSlot.DeclaredType);
        }
    }

    /// <summary>
    /// Parses one sp_executesql positional / named-value argument. Accepts
    /// the same shapes as a regular EXEC argument
    /// (<see cref="ParseExecArgument"/>) but doesn't enforce the no-mixed-
    /// position rule — sp_executesql's grammar is more permissive.
    /// </summary>
    private static (SqlValue Value, VariableSlot? OutputSlot) ParseSpExecuteSqlValueArg(ParserContext context, BatchContext batch)
    {
        if (context.Token is AtPrefixedString varRef)
        {
            var slot = batch.GetVariableSlot(varRef.Value);
            context.MoveNextOptional();
            VariableSlot? outputSlot = null;
            if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                outputSlot = slot;
                context.MoveNextOptional();
            }
            return (slot.Value, outputSlot);
        }
        if (context.Token is Literal lit)
        {
            context.MoveNextOptional();
            return (lit.Value, null);
        }
        if (context.Token is Numeric num)
        {
            context.MoveNextOptional();
            return (num.Value, null);
        }
        if (context.Token is ReservedKeyword { Keyword: Keyword.Null })
        {
            context.MoveNextOptional();
            return (SqlValue.Null(SqlType.Int32), null);
        }
        throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    /// <summary>
    /// Mini-parser for sp_executesql's parameter-declaration string. Splits
    /// on commas (outside parens), then parses each segment as
    /// <c>@name type [OUTPUT]</c>. Returns the ordered parameter list used
    /// to seed the dynamic batch.
    /// </summary>
    private static List<SpExecuteSqlParam> ParseSpExecuteSqlParamDefinitions(string source, SimulatedDbConnection connection)
    {
        // Wrap the param-def string in a synthetic SimulatedDbCommand so the
        // tokenizer can walk it through ParserContext. Reuse the outer
        // connection's Simulation reference — we don't dispatch through this
        // batch, only walk tokens.
        using var defCommand = new SimulatedDbCommand(connection.Simulation, connection);
#pragma warning disable CA2100
        defCommand.CommandText = source;
#pragma warning restore CA2100
        var defBatch = new BatchContext(defCommand);
        var defContext = defBatch.Parser;
        defContext.MoveNextOptional();

        var parameters = new List<SpExecuteSqlParam>();
        while (defContext.Token is not null)
        {
            if (defContext.Token is not AtPrefixedString name)
                throw SimulatedSqlException.SyntaxErrorNear(defContext);
            defContext.MoveNextRequired();

            // Type parsing reuses the procedure-parameter type grammar.
            var (type, _) = ParseSpExecuteSqlParamType(defContext);

            var isOutput = false;
            if (defContext.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output or ContextualKeyword.Out })
            {
                isOutput = true;
                defContext.MoveNextOptional();
            }

            parameters.Add(new SpExecuteSqlParam(name.Value, type, isOutput));

            if (defContext.Token is Operator { Character: ',' })
            {
                defContext.MoveNextRequired();
                continue;
            }
            break;
        }
        return parameters;
    }

    /// <summary>
    /// Parses a parameter type inside an sp_executesql parameter-declaration
    /// string. Shape mirrors <see cref="ParseProcedureParameterType"/> but
    /// without the optional default expression.
    /// </summary>
    private static (SqlType Type, int? DeclaredMaxLength) ParseSpExecuteSqlParamType(ParserContext context)
    {
        if (context.Token is not Name)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var qualifiedTypeName = BatchContext.ParseObjectName(context);
        var typeName = (Name)context.Token;
        context.MoveNextOptional();

        int? declaredMaxLength = null;
        int? declaredScale = null;
        if (context.Token is Operator { Character: '(' })
        {
            var lengthToken = context.GetNextRequired();
            declaredMaxLength = lengthToken is Numeric { Value: { IsNull: false } numericValue }
                ? numericValue.AsInt32
                : context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Max }
                    ? SqlType.MaxLengthSentinel
                    : throw SimulatedSqlException.SyntaxErrorNear(context);
            switch (context.GetNextRequired())
            {
                case Operator { Character: ',' }:
                    if (context.GetNextRequired() is not Numeric { Value: { IsNull: false } scaleValue })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    declaredScale = scaleValue.AsInt32;
                    if (context.GetNextRequired() is not Operator { Character: ')' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    break;
                case Operator { Character: ')' }:
                    break;
                default:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        var (resolvedType, _, _) = ResolveTypeReference(
            context.Batch, qualifiedTypeName, typeName, declaredMaxLength, declaredScale,
            index: 1, columnName: null);
        return (resolvedType, declaredMaxLength);
    }

    /// <summary>
    /// Dispatches a string of SQL as a fresh child batch. The child batch
    /// shares the outer connection (so transaction / temp-table / catalog
    /// state are shared) but has its own variable scope — outer
    /// <c>@</c>-variables are invisible to the dynamic SQL.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> ExecuteDynamicBatch(
        BatchContext outerBatch,
        string sqlText,
        Dictionary<string, VariableSlot>? preDeclaredVariables)
    {
        var connection = outerBatch.Connection;
        using var dynCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // dynamic SQL is the application's input; the caller is responsible for sanitization
        dynCommand.CommandText = sqlText;
#pragma warning restore CA2100

        // Seed an empty variable dict (so outer @vars don't leak in) plus
        // any pre-declared sp_executesql parameters. Use the proc-body
        // BatchContext ctor with a sentinel ProcFrame so RETURN N parses
        // without raising Msg 178 — dynamic-SQL batches inherit the proc-
        // body's RETURN semantics (RETURN value is captured but unused).
        var variables = preDeclaredVariables is null
            ? new Dictionary<string, VariableSlot>(StringComparer.InvariantCultureIgnoreCase)
            : new Dictionary<string, VariableSlot>(preDeclaredVariables, StringComparer.InvariantCultureIgnoreCase);
        var procFrame = new ProcFrame("<dynamic-sql>");
        var innerBatch = new BatchContext(dynCommand, variables, procFrame);

        connection.NestingLevel++;
        List<SimulatedStatementOutcome> outcomes;
        try
        {
            var parser = innerBatch.Parser;
            parser.MoveNextOptional();
            outcomes = [.. DispatchStatementsUntil(innerBatch, endKeyword: null)];
        }
        finally
        {
            connection.NestingLevel--;
        }

        // Copy any pre-declared variable's final value back to the caller's
        // slot (sp_executesql OUTPUT writeback path).
        if (preDeclaredVariables is not null)
        {
            foreach (var (name, _) in preDeclaredVariables)
                preDeclaredVariables[name] = variables[name];
        }

        foreach (var outcome in outcomes)
            yield return outcome;
    }

    /// <summary>
    /// One parameter declared in an sp_executesql parameter-declaration
    /// string. Distinct from <see cref="ProcedureParameter"/> because
    /// sp_executesql params have no defaults — every declared param must be
    /// bound by a positional/named arg.
    /// </summary>
    private readonly struct SpExecuteSqlParam(string name, SqlType type, bool isOutput)
    {
        public readonly string Name = name;
        public readonly SqlType Type = type;
        public readonly bool IsOutput = isOutput;
    }
}
